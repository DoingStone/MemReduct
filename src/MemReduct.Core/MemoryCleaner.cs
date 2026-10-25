using System.Diagnostics;
using MemReduct.Core.Cleaners;

namespace MemReduct.Core;

/// <summary>
/// Orchestrates a clean run: takes before/after snapshots, resolves the requested areas to
/// cleaners, and measures what each one actually reclaimed.
/// </summary>
/// <remarks>
/// <para>
/// Accounting is per structure, not per aggregate. A cleaner that can measure its own effect
/// precisely (trimming working sets) reports that number directly. The kernel page-list
/// operations cannot observe themselves, so the orchestrator samples the PDH page lists on
/// both sides of the call and attributes the change in the list that operation targets.
/// </para>
/// <para>
/// The aggregate "available memory" delta is recorded separately and never used as the
/// headline figure, because it cannot represent a standby purge: Windows counts free and
/// standby pages alike as available, so draining one into the other leaves the number
/// unchanged even when gigabytes of cache were released. Both figures are kept so neither
/// hides the other.
/// </para>
/// </remarks>
public sealed class MemoryCleaner
{
    /// <summary>Cleaners available to this instance, in execution order.</summary>
    public IReadOnlyList<IMemoryCleaner> Cleaners { get; }

    /// <summary>Creates an orchestrator over the default cleaner set.</summary>
    public MemoryCleaner()
        : this(CleanerRegistry.All)
    {
    }

    /// <summary>Creates an orchestrator over an explicit cleaner set (used by tests).</summary>
    public MemoryCleaner(IReadOnlyList<IMemoryCleaner> cleaners)
    {
        Cleaners = cleaners ?? throw new ArgumentNullException(nameof(cleaners));
    }

    /// <summary>Captures the current memory state.</summary>
    public static MemorySnapshot GetSnapshot() => MemoryInfoProvider.GetSnapshot();

    /// <summary>
    /// Builds a snapshot that reuses an already-open page-list query, so a run does not pay a
    /// query open per sample.
    /// </summary>
    private static MemorySnapshot TakeSnapshot(PageListReader? pageLists) =>
        MemoryInfoProvider.GetSnapshot(includePageLists: false) with { PageLists = pageLists?.TryRead() };

    /// <summary>Runs the requested areas synchronously.</summary>
    /// <param name="areas">Areas to clean. Defaults to <see cref="CleanAreas.Default"/>.</param>
    /// <param name="context">Run options.</param>
    /// <param name="progress">Optional per-area progress sink, invoked as each cleaner finishes.</param>
    public CleanReport Clean(CleanArea areas = CleanAreas.Default, CleanContext? context = null, IProgress<CleanOutcome>? progress = null)
    {
        context ??= CleanContext.Default;

        var selected = Cleaners.Where(cleaner => areas.HasFlag(cleaner.Area)).ToArray();
        var runStopwatch = Stopwatch.StartNew();

        // One open PDH query for the whole run: page-list samples then cost a single collect.
        using var pageLists = PageListReader.TryCreate();

        var before = TakeSnapshot(pageLists);
        var outcomes = new List<CleanOutcome>(selected.Length);

        foreach (var cleaner in selected)
        {
            var listsBefore = pageLists?.TryRead();
            var availableBefore = MemoryInfoProvider.GetAvailablePhysical();

            CleanOutcome outcome;
            try
            {
                outcome = cleaner.Clean(context);
            }
            catch (Exception ex)
            {
                // A cleaner must never take the whole run down with it.
                outcome = new CleanOutcome(
                    cleaner.Area,
                    Success: false,
                    ReleasedBytes: 0,
                    Message: $"{cleaner.DisplayName} failed: {ex.Message}");
            }

            var listsAfter = pageLists?.TryRead();
            var availableAfter = MemoryInfoProvider.GetAvailablePhysical();
            var measurement = CleanMeasurement.Between(
                listsBefore,
                listsAfter,
                (long)availableBefore,
                (long)availableAfter);

            // In a dry run nothing is issued, so any drift is other processes' doing and must
            // not be attributed to us. Otherwise a self-measuring cleaner keeps its own figure
            // and the rest are credited with the structure they drained.
            var released = context.DryRun || outcome.ReleasedBytes != 0
                ? outcome.ReleasedBytes
                : measurement.ReleasedFor(cleaner.Area);

            outcome = outcome with { ReleasedBytes = released, Measurement = measurement };

            outcomes.Add(outcome);
            progress?.Report(outcome);
        }

        var after = TakeSnapshot(pageLists);
        runStopwatch.Stop();

        return new CleanReport(areas, before, after, outcomes, runStopwatch.Elapsed, context.DryRun);
    }

    /// <summary>Runs the requested areas on a background thread.</summary>
    public Task<CleanReport> CleanAsync(
        CleanArea areas = CleanAreas.Default,
        CleanContext? context = null,
        IProgress<CleanOutcome>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Clean(areas, context, progress);
        }, cancellationToken);
    }

    /// <summary>Runs every cleaner with no side effects and reports what each one would do.</summary>
    public CleanReport DryRun(CleanArea areas = CleanAreas.All) =>
        Clean(areas, new CleanContext { DryRun = true });
}
