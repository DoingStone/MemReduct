namespace MemReduct.Core;

/// <summary>
/// An immutable point-in-time view of system memory, merged from
/// <c>GlobalMemoryStatusEx</c> and <c>GetPerformanceInfo</c>.
/// All sizes are bytes.
/// </summary>
public sealed record MemorySnapshot(
    ulong TotalPhysical,
    ulong AvailablePhysical,
    uint MemoryLoadPercent,
    ulong TotalPageFile,
    ulong AvailablePageFile,
    ulong TotalVirtual,
    ulong AvailableVirtual,
    ulong CommitTotal,
    ulong CommitLimit,
    ulong CommitPeak,
    ulong SystemCache,
    ulong KernelPaged,
    ulong KernelNonpaged,
    ulong PageSize,
    uint HandleCount,
    uint ProcessCount,
    uint ThreadCount,
    DateTimeOffset Timestamp)
{
    /// <summary>
    /// Per-page-list breakdown, when PDH could supply it. Null on systems where the
    /// <c>Memory</c> counter set is unavailable.
    /// </summary>
    public PageListInfo? PageLists { get; init; }

    /// <summary>Physical memory in use (total minus available).</summary>
    public ulong UsedPhysical => TotalPhysical > AvailablePhysical ? TotalPhysical - AvailablePhysical : 0;

    /// <summary>Page file bytes in use.</summary>
    public ulong UsedPageFile => TotalPageFile > AvailablePageFile ? TotalPageFile - AvailablePageFile : 0;

    /// <summary>Kernel paged + non-paged pool.</summary>
    public ulong KernelTotal => KernelPaged + KernelNonpaged;

    /// <summary>Commit charge as a percentage of the commit limit.</summary>
    public double CommitLoadPercent => CommitLimit == 0 ? 0 : (double)CommitTotal / CommitLimit * 100.0;

    /// <summary>Physical memory pressure as a percentage of installed RAM.</summary>
    public double LoadPercent => TotalPhysical == 0 ? 0 : (double)UsedPhysical / TotalPhysical * 100.0;

    /// <summary>Difference in available physical memory between two snapshots.</summary>
    public long AvailableDeltaFrom(MemorySnapshot earlier) =>
        (long)AvailablePhysical - (long)earlier.AvailablePhysical;
}
