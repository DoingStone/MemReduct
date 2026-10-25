namespace MemReduct.Core.Cleaners;

/// <summary>The result of one full clean run.</summary>
/// <param name="Requested">The areas that were asked for.</param>
/// <param name="Before">Snapshot taken immediately before the first cleaner ran.</param>
/// <param name="After">Snapshot taken immediately after the last cleaner ran.</param>
/// <param name="Outcomes">Per-area results, in execution order.</param>
/// <param name="Duration">Wall-clock time for the whole run.</param>
/// <param name="DryRun">True when no kernel call was actually issued.</param>
public sealed record CleanReport(
    CleanArea Requested,
    MemorySnapshot Before,
    MemorySnapshot After,
    IReadOnlyList<CleanOutcome> Outcomes,
    TimeSpan Duration,
    bool DryRun)
{
    /// <summary>
    /// Bytes released across all areas, each counted against the structure it targets. This is
    /// the headline figure: it is additive across areas in a way that available-memory deltas
    /// are not.
    /// </summary>
    public long TotalReleasedBytes => Outcomes.Sum(outcome => outcome.ReleasedBytes);

    /// <summary>
    /// Change in available physical memory (<c>free + standby</c>) across the whole run.
    /// Reported alongside <see cref="TotalReleasedBytes"/> rather than instead of it: a
    /// standby purge moves cached pages into the free list and so barely moves this number.
    /// Can be negative under concurrent load.
    /// </summary>
    public long TotalAvailableDelta => After.AvailableDeltaFrom(Before);

    /// <summary>Change in the standby page list across the run (negative means cache was released).</summary>
    public long StandbyDelta => (After.PageLists, Before.PageLists) is (PageListInfo a, PageListInfo b)
        ? PageListInfo.Delta(a.StandbyBytes, b.StandbyBytes)
        : 0;

    /// <summary>Change in the modified page list across the run.</summary>
    public long ModifiedDelta => (After.PageLists, Before.PageLists) is (PageListInfo a, PageListInfo b)
        ? PageListInfo.Delta(a.ModifiedBytes, b.ModifiedBytes)
        : 0;

    /// <summary>Change in the system file cache working set across the run.</summary>
    public long SystemCacheDelta => (After.PageLists, Before.PageLists) is (PageListInfo a, PageListInfo b)
        ? PageListInfo.Delta(a.CacheBytes, b.CacheBytes)
        : (long)After.SystemCache - (long)Before.SystemCache;

    /// <summary>Change in the page file in use across the whole run.</summary>
    public long UsedPageFileDelta => (long)After.UsedPageFile - (long)Before.UsedPageFile;

    /// <summary>True when every cleaner that ran reported success.</summary>
    public bool Success => Outcomes.Count > 0 && Outcomes.All(outcome => outcome.Success);

    /// <summary>Number of areas that reported success.</summary>
    public int SucceededCount => Outcomes.Count(outcome => outcome.Success);

    /// <summary>True when the page lists were readable, so per-structure figures are meaningful.</summary>
    public bool PageListsAvailable => Before.PageLists is not null && After.PageLists is not null;

    /// <summary>One-line summary suitable for a tray balloon or a log line.</summary>
    public string Summary
    {
        get
        {
            if (Outcomes.Count == 0)
            {
                return "无事可做——未选择任何清理区域。";
            }

            if (DryRun)
            {
                return $"试运行——已检查 {Outcomes.Count} 个区域，未发出任何内核调用" +
                       $"（{ByteSize.FormatDuration(Duration)}）。";
            }

            return $"共释放 {ByteSize.Format(TotalReleasedBytes)}，" +
                   $"{SucceededCount}/{Outcomes.Count} 个区域成功，" +
                   $"用时 {ByteSize.FormatDuration(Duration)}" +
                   $"（可用内存 {ByteSize.FormatDelta(TotalAvailableDelta)}，" +
                   $"负载 {Before.LoadPercent:F0}% → {After.LoadPercent:F0}%）。";
        }
    }
}
