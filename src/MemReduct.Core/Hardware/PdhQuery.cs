using System.Runtime.InteropServices;
using MemReduct.Core.Interop;

namespace MemReduct.Core.Hardware;

/// <summary>
/// An open PDH query over a fixed list of counters, kept alive so repeated samples do not pay
/// for re-opening the query.
/// <para>
/// Rate counters (the <c>% …</c> ones) are meaningless from a single collect — the first
/// collection only seeds the two samples PDH subtracts. Callers therefore collect once, wait out
/// the interval they want to measure, then read; <see cref="Collect"/> and the read methods are
/// deliberately separate so that waiting can happen on the caller's own schedule.
/// </para>
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private readonly List<(string Path, IntPtr Handle)> _counters = [];
    private IntPtr _query;
    private bool _disposed;

    // PdhGetFormattedCounterArray needs a caller-owned block, and the wildcarded GPU counters
    // hold one item plus a full instance name for every process engine on every adapter — tens
    // of kilobytes that used to be allocated and freed on every single sample. The block is kept
    // and only ever grows: PDH reports the size it needs, so after the first couple of samples
    // this stops allocating altogether. Released in Dispose.
    private IntPtr _arrayBuffer;
    private uint _arrayBufferBytes;

    // The formatted-value arrays are reused for the same reason: a poller formats the same
    // handful of counters every tick, and a fresh array per tick is pure garbage. Held at the
    // largest size ever seen, so a query whose counter count cannot change settles after the
    // first call. Callers must consume them within the tick they asked for.
    private double[]? _doubleScratch;
    private long[]? _largeScratch;

    private PdhQuery(IntPtr query) => _query = query;

    /// <summary>
    /// Opens a query over <paramref name="paths"/>, or returns null when any of them is not
    /// installed. All-or-nothing is deliberate: a query that silently drops one counter would
    /// return a shorter array than the caller destructures, which is worse than no reading.
    /// </summary>
    public static PdhQuery? TryCreate(params string[] paths)
    {
        if (!OperatingSystem.IsWindows() || paths.Length == 0)
        {
            return null;
        }

        try
        {
            if (Pdh.PdhOpenQuery(null, IntPtr.Zero, out var query) != Pdh.ERROR_SUCCESS || query == IntPtr.Zero)
            {
                return null;
            }

            var wrapper = new PdhQuery(query);

            foreach (var path in paths)
            {
                // The English form resolves on localised Windows, where the display name of the
                // counter is translated but its stable name is not.
                if (Pdh.PdhAddEnglishCounter(query, path, IntPtr.Zero, out var counter) != Pdh.ERROR_SUCCESS ||
                    counter == IntPtr.Zero)
                {
                    wrapper.Dispose();
                    return null;
                }

                wrapper._counters.Add((path, counter));
            }

            return wrapper;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The native handle for one of the paths this query was opened with.</summary>
    public IntPtr HandleFor(string path)
    {
        foreach (var (candidate, handle) in _counters)
        {
            if (string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>True when the query is open and still owns at least one counter.</summary>
    public bool IsUsable => !_disposed && _query != IntPtr.Zero && _counters.Count > 0;

    /// <summary>Takes one collection pass over every counter in the query.</summary>
    public bool Collect() => IsUsable && Pdh.PdhCollectQueryData(_query) == Pdh.ERROR_SUCCESS;

    /// <summary>
    /// Formats every counter as a double, in the order the paths were given. Returns false when
    /// any counter is not ready — a partly-zeroed set would silently drag a sum down.
    /// <para>
    /// The array is owned by this query and overwritten by the next call, so a caller must copy
    /// out anything it keeps beyond the current tick. Polling loops read it and finish, which is
    /// what this reuses: the alternative was a fresh array of counter count, sixty times a minute.
    /// </para>
    /// </summary>
    public bool TryFormatValues(out double[] values)
    {
        values = [];

        if (!IsUsable)
        {
            return false;
        }

        var result = _doubleScratch;

        if (result is null || result.Length < _counters.Count)
        {
            result = new double[_counters.Count];
            _doubleScratch = result;
        }

        for (var i = 0; i < _counters.Count; i++)
        {
            if (TryFormatDouble(_counters[i].Handle, out var value))
            {
                result[i] = value;
            }
            else
            {
                return false;
            }
        }

        values = result;
        return true;
    }

    /// <summary>Formats every counter as a raw integer, for counters that are byte counts.</summary>
    public bool TryFormatLargeValues(out long[] values)
    {
        values = [];

        if (!IsUsable)
        {
            return false;
        }

        var result = _largeScratch;

        if (result is null || result.Length < _counters.Count)
        {
            result = new long[_counters.Count];
            _largeScratch = result;
        }

        for (var i = 0; i < _counters.Count; i++)
        {
            if (TryFormatLarge(_counters[i].Handle, out var value))
            {
                result[i] = value;
            }
            else
            {
                return false;
            }
        }

        values = result;
        return true;
    }

    /// <summary>
    /// Reads one counter as a double. PDH's payload is an eight-byte union, so the DOUBLE format
    /// fills those bytes directly and they are reinterpreted rather than converted.
    /// </summary>
    internal static bool TryFormatDouble(IntPtr counter, out double value)
    {
        value = 0;

        if (counter == IntPtr.Zero ||
            Pdh.PdhGetFormattedCounterValue(counter, Pdh.PDH_FMT_DOUBLE, out _, out var formatted) != Pdh.ERROR_SUCCESS ||
            formatted.CStatus != 0)
        {
            return false;
        }

        value = BitConverter.Int64BitsToDouble(formatted.LargeValue);
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    internal static bool TryFormatLarge(IntPtr counter, out long value)
    {
        value = 0;

        if (counter == IntPtr.Zero ||
            Pdh.PdhGetFormattedCounterValue(counter, Pdh.PDH_FMT_LARGE, out _, out var formatted) != Pdh.ERROR_SUCCESS ||
            formatted.CStatus != 0)
        {
            return false;
        }

        value = formatted.LargeValue;
        return true;
    }

    /// <summary>
    /// Reads a wildcarded counter as one row per instance. <paramref name="asLarge"/> selects the
    /// integer format for byte counts; otherwise the values are doubles.
    /// <para>
    /// <paramref name="items"/> is refreshed in place when the caller passes its own list, so a
    /// poller that reads the same counter every second allocates nothing once the buffer has
    /// grown to fit and the instance list has settled.
    /// </para>
    /// <para>
    /// <c>Name</c> is a pointer to the instance name inside the retained native block, not a copy.
    /// Wildcarded GPU counters publish an item per process engine per adapter, so materialising
    /// each name cost tens of thousands of single-use strings every second. A caller reads the
    /// name through the pointer and the pointer is only valid until this query is read again.
    /// </para>
    /// </summary>
    internal bool TryReadCounterArray(
        IntPtr counter,
        bool asLarge,
        ref List<(IntPtr Name, double Double, long Large)> items)
    {
        items.Clear();

        if (counter == IntPtr.Zero)
        {
            return false;
        }

        // The first call returns PDH_MORE_DATA and reports the buffer size the second one needs.
        // The block is retained between calls: GPU Engine publishes an item for every process
        // engine on every adapter, so the same tens of kilobytes were being allocated, zeroed by
        // PDH and freed again on each sample of a one-second timer.
        var size = _arrayBufferBytes;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            // A zero size means the block has not been sized yet: hand PDH a null buffer and it
            // answers with the figure it needs rather than us guessing one.
            var buffer = IntPtr.Zero;

            if (size > 0)
            {
                if (_arrayBuffer == IntPtr.Zero || size > _arrayBufferBytes)
                {
                    if (_arrayBuffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(_arrayBuffer);
                    }

                    // Allocating exactly the reported size keeps a retry from growing the block twice.
                    _arrayBufferBytes = size;
                    _arrayBuffer = Marshal.AllocHGlobal((int)size);
                }

                buffer = _arrayBuffer;
            }

            var status = Pdh.PdhGetFormattedCounterArray(
                counter,
                asLarge ? Pdh.PDH_FMT_LARGE : Pdh.PDH_FMT_DOUBLE,
                ref size,
                out var count,
                buffer);

            if (status == Pdh.PDH_MORE_DATA)
            {
                // size now holds the figure the next attempt needs.
                continue;
            }

            if (status != Pdh.ERROR_SUCCESS || buffer == IntPtr.Zero)
            {
                return false;
            }

            var stride = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();

            // The items are read through a span over the native block rather than one
            // Marshal.PtrToStructure per row. PtrToStructure<T> is only allocation-free for
            // blittable T when the generic instantiation is shared, and for this struct the
            // runtime boxed every instance on the way out — 481 rows meant 19 KB of garbage per
            // sample, which at one sample a second is ~69 MB an hour for figures that are thrown
            // away immediately. Casting the bytes allocates nothing.
            var span = ReadItems(buffer, count, stride);

            for (var i = 0; i < span.Length; i++)
            {
                var item = span[i];

                if (item.CStatus != 0)
                {
                    continue;
                }

                if (item.SzName == IntPtr.Zero)
                {
                    continue;
                }

                var asDouble = asLarge ? item.LargeValue : BitConverter.Int64BitsToDouble(item.LargeValue);
                items.Add((item.SzName, asDouble, item.LargeValue));
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Views the items PDH wrote into <paramref name="buffer"/> as a span, without copying them.
    /// </summary>
    private static unsafe ReadOnlySpan<PDH_FMT_COUNTERVALUE_ITEM> ReadItems(IntPtr buffer, uint count, int stride)
    {
        if (buffer == IntPtr.Zero || count == 0)
        {
            return default;
        }

        var bytes = new ReadOnlySpan<byte>((void*)buffer, checked((int)count * stride));
        return MemoryMarshal.Cast<byte, PDH_FMT_COUNTERVALUE_ITEM>(bytes);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _counters.Clear();

        // The array buffer outlives individual reads, so it is released with the query rather
        // than in a finally on every sample.
        if (_arrayBuffer != IntPtr.Zero)
        {
            try
            {
                Marshal.FreeHGlobal(_arrayBuffer);
            }
            catch (Exception)
            {
                // Nothing useful to do if the block is already gone.
            }

            _arrayBuffer = IntPtr.Zero;
            _arrayBufferBytes = 0;
        }

        if (_query != IntPtr.Zero)
        {
            try
            {
                Pdh.PdhCloseQuery(_query);
            }
            catch (Exception)
            {
                // Closing a query that is already gone is not worth surfacing.
            }

            _query = IntPtr.Zero;
        }
    }
}
