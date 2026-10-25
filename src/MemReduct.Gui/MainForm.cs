using System.Diagnostics;
using MemReduct.Core;
using MemReduct.Core.Cleaners;
using MemReduct.Core.Hardware;

namespace MemReduct.Gui;

/// <summary>
/// The main window: live memory statistics, area selection, manual and scheduled cleaning,
/// and a notification-area presence that keeps the tool one click away.
/// </summary>
public sealed class MainForm : Form
{
    // Fixed row heights, expressed at the designed type scale and passed through Theme.S so the
    // 界面缩放 setting grows the layout with the text instead of leaving large fonts in small rows.
    // They are properties rather than constants for exactly that reason: a constant would be baked
    // in at compile time and could not follow a scale change.
    private static int HeaderHeight => Theme.S(68);

    // Row heights are not "how tall the card looks" — each card also carries a bottom margin and
    // Theme.CardPadding above and below its body, plus a CardTitleHeight title strip. The usable
    // body is (height - Theme.Gap - 2 * Theme.CardPadding - Theme.CardTitleHeight), so a card
    // whose rows sum to more than that clips its last row rather than growing. The comments below
    // record the arithmetic.
    private static int StatsHeight => Theme.S(236);
    private static int HardwareHeight => Theme.S(168);

    // One row, two cards: 清理区域 on the left and 自动清理 on the right. They used to take two
    // full-width rows, and the 硬件监控 card needs one of them. Both cards already sized their
    // contents from columns rather than from the window width, so at half width they keep their
    // alignment — only the preset buttons had to stack, because three 78 px buttons do not fit
    // in a quarter of the row.
    private static int MiddleHeight => Theme.S(204);
    private static int ActionsHeight => Theme.S(60);

    private readonly MemoryCleaner _cleaner = new();
    private readonly AppSettings _settings;
    private readonly List<(CleanArea Area, Switch Toggle)> _areaToggles = [];
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    private readonly System.Windows.Forms.Timer _countdownTimer = new();
    private readonly NotifyIcon _tray = new();
    private readonly ToolTip _tips = new();
    private readonly Icon? _appIcon;

    // The tray icon is redrawn as memory load moves. Tracked separately from _appIcon so
    // disposing a redraw can never free the window's own icon.
    private Icon? _trayIcon;
    private int _trayIconPercent = -1;
    private int _trayIconSize;

    private LoadBar _loadBar = null!;
    private Label _loadPercent = null!;
    private Label _loadUnit = null!;
    private Label _loadCaption = null!;
    private Label _loadState = null!;
    private Label _availableValue = null!;
    private Label _usedValue = null!;
    private Label _commitValue = null!;
    private Label _kernelValue = null!;
    private Label _countsValue = null!;
    private Label _freeValue = null!;
    private Label _standbyValue = null!;
    private Label _modifiedValue = null!;
    private Label _cacheValue = null!;

    // The labels that show "不可用" when the page-list counters cannot be opened. Held as a field
    // so the refresh path walks a ready-made group instead of allocating a fresh array each tick.
    private Label[] _unavailableLabels = [];
    private SlimListView _results = null!;
    private CardPanel _resultsCard = null!;
    private Label _summary = null!;
    private FlatButton _cleanButton = null!;
    private FlatButton _dryRunButton = null!;
    private FlatButton _elevateButton = null!;
    private Switch _autoEnabled = null!;
    private NumericUpDown _autoMinutes = null!;
    private Switch _autoThreshold = null!;
    private NumericUpDown _thresholdPercent = null!;
    private Label _nextRunLabel = null!;
    private CardPanel _automationCard = null!;
    private ToolStripStatusLabel _statusLabel = null!;

    // 硬件监控 card. Every figure is written by RefreshHardware, and each starts in the state it
    // shows before the first successful sample rather than as an empty string, so a collector
    // that cannot start reads as "unavailable" instead of as a blank.
    private readonly HardwareInfoProvider _hardware = new();

    // The page-list counters used to be opened and closed on every refresh tick — one query
    // open, six counter registrations, one collect, six formatted reads and one close, every
    // second, for the life of the session. The reader holds the query open instead, which is
    // exactly what the CLI does (Program.cs reuses one PageListReader for the whole run).
    // Lazily created so a machine without the counter set pays one failed open, not one per tick.
    private PageListReader? _pageLists;
    private bool _pageListsInitialized;
    private CardPanel _hardwareCard = null!;
    private Label _cpuNameValue = null!;
    private Label _cpuLoadValue = null!;
    private LoadBar _cpuLoadBar = null!;
    private Label _cpuDetailValue = null!;
    private CoreLoadStrip _coreStrip = null!;
    private Label _gpuNameValue = null!;
    private Label _gpuLoadValue = null!;
    private LoadBar _gpuLoadBar = null!;
    private Label _gpuMemoryValue = null!;
    private LoadBar _gpuMemoryBar = null!;
    private Label _gpuTempValue = null!;
    private FlowLayoutPanel _temperatureChips = null!;

    // The chip row is rebuilt only when the sensor set changes, so it needs a key to compare
    // against and a place to look up the value label each tick writes into.
    private string _temperatureSignature = string.Empty;
    private readonly Dictionary<string, Label> _temperatureChipValues = new();

    // Menu items that mirror a setting, kept so both directions can be synchronised.
    private ToolStripMenuItem _menuMinimizeToTray = null!;
    private ToolStripMenuItem _menuStartWithWindows = null!;
    private readonly Dictionary<AppTheme, ToolStripMenuItem> _menuThemes = [];
    private readonly Dictionary<double, ToolStripMenuItem> _menuScales = [];
    private ToolStripMenuItem _menuNotifications = null!;
    private readonly Dictionary<int, ToolStripMenuItem> _menuIntervals = [];

    private MemorySnapshot? _lastSnapshot;
    private DateTimeOffset? _nextAutoRun;
    private bool _busy;
    private bool _reallyExit;
    private bool _trayHintShown;

    // Cleared by the first request to show the window — see SetVisibleCore.
    private bool _startInTray = true;

    public MainForm(AppSettings settings)
    {
        _settings = settings;
        _appIcon = TryLoadAppIcon();

        Text = "MemReduct —— 内存清理工具";
        Icon = _appIcon;

        // Everything in this window is laid out by hand and sized through Theme.S, so the form's
        // own font-driven autoscaling is switched off. A code-only form has no design-time font
        // metric to scale from, and assigning the 10 pt body font after ClientSize made WinForms
        // rescale the window straight back down to MinimumSize — the window opened at its floor
        // instead of the size the scale asked for. The font is still assigned here, because every
        // child that does not set its own inherits it.
        AutoScaleMode = AutoScaleMode.None;
        Font = Theme.Body;

        // The window is sized from the same scale as the type, so a larger interface gets a
        // larger window rather than the same window with clipped rows. Clamped to the working
        // area because the top scale step would otherwise open taller than the screen and put
        // the buttons off the bottom edge.
        var working = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        ClientSize = new Size(
            Math.Min(Theme.S(1240), working.Width - 40),
            Math.Min(Theme.S(1000), working.Height - 40));
        MinimumSize = new Size(Theme.S(900), Theme.S(700));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Canvas;
        ForeColor = Theme.TextPrimary;

        // The renderer belongs on the manager, not on each strip. MenuStrip.Renderer styles the
        // bar itself, but a drop-down is a separate ToolStripDropDownMenu that takes its renderer
        // from ToolStripManager — so setting it per strip leaves every drop-down in the system
        // light theme, which is a white panel hanging under a dark window. It also has to be
        // assigned before the strips are constructed, because each one captures the manager's
        // renderer as it is created.
        ToolStripManager.Renderer = new DarkMenuRenderer();

        BuildUi();
        BuildMenu();
        BuildTray();

        // Captures the baseline the first RefreshHardware subtracts from. Without this the first
        // sample has no previous counter value to diff against, and a CPU load computed from a
        // zero baseline reads as a solid 100 % for one frame.
        _hardware.Prime();

        _refreshTimer.Interval = Math.Clamp(_settings.RefreshIntervalMs, 250, 60_000);
        _refreshTimer.Tick += (_, _) => RefreshStats();
        _refreshTimer.Start();

        _countdownTimer.Interval = 1000;
        _countdownTimer.Tick += (_, _) => OnCountdownTick();
        _countdownTimer.Start();

        ApplySettingsToUi();
        RefreshStats();
    }

    // ---------------------------------------------------------------- layout

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            BackColor = Theme.Canvas,
            Padding = new Padding(16, 14, 16, 12),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, HeaderHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, StatsHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, HardwareHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, MiddleHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, ActionsHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildStatsCard(), 0, 1);
        root.Controls.Add(BuildHardwareCard(), 0, 2);
        root.Controls.Add(BuildMiddleRow(), 0, 3);
        root.Controls.Add(BuildActionsRow(), 0, 4);
        root.Controls.Add(BuildResultsCard(), 0, 5);

        var status = new StatusStrip
        {
            BackColor = Theme.Canvas,
            ForeColor = Theme.TextMuted,
            SizingGrip = false,
            Font = Theme.Caption,
        };
        _statusLabel = new ToolStripStatusLabel("就绪")
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        status.Items.Add(_statusLabel);

        Controls.Add(root);
        Controls.Add(status);
    }

    /// <summary>
    /// Rebuilds every control after the palette or the type scale changes.
    /// <para>
    /// Neither can be applied in place: each control reads the colours and fonts as it is
    /// constructed and then holds them, so switching either means constructing new controls.
    /// Rather than thread a refresh path through six owner-drawn classes, the window's children
    /// are discarded and built again — it is one form with a handful of children, so the cost is
    /// a frame.
    /// </para>
    /// <para>
    /// The result rows are cloned across the rebuild. They are the one piece of state that is not
    /// derived from the settings, and losing the list on a font-size change would look like the
    /// clean had been undone.
    /// </para>
    /// </summary>
    private void RebuildUi()
    {
        SuspendLayout();

        var previousRows = _results?.Items.Cast<ListViewItem>().Select(item => (ListViewItem)item.Clone()).ToArray()
            ?? [];
        var previousSummary = _summary?.Text;

        foreach (Control control in Controls.Cast<Control>().ToArray())
        {
            Controls.Remove(control);
            control.Dispose();
        }

        _areaToggles.Clear();
        _menuIntervals.Clear();

        // The tray menu is rebuilt too, and a NotifyIcon keeps whichever strip was last assigned.
        _tray.ContextMenuStrip?.Dispose();
        _tray.ContextMenuStrip = null;

        BackColor = Theme.Canvas;
        ForeColor = Theme.TextPrimary;
        Font = Theme.Body;

        BuildUi();
        BuildMenu();
        BuildTray();

        ApplySettingsToUi();
        RefreshStats();

        // The fields are assigned inside BuildUi; the compiler cannot see that across the call,
        // so the suppression states what the call order already guarantees.
        foreach (var row in previousRows)
        {
            _results!.Items.Add(row);
        }

        if (previousRows.Length > 0 && previousSummary is not null)
        {
            _summary!.Text = previousSummary;
            _results!.AutoSizeColumns();
        }

        ResumeLayout(performLayout: true);
    }

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };

        if (_appIcon is not null)
        {
            var logo = new PictureBox
            {
                Image = _appIcon.ToBitmap(),
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(Theme.S(30), Theme.S(30)),
                Location = new Point(0, Theme.S(8)),
                BackColor = Color.Transparent,
            };
            panel.Controls.Add(logo);
        }

        var title = new Label
        {
            Text = "MemReduct",
            Font = Theme.AppTitle,
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(Theme.S(40), Theme.S(2)),
        };

        var subtitle = new Label
        {
            Text = RuntimeInfo.Describe(),
            Font = Theme.Caption,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(Theme.S(42), Theme.S(34)),
        };

        _elevateButton = new FlatButton
        {
            Text = "以管理员身份重启",
            Variant = ButtonVariant.Secondary,
            Size = new Size(Theme.S(150), Theme.S(30)),
            Visible = !PrivilegeEnabler.IsElevated,
        };
        _elevateButton.Click += (_, _) => RestartElevated();
        _tips.SetToolTip(
            _elevateButton,
            "大多数清理区域需要管理员权限。没有权限时，程序会明确报告被拒绝，而不是静默失败。");

        var badge = new Label
        {
            Text = PrivilegeEnabler.IsElevated ? "● 管理员权限" : "● 标准用户",
            Font = Theme.Caption,
            ForeColor = PrivilegeEnabler.IsElevated ? Theme.Success : Theme.Warning,
            BackColor = Color.Transparent,
            AutoSize = true,
        };

        // Right-aligned as a group: the badge and the button keep their gap whatever the window
        // width, instead of each being pinned to the edge and colliding as it narrows.
        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 8, 0, 0),
        };
        right.Controls.Add(badge);
        if (_elevateButton.Visible)
        {
            _elevateButton.Margin = new Padding(14, 0, 0, 0);
            right.Controls.Add(_elevateButton);
        }

        panel.Controls.AddRange([title, subtitle, right]);
        right.BringToFront();

        return panel;
    }

    private Control BuildStatsCard()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Title = "内存概览",
            Margin = new Padding(0, 0, 0, Theme.Gap),
        };

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Surface,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(258)));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        body.Controls.Add(BuildHero(), 0, 0);
        body.Controls.Add(BuildStatGrid(), 1, 0);

        card.Body.Controls.Add(body);
        return card;
    }

    /// <summary>
    /// CPU, GPU and the temperature sensors, side by side.
    /// <para>
    /// Every reading arrives as null when it cannot be measured, so each slot shows an em dash
    /// until a real value lands. Nothing here falls back to zero: a drive that refuses the
    /// temperature IOCTL simply has no chip, which is a different statement from "26 °C".
    /// </para>
    /// </summary>
    private Control BuildHardwareCard()
    {
        _hardwareCard = new CardPanel
        {
            Dock = DockStyle.Fill,
            Title = "硬件监控",
            Margin = new Padding(0, 0, 0, Theme.Gap),
        };

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Theme.Surface,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        body.Controls.Add(BuildCpuColumn(), 0, 0);
        body.Controls.Add(BuildGpuColumn(), 1, 0);
        body.Controls.Add(BuildTemperatureColumn(), 2, 0);

        _hardwareCard.Body.Controls.Add(body);
        return _hardwareCard;
    }

    private Control BuildCpuColumn()
    {
        var column = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 0, Theme.Gap, 0),
            Padding = Padding.Empty,
        };

        // Rows that hold a caption are sized from the font, not from a designed constant: a
        // 16-px row clips the top and bottom off an 8.5 pt 中文 glyph.
        column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.RowHeight(Theme.Caption, 16)));
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.RowHeight(Theme.BodyBold, 28)));
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.RowHeight(Theme.Caption, 18)));
        column.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _cpuNameValue = MakeHardwareCaption("—");
        _cpuNameValue.Dock = DockStyle.Fill;

        _cpuLoadValue = MakeHardwareValue("—");
        _cpuLoadBar = new LoadBar
        {
            Dock = DockStyle.Fill,
            SurfaceColor = Theme.Surface,
            Margin = new Padding(Theme.S(8), Theme.S(9), 0, Theme.S(9)),
        };

        _cpuDetailValue = MakeHardwareCaption("—");
        _cpuDetailValue.Dock = DockStyle.Fill;

        // Left to fill, and deliberately so: the strip's bars are bottom-anchored, so the extra
        // height becomes taller bars rather than empty space under them.
        _coreStrip = new CoreLoadStrip
        {
            Dock = DockStyle.Fill,
            SurfaceColor = Theme.Surface,
            Margin = new Padding(0, Theme.S(4), 0, 0),
        };

        column.Controls.Add(_cpuNameValue, 0, 0);
        column.Controls.Add(BuildMetricRow(_cpuLoadValue, _cpuLoadBar), 0, 1);
        column.Controls.Add(_cpuDetailValue, 0, 2);
        column.Controls.Add(_coreStrip, 0, 3);
        return column;
    }

    private Control BuildGpuColumn()
    {
        var column = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 0, Theme.Gap, 0),
            Padding = Padding.Empty,
        };

        column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.RowHeight(Theme.Caption, 16)));
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.RowHeight(Theme.BodyBold, 28)));
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.RowHeight(Theme.BodyBold, 28)));
        column.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _gpuNameValue = MakeHardwareCaption("—");
        _gpuNameValue.Dock = DockStyle.Fill;

        _gpuLoadValue = MakeHardwareValue("—");
        _gpuLoadBar = new LoadBar
        {
            Dock = DockStyle.Fill,
            SurfaceColor = Theme.Surface,
            Margin = new Padding(Theme.S(8), Theme.S(9), 0, Theme.S(9)),
        };

        _gpuMemoryValue = MakeHardwareValue("—");
        _gpuMemoryBar = new LoadBar
        {
            Dock = DockStyle.Fill,
            SurfaceColor = Theme.Surface,
            Margin = new Padding(Theme.S(8), Theme.S(9), 0, Theme.S(9)),
        };

        // Temperature sits on its own band under the two bars rather than beside the memory
        // figure: a °C reading shares no scale with a byte count, and pairing them in one row
        // invites the bar's colour to be read as applying to both.
        _gpuTempValue = MakeHardwareCaption("—");
        _gpuTempValue.Dock = DockStyle.Fill;

        column.Controls.Add(_gpuNameValue, 0, 0);
        column.Controls.Add(BuildMetricRow(_gpuLoadValue, _gpuLoadBar), 0, 1);

        // Wider than the load row: "1.78 GB / 8 GB" does not fit where "5%" does, and an ellipsis
        // on a byte count hides the very digit that tells the user how much is left.
        column.Controls.Add(BuildMetricRow(_gpuMemoryValue, _gpuMemoryBar, valueWidth: 190), 0, 2);
        column.Controls.Add(_gpuTempValue, 0, 3);
        return column;
    }

    private Control BuildTemperatureColumn()
    {
        var column = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Surface,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        column.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.RowHeight(Theme.Caption, 16)));

        // Populated on demand by RefreshTemperatures, which keeps one chip per distinct sensor
        // name so a steady reading costs no allocations at all.
        _temperatureChips = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoScroll = true,
            BackColor = Theme.Surface,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        // The chips just became a brand-new empty panel, so whatever the previous panel was
        // showing is gone. ApplyTemperatures only rebuilds chips when the sensor signature has
        // changed, so without this reset a regenerated panel would compare equal to the old
        // signature, skip the rebuild, and stay empty for the rest of the session — which is
        // exactly what a theme or scale change used to do to the temperature row.
        _temperatureSignature = string.Empty;
        _temperatureChipValues.Clear();

        // Stated in the window rather than left for the user to infer from a missing row: Windows
        // exposes no user-mode CPU package temperature on this machine, so the absence of a CPU
        // chip means "no interface", not "cool enough to omit".
        var note = new Label
        {
            Dock = DockStyle.Fill,
            Text = "CPU 封装温度需主板传感器 · 无读数的硬盘不显示",
            Font = Theme.Caption,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
        };

        column.Controls.Add(_temperatureChips, 0, 0);
        column.Controls.Add(note, 0, 1);
        return column;
    }

    /// <summary>
    /// Reads as <c>value</c> … <c>bar</c>; the bar only ever fills what the value names.
    /// <para>
    /// <paramref name="valueWidth"/> is per row rather than one constant for the card because the
    /// two kinds of figure differ in length: "21.9%" is five characters and "1.78 GB / 8 GB" is
    /// thirteen, so a single width either truncates the memory reading or leaves the load figure
    /// stranded in a mostly empty column.
    /// </para>
    /// </summary>
    private static Control BuildMetricRow(Label value, Control bar, int valueWidth = 86)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Surface,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(valueWidth)));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        value.Dock = DockStyle.Fill;
        row.Controls.Add(value, 0, 0);
        row.Controls.Add(bar, 1, 0);
        return row;
    }

    private static Label MakeHardwareCaption(string text) => new()
    {
        Text = text,
        Font = Theme.Caption,
        ForeColor = Theme.TextMuted,
        BackColor = Color.Transparent,
        AutoEllipsis = true,
        Margin = Padding.Empty,
    };

    private static Label MakeHardwareValue(string text) => new()
    {
        Text = text,
        Font = Theme.BodyBold,
        ForeColor = Theme.TextPrimary,
        BackColor = Color.Transparent,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
        Margin = Padding.Empty,
    };

    /// <summary>The hero load figure: one large number, a bar, and the reading it came from.</summary>
    private Control BuildHero()
    {
        // Four stacked bands instead of one canvas with hand-placed labels.
        //
        // The "%" used to be placed at _loadPercent.Bottom - _loadUnit.Height - 5 inside a panel
        // that also held the bar at a fixed y. A label's box is taller than the digits drawn in
        // it, and how much taller depends on the font, so that offset moved with the type scale
        // while the bar did not — leaving the unit hanging over the bar with a few pixels of
        // clearance that vanished as the font grew. Bands make the separation structural: the
        // figure cannot leave its row, so the bar cannot be reached no matter what the font is.
        var hero = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Theme.Surface,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        hero.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hero.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(68)));
        hero.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(18)));
        hero.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(28)));
        hero.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var figure = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            Margin = Padding.Empty,
        };

        _loadPercent = new Label
        {
            Text = "—",
            Font = Theme.Display,
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(-4, 0),
        };

        _loadUnit = new Label
        {
            Text = "%",
            Font = Theme.DisplayUnit,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize = true,
        };

        figure.Controls.AddRange([_loadPercent, _loadUnit]);

        _loadBar = new LoadBar
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, Theme.S(8)),
        };

        _loadCaption = new Label
        {
            Text = "—",
            Font = Theme.StatValue,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        };

        _loadState = new Label
        {
            Text = "—",
            Font = Theme.Caption,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        };

        hero.Controls.Add(figure, 0, 0);
        hero.Controls.Add(_loadBar, 0, 1);
        hero.Controls.Add(_loadCaption, 0, 2);
        hero.Controls.Add(_loadState, 0, 3);
        return hero;
    }

    /// <summary>
    /// Two columns of label/value pairs, laid out as a grid rather than as absolutely placed
    /// labels, so the values line up whatever the digits are.
    /// </summary>
    private Control BuildStatGrid()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 5,
            BackColor = Theme.Surface,
            Margin = new Padding(12, 0, 0, 0),
        };

        // The caption columns size to their content rather than to a guessed pixel width. A
        // fixed width that fits 可用内存 clips 进程与线程, and the clip is invisible until the
        // window is rendered — CJK glyphs are wider than their Latin counterparts, so a width
        // that looks generous in the source is tight on screen.
        //
        // The first value column is absolute because it is also what keeps the second caption
        // column aligned: an AutoSize value column would move with the length of the text, so
        // 内核池's wide "626.52 MB 分页 · 961.35 MB 非分页" would drag 空闲（含零页） to the right
        // on its row and leave the two halves of the grid out of step. It is wide enough for the
        // longest value it has to hold, which is the kernel-pool line.
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(310)));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        for (var i = 0; i < 5; i++)
        {
            // Room for a 10 pt row plus a little slack; the last row carries the long
            // "288 个进程 · 5698 个线程 · 150995 个句柄" line and is given more.
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(i == 4 ? 32 : 30)));
        }

        _availableValue = AddStatPair(grid, 0, 0, "可用内存");
        _usedValue = AddStatPair(grid, 1, 0, "已用内存");
        _commitValue = AddStatPair(grid, 2, 0, "提交内存");
        _kernelValue = AddStatPair(grid, 3, 0, "内核池");

        _freeValue = AddStatPair(grid, 0, 2, "空闲（含零页）");
        _standbyValue = AddStatPair(grid, 1, 2, "备用列表");
        _modifiedValue = AddStatPair(grid, 2, 2, "已修改页");
        _cacheValue = AddStatPair(grid, 3, 2, "缓存工作集");

        // Cached because RebuildUi replaces these labels; the group is rebuilt and re-assigned
        // here, so the refresh path never has to allocate an array to walk them.
        _unavailableLabels = [_freeValue, _standbyValue, _modifiedValue, _cacheValue];

        // The process/thread/handle line is one long string, so it spans the value columns
        // rather than being squeezed into one.
        var countsCaption = MakeStatCaption("进程与线程");
        grid.Controls.Add(countsCaption, 0, 4);

        _countsValue = MakeStatValue("");
        grid.Controls.Add(_countsValue, 1, 4);
        grid.SetColumnSpan(_countsValue, 3);

        return grid;
    }

    private Label AddStatPair(TableLayoutPanel grid, int row, int captionColumn, string caption)
    {
        grid.Controls.Add(MakeStatCaption(caption), captionColumn, row);

        var value = MakeStatValue("");
        grid.Controls.Add(value, captionColumn + 1, row);
        return value;
    }

    private static Label MakeStatCaption(string text) => new()
    {
        Text = text,
        Font = Theme.Body,
        ForeColor = Theme.TextMuted,
        BackColor = Color.Transparent,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 0, 8, 0),
    };

    private static Label MakeStatValue(string text) => new()
    {
        Text = text,
        Font = Theme.StatValue,
        ForeColor = Theme.TextPrimary,
        BackColor = Color.Transparent,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0),
    };

    /// <summary>
    /// 清理区域 beside 自动清理, in the row they used to occupy together as two separate rows.
    /// <para>
    /// Neither card reads its own width: the areas grid sizes its columns by percentage and its
    /// preset buttons to a fixed size, and the automation grid pins the label columns and lets
    /// the last one carry the remainder. Both therefore keep their internal alignment at half
    /// width and only lose horizontal room, which is the one dimension they have spare.
    /// </para>
    /// </summary>
    private Control BuildMiddleRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Canvas,
            Margin = new Padding(0, 0, 0, Theme.Gap),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // The gap between the two cards is carried as their outer margins rather than as padding
        // on the row, so the row itself still lines up with the cards above and below it.
        var areas = BuildAreasCard();
        areas.Margin = new Padding(0, 0, Theme.Gap / 2, 0);

        var automation = BuildAutomationCard();
        automation.Margin = new Padding(Theme.Gap / 2, 0, 0, 0);

        row.Controls.Add(areas, 0, 0);
        row.Controls.Add(automation, 1, 0);
        return row;
    }

    private Control BuildAreasCard()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Title = "清理区域",
            Margin = new Padding(0, 0, 0, Theme.Gap),
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 4,
            BackColor = Theme.Surface,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));

        // Scaled, not literal. The rows used to be fixed at 26 while the preset buttons inside
        // them were Theme.S(26), so at a UI scale above 100 % each button was taller than its own
        // row and the third one was clipped by the bottom of the card.
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(26)));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(26)));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(26)));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var areas = CleanerRegistry.All.Select(cleaner => cleaner.Area).Distinct().ToArray();

        for (var i = 0; i < areas.Length; i++)
        {
            var area = areas[i];
            var cleaner = CleanerRegistry.For(area);

            var toggle = new Switch
            {
                Text = cleaner.RequiresElevation ? cleaner.DisplayName + "（管理员）" : cleaner.DisplayName,
                AutoSize = false,
                Height = 22,
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
            };

            _tips.SetToolTip(toggle, cleaner.Description);
            toggle.CheckedChanged += (_, _) => OnAreaSelectionChanged();
            _areaToggles.Add((area, toggle));
            grid.Controls.Add(toggle, i % 2, i / 2);
        }

        grid.Controls.Add(BuildPresets(), 2, 0);
        grid.SetRowSpan(grid.GetControlFromPosition(2, 0)!, 3);

        var note = new Label
        {
            Text = "“组合页列表”与“备用列表”“已修改页”范围重叠，因此默认不勾选。",
            Font = Theme.Caption,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0),
        };
        grid.Controls.Add(note, 0, 3);
        grid.SetColumnSpan(note, 3);

        card.Body.Controls.Add(grid);
        return card;
    }

    private Control BuildPresets()
    {
        // Stacked rather than side by side: three 78 px buttons are wider than the quarter of a
        // row this column now holds, and a FlowLayoutPanel with wrap disabled would have pushed
        // the third one out of the card instead of wrapping it.
        var presets = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.TopDown,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };

        presets.Controls.Add(MakePreset("默认组合", () => CleanAreas.Default));
        presets.Controls.Add(MakePreset("全部区域", () => CleanAreas.All));
        presets.Controls.Add(MakePreset("清空选择", () => CleanArea.None));

        return presets;
    }

    private FlatButton MakePreset(string text, Func<CleanArea> areas)
    {
        var button = new FlatButton
        {
            Text = text,
            Variant = ButtonVariant.Ghost,
            Size = new Size(Theme.S(78), Theme.S(26)),
            Margin = new Padding(6, 0, 0, 0),
            Font = Theme.Caption,
        };

        button.Click += (_, _) => SetSelectedAreas(areas());
        return button;
    }

    private Control BuildActionsRow()
    {
        // A FlowLayoutPanel, not absolute Locations: the buttons are sized through Theme.S, so at a
        // larger UI scale a hardcoded x for the second button would sit inside the first one.
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Canvas,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        _cleanButton = new FlatButton
        {
            Text = "立即清理",
            Variant = ButtonVariant.Primary,
            Size = new Size(Theme.S(122), Theme.S(38)),
            Margin = new Padding(0, Theme.S(2), Theme.S(10), 0),
        };
        _cleanButton.Click += async (_, _) => await RunCleanAsync(dryRun: false);

        _dryRunButton = new FlatButton
        {
            Text = "试运行",
            Variant = ButtonVariant.Secondary,
            Size = new Size(Theme.S(92), Theme.S(38)),
            Margin = new Padding(0, Theme.S(2), Theme.S(12), 0),
        };
        _dryRunButton.Click += async (_, _) => await RunCleanAsync(dryRun: true);
        _tips.SetToolTip(
            _dryRunButton,
            "只报告每个区域会做什么、以及内存自身的自然波动，不发出任何内核调用。");

        _summary = new Label
        {
            Text = "就绪。",
            Font = Theme.Body,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, Theme.S(12), 0, 0),
        };

        panel.Controls.AddRange([_cleanButton, _dryRunButton, _summary]);
        return panel;
    }

    private Control BuildAutomationCard()
    {
        _automationCard = new CardPanel
        {
            Dock = DockStyle.Fill,
            Title = "自动清理",
            // No margin here: the row sets the outer spacing, because the two cards it holds have
            // to be spaced against each other as well as against the rows above and below.
            Margin = Padding.Empty,
        };

        // One grid for both rows, not one flow panel per row. The two rows have to line up on the
        // spinner and on the unit label, and a per-row flow layout cannot do that: in a flow the
        // position of each control comes from the widths of everything before it, so two spinners
        // of different widths push their labels to different places. Sharing the columns makes the
        // alignment a property of the layout instead of a coincidence of the contents.
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 2,
            BackColor = Theme.Surface,
        };

        var rowHeight = Theme.S(30);
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(170)));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(90)));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));

        // A third, empty row that soaks up whatever height is left over. Two Absolute rows alone
        // do not add up to the card's body, and a TableLayoutPanel with no Percent row hands the
        // surplus to the LAST row instead of leaving it empty: the second row came out 48px tall
        // against the first row's 30, so 负载达到 sat 9px below 定时清理 and "% 时清理" was centred
        // in the wrong cell. The spacer row is what keeps the two real rows the same height.
        grid.RowCount = 3;
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _autoEnabled = new Switch { Text = "定时清理", Dock = DockStyle.Fill, Margin = new Padding(0) };
        _autoMinutes = MakeSpinner(1, 1440, 30);
        var minutesLabel = MakeInlineLabel("分钟");

        _autoThreshold = new Switch { Text = "负载达到", Dock = DockStyle.Fill, Margin = new Padding(0) };
        _thresholdPercent = MakeSpinner(10, 99, 85);
        var percentLabel = MakeInlineLabel("% 时清理");

        _nextRunLabel = new Label
        {
            Text = string.Empty,
            Font = Theme.Body,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(12, 0, 0, 0),
        };

        grid.Controls.Add(_autoEnabled, 0, 0);
        grid.Controls.Add(_autoMinutes, 1, 0);
        grid.Controls.Add(minutesLabel, 2, 0);
        grid.Controls.Add(_autoThreshold, 0, 1);
        grid.Controls.Add(_thresholdPercent, 1, 1);
        grid.Controls.Add(percentLabel, 2, 1);
        grid.Controls.Add(_nextRunLabel, 3, 0);
        grid.SetRowSpan(_nextRunLabel, 2);

        _autoEnabled.CheckedChanged += (_, _) => OnAutomationChanged();
        _autoThreshold.CheckedChanged += (_, _) => OnAutomationChanged();
        _autoMinutes.ValueChanged += (_, _) => OnAutomationChanged();
        _thresholdPercent.ValueChanged += (_, _) => OnAutomationChanged();

        _automationCard.Body.Controls.Add(grid);
        return _automationCard;
    }

    private static Label MakeInlineLabel(string text) => new()
    {
        Text = text,
        Font = Theme.Body,
        ForeColor = Theme.TextMuted,
        BackColor = Color.Transparent,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(8, 0, 0, 0),
    };

    /// <summary>
    /// A spinner sized by its grid cell rather than by a pixel width, so the two rows stay the
    /// same width as the type scale and the label text change.
    /// </summary>
    private static NumericUpDown MakeSpinner(int minimum, int maximum, int value) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Value = value,
        Dock = DockStyle.Fill,
        Font = Theme.Body,
        BackColor = Theme.SurfaceHi,
        ForeColor = Theme.TextPrimary,
        BorderStyle = BorderStyle.FixedSingle,
        TextAlign = HorizontalAlignment.Center,
        Margin = new Padding(0, 2, 0, 2),
    };

    private Control BuildResultsCard()
    {
        _resultsCard = new CardPanel
        {
            Dock = DockStyle.Fill,
            Title = "清理结果",
            Hint = "尚无记录",
            Margin = new Padding(0),
        };

        _results = new SlimListView { Dock = DockStyle.Fill };
        _results.Columns.Add("清理区域", 140);
        _results.Columns.Add("结果", 66);
        _results.Columns.Add("释放", 92);
        _results.Columns.Add("用时", 78);
        _results.Columns.Add("详情", 380);

        // The stock header paints only the band its columns cover; anything to the right of the
        // last column keeps the system light background and reads as a bright bar across a dark
        // window. SlimListView closes that gap itself and re-fits whenever its client area
        // changes, so there is nothing to wire up here.
        _resultsCard.Body.Controls.Add(_results);
        return _resultsCard;
    }

    // ------------------------------------------------------------------ menu

    private void BuildMenu()
    {
        var menu = new MenuStrip
        {
            BackColor = Theme.Canvas,
            ForeColor = Theme.TextSecondary,
            Font = Theme.Body,
            RenderMode = ToolStripRenderMode.Professional,
            GripStyle = ToolStripGripStyle.Hidden,
            Padding = new Padding(8, 2, 0, 2),
        };

        menu.Items.Add(BuildFileMenu());
        menu.Items.Add(BuildSettingsMenu());
        menu.Items.Add(BuildHelpMenu());

        ApplyMenuTheme(menu);

        Controls.Add(menu);
        MainMenuStrip = menu;
    }

    /// <summary>
    /// Pushes the dark renderer and palette through an entire strip tree, drop-downs included.
    /// <para>
    /// Neither <c>MenuStrip.Renderer</c> nor <c>ToolStripManager.Renderer</c> is enough on its
    /// own. The first styles only the bar; the second is read when a strip is constructed, and a
    /// drop-down is constructed lazily the first time it is shown — so a menu bar that looks
    /// right still drops a white panel, because the drop-down fell back to the system theme.
    /// Assigning the renderer to each <see cref="ToolStripDropDown"/> explicitly is the only
    /// version that does not depend on that timing.
    /// </para>
    /// </summary>
    private static void ApplyMenuTheme(ToolStrip strip)
    {
        strip.Renderer = DarkMenuRenderer.Shared;
        strip.BackColor = strip is MenuStrip ? Theme.Canvas : Theme.Surface;
        strip.ForeColor = Theme.TextSecondary;

        foreach (ToolStripItem item in strip.Items)
        {
            if (item is ToolStripDropDownItem drop)
            {
                ApplyMenuTheme(drop.DropDown);
            }
        }
    }

    private ToolStripMenuItem BuildFileMenu()
    {
        var file = new ToolStripMenuItem("文件(&F)");

        file.DropDownItems.Add(MakeMenuItem("立即清理(&C)", (_, _) => _ = RunCleanAsync(dryRun: false), Keys.Control | Keys.R));
        file.DropDownItems.Add(MakeMenuItem("试运行(&T)", (_, _) => _ = RunCleanAsync(dryRun: true), Keys.Control | Keys.T));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(MakeMenuItem("隐藏到通知区域(&H)", (_, _) => HideToTray()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(MakeMenuItem("退出(&X)", (_, _) =>
        {
            _reallyExit = true;
            Close();
        }));

        return file;
    }

    private ToolStripMenuItem BuildSettingsMenu()
    {
        var settings = new ToolStripMenuItem("设置(&S)");

        // The trigger switches and the area toggles used to live here as a second copy of the
        // controls that are already on the cards. They now stay in one place only: the card, so
        // the menu cannot drift out of step with what the window shows.
        _menuMinimizeToTray = MakeMenuItem("关闭窗口时最小化到托盘", (_, _) =>
        {
            _settings.MinimizeToTray = _menuMinimizeToTray.Checked;
            _settings.Save();
        });
        _menuMinimizeToTray.CheckOnClick = true;

        _menuNotifications = MakeMenuItem("清理后显示通知", (_, _) =>
        {
            _settings.ShowNotifications = _menuNotifications.Checked;
            _settings.Save();
        });
        _menuNotifications.CheckOnClick = true;

        settings.DropDownItems.Add(_menuMinimizeToTray);
        settings.DropDownItems.Add(_menuNotifications);
        settings.DropDownItems.Add(new ToolStripSeparator());

        var interval = new ToolStripMenuItem("刷新间隔(&R)");
        foreach (var ms in new[] { 500, 1000, 2000, 5000 })
        {
            var label = ms < 1000 ? $"{ms / 1000.0:0.#} 秒" : $"{ms / 1000} 秒";
            var item = MakeMenuItem(label, (_, _) => SetRefreshInterval(ms));
            _menuIntervals[ms] = item;
            interval.DropDownItems.Add(item);
        }

        settings.DropDownItems.Add(interval);
        settings.DropDownItems.Add(new ToolStripSeparator());

        // Appearance. Both submenus carry a check mark for the current value and both rebuild the
        // window, because neither colour nor font can be swapped on a control that already exists.
        var theme = new ToolStripMenuItem("主题(&T)");
        foreach (var mode in new[] { AppTheme.Dark, AppTheme.Light })
        {
            var label = mode == AppTheme.Dark ? "深色" : "浅色";
            var item = MakeMenuItem(label, (_, _) => SetTheme(mode));
            _menuThemes[mode] = item;
            theme.DropDownItems.Add(item);
        }

        var scale = new ToolStripMenuItem("界面缩放(&Z)");
        foreach (var (label, value) in Theme.ScaleSteps)
        {
            var item = MakeMenuItem($"{label}（{value * 100:0}%）", (_, _) => SetUiScale(value));
            _menuScales[value] = item;
            scale.DropDownItems.Add(item);
        }

        settings.DropDownItems.Add(theme);
        settings.DropDownItems.Add(scale);

        // Registers a sign-in scheduled task, falling back to HKEY_CURRENT_USER\...\Run, so the
        // check mark is set from what is actually registered rather than from the checkbox — see
        // SyncMenu.
        _menuStartWithWindows = MakeMenuItem("开机时自动启动", (_, _) =>
        {
            if (!StartupRegistration.Set(_menuStartWithWindows.Checked))
            {
                MessageBox.Show(
                    this,
                    "无法写入开机启动项。可能是计划任务与注册表都被安全软件保护，或当前账户没有权限。",
                    "MemReduct",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            _settings.StartWithWindows = StartupRegistration.IsEnabled();
            _settings.Save();
        });
        _menuStartWithWindows.CheckOnClick = true;

        settings.DropDownItems.Add(new ToolStripSeparator());
        settings.DropDownItems.Add(_menuStartWithWindows);
        settings.DropDownItems.Add(new ToolStripSeparator());
        settings.DropDownItems.Add(MakeMenuItem("打开配置文件位置(&O)", (_, _) => OpenConfigFolder()));
        settings.DropDownItems.Add(MakeMenuItem("恢复默认设置(&D)", (_, _) => ResetSettings()));

        return settings;
    }

    private ToolStripMenuItem BuildHelpMenu()
    {
        var help = new ToolStripMenuItem("帮助(&H)");

        var elevate = MakeMenuItem("以管理员身份重启(&E)", (_, _) => RestartElevated());
        elevate.Enabled = !PrivilegeEnabler.IsElevated;
        help.DropDownItems.Add(elevate);
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(MakeMenuItem("关于 MemReduct(&A)", (_, _) => ShowAbout()));

        return help;
    }

    private static ToolStripMenuItem MakeMenuItem(
        string text,
        EventHandler onClick,
        Keys shortcut = Keys.None)
    {
        var item = new ToolStripMenuItem(text)
        {
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
        };

        if (shortcut != Keys.None)
        {
            item.ShortcutKeys = shortcut;
            item.ShowShortcutKeys = true;
        }

        item.Click += onClick;
        return item;
    }

    /// <summary>Pushes the current settings into every menu item that mirrors one.</summary>
    private void SyncMenu()
    {
        _menuMinimizeToTray.Checked = _settings.MinimizeToTray;
        _menuNotifications.Checked = _settings.ShowNotifications;

        foreach (var (mode, item) in _menuThemes)
        {
            item.Checked = _settings.Theme == mode;
        }

        foreach (var (scale, item) in _menuScales)
        {
            item.Checked = Math.Abs(_settings.UiScale - scale) < 0.001;
        }

        // Read the registration back rather than trusting the saved flag: it can be removed or
        // switched off by another tool, or left pointing at a copy of the exe that has since
        // been moved.
        _settings.StartWithWindows = StartupRegistration.IsEnabled();
        _menuStartWithWindows.Checked = _settings.StartWithWindows;

        foreach (var (ms, item) in _menuIntervals)
        {
            item.Checked = _settings.RefreshIntervalMs == ms;
        }
    }

    /// <summary>Switches the palette, then rebuilds the window so every control picks it up.</summary>
    private void SetTheme(AppTheme mode)
    {
        if (mode == _settings.Theme)
        {
            return;
        }

        _settings.Theme = mode;
        _settings.Save();
        Theme.Apply(_settings.Theme, _settings.UiScale);
        RebuildUi();
    }

    /// <summary>
    /// Switches the type scale, resizes the window to match, then rebuilds.
    /// <para>
    /// The resize is not optional: the fixed row heights are derived from the scale, so a scale
    /// change without a matching window change would leave the results card either squeezed to
    /// nothing or floating in a band of empty canvas.
    /// </para>
    /// </summary>
    private void SetUiScale(double scale)
    {
        if (Math.Abs(scale - _settings.UiScale) < 0.001)
        {
            return;
        }

        _settings.UiScale = scale;
        _settings.Save();
        Theme.Apply(_settings.Theme, _settings.UiScale);

        var working = Screen.FromControl(this).WorkingArea;
        ClientSize = new Size(
            Math.Min(Theme.S(1240), working.Width - 40),
            Math.Min(Theme.S(1000), working.Height - 40));
        MinimumSize = new Size(Theme.S(900), Theme.S(700));

        RebuildUi();
    }

    private void SetRefreshInterval(int milliseconds)
    {
        _settings.RefreshIntervalMs = Math.Clamp(milliseconds, 250, 60_000);
        _settings.Save();
        _refreshTimer.Stop();
        _refreshTimer.Interval = _settings.RefreshIntervalMs;
        _refreshTimer.Start();
        SyncMenu();
    }

    private void ResetSettings()
    {
        var confirmation = MessageBox.Show(
            this,
            "把所有设置恢复为默认值？\n\n清理区域、自动清理、通知和刷新间隔都会被重置。",
            "恢复默认设置",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question);

        if (confirmation != DialogResult.OK)
        {
            return;
        }

        var defaults = new AppSettings();
        _settings.Areas = defaults.Areas;
        _settings.AutoCleanEnabled = defaults.AutoCleanEnabled;
        _settings.AutoCleanIntervalMinutes = defaults.AutoCleanIntervalMinutes;
        _settings.AutoCleanOnThreshold = defaults.AutoCleanOnThreshold;
        _settings.AutoCleanThresholdPercent = defaults.AutoCleanThresholdPercent;
        _settings.ShowNotifications = defaults.ShowNotifications;
        _settings.MinimizeToTray = defaults.MinimizeToTray;
        _settings.RefreshIntervalMs = defaults.RefreshIntervalMs;
        _settings.Save();

        ApplySettingsToUi();
        SetRefreshInterval(_settings.RefreshIntervalMs);
        _summary.Text = "设置已恢复为默认值。";
        _summary.ForeColor = Theme.TextSecondary;
    }

    private void OpenConfigFolder()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.ConfigDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppSettings.ConfigDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "无法打开配置文件夹：\n\n" + ex.Message,
                "MemReduct",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            this,
            "MemReduct 1.0.0\n" +
            "内存清理工具\n\n" +
            RuntimeInfo.Describe() + "\n" +
            $"配置文件：{AppSettings.ConfigFilePath}\n\n" +
            "托盘图标显示当前内存占用：单击中键立即清理，双击打开主窗口。",
            "关于 MemReduct",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void HideToTray()
    {
        Hide();

        if (!_trayHintShown && _settings.ShowNotifications)
        {
            _trayHintShown = true;
            _tray.ShowBalloonTip(
                3000,
                "MemReduct 仍在运行",
                "自动清理会在通知区域继续进行。要退出请使用托盘菜单中的“退出”。",
                ToolTipIcon.Info);
        }
    }

    /// <summary>
    /// Says where the tool went, once per run: the window does not open by itself any more, so
    /// the first thing the notification area shows has to say that it is there.
    /// </summary>
    private void ShowTrayStartupHint()
    {
        if (_trayHintShown || !_settings.ShowNotifications)
        {
            return;
        }

        _trayHintShown = true;
        _tray.ShowBalloonTip(
            3000,
            "MemReduct 已启动",
            "程序在通知区域运行，双击托盘图标打开主窗口。",
            ToolTipIcon.Info);
    }

    private void BuildTray()
    {
        _tray.Icon = _appIcon ?? SystemIcons.Application;
        _tray.Text = "MemReduct";
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowFromTray();

        // Middle click is the one-button clean. MouseClick rather than MouseDown so the
        // action fires on release, matching every other button in the interface.
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Middle)
            {
                _ = RunCleanAsync(dryRun: false);
            }
        };

        var menu = new ContextMenuStrip
        {
            BackColor = Theme.Surface,
            ForeColor = Theme.TextSecondary,
            Font = Theme.Body,
        };

        menu.Items.Add(MakeMenuItem("打开主窗口", (_, _) => ShowFromTray()));
        menu.Items.Add(MakeMenuItem("立即清理内存", (_, _) => _ = RunCleanAsync(dryRun: false)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(MakeMenuItem("设置", (_, _) => ShowSettingsFromTray()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(MakeMenuItem("退出", (_, _) =>
        {
            _reallyExit = true;
            Close();
        }));

        ApplyMenuTheme(menu);

        _tray.ContextMenuStrip = menu;
    }

    /// <summary>Shows the window and opens the settings menu, from the tray's own menu.</summary>
    private void ShowSettingsFromTray()
    {
        ShowFromTray();

        if (MainMenuStrip?.Items[1] is ToolStripMenuItem settings)
        {
            settings.ShowDropDown();
        }
    }

    // ------------------------------------------------------------- behaviour

    private void ApplySettingsToUi()
    {
        SetSelectedAreas(_settings.Areas);
        _autoEnabled.Checked = _settings.AutoCleanEnabled;
        _autoMinutes.Value = Math.Clamp(_settings.AutoCleanIntervalMinutes, 1, 1440);
        _autoThreshold.Checked = _settings.AutoCleanOnThreshold;
        _thresholdPercent.Value = Math.Clamp(_settings.AutoCleanThresholdPercent, 10, 99);
        OnAutomationChanged();
    }

    private CleanArea SelectedAreas()
    {
        var areas = CleanArea.None;
        foreach (var (area, toggle) in _areaToggles)
        {
            if (toggle.Checked)
            {
                areas |= area;
            }
        }

        return areas;
    }

    private void SetSelectedAreas(CleanArea areas)
    {
        foreach (var (area, toggle) in _areaToggles)
        {
            toggle.Checked = (areas & area) != 0;
        }

        OnAreaSelectionChanged();
    }

    /// <summary>
    /// Keeps the menu and the persistence in step with the switches. Called from the switches
    /// themselves, so it must not write back into them.
    /// </summary>
    private void OnAreaSelectionChanged()
    {
        _settings.Areas = SelectedAreas();
        _settings.Save();
        SyncMenu();
    }

    private void OnAutomationChanged()
    {
        var running = _autoEnabled.Checked;
        _autoMinutes.Enabled = running;
        _thresholdPercent.Enabled = _autoThreshold.Checked;

        _automationCard.TitleSuffix = running ? string.Empty : "未启用";
        _automationCard.TitleSuffixColor = Theme.TextMuted;

        _nextAutoRun = running
            ? DateTimeOffset.Now.AddMinutes((double)_autoMinutes.Value)
            : null;

        _settings.AutoCleanEnabled = running;
        _settings.AutoCleanIntervalMinutes = (int)_autoMinutes.Value;
        _settings.AutoCleanOnThreshold = _autoThreshold.Checked;
        _settings.AutoCleanThresholdPercent = (int)_thresholdPercent.Value;
        _settings.Areas = SelectedAreas();
        _settings.Save();

        SyncMenu();
        UpdateNextRunLabel();
    }

    private void UpdateNextRunLabel()
    {
        if (_nextAutoRun is not { } next || !_autoEnabled.Checked)
        {
            _nextRunLabel.Text = _autoEnabled.Checked
                ? string.Empty
                : "未启用自动清理";
            return;
        }

        var remaining = next - DateTimeOffset.Now;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        _nextRunLabel.Text = remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours} 小时 {remaining.Minutes:00} 分后自动清理"
            : $"{remaining.Minutes:00}:{remaining.Seconds:00} 后自动清理";
    }

    private void OnCountdownTick()
    {
        if (_busy)
        {
            return;
        }

        UpdateNextRunLabel();

        // Threshold trigger: act as soon as the machine gets busy, without waiting for the timer.
        if (_autoThreshold.Checked && _lastSnapshot is { } snapshot &&
            snapshot.LoadPercent >= (double)_thresholdPercent.Value)
        {
            _nextAutoRun = _autoEnabled.Checked
                ? DateTimeOffset.Now.AddMinutes((double)_autoMinutes.Value)
                : null;
            _ = RunCleanAsync(dryRun: false);
            return;
        }

        if (_autoEnabled.Checked && _nextAutoRun is { } next && DateTimeOffset.Now >= next)
        {
            _nextAutoRun = DateTimeOffset.Now.AddMinutes((double)_autoMinutes.Value);
            _ = RunCleanAsync(dryRun: false);
        }
    }

    /// <summary>
    /// Reads the page-list breakdown through one long-lived PDH query.
    /// <para>
    /// <see cref="MemoryInfoProvider.GetSnapshot(bool)"/> defaults to
    /// <c>includePageLists: true</c>, which routes through
    /// <see cref="PageListReader.ReadOnce"/> — a full open/collect/close cycle per call. On a
    /// 1-second refresh that opened and closed six counter handles sixty times a minute for a
    /// figure that barely moves. The query is kept for the life of the window; PDH rate counters
    /// are the only ones that need a retained query for correctness, and this is a set of
    /// <c>… Bytes</c> gauges, so one held query reads all of them exactly as before.
    /// </para>
    /// A failed open is remembered by the null reader, so a machine without the counter set
    /// re-attempts nothing and simply falls back to the aggregate counters.
    /// </summary>
    private PageListInfo? TakePageLists()
    {
        if (!_pageListsInitialized)
        {
            _pageListsInitialized = true;

            try
            {
                _pageLists = PageListReader.TryCreate();
            }
            catch (Exception)
            {
                _pageLists = null;
            }
        }

        return _pageLists?.TryRead();
    }

    private void RefreshStats()
    {
        try
        {
            var snapshot = MemoryInfoProvider.GetSnapshot(includePageLists: false) with
            {
                PageLists = TakePageLists(),
            };
            _lastSnapshot = snapshot;

            var band = Theme.ForLoad(snapshot.LoadPercent);

            _loadPercent.Text = snapshot.LoadPercent.ToString("F1");
            _loadUnit.Location = new Point(
                _loadPercent.Right - 3,
                _loadPercent.Bottom - _loadUnit.Height - 5);

            _loadBar.Percent = snapshot.LoadPercent;
            _loadCaption.Text =
                $"{ByteSize.Format((long)snapshot.UsedPhysical)} / " +
                $"{ByteSize.Format((long)snapshot.TotalPhysical)} 已用";

            _loadState.Text = $"● {DescribeLoad(snapshot.LoadPercent)}";
            _loadState.ForeColor = band;

            _availableValue.Text = ByteSize.Format((long)snapshot.AvailablePhysical);
            _availableValue.ForeColor = snapshot.LoadPercent >= 90 ? Theme.Danger : Theme.TextPrimary;

            _usedValue.Text = ByteSize.Format((long)snapshot.UsedPhysical);
            _commitValue.Text =
                $"{ByteSize.Format((long)snapshot.CommitTotal)} / {ByteSize.Format((long)snapshot.CommitLimit)}" +
                $"  ({snapshot.CommitLoadPercent:F0}%)";
            _kernelValue.Text =
                $"{ByteSize.Format((long)snapshot.KernelPaged)} 分页 · " +
                $"{ByteSize.Format((long)snapshot.KernelNonpaged)} 非分页";
            _countsValue.Text =
                $"{snapshot.ProcessCount} 个进程 · {snapshot.ThreadCount} 个线程 · {snapshot.HandleCount} 个句柄";

            if (snapshot.PageLists is { } lists)
            {
                _freeValue.Text = ByteSize.Format(lists.FreeBytes);
                _standbyValue.Text =
                    $"{ByteSize.Format(lists.StandbyBytes)}（{ByteSize.Format(lists.PurgeableStandbyBytes)} 可清除）";
                _modifiedValue.Text = ByteSize.Format(lists.ModifiedBytes);
                _cacheValue.Text = ByteSize.Format(lists.CacheBytes);
            }
            else
            {
                foreach (var label in _unavailableLabels)
                {
                    label.Text = "不可用";
                    label.ForeColor = Theme.Warning;
                }
            }

            UpdateTrayIcon(snapshot.LoadPercent);

            // The hover text is the only hint the tray icon carries. A ToolTip component cannot
            // attach to a NotifyIcon (it is not a Control), so the middle-click affordance is
            // advertised here, where RefreshStats is the only writer.
            _tray.Text =
                $"MemReduct —— 内存占用 {snapshot.LoadPercent:F0}%（中键单击立即清理）";
            _statusLabel.Text =
                $"采样 {snapshot.Timestamp:HH:mm:ss}  ·  " +
                $"可用 {ByteSize.Format((long)snapshot.AvailablePhysical)}  ·  " +
                $"负载 {snapshot.LoadPercent:F1}%";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "无法获取内存信息：" + ex.Message;
        }

        // Called outside the try above so that a hardware failure cannot blank the memory
        // figures. It carries its own guard for the same reason: the two paths report through
        // different native APIs, and only one of them is the reason the window exists.
        RefreshHardware();
    }

    /// <summary>
    /// Samples CPU, GPU and temperature once and pushes the readings into the hardware card.
    /// <para>
    /// One read per tick and never two: <see cref="HardwareInfoProvider.Read"/> works out load
    /// from the difference against the previous reading, so a second call in the same tick would
    /// diff against itself and paint a solid 0 % over a machine that is working flat out.
    /// </para>
    /// </summary>
    private void RefreshHardware()
    {
        try
        {
            var snapshot = _hardware.Read();
            ApplyCpu(snapshot);
            ApplyGpu(snapshot);
            ApplyTemperatures(snapshot.Temperatures);

            // Cleared on success so a fault that later resolves does not leave a stale error
            // sitting next to live numbers.
            _hardwareCard.Hint = string.Empty;
        }
        catch (Exception ex)
        {
            _hardwareCard.Hint = "硬件信息不可用：" + ex.Message;
        }
    }

    private void ApplyCpu(HardwareSnapshot snapshot)
    {
        var name = snapshot.CpuName;
        _cpuNameValue.Text = string.IsNullOrWhiteSpace(name) ? "未报告型号" : name;
        _cpuNameValue.ForeColor = string.IsNullOrWhiteSpace(name)
            ? Theme.TextMuted
            : Theme.TextSecondary;

        if (snapshot.CpuLoadPercent is { } load)
        {
            _cpuLoadValue.Text = load.ToString("F1") + "%";
            _cpuLoadValue.ForeColor = Theme.ForLoad(load);
            _cpuLoadBar.Percent = load;
        }
        else
        {
            _cpuLoadValue.Text = "—";
            _cpuLoadValue.ForeColor = Theme.TextMuted;
            _cpuLoadBar.Percent = 0;
        }

        // Built as a list rather than one interpolated string, so a missing part leaves a
        // readable sentence instead of a dangling separator or a fabricated zero.
        var parts = new List<string>(3);
        if (snapshot.CpuCoreCount is { } cores && snapshot.CpuThreadCount is { } threads)
        {
            parts.Add($"{cores} 核心 / {threads} 线程");
        }

        if (snapshot.CpuFrequencyMhz is { } mhz)
        {
            parts.Add($"{mhz:F0} MHz");
        }

        if (snapshot.CpuBaseFrequencyMhz is { } baseMhz)
        {
            parts.Add($"基准 {baseMhz:F0} MHz");
        }

        _cpuDetailValue.Text = parts.Count > 0 ? string.Join(" · ", parts) : "—";
        _coreStrip.SetValues(snapshot.CpuCoreLoads);
    }

    private void ApplyGpu(HardwareSnapshot snapshot)
    {
        var gpu = snapshot.Gpus.Count > 0 ? snapshot.Gpus[0] : null;
        if (gpu is null)
        {
            _gpuNameValue.Text = "未检测到显卡";
            _gpuNameValue.ForeColor = Theme.TextMuted;
            _gpuLoadValue.Text = "—";
            _gpuLoadValue.ForeColor = Theme.TextMuted;
            _gpuMemoryValue.Text = "—";
            _gpuMemoryValue.ForeColor = Theme.TextMuted;
            _gpuTempValue.Text = "温度 —";
            _gpuTempValue.ForeColor = Theme.TextMuted;
            _gpuLoadBar.Percent = 0;
            _gpuMemoryBar.Percent = 0;
            return;
        }

        _gpuNameValue.Text = gpu.Name;
        _gpuNameValue.ForeColor = Theme.TextSecondary;

        if (gpu.LoadPercent is { } load)
        {
            _gpuLoadValue.Text = load.ToString("F0") + "%";
            _gpuLoadValue.ForeColor = Theme.ForLoad(load);
            _gpuLoadBar.Percent = load;
        }
        else
        {
            _gpuLoadValue.Text = "—";
            _gpuLoadValue.ForeColor = Theme.TextMuted;
        }

        // Both the used figure and the share are derived here rather than taken from the
        // provider: a memory total reported as zero would divide by zero there, and the card
        // would rather show "—" than a percentage of nothing.
        if (gpu.MemoryUsed is { } used && gpu.MemoryTotal is { } total && total > 0)
        {
            _gpuMemoryValue.Text = FormatPair((long)used, (long)total);
            _gpuMemoryValue.ForeColor = Theme.TextPrimary;
            _gpuMemoryBar.Percent = used * 100.0 / total;
        }
        else
        {
            _gpuMemoryValue.Text = "—";
            _gpuMemoryValue.ForeColor = Theme.TextMuted;
            _gpuMemoryBar.Percent = 0;
        }

        if (gpu.TemperatureCelsius is { } temp)
        {
            _gpuTempValue.Text = $"温度 {temp:F0} °C";
            _gpuTempValue.ForeColor = ForTemperature(temp);
        }
        else
        {
            _gpuTempValue.Text = "温度 —";
            _gpuTempValue.ForeColor = Theme.TextMuted;
        }
    }

    /// <summary>
    /// Formats a used/total pair, naming the unit once. Both halves usually land on the same
    /// unit, and repeating it ("1.71 GB / 8 GB") is exactly what pushes the reading past the
    /// width the memory row has — the bar is worth more space than a second word for GB.
    /// Differing units are kept whole, because "512 MB / 8 GB" with the MB dropped is a lie.
    /// </summary>
    private static string FormatPair(long from, long to)
    {
        var fromText = ByteSize.Format(from);
        var toText = ByteSize.Format(to);
        var fromUnit = fromText[SpaceIndex(fromText)..].TrimStart();
        var toUnit = toText[SpaceIndex(toText)..].TrimStart();

        return string.Equals(fromUnit, toUnit, StringComparison.OrdinalIgnoreCase)
            ? $"{fromText[..SpaceIndex(fromText)].TrimEnd()} / {toText}"
            : $"{fromText} / {toText}";
    }

    private static int SpaceIndex(string text) => Math.Max(0, text.LastIndexOf(' '));

    /// <summary>
    /// Bands a temperature reading. There is no universal safe limit — a GPU thermal-throttles
    /// well above where a mechanical drive dies — so these are deliberately generous and only
    /// colour the end of the range, leaving normal readings in the neutral text colour.
    /// </summary>
    private static Color ForTemperature(double celsius) => celsius switch
    {
        >= 90 => Theme.Danger,
        >= 80 => Theme.Warning,
        _ => Theme.TextPrimary,
    };

    /// <summary>
    /// Rebuilds the chip row only when the sensor set itself changes. A steady temperature costs
    /// no allocation and no layout pass, which matters at the default two-second refresh.
    /// </summary>
    private void ApplyTemperatures(IReadOnlyList<TemperatureSample> temperatures)
    {
        var signature = string.Join("|", temperatures.Select(t => $"{t.Name}|{t.Source}"));
        if (!string.Equals(signature, _temperatureSignature, StringComparison.Ordinal))
        {
            _temperatureSignature = signature;
            _temperatureChips.Controls.Clear();
            _temperatureChipValues.Clear();

            foreach (var sensor in temperatures)
            {
                var (chip, value) = BuildTemperatureChip(sensor.Name);
                _temperatureChipValues[sensor.Name] = value;
                _temperatureChips.Controls.Add(chip);
            }
        }

        foreach (var sensor in temperatures)
        {
            if (!_temperatureChipValues.TryGetValue(sensor.Name, out var value))
            {
                continue;
            }

            if (sensor.Celsius is { } celsius)
            {
                value.Text = $"{celsius:F0} °C";
                value.ForeColor = ForTemperature(celsius);
            }
            else
            {
                value.Text = "—";
                value.ForeColor = Theme.TextMuted;
            }
        }
    }

    private static (Panel Chip, Label Value) BuildTemperatureChip(string name)
    {
        var value = new Label
        {
            Text = "—",
            Font = Theme.StatValue,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
        };

        var caption = new Label
        {
            Text = name,
            Font = Theme.Caption,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,

            // Transparent, not filled: the chip paints a rounded face underneath, and a square
            // fill here would paint over its corners and leave them showing the card behind.
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, Theme.S(8), Theme.S(8)),
            Padding = new Padding(Theme.S(10), Theme.S(6), Theme.S(10), Theme.S(6)),
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.RowHeight(Theme.Caption, 16)));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(caption, 0, 0);
        body.Controls.Add(value, 0, 1);

        // Sized rather than left to AutoSize: a chip that reflows on every sensor-name length
        // would move the other chips beside it, and the value has to stay on the line under its
        // own caption for the pairing to read at a glance.
        //
        // Narrow enough that three fit on one line of the column that holds them. Three sensors
        // is the common case — one GPU and two drives — and a fourth row of chips has nowhere to
        // go inside a card this tall, so the width is chosen against the column rather than
        // against the longest sensor name; the caption ellipsises instead.
        //
        // Height is summed from its parts for the same reason the rows are: the caption line and
        // the value line are both font-dependent, so a fixed 54 would clip the pair at a larger
        // type scale.
        var chipHeight = Theme.RowHeight(Theme.Caption, 16)
            + Theme.LineHeight(Theme.StatValue)
            + Theme.S(12);

        // Painted rather than a plain Panel: a fill alone is only 1.06:1 against the card it sits
        // on in the dark palette, so the chips read as loose numbers floating in the column. The
        // stroked edge gives them a boundary without needing a heavier fill, which would fight the
        // card for attention.
        var chip = new Panel
        {
            BackColor = Theme.Surface,
            Size = new Size(Theme.S(106), chipHeight),
            Margin = Padding.Empty,
        };
        chip.Paint += (_, e) => Theme.DrawSurface(
            e.Graphics,
            chip.ClientRectangle,
            Theme.SurfaceSunken,
            Theme.Border,
            Theme.ControlRadius);
        chip.Controls.Add(body);
        return (chip, value);
    }

    /// <summary>Plain-language reading of a load band, so the colour is not the only signal.</summary>
    private static string DescribeLoad(double percent) => percent >= 90 ? "负载危险"
        : percent >= 80 ? "负载紧张"
        : percent >= 60 ? "负载偏高"
        : "负载正常";

    /// <summary>
    /// Repaints the notification-area icon so it shows the current memory load as a square.
    /// Rebuilt only when the rounded percentage actually changes, because assigning
    /// NotifyIcon.Icon is a shell call and this runs on every refresh tick.
    /// </summary>
    private void UpdateTrayIcon(double loadPercent)
    {
        var percent = (int)Math.Round(Math.Clamp(loadPercent, 0, 100));

        // Read the shell's small-icon size once and cache it: it is a property read, not a
        // constant, and it changes if the window moves to a display with a different DPI.
        if (_trayIconSize == 0)
        {
            _trayIconSize = Math.Max(16, SystemInformation.SmallIconSize.Width);
        }

        if (percent == _trayIconPercent && _trayIcon is not null)
        {
            return;
        }

        var previous = _trayIcon;
        _trayIcon = TrayIconRenderer.Create(percent, _trayIconSize);
        _trayIconPercent = percent;
        _tray.Icon = _trayIcon;

        // Hand the shell the new icon before releasing the old handle, so the notification
        // area never paints a freed HICON.
        previous?.Dispose();
    }

    private async Task RunCleanAsync(bool dryRun)
    {
        if (_busy)
        {
            return;
        }

        var areas = SelectedAreas();
        if (areas == CleanArea.None)
        {
            _summary.Text = "未选择任何清理区域。";
            _summary.ForeColor = Theme.Warning;
            return;
        }

        _busy = true;
        SetBusy(true);
        _results.Items.Clear();
        _resultsCard.Hint = dryRun ? "试运行中…" : "清理中…";
        _summary.Text = dryRun ? "正在检查…" : "正在清理…";
        _summary.ForeColor = Theme.TextSecondary;

        var context = new CleanContext
        {
            DryRun = dryRun,
            // Leave this window's own working set alone: trimming it only forces the UI to
            // fault the same pages straight back in.
            IncludeSelf = false,
        };

        var progress = new SyncProgress<CleanOutcome>(outcome =>
        {
            if (!IsHandleCreated || IsDisposed)
            {
                return;
            }

            try
            {
                BeginInvoke(() => AddResultRow(outcome));
            }
            catch (ObjectDisposedException)
            {
                // The window went away mid-run; nothing left to report to.
            }
            catch (InvalidOperationException)
            {
                // Handle destroyed between the check and the call.
            }
        });

        try
        {
            var report = await Task.Run(() => _cleaner.Clean(areas, context, progress));
            ShowSummary(report, dryRun);

            if (!dryRun && _settings.ShowNotifications && report.TotalReleasedBytes > 0)
            {
                _tray.ShowBalloonTip(
                    4000,
                    "MemReduct",
                    $"已释放 {ByteSize.Format(report.TotalReleasedBytes)} 内存。",
                    ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            _summary.Text = "清理失败：" + ex.Message;
            _summary.ForeColor = Theme.Danger;
        }
        finally
        {
            _busy = false;
            SetBusy(false);
            _results.AutoSizeColumns();
            RefreshStats();
        }
    }

    private void SetBusy(bool busy)
    {
        _cleanButton.Enabled = !busy;
        _dryRunButton.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void AddResultRow(CleanOutcome outcome)
    {
        var item = new ListViewItem(CleanerRegistry.For(outcome.Area).DisplayName)
        {
            ForeColor = outcome.Success ? Theme.TextPrimary : Theme.Danger,
        };

        item.SubItems.Add(outcome.Success ? "成功" : "失败");
        item.SubItems.Add(outcome.ReleasedBytes > 0 ? ByteSize.Format(outcome.ReleasedBytes) : "—");
        item.SubItems.Add(outcome.Duration > TimeSpan.Zero ? ByteSize.FormatDuration(outcome.Duration) : "—");

        // A refusal carries an NTSTATUS, which is the only way to tell "no rights" from
        // "this build does not implement it".
        var detail = outcome.Message;
        if (outcome.NtStatus != 0)
        {
            detail = $"{detail}  [{CleanOutcome.FormatNtStatus(outcome.NtStatus)}: {CleanOutcome.DescribeNtStatus(outcome.NtStatus)}]";
        }

        item.SubItems.Add(detail);
        item.ToolTipText = detail;

        _results.Items.Add(item);
        item.EnsureVisible();

        // Re-fit now that the row exists. Adding rows is what makes the vertical scrollbar
        // appear, and it is the scrollbar that narrows the client area the columns were sized
        // against — so a fit taken before the first row leaves them too wide and summons a
        // horizontal scrollbar underneath, which then eats the height the last row needed.
        _results.AutoSizeColumns();

        _resultsCard.Hint = $"{_results.Items.Count} 个区域";
    }

    private void ShowSummary(CleanReport report, bool dryRun)
    {
        if (dryRun)
        {
            _summary.Text =
                $"试运行 · 已检查 {report.SucceededCount}/{report.Outcomes.Count} 个区域 · " +
                $"系统自然波动 {ByteSize.FormatDelta(report.TotalAvailableDelta)} · " +
                $"备用列表 {ByteSize.FormatDelta(report.StandbyDelta)} " +
                $"（{ByteSize.FormatDuration(report.Duration)}）";
        }
        else
        {
            _summary.Text =
                $"已释放 {ByteSize.Format(report.TotalReleasedBytes)} · " +
                $"可用内存 {ByteSize.FormatDelta(report.TotalAvailableDelta)} · " +
                $"负载 {report.Before.LoadPercent:F0}% → {report.After.LoadPercent:F0}% · " +
                $"{report.SucceededCount}/{report.Outcomes.Count} 个区域成功" +
                $"（{ByteSize.FormatDuration(report.Duration)}）";
        }

        _summary.ForeColor = report.Success ? Theme.Success : Theme.Warning;
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
        BringToFront();
    }

    private void RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                UseShellExecute = true,
                Verb = "runas",
            });

            _reallyExit = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "无法以管理员权限重新启动：\n\n" + ex.Message,
                "MemReduct",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private static Icon? TryLoadAppIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The tool comes up in the notification area rather than on screen.
    /// <para>
    /// <see cref="Application.Run(Form)"/> shows the main window as it opens the message loop;
    /// that first request is the one swallowed here. The handle is still created, because the
    /// window stays the object the cleaning pipeline marshals its results through — a clean that
    /// finishes while the window has never been shown would otherwise <c>BeginInvoke</c> on a
    /// control with no handle.
    /// </para>
    /// <para>
    /// Every later request — the tray icon's double click, its 打开主窗口 item, the settings entry
    /// behind it — reaches the window normally.
    /// </para>
    /// </summary>
    protected override void SetVisibleCore(bool value)
    {
        if (!_startInTray)
        {
            base.SetVisibleCore(value);
            return;
        }

        if (!IsHandleCreated)
        {
            CreateHandle();
        }

        _startInTray = false;
        BeginInvoke(() => ShowTrayStartupHint());
        base.SetVisibleCore(false);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window parks the tool in the notification area so scheduled cleaning
        // keeps working; only the tray's Exit item, or a restart, really quits.
        if (!_reallyExit && _settings.MinimizeToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _settings.Areas = SelectedAreas();
        _settings.Save();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose();
            _countdownTimer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _tips.Dispose();
            _trayIcon?.Dispose();
            _appIcon?.Dispose();
            // Holds an NVML session, PDH query handles and a DXGI factory; none of them are
            // freed by the finalizer of any control, so they are released explicitly here.
            _hardware.Dispose();

            // The page-list query is held open for the life of the window, so it is closed
            // here rather than by a using block on each read.
            _pageLists?.Dispose();
            _pageLists = null;
        }

        base.Dispose(disposing);
    }
}
