namespace MemReduct.Core.Cleaners;

/// <summary>
/// Everything observed around one cleaner call, so an operation's effect can be attributed to
/// the structure it actually targeted rather than to one aggregate number.
/// <para>
/// This exists because "available memory" is a poor universal yardstick. Windows counts
/// <c>free + standby</c> as available, so purging the standby list moves pages between two
/// categories that are both "available" and the headline number does not budge — even though
/// several gigabytes of cache were genuinely released. Each area therefore reports the
/// structure it drained.
/// </para>
/// </summary>
/// <param name="PageListsAvailable">False when PDH could not supply the page lists; deltas are then zero.</param>
/// <param name="AvailableDelta">Change in available physical memory (<c>free + standby</c>).</param>
/// <param name="FreeDelta">Change in the free/zero page list.</param>
/// <param name="StandbyDelta">Change in total standby bytes (negative == released cache).</param>
/// <param name="ModifiedDelta">Change in the modified page list (negative == drained to disk).</param>
/// <param name="CacheDelta">Change in the system cache working set (negative == trimmed).</param>
public sealed record CleanMeasurement(
    bool PageListsAvailable,
    long AvailableDelta,
    long FreeDelta,
    long StandbyDelta,
    long ModifiedDelta,
    long CacheDelta)
{
    /// <summary>A measurement with no page-list support, used before the first sample.</summary>
    public static CleanMeasurement Unavailable { get; } = new(false, 0, 0, 0, 0, 0);

    /// <summary>
    /// Bytes genuinely released from the structure this area targets. Negative changes are
    /// clamped to zero so unrelated system activity is never reported as reclamation.
    /// </summary>
    public long ReleasedFor(CleanArea area) => area switch
    {
        // The working-set cleaner measures its own effect precisely (sum of trimmed working
        // sets) and reports it directly, so no derived figure is needed here.
        CleanArea.WorkingSet => 0,

        // Trimming the file cache shrinks the cache working set.
        CleanArea.SystemFileCache => Math.Max(0, -CacheDelta),

        // Purging standby drains the standby list into the free list.
        CleanArea.StandbyList => Math.Max(0, -StandbyDelta),

        // Flushing writes dirty pages out, draining the modified list.
        CleanArea.ModifiedPageList => Math.Max(0, -ModifiedDelta),

        // Both lists, since the combined command touches both.
        CleanArea.CombinedMemoryList => Math.Max(0, -(StandbyDelta + ModifiedDelta)),

        // Hive cache pages are not individually visible; available memory is the only signal.
        CleanArea.RegistryCache => Math.Max(0, AvailableDelta),

        _ => Math.Max(0, AvailableDelta),
    };

    /// <summary>Captures a measurement between two page-list samples plus an available-memory pair.</summary>
    public static CleanMeasurement Between(
        PageListInfo? before,
        PageListInfo? after,
        long availableBefore,
        long availableAfter)
    {
        if (before is null || after is null)
        {
            return Unavailable with { AvailableDelta = availableAfter - availableBefore };
        }

        return new CleanMeasurement(
            PageListsAvailable: true,
            AvailableDelta: availableAfter - availableBefore,
            FreeDelta: PageListInfo.Delta(after.FreeBytes, before.FreeBytes),
            StandbyDelta: PageListInfo.Delta(after.StandbyBytes, before.StandbyBytes),
            ModifiedDelta: PageListInfo.Delta(after.ModifiedBytes, before.ModifiedBytes),
            CacheDelta: PageListInfo.Delta(after.CacheBytes, before.CacheBytes));
    }
}
