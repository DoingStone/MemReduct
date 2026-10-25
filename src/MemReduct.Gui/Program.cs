using MemReduct.Core;

namespace MemReduct.Gui;

internal static class Program
{
    /// <summary>
    /// Per-session name, so a second launch is detected inside this logon session only.
    /// </summary>
    private const string MutexName = @"Local\MemReduct.SingleInstance";

    [STAThread]
    private static void Main()
    {
        // Two copies would fight over the same memory lists and double up their timers, so a
        // second launch just points at the copy already in the notification area.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "MemReduct 已经在运行。\n\n请在通知区域（时钟旁边）找到它。",
                "MemReduct",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        Application.ThreadException += (_, e) => ReportFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception);

        // The palette and the type scale have to be in place before the window is constructed:
        // every control reads them as it is built, and the fonts it captures are the ones Apply
        // creates. Rebuilding the window later is what switches them.
        var settings = AppSettings.Load();
        Theme.Apply(settings.Theme, settings.UiScale);

        Application.Run(new MainForm(settings));
    }
    /// <summary>
    /// Surfaces an unexpected failure instead of vanishing, then leaves the process to die —
    /// a memory tool that dies silently is worse than one that explains itself.
    /// </summary>
    private static void ReportFatal(Exception? exception)
    {
        var detail = exception?.ToString() ?? "发生了未知错误。";

        try
        {
            MessageBox.Show(
                detail,
                "MemReduct —— 意外错误",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (Exception)
        {
            // Nothing else we can do if even the message box fails.
        }
    }
}
