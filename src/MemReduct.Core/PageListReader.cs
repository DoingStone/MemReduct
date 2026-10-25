using System.Runtime.InteropServices;
using MemReduct.Core.Interop;

namespace MemReduct.Core;

/// <summary>
/// Reads the <c>Memory</c> PDH counter set into a <see cref="PageListInfo"/>.
/// <para>
/// Holds an open PDH query so repeated samples are cheap — useful for the GUI's live view and
/// for taking before/after measurements around each cleaner. Counter paths are registered by
/// their English names, so this works on localised Windows installations too.
/// </para>
/// <para>
/// PDH is best-effort here: if the counter set is unavailable (a stripped-down or
/// counter-disabled image) <see cref="TryRead"/> returns <c>null</c> and callers fall back to
/// the aggregate counters from <c>GetPerformanceInfo</c>. Nothing about cleaning depends on it.
/// </para>
/// </summary>
public sealed class PageListReader : IDisposable
{
    private const string Category = "\\Memory\\";

    private readonly List<(string Name, IntPtr Handle)> _counters = [];
    private IntPtr _query;
    private bool _disposed;

    // Six readings, refilled on every collection. The reader is long-lived (the GUI keeps one
    // open for the life of its window), so this is allocated once rather than once per tick.
    private readonly long[] _values = new long[6];

    private PageListReader(IntPtr query)
    {
        _query = query;
    }

    /// <summary>
    /// Opens a query over the page-list counters, or returns <c>null</c> when PDH cannot
    /// provide them.
    /// </summary>
    public static PageListReader? TryCreate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (Pdh.PdhOpenQuery(null, IntPtr.Zero, out var query) != Pdh.ERROR_SUCCESS || query == IntPtr.Zero)
        {
            return null;
        }

        var reader = new PageListReader(query);
        string[] names =
        [
            "Free & Zero Page List Bytes",
            "Standby Cache Core Bytes",
            "Standby Cache Normal Priority Bytes",
            "Standby Cache Reserve Bytes",
            "Modified Page List Bytes",
            "Cache Bytes",
        ];

        foreach (var name in names)
        {
            if (Pdh.PdhAddEnglishCounter(query, Category + name, IntPtr.Zero, out var counter) != Pdh.ERROR_SUCCESS)
            {
                // A missing counter makes the whole page-list view unreliable; give up rather
                // than report a partially-zeroed picture.
                reader.Dispose();
                return null;
            }

            reader._counters.Add((name, counter));
        }

        // Some counters report nothing until the query has been collected once.
        Pdh.PdhCollectQueryData(query);

        return reader;
    }

    /// <summary>Reads a fresh sample, or <c>null</c> if the query failed or is disposed.</summary>
    public PageListInfo? TryRead()
    {
        if (_disposed || _query == IntPtr.Zero)
        {
            return null;
        }

        if (Pdh.PdhCollectQueryData(_query) != Pdh.ERROR_SUCCESS)
        {
            return null;
        }

        // The readings go into the reader's own buffer: the same six counters are read every
        // tick, and allocating an array to hold them once per second for the life of the session
        // was pure garbage.
        var values = _values;
        for (var i = 0; i < _counters.Count; i++)
        {
            if (Pdh.PdhGetFormattedCounterValue(_counters[i].Handle, Pdh.PDH_FMT_LARGE, out _, out var value) != Pdh.ERROR_SUCCESS)
            {
                return null;
            }

            values[i] = value.LargeValue;
        }

        return new PageListInfo(
            FreeBytes: values[0],
            StandbyCoreBytes: values[1],
            StandbyNormalBytes: values[2],
            StandbyReserveBytes: values[3],
            ModifiedBytes: values[4],
            CacheBytes: values[5],
            Timestamp: DateTimeOffset.Now);
    }

    /// <summary>Convenience one-shot read that opens and closes a query.</summary>
    public static PageListInfo? ReadOnce()
    {
        using var reader = TryCreate();
        return reader?.TryRead();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _counters.Clear();

        if (_query != IntPtr.Zero)
        {
            Pdh.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }
}
