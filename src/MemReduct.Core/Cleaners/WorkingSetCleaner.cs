using System.Diagnostics;
using MemReduct.Core.Interop;

namespace MemReduct.Core.Cleaners;

/// <summary>
/// Trims the working set of every accessible process with <c>EmptyWorkingSet</c>.
/// <para>
/// This does not destroy data: it asks each process's memory manager to move its
/// resident pages out to the standby list, from where they are faulted back in on
/// demand. The cost is a short burst of soft page faults the next time each trimmed
/// process touches its own memory; the benefit is that pages a process has not
/// touched in a long time stop occupying the active working set.
/// </para>
/// </summary>
public sealed class WorkingSetCleaner : IMemoryCleaner
{
    public CleanArea Area => CleanArea.WorkingSet;

    public string DisplayName => "工作集";

    public string Description => "收缩所有可访问进程的常驻工作集（EmptyWorkingSet）。";

    // Processes owned by other users need SeDebugPrivilege, which needs elevation.
    // Processes owned by the current user are trimmable unelevated.
    public bool RequiresElevation => false;

    public CleanOutcome Clean(CleanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stopwatch = Stopwatch.StartNew();

        // SeDebugPrivilege widens OpenProcess coverage; failing to get it is not fatal.
        PrivilegeEnabler.Enable(PrivilegeEnabler.SeDebugPrivilege);

        var self = Environment.ProcessId;
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            return new CleanOutcome(
                Area,
                Success: false,
                ReleasedBytes: 0,
                Message: $"无法枚举进程：{ex.Message}",
                Duration: stopwatch.Elapsed);
        }

        var trimmed = 0;
        var skipped = 0;
        var failed = 0;
        long workingSetBefore = 0;
        long workingSetAfter = 0;
        long largestTrim = 0;

        foreach (var process in processes)
        {
            var pid = 0;
            try
            {
                pid = process.Id;
                string name;
                try
                {
                    name = process.ProcessName;
                }
                catch
                {
                    name = string.Empty;
                }

                if (pid == self && !context.IncludeSelf)
                {
                    skipped++;
                    continue;
                }

                if (pid <= 4 || context.ProcessDenyList.Contains(name))
                {
                    skipped++;
                    continue;
                }

                long before = 0;
                try
                {
                    before = process.WorkingSet64;
                }
                catch
                {
                    // Protected or exiting process; the native call below decides.
                }

                if (context.DryRun)
                {
                    workingSetBefore += before;
                    continue;
                }

                var handle = NativeMethods.OpenProcess(ProcessAccess.ForEmptyWorkingSet, false, pid);
                if (handle == IntPtr.Zero)
                {
                    // Retry with the narrower query right, which some sandboxed
                    // processes grant while denying PROCESS_QUERY_INFORMATION.
                    handle = NativeMethods.OpenProcess(
                        ProcessAccess.PROCESS_QUERY_LIMITED_INFORMATION | ProcessAccess.PROCESS_SET_QUOTA,
                        false,
                        pid);
                }

                if (handle == IntPtr.Zero)
                {
                    failed++;
                    continue;
                }

                try
                {
                    if (NativeMethods.EmptyWorkingSet(handle))
                    {
                        trimmed++;

                        long after = 0;
                        try
                        {
                            process.Refresh();
                            after = process.WorkingSet64;
                        }
                        catch
                        {
                            // Exited mid-flight.
                        }

                        workingSetBefore += before;
                        workingSetAfter += after;
                        if (before - after > largestTrim)
                        {
                            largestTrim = before - after;
                        }
                    }
                    else
                    {
                        failed++;
                    }
                }
                finally
                {
                    NativeMethods.CloseHandle(handle);
                }
            }
            catch
            {
                failed++;
            }
            finally
            {
                process.Dispose();
            }
        }

        stopwatch.Stop();

        var reclaimed = workingSetBefore - workingSetAfter;
        var message = context.DryRun
            ? $"试运行：将收缩 {processes.Length - skipped} 个进程" +
              $"（工作集合计 {ByteSize.Format(workingSetBefore)}）。"
            : $"已收缩 {trimmed}/{trimmed + failed} 个进程，回收工作集 {ByteSize.Format(reclaimed)}" +
              (largestTrim > 0 ? $"（单进程最多 {ByteSize.Format(largestTrim)}）。" : "。");

        // A dry run that enumerated successfully succeeded. A real run succeeds as long as it
        // trimmed something; individual refusals are expected (protected processes) and are
        // reported through FailedProcesses rather than by failing the whole area.
        var success = context.DryRun || trimmed > 0 || failed == 0;

        return new CleanOutcome(
            Area,
            Success: success,
            ReleasedBytes: reclaimed,
            Message: message,
            AffectedProcesses: trimmed,
            FailedProcesses: failed,
            Duration: stopwatch.Elapsed);
    }
}
