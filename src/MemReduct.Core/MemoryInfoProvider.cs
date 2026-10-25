using System.Runtime.InteropServices;
using MemReduct.Core.Interop;

namespace MemReduct.Core;

/// <summary>
/// Reads system memory counters. Combines <c>GlobalMemoryStatusEx</c> (which is what
/// Task Manager's headline numbers come from) with <c>GetPerformanceInfo</c> (which adds
/// commit charge, kernel pool sizes and the system cache).
/// </summary>
public static class MemoryInfoProvider
{
    private const ulong FallbackPageSize = 4096;

    /// <summary>Captures the current memory state. Throws <see cref="System.ComponentModel.Win32Exception"/> if the OS refuses.</summary>
    /// <param name="includePageLists">
    /// Also read the PDH page-list counters. Costs a query open/collect, so pass false for
    /// tight sampling loops that only need the headline numbers.
    /// </param>
    public static MemorySnapshot GetSnapshot(bool includePageLists = true)
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
        {
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                "GlobalMemoryStatusEx failed.");
        }

        ulong commitTotal = 0, commitLimit = 0, commitPeak = 0;
        ulong systemCache = 0, kernelPaged = 0, kernelNonpaged = 0;
        uint handleCount = 0, processCount = 0, threadCount = 0;
        var pageSize = FallbackPageSize;

        var perf = new PERFORMANCE_INFORMATION
        {
            cb = (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>(),
        };

        if (NativeMethods.GetPerformanceInfo(out perf, perf.cb))
        {
            if (perf.PageSize > 0)
            {
                pageSize = perf.PageSize;
            }

            // Every SIZE_T in PERFORMANCE_INFORMATION is a page count.
            commitTotal = (ulong)perf.CommitTotal * pageSize;
            commitLimit = (ulong)perf.CommitLimit * pageSize;
            commitPeak = (ulong)perf.CommitPeak * pageSize;
            systemCache = (ulong)perf.SystemCache * pageSize;
            kernelPaged = (ulong)perf.KernelPaged * pageSize;
            kernelNonpaged = (ulong)perf.KernelNonpaged * pageSize;
            handleCount = perf.HandleCount;
            processCount = perf.ProcessCount;
            threadCount = perf.ThreadCount;
        }

        var snapshot = new MemorySnapshot(
            TotalPhysical: status.ullTotalPhys,
            AvailablePhysical: status.ullAvailPhys,
            MemoryLoadPercent: status.dwMemoryLoad,
            TotalPageFile: status.ullTotalPageFile,
            AvailablePageFile: status.ullAvailPageFile,
            TotalVirtual: status.ullTotalVirtual,
            AvailableVirtual: status.ullAvailVirtual,
            CommitTotal: commitTotal,
            CommitLimit: commitLimit,
            CommitPeak: commitPeak,
            SystemCache: systemCache,
            KernelPaged: kernelPaged,
            KernelNonpaged: kernelNonpaged,
            PageSize: pageSize,
            HandleCount: handleCount,
            ProcessCount: processCount,
            ThreadCount: threadCount,
            Timestamp: DateTimeOffset.Now);

        // The page lists come from a different source (PDH) that can legitimately be absent,
        // so they are attached rather than required. Callers that sample in a tight loop pass
        // includePageLists: false and reuse one open query instead.
        return includePageLists
            ? snapshot with { PageLists = PageListReader.ReadOnce() }
            : snapshot;
    }

    /// <summary>
    /// Convenience wrapper that reports only available physical memory, used for
    /// before/after measurement around a cleaner.
    /// </summary>
    public static ulong GetAvailablePhysical()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return NativeMethods.GlobalMemoryStatusEx(ref status) ? status.ullAvailPhys : 0;
    }
}
