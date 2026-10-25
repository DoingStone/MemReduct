using System.Diagnostics;
using System.Runtime.InteropServices;
using MemReduct.Core.Interop;

namespace MemReduct.Core.Cleaners;

/// <summary>
/// Drives the kernel's page-list commands through
/// <c>NtSetSystemInformation(SystemMemoryListInformation, …)</c>.
/// <para>Three distinct lists can be acted on, which is why this one class backs three areas:</para>
/// <list type="bullet">
///   <item><b>Standby list</b> (command 4) — cached pages kept in case they are needed again.
///     Purging returns them to the free list, so available memory rises.</item>
///   <item><b>Modified page list</b> (command 3) — dirty pages waiting to be written to disk.
///     Flushing writes them now instead of under pressure later.</item>
///   <item><b>Combined</b> (commands 3 → 5 → 4) — flush first, then take the low-priority
///     standby pages, then the rest. This is the ordering RAMMap's "Empty Standby List"
///     effectively performs and yields the largest single reclaim.</item>
/// </list>
/// <para>Requires <c>SeProfileSingleProcessPrivilege</c> (elevated).</para>
/// </summary>
public sealed class MemoryListCleaner : IMemoryCleaner
{
    private readonly CleanArea _area;
    private readonly SystemMemoryListCommand[] _commands;

    private MemoryListCleaner(CleanArea area, string displayName, string description, params SystemMemoryListCommand[] commands)
    {
        _area = area;
        _commands = commands;
        DisplayName = displayName;
        Description = description;
    }

    public static MemoryListCleaner StandbyList { get; } = new(
        CleanArea.StandbyList,
        "备用列表",
        "将缓存的备用页面释放回空闲列表（类别 80，命令 4）。",
        SystemMemoryListCommand.MemoryPurgeStandbyList);

    public static MemoryListCleaner ModifiedPageList { get; } = new(
        CleanArea.ModifiedPageList,
        "已修改页列表",
        "立即将脏页写入磁盘，而不是等到出现内存压力时（类别 80，命令 3）。",
        SystemMemoryListCommand.MemoryFlushModifiedList);

    public static MemoryListCleaner CombinedMemoryList { get; } = new(
        CleanArea.CombinedMemoryList,
        "组合页列表",
        "先刷新已修改页，再清除低优先级和普通备用页（类别 80，命令 3、5、4）。",
        SystemMemoryListCommand.MemoryFlushModifiedList,
        SystemMemoryListCommand.MemoryPurgeLowPriorityStandbyList,
        SystemMemoryListCommand.MemoryPurgeStandbyList);

    public CleanArea Area => _area;

    public string DisplayName { get; }

    public string Description { get; }

    public bool RequiresElevation => true;

    public CleanOutcome Clean(CleanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stopwatch = Stopwatch.StartNew();

        var privilege = PrivilegeEnabler.Enable(PrivilegeEnabler.SeProfileSingleProcessPrivilege);
        if (privilege != PrivilegeState.Enabled)
        {
            stopwatch.Stop();
            return new CleanOutcome(
                _area,
                Success: false,
                ReleasedBytes: 0,
                Message: $"SeProfileSingleProcessPrivilege 不可用（{privilege}）；请以管理员身份运行。",
                Duration: stopwatch.Elapsed);
        }

        if (context.DryRun)
        {
            stopwatch.Stop();
            return new CleanOutcome(
                _area,
                Success: true,
                ReleasedBytes: 0,
                Message: $"试运行：将执行 {CommandList()}。",
                Duration: stopwatch.Elapsed);
        }

        var lastStatus = 0;
        var applied = 0;
        var refused = new List<string>();

        foreach (var command in _commands)
        {
            var status = Issue(command);
            lastStatus = status;

            if (CleanOutcome.IsNtSuccess(status))
            {
                applied++;
            }
            else
            {
                refused.Add($"{Describe(command)}={CleanOutcome.DescribeNtStatus(status)}");
            }
        }

        stopwatch.Stop();

        var success = applied == _commands.Length;
        var message = success
            ? $"已执行 {CommandList()}。"
            : $"部分完成：已执行 {applied}/{_commands.Length} 条命令（{string.Join("，", refused)}）。";

        return new CleanOutcome(
            _area,
            Success: success,
            ReleasedBytes: 0, // measured by the orchestrator from the page lists
            Message: message,
            NtStatus: lastStatus,
            Duration: stopwatch.Elapsed);
    }

    /// <summary>
    /// Chinese name for a page-list command. Without this the messages would leak enum
    /// identifiers such as <c>MemoryPurgeStandbyList</c> into user-facing output.
    /// </summary>
    private static string Describe(SystemMemoryListCommand command) => command switch
    {
        SystemMemoryListCommand.MemoryCaptureAccessedBits => "捕获访问位",
        SystemMemoryListCommand.MemoryCaptureAndResetAccessedBits => "捕获并重置访问位",
        SystemMemoryListCommand.MemoryEmptyWorkingSets => "清空工作集",
        SystemMemoryListCommand.MemoryFlushModifiedList => "刷新已修改页",
        SystemMemoryListCommand.MemoryPurgeLowPriorityStandbyList => "清除低优先级备用页",
        SystemMemoryListCommand.MemoryPurgeStandbyList => "清除备用列表",
        _ => command.ToString(),
    };

    /// <summary>The configured command sequence, rendered for a message.</summary>
    private string CommandList() => string.Join(" → ", _commands.Select(Describe));

    private static int Issue(SystemMemoryListCommand command)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(buffer, (int)command);
            return NativeMethods.NtSetSystemInformation(
                SystemInformationClass.SystemMemoryListInformation,
                buffer,
                sizeof(int));
        }
        catch
        {
            return unchecked((int)0xC0000002); // STATUS_NOT_IMPLEMENTED
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
