using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MemReduct.Core;
using MemReduct.Core.Cleaners;

namespace MemReduct.Cli;

/// <summary>Entry point and command dispatch for the <c>memreduct</c> console tool.</summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>How many processes are attached to this process's console.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint count);

    private static int Main(string[] args)
    {
        var exitCode = Run(args);

        // Only when the console belongs to this process alone — see LaunchedByDoubleClick.
        if (LaunchedByDoubleClick())
        {
            Console.WriteLine();
            Console.WriteLine("按任意键关闭此窗口…");
            try
            {
                Console.ReadKey(intercept: true);
            }
            catch (InvalidOperationException)
            {
                // No console input available after all; nothing to hold open.
            }
        }

        return exitCode;
    }

    /// <summary>
    /// True when this process is the only one attached to its console, i.e. it was started by
    /// double-clicking the executable rather than from an existing shell.
    /// <para>
    /// Windows destroys a console window as soon as its last attached process exits, so a
    /// double-clicked run would print its table and vanish before it could be read. When the
    /// output is redirected, or a shell is sharing the console, there is nothing to hold open
    /// and the tool stays scriptable.
    /// </para>
    /// </summary>
    private static bool LaunchedByDoubleClick()
    {
        if (Console.IsOutputRedirected || Console.IsInputRedirected || Console.IsErrorRedirected)
        {
            return false;
        }

        var buffer = new uint[4];
        return GetConsoleProcessList(buffer, (uint)buffer.Length) == 1;
    }

    private static int Run(string[] args)
    {
        Output.Init();

        if (args.Length == 0)
        {
            return CommandStatus(new Options());
        }

        var command = args[0].ToLowerInvariant();
        var options = Options.Parse(args.Skip(1));

        if (options.Help || command is "help" or "-h" or "--help" or "/?")
        {
            PrintUsage();
            return 0;
        }

        if (options.Error is not null)
        {
            Output.Failure(options.Error);
            Output.Line();
            PrintUsage();
            return 2;
        }

        try
        {
            return command switch
            {
                "status" or "stat" or "info" => CommandStatus(options),
                "clean" or "purge" or "free" => CommandClean(options),
                "areas" or "list" => CommandAreas(options),
                "version" or "--version" => CommandVersion(),
                _ => UnknownCommand(command),
            };
        }
        catch (Exception ex)
        {
            Output.Failure($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int UnknownCommand(string command)
    {
        Output.Failure($"未知命令“{command}”。");
        Output.Line();
        PrintUsage();
        return 2;
    }

    // ------------------------------------------------------------------ status

    private static int CommandStatus(Options options)
    {
        if (options.WatchSeconds is { } seconds)
        {
            return CommandWatch(seconds, options);
        }

        var snapshot = MemoryCleaner.GetSnapshot();

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(ToJson(snapshot), JsonOptions));
            return 0;
        }

        PrintStatus(snapshot);
        return 0;
    }

    private static void PrintStatus(MemorySnapshot snapshot)
    {
        Output.Line();
        Output.LineColored("  " + RuntimeInfo.Describe(), ConsoleColor.DarkGray);
        Output.Header("内存状态");

        Console.Write("  ");
        Output.Colored(Output.Bar(snapshot.LoadPercent), Output.LoadColor(snapshot.LoadPercent));
        Output.Line($"   已用 {ByteSize.Format((long)snapshot.UsedPhysical)} / {ByteSize.Format((long)snapshot.TotalPhysical)}");
        Output.Line();

        Output.Field("物理内存总量", ByteSize.Format((long)snapshot.TotalPhysical));
        Output.Field("物理内存可用", ByteSize.Format((long)snapshot.AvailablePhysical), ConsoleColor.Green);
        Output.Field("物理内存已用", ByteSize.Format((long)snapshot.UsedPhysical));

        Output.Field("页面文件总量", ByteSize.Format((long)snapshot.TotalPageFile));
        Output.Field("页面文件可用", ByteSize.Format((long)snapshot.AvailablePageFile));
        Output.Field("页面文件已用", ByteSize.Format((long)snapshot.UsedPageFile));

        Output.Field("提交内存", $"{ByteSize.Format((long)snapshot.CommitTotal)} / {ByteSize.Format((long)snapshot.CommitLimit)}  ({snapshot.CommitLoadPercent:F1}%)");
        Output.Field("提交峰值", ByteSize.Format((long)snapshot.CommitPeak));

        Output.Field("系统缓存", ByteSize.Format((long)snapshot.SystemCache), ConsoleColor.Cyan);
        Output.Field("内核分页池", ByteSize.Format((long)snapshot.KernelPaged));
        Output.Field("内核非分页池", ByteSize.Format((long)snapshot.KernelNonpaged));

        Output.Field("虚拟内存总量", ByteSize.Format((long)snapshot.TotalVirtual));
        Output.Field("虚拟内存可用", ByteSize.Format((long)snapshot.AvailableVirtual));

        Output.Field("页面大小", $"{snapshot.PageSize} 字节");
        Output.Field("进程数", snapshot.ProcessCount.ToString());
        Output.Field("线程数", snapshot.ThreadCount.ToString());
        Output.Field("句柄数", snapshot.HandleCount.ToString());

        if (snapshot.PageLists is { } lists)
        {
            Output.Line();
            Output.LineColored("  页面列表", ConsoleColor.White);
            Output.Field("空闲（含零页）", ByteSize.Format(lists.FreeBytes), ConsoleColor.Green);
            Output.Field("备用列表合计", ByteSize.Format(lists.StandbyBytes), ConsoleColor.Cyan);
            Output.Field("  核心（永不清除）", ByteSize.Format(lists.StandbyCoreBytes), ConsoleColor.DarkGray);
            Output.Field("  普通优先级", ByteSize.Format(lists.StandbyNormalBytes), ConsoleColor.DarkGray);
            Output.Field("  保留", ByteSize.Format(lists.StandbyReserveBytes), ConsoleColor.DarkGray);
            Output.Field("可清除的备用页", ByteSize.Format(lists.PurgeableStandbyBytes), ConsoleColor.Yellow);
            Output.Field("已修改页", ByteSize.Format(lists.ModifiedBytes), ConsoleColor.Yellow);
            Output.Field("缓存工作集", ByteSize.Format(lists.CacheBytes), ConsoleColor.Cyan);
            Output.Line();
            Output.LineColored("  可用内存 = 空闲 + 备用。备用页是缓存数据，需要时会自动回收，", ConsoleColor.DarkGray);
            Output.LineColored("  所以清除备用列表释放的是缓存，而“可用内存”这个数字", ConsoleColor.DarkGray);
            Output.LineColored("  几乎不会变化。", ConsoleColor.DarkGray);
        }

        Output.Line();
        Output.LineColored($"  采样时间 {snapshot.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}", ConsoleColor.DarkGray);
    }

    // ------------------------------------------------------------------ watch

    private static int CommandWatch(double seconds, Options options)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 0.5, 60));
        Output.LineColored($"  每 {interval.TotalSeconds:0.#} 秒采样一次内存——按 Ctrl+C 停止。", ConsoleColor.DarkGray);
        Output.Line();

        using var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };

        var first = true;
        using var pageLists = PageListReader.TryCreate();

        while (!stop.IsSet)
        {
            var snapshot = MemoryInfoProvider.GetSnapshot(includePageLists: false) with
            {
                PageLists = pageLists?.TryRead(),
            };

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(ToJson(snapshot), JsonOptions));
            }
            else
            {
                if (!first)
                {
                    Console.WriteLine();
                }

                Console.Write($"  {snapshot.Timestamp:HH:mm:ss}  ");
                Output.Colored(Output.Bar(snapshot.LoadPercent, 24), Output.LoadColor(snapshot.LoadPercent));
                Console.Write($"  可用 {ByteSize.Format((long)snapshot.AvailablePhysical),9}");
                Console.Write($"  缓存 {ByteSize.Format((long)snapshot.SystemCache),9}");
                if (snapshot.PageLists is { } lists)
                {
                    Console.Write($"  备用 {ByteSize.Format(lists.StandbyBytes),9}");
                    Console.Write($"  空闲 {ByteSize.Format(lists.FreeBytes),9}");
                }

                Console.WriteLine($"  提交 {ByteSize.Format((long)snapshot.CommitTotal),9}");
            }

            first = false;
            stop.Wait(interval);
        }

        return 0;
    }

    // ------------------------------------------------------------------ clean

    private static int CommandClean(Options options)
    {
        var areas = options.Areas;

        if (!PrivilegeEnabler.IsElevated && (areas & CleanAreas.RequiresElevation) != 0)
        {
            Output.Warning("当前不是以管理员身份运行——系统文件缓存、页面列表和注册表重整都会被拒绝。");
        }

        var context = new CleanContext { DryRun = options.DryRun };

        if (!options.Json && !options.Quiet)
        {
            Output.Line();
            Output.LineColored("  " + RuntimeInfo.Describe(), ConsoleColor.DarkGray);
            Output.Header(options.DryRun ? "内存清理（试运行）" : "内存清理");
        }

        var cleaner = new MemoryCleaner();
        var stopwatch = Stopwatch.StartNew();

        var report = cleaner.Clean(areas, context, new SyncProgress<CleanOutcome>(outcome =>
        {
            if (options.Json || options.Quiet)
            {
                return;
            }

            // Compose the whole row first so a single write lands on the console.
            var area = CleanerRegistry.ToToken(outcome.Area).PadRight(11);
            var delta = outcome.Success ? ByteSize.FormatDelta(outcome.ReleasedBytes).PadLeft(12) : "           -";
            var timing = ByteSize.FormatDuration(outcome.Duration).PadLeft(9);
            var line = $"  {area}{delta}  {timing}  {outcome.Message}";

            Output.LineColored(line, outcome.Success
                ? (outcome.ReleasedBytes > 0 ? ConsoleColor.Green : ConsoleColor.Gray)
                : ConsoleColor.Red);
        }));

        stopwatch.Stop();

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(ToJson(report), JsonOptions));
            return report.Success ? 0 : 1;
        }

        if (options.Quiet)
        {
            Console.WriteLine(report.TotalReleasedBytes);
            return report.Success ? 0 : 1;
        }

        Output.Line();
        Output.LineColored("  " + new string('-', 60), ConsoleColor.DarkGray);

        if (options.DryRun)
        {
            // A dry run issues no kernel calls, so any change here is unrelated system activity.
            Output.Field("可用内存变化", ByteSize.FormatDelta(report.TotalAvailableDelta), ConsoleColor.DarkGray);
            Output.Field("  （系统自然波动——未发出任何调用）", string.Empty, ConsoleColor.DarkGray);
        }
        else
        {
            // The headline figure is what the cleaners released from the structures they
            // target. The available-memory delta is shown next to it, not instead of it:
            // purging standby drains cached pages into the free list, which leaves available
            // memory almost unchanged while releasing gigabytes.
            Output.Field(
                "已释放",
                ByteSize.Format(report.TotalReleasedBytes),
                report.TotalReleasedBytes > 0 ? ConsoleColor.Green : ConsoleColor.Yellow);
            Output.Field("可用内存变化", ByteSize.FormatDelta(report.TotalAvailableDelta));
        }

        Output.Field("清理前可用", ByteSize.Format((long)report.Before.AvailablePhysical));
        Output.Field("清理后可用", ByteSize.Format((long)report.After.AvailablePhysical));

        if (report.PageListsAvailable)
        {
            Output.Field("备用列表变化", ByteSize.FormatDelta(report.StandbyDelta));
            Output.Field("已修改页变化", ByteSize.FormatDelta(report.ModifiedDelta));
        }

        Output.Field("缓存变化", ByteSize.FormatDelta(report.SystemCacheDelta));
        Output.Field("内存负载", $"{report.Before.LoadPercent:F1}% → {report.After.LoadPercent:F1}%");
        Output.Field("用时", ByteSize.FormatDuration(stopwatch.Elapsed));
        Output.Field("成功区域", $"{report.SucceededCount}/{report.Outcomes.Count}");
        Output.Line();

        foreach (var failure in report.Outcomes.Where(o => !o.Success))
        {
            Output.Failure($"{CleanerRegistry.ToToken(failure.Area)}: {failure.Message}");
        }

        if (report.Success)
        {
            Output.Success(options.DryRun ? "试运行完成。" : "清理完成。");
        }

        Output.Line();
        return report.Success ? 0 : 1;
    }

    // ------------------------------------------------------------------ areas

    private static int CommandAreas(Options options)
    {
        if (options.Json)
        {
            var payload = CleanerRegistry.All.Select(c => new
            {
                area = CleanerRegistry.ToToken(c.Area),
                flag = c.Area.ToString(),
                name = c.DisplayName,
                description = c.Description,
                requiresElevation = c.RequiresElevation,
                inDefaultSet = CleanAreas.Default.HasFlag(c.Area),
            });
            Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
            return 0;
        }

        Output.Header("可清理区域");
        foreach (var cleaner in CleanerRegistry.All)
        {
            var token = CleanerRegistry.ToToken(cleaner.Area);
            var marker = CleanAreas.Default.HasFlag(cleaner.Area) ? "*" : " ";
            Output.Colored($"  {marker} {token,-11}", ConsoleColor.White);
            Output.Colored(Output.PadDisplay(cleaner.DisplayName, 22), ConsoleColor.Cyan);
            Output.LineColored(cleaner.RequiresElevation ? "需要管理员" : "任意用户", cleaner.RequiresElevation ? ConsoleColor.Yellow : ConsoleColor.Green);
            Output.LineColored($"      {cleaner.Description}", ConsoleColor.Gray);
            Output.Line();
        }

        Output.LineColored("  * = 包含在默认清理集合中", ConsoleColor.DarkGray);
        Output.LineColored("  区域代号：" + string.Join(", ", CleanerRegistry.All.Select(c => CleanerRegistry.ToToken(c.Area))) + ", default, all", ConsoleColor.DarkGray);
        Output.Line();
        return 0;
    }

    private static int CommandVersion()
    {
        Console.WriteLine(RuntimeInfo.Describe());
        return 0;
    }

    // ------------------------------------------------------------------ json projections

    private static object ToJson(MemorySnapshot s) => new
    {
        timestamp = s.Timestamp,
        totalPhysicalBytes = s.TotalPhysical,
        availablePhysicalBytes = s.AvailablePhysical,
        usedPhysicalBytes = s.UsedPhysical,
        loadPercent = Math.Round(s.LoadPercent, 2),
        totalPageFileBytes = s.TotalPageFile,
        availablePageFileBytes = s.AvailablePageFile,
        usedPageFileBytes = s.UsedPageFile,
        totalVirtualBytes = s.TotalVirtual,
        availableVirtualBytes = s.AvailableVirtual,
        commitTotalBytes = s.CommitTotal,
        commitLimitBytes = s.CommitLimit,
        commitPeakBytes = s.CommitPeak,
        systemCacheBytes = s.SystemCache,
        kernelPagedBytes = s.KernelPaged,
        kernelNonPagedBytes = s.KernelNonpaged,
        pageSizeBytes = s.PageSize,
        processes = s.ProcessCount,
        threads = s.ThreadCount,
        handles = s.HandleCount,
        elevated = PrivilegeEnabler.IsElevated,
        pageLists = s.PageLists is { } p
            ? new
            {
                freeBytes = p.FreeBytes,
                standbyCoreBytes = p.StandbyCoreBytes,
                standbyNormalBytes = p.StandbyNormalBytes,
                standbyReserveBytes = p.StandbyReserveBytes,
                standbyTotalBytes = p.StandbyBytes,
                purgeableStandbyBytes = p.PurgeableStandbyBytes,
                modifiedBytes = p.ModifiedBytes,
                cacheBytes = p.CacheBytes,
            }
            : null,
    };

    private static object ToJson(CleanReport r) => new
    {
        requestedAreas = r.Requested.ToString(),
        dryRun = r.DryRun,
        success = r.Success,
        // What the cleaners released from the structures they targeted.
        totalReleasedBytes = r.TotalReleasedBytes,
        // Kept separate because a standby purge cannot move this number.
        totalAvailableDeltaBytes = r.TotalAvailableDelta,
        systemCacheDeltaBytes = r.SystemCacheDelta,
        standbyDeltaBytes = r.PageListsAvailable ? r.StandbyDelta : (long?)null,
        modifiedDeltaBytes = r.PageListsAvailable ? r.ModifiedDelta : (long?)null,
        durationMs = Math.Round(r.Duration.TotalMilliseconds, 1),
        availableBeforeBytes = r.Before.AvailablePhysical,
        availableAfterBytes = r.After.AvailablePhysical,
        loadBeforePercent = Math.Round(r.Before.LoadPercent, 2),
        loadAfterPercent = Math.Round(r.After.LoadPercent, 2),
        summary = r.Summary,
        areas = r.Outcomes.Select(o => new
        {
            area = CleanerRegistry.ToToken(o.Area),
            success = o.Success,
            releasedBytes = o.ReleasedBytes,
            message = o.Message,
            affectedProcesses = o.AffectedProcesses,
            failedProcesses = o.FailedProcesses,
            ntStatus = o.NtStatus == 0 ? null : CleanOutcome.FormatNtStatus(o.NtStatus),
            ntStatusName = o.NtStatus == 0 ? null : CleanOutcome.DescribeNtStatus(o.NtStatus),
            durationMs = Math.Round(o.Duration.TotalMilliseconds, 1),
            observation = o.Observation.PageListsAvailable || o.Observation.AvailableDelta != 0
                ? new
                {
                    // Discriminator. Without it a genuine 0 delta and "PDH was unavailable,
                    // so every delta is a placeholder 0" serialise identically — the exact
                    // ambiguity this measurement model exists to remove.
                    pageListsAvailable = o.Observation.PageListsAvailable,
                    availableDeltaBytes = o.Observation.AvailableDelta,
                    freeDeltaBytes = o.Observation.FreeDelta,
                    standbyDeltaBytes = o.Observation.StandbyDelta,
                    modifiedDeltaBytes = o.Observation.ModifiedDelta,
                    cacheDeltaBytes = o.Observation.CacheDelta,
                }
                : null,
        }),
    };

    // ------------------------------------------------------------------ usage

    private static void PrintUsage()
    {
        Output.LineColored("  memreduct —— Windows 内存清理工具", ConsoleColor.Cyan);
        Output.Line();
        Output.LineColored("  用法", ConsoleColor.White);
        Output.Line("    memreduct <命令> [选项]");
        Output.Line();
        Output.LineColored("  命令", ConsoleColor.White);
        Output.Line("    status              显示当前内存计数器（默认命令）");
        Output.Line("    clean               执行清理并报告回收了多少内存");
        Output.Line("    areas               列出所有可清理区域及其权限要求");
        Output.Line("    version             输出版本与权限状态");
        Output.Line("    help                显示本帮助");
        Output.Line();
        Output.LineColored("  选项", ConsoleColor.White);
        Output.Line("    --areas=<列表>      指定 clean 要清理的区域，用逗号分隔。");
        Output.Line("                        workingset, filecache, standby, modified,");
        Output.Line("                        combined, registry, default, all");
        Output.Line("    --dry-run           只报告 clean 会做什么，不实际改动系统");
        Output.Line("    --watch[=<秒>]      持续采样输出，而不是只打印一次");
        Output.Line("    --json              输出机器可读的 JSON");
        Output.Line("    --quiet             只输出释放的字节数（便于脚本调用）");
        Output.Line("    -h, --help          显示本帮助");
        Output.Line();
        Output.LineColored("  示例", ConsoleColor.White);
        Output.Line("    memreduct status");
        Output.Line("    memreduct status --watch=2");
        Output.Line("    memreduct clean --areas=workingset,standby");
        Output.Line("    memreduct clean --dry-run --json");
        Output.Line();
        Output.LineColored("  权限说明：系统文件缓存、页面列表和注册表重整", ConsoleColor.DarkGray);
        Output.LineColored("  都需要以管理员身份（右键“以管理员身份运行”）启动本程序。", ConsoleColor.DarkGray);
        Output.Line();
    }

    // ------------------------------------------------------------------ options

    private sealed class Options
    {
        internal bool Json { get; private set; }

        internal bool Quiet { get; private set; }

        internal bool DryRun { get; private set; }

        internal bool Help { get; private set; }

        internal double? WatchSeconds { get; private set; }

        internal CleanArea Areas { get; private set; } = CleanAreas.Default;

        internal string? Error { get; private set; }

        internal static Options Parse(IEnumerable<string> args)
        {
            var options = new Options();

            foreach (var raw in args)
            {
                var arg = raw.Trim();
                if (arg.Length == 0)
                {
                    continue;
                }

                if (!arg.StartsWith('-'))
                {
                    // Bare positional: treat as an area list for convenience.
                    if (!CleanerRegistry.TryParseAreas(arg, out var positional, out var positionalError))
                    {
                        options.Error ??= positionalError;
                    }
                    else
                    {
                        options.Areas = positional;
                    }

                    continue;
                }

                var (name, value) = SplitOption(arg);
                switch (name)
                {
                    case "json":
                        options.Json = true;
                        break;
                    case "quiet":
                    case "q":
                        options.Quiet = true;
                        break;
                    case "dry-run":
                    case "dryrun":
                    case "n":
                        options.DryRun = true;
                        break;
                    case "help":
                    case "h":
                    case "?":
                        options.Help = true;
                        break;
                    case "areas":
                    case "area":
                    case "a":
                        if (!CleanerRegistry.TryParseAreas(value, out var areas, out var error))
                        {
                            options.Error ??= error;
                        }
                        else
                        {
                            options.Areas = areas;
                        }

                        break;
                    case "watch":
                    case "w":
                        options.WatchSeconds = value is null ? 2.0 : double.TryParse(value, out var seconds) ? seconds : 2.0;
                        break;
                    default:
                        options.Error ??= $"Unknown option '{name}'.";
                        break;
                }
            }

            return options;
        }

        private static (string Name, string? Value) SplitOption(string arg)
        {
            var trimmed = arg.TrimStart('-');
            var separator = trimmed.IndexOf('=');
            if (separator < 0)
            {
                return (trimmed.ToLowerInvariant(), null);
            }

            return (trimmed[..separator].ToLowerInvariant(), trimmed[(separator + 1)..]);
        }
    }
}
