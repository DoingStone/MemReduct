# MemReduct

A memory cleaning tool for Windows: a library, a command-line tool, and a small
desktop app that release memory Windows is holding but no longer needs.

The user interface — command-line output and desktop app alike — is in Chinese.

Everything here talks to the kernel directly. There is no driver, no service, and
no bundled runtime requirement beyond the .NET 8 desktop runtime.

---

## Cleaning areas

Each area maps to a specific Windows mechanism. The command tokens are what you
pass to `--areas=`.

| Token | Area | Mechanism | Admin |
|---|---|---|---|
| `workingset` | Working sets | `EmptyWorkingSet` on every accessible process | no |
| `filecache` | System file cache | `NtSetSystemInformation` class **21** | yes |
| `modified` | Modified page list | class **80**, command 3 (`MemoryFlushModifiedList`) | yes |
| `standby` | Standby list | class **80**, command 4 (`MemoryPurgeStandbyList`) | yes |
| `combined` | Combined page lists | class **80**, commands 3 → 5 → 4 | yes |
| `registry` | Registry cache | class **155** (`SystemRegistryReconciliationInformation`) | yes |

`default` = `workingset,filecache,standby,modified,registry` — everything except
`combined`, which overlaps two areas that are already in the set.

Aliases are accepted for convenience: `ws`, `cache`, `lists`, `all`, `default`.
Separators may be commas, semicolons, pipes, or spaces.

---

## Why "released" and "available" are reported separately

Most memory cleaners show a single "freed" number. That number is misleading, and
this tool deliberately does not use it.

Windows accounts for physical pages in four buckets:

- **Free** — zeroed or free, immediately allocatable.
- **Standby** — pages holding valid cached data that were evicted from a working
  set. They are reclaimed instantly when something allocates, but until then they
  serve as cache.
- **Modified** — dirty pages queued for write-back before reuse.
- **Active** — everything else, i.e. the working sets themselves.

`GlobalMemoryStatusEx` reports **available = free + standby**. So purging the
standby list moves pages from one bucket that counts as available into another
bucket that also counts as available: the cache is genuinely released, but the
"available" figure barely moves.

Measured on the development machine, purging the standby list released **7.68 GB**
of cache while the available-memory figure reported **−43 MB**. Reporting that as
"freed: −43 MB" would have been actively wrong.

So each area reports **Released** — bytes drained from the structure that area
actually targets — and the availability delta is shown next to it, labelled, never
instead of it:

```
  standby        +1.53 GB     236 ms  MemoryPurgeStandbyList applied.

  Released              1.53 GB
  Available change      -4.11 MB      <- expected: standby became free
  Standby change        -1.53 GB      <- the structure that actually drained
```

To measure this, the tool reads the page lists from the PDH `\Memory\` counter set
(`Free & Zero Page List Bytes`, the three standby priority bands, `Modified Page
List Bytes`, `Cache Bytes`). Counters are registered by their **English** names via
`PdhAddEnglishCounter`, so the tool works on localised Windows installations. If
the counter set is unavailable the page-list rows simply disappear and the
aggregate figures still work — nothing about cleaning depends on PDH.

The two sources cross-check: free + standby equals the `GlobalMemoryStatusEx`
available figure.

---

## Requirements

- Windows 10 or later (developed and verified on Windows 11 build 26200).
- **Administrator rights** for everything except working-set trimming. Without
  them the tool still runs and reports the exact NTSTATUS refusal per area rather
  than failing silently.

## Build

```powershell
dotnet build MemReduct.sln -c Release
```

Or use the script, which also produces self-contained single-file executables:

```powershell
.\build.ps1                      # framework-dependent build
.\build.ps1 -Publish             # self-contained single-file, win-x64
.\build.ps1 -Publish -Runtime win-arm64
```

`-Publish` also drops the finished executables into `dist\`, one file per front end
and nothing else:

```
dist\MemReduct.exe        the desktop app  (double-click this)
dist\MemReduct-CLI.exe    the console tool
```

Both are self-contained, so they run on a machine with no .NET installed, and each
is a genuine *single file* — the native libraries that `PublishSingleFile` leaves
behind by default are embedded with `IncludeNativeLibrariesForSelfExtract` and unpack
to a temp directory on first run. Copy either one anywhere on its own; it needs no
folder beside it.

Double-clicking the console tool prints the status table and then waits for a key.
That pause is conditional: it happens only when the process is the sole owner of its
console (`GetConsoleProcessList` returns 1) and nothing is redirected, so
`memreduct status` from a script or a pipe is unaffected.

The application icon is generated from source rather than checked in as an opaque
blob:

```powershell
.\tools\make-icon.ps1
```

---

## Command line

```
memreduct [command] [options]

Commands
  status          Show current memory statistics (default)
  clean           Clean the selected areas
  areas           List the available cleaning areas
  version         Show version information
  help            Show this help

Options
  --areas=<list>  Areas to clean (default: workingset,filecache,
                  standby,modified,registry)
  --dry-run       Report what each area would do without issuing any kernel call
  --watch[=<sec>] Refresh the status continuously (default 2 seconds)
  --json          Emit machine-readable JSON
  --quiet         Print only the released byte count
  -h, --help      Show this help
```

Exit codes: `0` success, `1` a cleaner failed, `2` usage error.

Examples:

```powershell
memreduct status
memreduct status --watch=1
memreduct clean --areas=standby
memreduct clean --dry-run
memreduct clean --json | ConvertFrom-Json
memreduct clean --quiet      # just the byte count, for scripting
```

`--dry-run` issues no kernel calls at all. Any change it reports is drift caused by
other processes on the machine, which is useful as a baseline, so it is labelled
"system drift" rather than presented as a result.

---

## Desktop app

Double-click `dist\MemReduct.exe` (see [Build](#build)), or run the development build:

```powershell
.\src\MemReduct.Gui\bin\Release\net8.0-windows\memreduct-gui.exe
```

- A memory-load hero with a colour-coded bar, and the full statistics grid beside it,
  including the page-list breakdown.
- Per-area switches with `默认组合` / `全部区域` / `清空选择` presets.
- **立即清理** and **试运行**; results appear per area as they complete, with columns
  that fit themselves to the table.
- A **menu bar** — `文件` / `设置` / `帮助` — covering everything the window can do.
- Automatic cleaning on a timer, and optionally as soon as memory load crosses a
  threshold you choose.
- Lives in the notification area, and **starts there**: launching it, at logon or by hand,
  puts the icon in the tray and leaves the window closed. Double-click the icon — or pick
  打开主窗口 from its menu — to open the window; closing it parks the tool back in the tray
  so scheduled cleaning keeps working, and **退出** in the tray menu really quits.
- If launched without administrator rights it shows a **以管理员身份重启** button
  instead of quietly doing half its job.

### The interface

The window is drawn in one place, from one palette. `Theme.cs` holds every colour,
font and measurement, so a change lands everywhere at once instead of being repeated
across a dozen controls.

- Depth comes from **surface colour and a hairline**, not shadows. `CardPanel` paints a
  rounded face with a one-pixel border, a title strip and a rule under it; each group of
  settings sits on its own card over a darker canvas.
- There are **three text tiers** and no more — primary, secondary, muted. Every label
  picks one, which is what keeps a dense screen readable.
- The load bands are shared by the bar, the caption and the tray icon, so one reading
  never appears in two colours.
- **The controls are owner-drawn.** `Button` with `FlatStyle.Flat` still paints a focus
  rectangle, ignores the border colour on hover and draws a grey wash on mouse-over —
  three details that make a dark theme look broken. `FlatButton`, `Switch`, `LoadBar`,
  `CardPanel` and `SlimListView` paint themselves instead.
- `SlimListView` exists because the stock `ListView` draws its column header in the
  system light-theme grey, which is a bright bar across an otherwise dark window.
  `HeaderStyle = Nonclickable` does not change that — it only stops the header
  responding to clicks. The scrollbars and the header's own background are separate
  windows drawn by the system theme, so the control also asks for the explorer dark
  theme (`SetWindowTheme`) for the parts it cannot paint.
- Column widths **re-fit after every row**, and on any client-size change. A `ListView`
  gains its vertical scrollbar only once rows exist, and that scrollbar narrows the
  client area the columns were sized against — so a fit taken earlier leaves them too
  wide and summons a horizontal scrollbar, which then eats the height the last row
  needed.

The custom controls declare their accessibility role and implement `DoDefaultAction`,
so they appear to screen readers and UI Automation as the buttons and check-buttons
they look like, rather than as unlabelled panes.

### The menu bar

| Menu | Items |
| --- | --- |
| `文件` | 立即清理 (`Ctrl+R`), 试运行 (`Ctrl+T`), 隐藏到通知区域, 退出 |
| `设置` | 定时自动清理, 负载触发清理, 清理区域 ▸, 关闭窗口时最小化到托盘, 清理后显示通知, 刷新间隔 ▸, 主题 ▸, 界面缩放 ▸, 开机时自动启动, 打开配置文件位置, 恢复默认设置 |
| `帮助` | 以管理员身份重启, 关于 MemReduct |

The `设置` menu is **not a second copy of the window's state**. Check marks are written
from the live settings by one `SyncMenu` pass whenever anything changes, and every item
routes through the same handler the corresponding control uses, so the two can never
disagree.

Menus are themed through the whole strip tree. `MenuStrip.Renderer` styles only the bar,
and `ToolStripManager.Renderer` is read when a strip is constructed — but a drop-down is
constructed lazily the first time it is shown, so a menu bar that looks right still
drops a white panel. The renderer is therefore assigned to each `ToolStripDropDown`
explicitly, and the check mark is drawn rather than stock: the system glyph is a dark
bitmap that all but vanishes on a dark menu, which would make a checked setting read as
unchecked.

### Themes and scaling

`Theme.cs` is the only place a colour, a font or a corner radius is written down. Nothing
else in the project contains a literal colour, and every geometry constant goes through
`Theme.S(n)`, which multiplies by the current UI scale — so the whole window is drawn from
one palette and one scale factor.

Both are switchable at runtime from `设置`. Colours and fonts are **properties, not
`static readonly` fields**, because a control that cached `Theme.Body` at construction
would keep the old font forever. Neither can be swapped in place for the same reason: the
menu and the capture of settings both call `RebuildUi()`, which disposes every child
control and builds the window again. Result rows are carried across with
`(ListViewItem)item.Clone()` so a theme change does not throw away what you just cleaned.

The light palette is not the dark one inverted. The dark accent green is chosen to glow
against near-black and turns pale on white, so the light theme uses a darker green
`(13,148,96)`; the load-band colours are likewise deepened, because the dark-theme shades
fail contrast against a white card.

Four scale steps are offered — 紧凑 (90%), 标准 (100%), 较大 (115%), 特大 (130%). The
window is resized to fit the new scale rather than clipping, and clamped to the working
area, so 特大 on a small screen still produces a usable window.

### Starting with Windows

`开机时自动启动` registers a per-user Scheduled Task named `MemReduct` — a logon trigger
with a 15 second delay, `RunLevel=HighestAvailable`, and an interactive logon token — rather
than a value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

The reason is elevation. The shell starts `Run` entries with the ordinary, non-elevated
token, and an executable carrying the `RUNASADMIN` compatibility flag (compatibility tab →
*Run this program as an administrator*) cannot be created from that context at all: Explorer
logs the command as started with **process id 0** and no window ever appears. That is what
"start-up is configured but nothing starts" looks like, and in Task Manager's start-up tab
it is indistinguishable from a working entry. A task with `RunLevel=HighestAvailable` starts
the same image with the full token and no consent prompt, which is also the token this tool
needs to release the standby list.

The task is written through `schtasks.exe` (`/Create /XML`, version 1.3 of the task schema)
rather than the Task Scheduler COM API, so the very same definition can be registered or
inspected by hand — `schtasks /Query /TN MemReduct /XML` prints it — while diagnosing a
machine. The check mark is read back from the registered definition rather than from the
settings file, so a task that was disabled in Task Scheduler, or that still points at a
folder which has since been moved, correctly reports as off.

The legacy `Run` value is deleted whenever the task is in place: two registrations would
start two copies, and the second copy only manages to show an "already running" message box.
When the task cannot be created — a machine that blocks Task Scheduler — the `Run` value is
written instead, as a degraded but still working fallback.

### Starting in the notification area

The window is never opened by itself. `Application.Run` shows the main window as it starts the
message loop, and `MainForm.SetVisibleCore` swallows that one request — so a logon start is
silent, and a hand launch leaves nothing on screen but the tray icon. The window handle is
still created at that moment, because the window remains the object the cleaning pipeline
marshals its per-area results through: a clean that finishes while the window has never been
shown would otherwise post to a control with no handle. One balloon says the tool is in the
tray, and both a double click on the icon and 打开主窗口 in its menu bring the window up.

### The tray icon is a readout

The notification-area icon is not a logo. It is a **solid square, coloured by the
current memory load, with the load number centred in it**. The colour bands match the
rest of the interface — green below 60%, yellow from 60%, orange from 80%, red from
90% — and the four hues are kept deliberately far apart, because at 16 px two
neighbouring shades of the same colour read as one colour.

Two details are what make it legible at that size:

- The number is drawn from a hand-made **3 × 5 pixel font**, not with
  `Graphics.DrawString`. A real font at 16 px renders as grey antialiased mush; a
  bitmap font stays crisp because every glyph pixel lands on exactly one icon pixel.
  The whole icon is drawn with `SmoothingMode.None` for the same reason.
- There is **no percent sign**. A trailing glyph costs a whole glyph cell of width,
  and width is what caps the scale: at 16 px `49%` fits only at 1×, while `49` fits at
  2× and is therefore twice as tall and twice as wide. The unit lives in the tooltip
  instead, which reads `内存占用 49%（中键单击立即清理）`.

The icon is rebuilt only when the *rounded* percentage changes, because assigning
`NotifyIcon.Icon` is a shell call rather than a field write, and the statistics
refresh runs on a timer. Each rebuild hands the new `HICON` to the shell before the
old one is released, so the notification area never paints a freed handle — and the
tray icon is tracked separately from the window icon so a redraw can never dispose
the icon the main window is using.

> Windows 11 hides new tray icons in the overflow flyout by default. To keep it on the
> taskbar: **Settings → Personalization → Taskbar → Other system tray icons**, or drag
> it out of the flyout.

### Middle-click to clean

**Middle-click the tray icon to clean immediately**, without opening the window. It
runs the same clean the 立即清理 button runs, over the currently selected areas, and
reports through the same notification balloon. The handler is bound to `MouseClick`
rather than `MouseDown` so the action fires on release, like every other button in
the application.

Settings are stored at `%APPDATA%\MemReduct\settings.json` and are written
best-effort — a missing or corrupt file never prevents startup.

---

## Project layout

```
MemReduct.sln
src/
  MemReduct.Core/          Library: interop, cleaners, measurement
    Interop/               P/Invoke, constants, structs, privilege handling
    Cleaners/              One cleaner per area, plus the registry and report types
  MemReduct.Cli/           memreduct.exe
  MemReduct.Gui/           memreduct-gui.exe (WinForms)
    Theme.cs               Every colour, font and measurement
    CardPanel.cs           A titled, rounded group box
    FlatButton.cs          Owner-drawn button (Primary / Secondary / Ghost)
    Switch.cs              Owner-drawn on/off switch
    LoadBar.cs             Owner-drawn load bar
    SlimListView.cs        Owner-drawn results table
    DarkMenuRenderer.cs    Menu bar, drop-downs and the tray menu
    TrayIconRenderer.cs    Draws the tray icon from the load percentage
    MainForm.cs            Window layout, menu bar and the clean pipeline
tools/
  make-icon.ps1            Draws app.ico
```

### Notes on the interop

Two details are easy to get wrong and are handled explicitly:

- `AdjustTokenPrivileges` returns success even when it assigned nothing. Privilege
  enabling therefore reads `Marshal.GetLastWin32Error()` immediately after the
  call (`0` = enabled, `1300` = `ERROR_NOT_ALL_ASSIGNED` = not held) before any
  other P/Invoke can clobber it.
- The `SIZE_T` fields of `PERFORMANCE_INFORMATION` are page counts, not byte
  counts, and are multiplied by the page size.

The undocumented `NtSetSystemInformation` classes used here (21, 80, 155) are the
same ones long-standing Windows memory cleaners rely on. Every one of them was
verified against a measured effect on a real machine before being wired in.

---

## A note on safety

Trimming working sets and purging the standby list are safe, supported operations —
Windows reclaims those pages on demand. Both carry a real cost, though: the next
access to a discarded page has to come from disk. That is why the tool shows you
what it released instead of a single flattering number, and why `--dry-run` exists.

---

## License

Released under the MIT License. See [LICENSE](LICENSE).

