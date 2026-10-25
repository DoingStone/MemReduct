using System.Reflection;
using MemReduct.Gui;

namespace Smoke;

/// <summary>
/// Exercises SlimListView.AutoSizeColumns in every hostile state that could produce the
/// reported NullReferenceException. The method must never throw: it is called from
/// OnClientSizeChanged, which WinForms raises from WM_WINDOWPOSCHANGED, outside any
/// catch, so anything escaping it becomes the "MemReduct —— 意外错误" fatal dialog.
/// </summary>
internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Length > 0 && args[0] == "live")
        {
            return LiveResize();
        }

        Run("no handle yet, no columns", lv => { });
        Run("handle created, no columns", lv => Realize(lv));
        Run("one column", lv => { Realize(lv); lv.Columns.Add("清理区域", 140); });
        Run("all five MainForm columns", lv =>
        {
            Realize(lv);
            AddMainFormColumns(lv);
        });
        Run("columns added without handle", lv => AddMainFormColumns(lv));
        Run("after Dispose", lv =>
        {
            Realize(lv);
            AddMainFormColumns(lv);
            lv.Dispose();
        });
        Run("after Dispose, no handle", lv =>
        {
            AddMainFormColumns(lv);
            lv.Dispose();
        });
        Run("with rows and subitems", lv =>
        {
            Realize(lv);
            AddMainFormColumns(lv);
            for (var i = 0; i < 40; i++)
            {
                lv.Items.Add(new ListViewItem(new[]
                {
                    "工作集", i % 2 == 0 ? "已清理" : "跳过",
                    (i * 128).ToString(), (i * 7).ToString(), "第 " + i + " 行详情",
                }));
            }
        });
        Run("narrow client area", lv =>
        {
            Realize(lv);
            AddMainFormColumns(lv);
            lv.Width = 40;
        });
        Run("call while columns are being mutated", lv =>
        {
            Realize(lv);
            AddMainFormColumns(lv);
            lv.Columns.Clear();
        });
        Run("detail column hidden then shown", lv =>
        {
            Realize(lv);
            AddMainFormColumns(lv);
            lv.Columns[4]!.Width = 0;
            lv.Columns[4]!.Width = 380;
        });

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? "SMOKE PASS - AutoSizeColumns never threw in 12 states"
            : $"SMOKE FAIL - {_failures} state(s) threw");

        return _failures == 0 ? 0 : 1;
    }

    private static void Run(string label, Action<SlimListView> arrange)
    {
        var list = new SlimListView { Dock = DockStyle.Fill };

        try
        {
            arrange(list);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  arrange failed for '{label}': {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            list.AutoSizeColumns();
            Console.WriteLine($"PASS  {label}");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"FAIL  {label} -> {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("      " + ex.StackTrace?.Split('\n')[0].Trim());
        }
        finally
        {
            try { list.Dispose(); } catch { /* disposal is not under test */ }
        }
    }

    private static void AddMainFormColumns(SlimListView list)
    {
        list.Columns.Add("清理区域", 140);
        list.Columns.Add("结果", 66);
        list.Columns.Add("释放", 92);
        list.Columns.Add("用时", 78);
        list.Columns.Add("详情", 380);
    }

    private static void Realize(SlimListView list)
    {
        var host = new Form { Width = 900, Height = 400, ShowInTaskbar = false };
        host.Controls.Add(list);
        _ = host.Handle;
        host.CreateControl();
    }

    /// <summary>
    /// Drives a real window through a sequence of size changes on a running message loop.
    /// This is the exact path from the user's crash: WM_WINDOWPOSCHANGED ->
    /// Control.WmWindowPosChanged -> UpdateBounds -> OnClientSizeChanged -> AutoSizeColumns.
    /// Any exception that escapes the layout pass is routed to the unhandled-exception
    /// handler here, which is what produced the fatal dialog in the shipped app.
    /// </summary>
    private static int LiveResize()
    {
        var escaped = new List<string>();

        Application.ThreadException += (_, e) => escaped.Add("ThreadException: " + e.Exception.GetType().Name + ": " + e.Exception.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            escaped.Add("UnhandledException: " + (ex?.GetType().Name ?? "?") + ": " + ex?.Message);
        };

        var list = new SlimListView { Dock = DockStyle.Fill };
        AddMainFormColumns(list);
        for (var i = 0; i < 30; i++)
        {
            list.Items.Add(new ListViewItem(new[] { "工作集", "已清理", "1024", "3", "详情 " + i }));
        }

        var host = new Form { Text = "smoke-live", Width = 900, Height = 400, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterScreen };
        host.Controls.Add(list);

        var sizes = new (int W, int H)[]
        {
            (1200, 700), (640, 300), (200, 120), (1920, 1080), (300, 900),
            (640, 300), (1200, 700), (100, 100), (900, 400),
        };
        var step = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 120 };
        timer.Tick += (_, _) =>
        {
            if (step >= sizes.Length)
            {
                timer.Stop();
                timer.Dispose();
                host.Close();
                return;
            }

            host.Size = new Size(sizes[step].W, sizes[step].H);
            step++;
        };

        host.Shown += (_, _) => timer.Start();
        Application.Run(host);

        if (escaped.Count == 0)
        {
            Console.WriteLine($"LIVE PASS - {sizes.Length} real resizes, nothing escaped the layout pass");
            return 0;
        }

        Console.WriteLine($"LIVE FAIL - {escaped.Count} exception(s) escaped:");
        foreach (var line in escaped)
        {
            Console.WriteLine("  " + line);
        }

        return 1;
    }
}
