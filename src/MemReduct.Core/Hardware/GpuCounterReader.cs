using System.Globalization;
using System.Runtime.InteropServices;

namespace MemReduct.Core.Hardware;

/// <summary>
/// Derives one utilisation figure per graphics adapter from the <c>GPU Engine</c> counters.
/// <para>
/// The counters are published per <i>process × engine × adapter</i>, so neither a plain sum nor
/// a plain average means anything. This reader groups them by adapter and by engine type, adds
/// up each type across processes, and reports the <b>busiest</b> type — which is the figure a
/// user reads as "GPU 占用" and the one Task Manager shows. Summing every engine instead would
/// count the same work several times over, because one process can be active on the 3D, copy
/// and video-decode engines at the same moment.
/// </para>
/// </summary>
internal sealed class GpuCounterReader : IDisposable
{
    private const string EnginePath = @"\GPU Engine(*)\Utilization Percentage";
    private const string AdapterMemoryPath = @"\GPU Adapter Memory(*)\Dedicated Usage";

    private readonly PdhQuery _engines;
    private readonly PdhQuery? _memory;
    private bool _disposed;

    // Reused across samples. GPU Engine publishes an item per process engine per adapter — several
    // hundred rows on an ordinary desktop — and the sample runs on a one-second timer, so the
    // dictionaries, the row lists and the native buffer are held rather than rebuilt, and each
    // instance name is read straight out of that retained block instead of being copied into a
    // string apiece. Everything below is cleared at the start of each read; nothing here may be
    // retained by a caller past the tick that produced it.
    // Not readonly: PdhQuery refreshes them in place through `ref`, which is the whole point of
    // holding them — the list object survives, only its contents are repopulated.
    private List<(IntPtr Name, double Double, long Large)> _engineRows = [];
    private List<(IntPtr Name, double Double, long Large)> _memoryRows = [];
    private readonly Dictionary<(uint High, uint Low, string Engine), double> _engineTotals = [];
    private readonly Dictionary<(uint High, uint Low), double> _busiest = [];
    private readonly Dictionary<string, double> _utilization = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ulong> _memoryBytes = new(StringComparer.OrdinalIgnoreCase);

    // An instance name spells its LUID in hex and its engine type as text, and both repeat across
    // every row: this machine has two adapters and a handful of engine types. Pooling the two
    // strings keeps the join key allocation-free after the first sample, because a row only has to
    // compare its span against a list that never grows past a few entries. The pool is cleared
    // with the reader, not per read.
    private readonly List<(ulong High, ulong Low, string Text)> _luidPool = [];
    private readonly List<string> _enginePool = [];

    private GpuCounterReader(PdhQuery engines, PdhQuery? memory)
    {
        _engines = engines;
        _memory = memory;
    }

    /// <summary>
    /// Opens the GPU counter sets, or returns null when they are unavailable — an older Windows
    /// build, or a session whose graphics performance counters are disabled.
    /// </summary>
    public static GpuCounterReader? TryCreate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var engines = PdhQuery.TryCreate(EnginePath);
        if (engines is null)
        {
            return null;
        }

        // Dedicated usage is a bonus: without it the card still shows utilisation, so failing to
        // open this set downgrades one figure instead of disabling the reader.
        var memory = PdhQuery.TryCreate(AdapterMemoryPath);
        return new GpuCounterReader(engines, memory);
    }

    /// <summary>
    /// Takes one collection pass. Rate counters need two passes before they can be formatted,
    /// so this is called once at construction and once per sample.
    /// </summary>
    public bool Collect()
    {
        if (_disposed)
        {
            return false;
        }

        var collected = _engines.Collect();
        _memory?.Collect();
        return collected;
    }

    /// <summary>
    /// Per-adapter utilisation in percent, keyed by the normalised LUID
    /// (<c>0x000000000001278F</c>), plus per-adapter dedicated memory in bytes.
    /// <para>
    /// Both dictionaries are owned by this reader and overwritten on the next read, so a caller
    /// must consume them within the tick rather than holding on to them.
    /// </para>
    /// </summary>
    public (Dictionary<string, double> Utilization, Dictionary<string, ulong> Memory) Read()
    {
        _utilization.Clear();
        _memoryBytes.Clear();
        _engineTotals.Clear();
        _busiest.Clear();

        if (_disposed)
        {
            return (_utilization, _memoryBytes);
        }

        // Both counters are rates: PDH formats them from the delta between two collection passes,
        // so a read has to advance the query first. The construction-time collect seeds the pair;
        // this one supplies the other end of it for the current tick.
        Collect();

        if (_engines.TryReadCounterArray(_engines.HandleFor(EnginePath), asLarge: false, ref _engineRows))
        {
            // adapter + engine type → summed percent across every process using that type. The
            // instance name is read out of the retained PDH block here, so the only allocation
            // left on this path is the first time an engine type or an adapter is seen.
            foreach (var (instance, value, _) in _engineRows)
            {
                if (!TryParseEngineInstance(instance, out var high, out var low, out var engineType))
                {
                    continue;
                }

                var key = (high, low, GetEngineText(engineType));
                _engineTotals[key] = _engineTotals.TryGetValue(key, out var sum) ? sum + value : value;
            }

            // The figure a user reads as "GPU 占用" is the busiest engine type, not the sum of
            // every type: one process can be active on 3D, copy and decode at the same moment.
            foreach (var ((high, low, _), value) in _engineTotals)
            {
                var key = (high, low);
                _busiest[key] = _busiest.TryGetValue(key, out var current) ? Math.Max(current, value) : value;
            }

            foreach (var ((high, low), busiest) in _busiest)
            {
                _utilization[GetLuidText(high, low)] = Math.Clamp(busiest, 0, 100);
            }
        }

        if (_memory is not null &&
            _memory.TryReadCounterArray(_memory.HandleFor(AdapterMemoryPath), asLarge: true, ref _memoryRows))
        {
            foreach (var (instance, _, value) in _memoryRows)
            {
                if (value > 0 && TryParseAdapterLuid(ReadInstance(instance), out var high, out var low))
                {
                    _memoryBytes[GetLuidText(high, low)] = (ulong)value;
                }
            }
        }

        return (_utilization, _memoryBytes);
    }

    private const string LuidMarker = "luid_";
    private const string EngineTypeMarker = "_engtype_";

    /// <summary>
    /// Splits <c>pid_2000_luid_0x00000000_0x0001278f_phys_0_eng_0_engtype_3d</c> into the adapter
    /// LUID and the engine type.
    /// <para>
    /// The instance name is read in place out of the block PDH filled. Copying it into a string
    /// first meant one string per process engine per adapter per sample — hundreds of throwaway
    /// strings a second — for text that is only ever compared and counted.
    /// </para>
    /// </summary>
    internal static bool TryParseEngineInstance(
        IntPtr instance,
        out uint high,
        out uint low,
        out ReadOnlySpan<char> engineType)
    {
        high = 0;
        low = 0;
        engineType = default;

        var text = ReadInstance(instance);
        if (text.IsEmpty)
        {
            return false;
        }

        var luidIndex = text.IndexOf(LuidMarker, StringComparison.OrdinalIgnoreCase);
        if (luidIndex < 0)
        {
            return false;
        }

        var start = luidIndex + LuidMarker.Length;
        var engineIndex = text[start..].IndexOf(EngineTypeMarker, StringComparison.OrdinalIgnoreCase);
        if (engineIndex < 0)
        {
            return false;
        }

        engineIndex += start;

        if (!TryParseAdapterLuid(text[start..engineIndex], out high, out low))
        {
            return false;
        }

        var typeStart = engineIndex + EngineTypeMarker.Length;
        var typeEnd = text.Length;

        while (typeEnd > typeStart && char.IsWhiteSpace(text[typeEnd - 1]))
        {
            typeEnd--;
        }

        if (typeEnd <= typeStart)
        {
            return false;
        }

        engineType = text[typeStart..typeEnd];
        return true;
    }

    /// <summary>
    /// Normalises PDH's <c>0x00000000_0x0001278f</c> and DXGI's <c>0x000000000001278F</c> to one
    /// spelling, so the two sources can be joined directly.
    /// <para>
    /// The instance name is a whole segment, not just the LUID — GPU Engine publishes
    /// <c>luid_0x00000000_0x0001278f_phys_0_eng_0</c> and GPU Adapter Memory publishes
    /// <c>luid_0x00000000_0x0001278f_phys_0</c>. So this takes the first one or two
    /// <c>0x</c>-prefixed groups it meets and ignores the trailing <c>phys</c>/<c>eng</c> segments,
    /// rather than insisting that the value is nothing but the LUID. The earlier version required
    /// the split to yield exactly one or two parts, which neither real instance name does — every
    /// reading was silently discarded.
    /// </para>
    /// </summary>
    internal static bool TryParseAdapterLuid(ReadOnlySpan<char> value, out uint high, out uint low)
    {
        high = 0;
        low = 0;

        ulong first = 0;
        ulong second = 0;
        var found = 0;
        var rest = value;

        while (true)
        {
            var underscore = rest.IndexOf('_');
            var token = underscore < 0 ? rest : rest[..underscore];

            if (token.Length > 2 &&
                token[0] == '0' &&
                (token[1] == 'x' || token[1] == 'X') &&
                ulong.TryParse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
            {
                if (found == 0)
                {
                    first = parsed;
                }
                else
                {
                    second = parsed;
                }

                found++;

                if (found == 2)
                {
                    break;
                }
            }

            if (underscore < 0)
            {
                break;
            }

            rest = rest[(underscore + 1)..];
        }

        if (found == 0)
        {
            return false;
        }

        if (found == 1)
        {
            // A single group is the whole 64-bit LUID, so it splits into the two DXGI halves.
            high = (uint)(first >> 32);
            low = (uint)first;
            return true;
        }

        high = (uint)first;
        low = (uint)second;
        return true;
    }

    /// <summary>
    /// The spelled LUID used as the join key, reused from the pool once an adapter has been seen.
    /// An LUID never changes for the life of the reader, so a hit here stops the key from being
    /// rebuilt on every row of every sample.
    /// </summary>
    private string GetLuidText(uint high, uint low)
    {
        foreach (var (knownHigh, knownLow, text) in _luidPool)
        {
            if (knownHigh == high && knownLow == low)
            {
                return text;
            }
        }

        var fresh = $"0x{high:X8}{low:X8}";
        _luidPool.Add((high, low, fresh));
        return fresh;
    }

    /// <summary>The engine type as a pooled string — there are only a handful on any machine.</summary>
    private string GetEngineText(ReadOnlySpan<char> engineType)
    {
        foreach (var known in _enginePool)
        {
            if (engineType.Equals(known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        var fresh = engineType.ToString();
        _enginePool.Add(fresh);
        return fresh;
    }

    /// <summary>
    /// Views a NUL-terminated UTF-16 instance name without copying it. The pointer points into the
    /// block PDH filled, which stays valid until the next read of that query.
    /// </summary>
    private static unsafe ReadOnlySpan<char> ReadInstance(IntPtr instance)
    {
        if (instance == IntPtr.Zero)
        {
            return default;
        }

        var text = (char*)instance;
        var length = 0;

        while (text[length] != '\0')
        {
            length++;
        }

        return length == 0 ? default : new ReadOnlySpan<char>(text, length);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engines.Dispose();
        _memory?.Dispose();
    }
}
