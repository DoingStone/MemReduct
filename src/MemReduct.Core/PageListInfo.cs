namespace MemReduct.Core;

/// <summary>
/// A point-in-time view of the Windows page lists, read from the PDH <c>Memory</c> counter set.
/// <para>
/// Windows accounts for physical pages in four buckets, and knowing which one memory sits in
/// is what makes a cleaner's effect legible:
/// </para>
/// <list type="bullet">
///   <item><b>Free</b> — zeroed or free pages, immediately allocatable.</item>
///   <item><b>Standby</b> — pages holding valid cached data that were evicted from a working
///     set. They are reclaimed instantly for a new allocation, but until then they serve as
///     cache. This is the largest bucket on a busy machine.</item>
///   <item><b>Modified</b> — dirty pages queued to be written to disk before reuse.</item>
///   <item><b>Active</b> — everything else: the working sets themselves.</item>
/// </list>
/// <para>
/// <c>Free + Standby</c> is what Task Manager and <c>GlobalMemoryStatusEx</c> call "available".
/// </para>
/// </summary>
/// <param name="FreeBytes">Free &amp; zero page list.</param>
/// <param name="StandbyCoreBytes">Standby pages that must be retained (never purged).</param>
/// <param name="StandbyNormalBytes">Standby pages reclaimable before touching lower priorities.</param>
/// <param name="StandbyReserveBytes">Standby pages used as a reserve for low-priority allocations.</param>
/// <param name="ModifiedBytes">Modified page list (dirty pages awaiting write-back).</param>
/// <param name="CacheBytes">Total system cache working set.</param>
/// <param name="Timestamp">When the sample was taken.</param>
public sealed record PageListInfo(
    long FreeBytes,
    long StandbyCoreBytes,
    long StandbyNormalBytes,
    long StandbyReserveBytes,
    long ModifiedBytes,
    long CacheBytes,
    DateTimeOffset Timestamp)
{
    /// <summary>All standby pages, across every priority band.</summary>
    public long StandbyBytes => StandbyCoreBytes + StandbyNormalBytes + StandbyReserveBytes;

    /// <summary>Free + standby — the value Windows reports as "available" memory.</summary>
    public long AvailableBytes => FreeBytes + StandbyBytes;

    /// <summary>Standby pages a purge may legitimately release (core standby is never purged).</summary>
    public long PurgeableStandbyBytes => StandbyNormalBytes + StandbyReserveBytes;

    /// <summary>Signed change in a metric between two samples.</summary>
    public static long Delta(long after, long before) => after - before;
}
