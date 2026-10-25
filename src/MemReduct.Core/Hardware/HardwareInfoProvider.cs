using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MemReduct.Core.Interop;

namespace MemReduct.Core.Hardware;

/// <summary>
/// Reads processor, graphics and sensor activity from the operating system.
/// <para>
/// Two sampling styles meet here. CPU utilisation is a <b>delta</b>: the kernel's per-processor
/// counters are captured by <see cref="Prime"/> and read again by <see cref="Read"/>, so the
/// figure describes the interval in between rather than a stale one that ended before the caller
/// asked. GPU, clock and temperature readings are <b>snapshots</b> and are taken fresh.
/// </para>
/// <para>
/// Every source may be absent, and absence is reported as <c>null</c> rather than zero. Nothing
/// here throws to the caller: a monitoring card that fails has to leave the rest of the window
/// working, because cleaning still works without it.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardwareInfoProvider : IDisposable
{
    private readonly object _gate = new();

    private CoreTimes[]? _primed;
    private bool _disposed;

    // The processor table is fetched with NtQuerySystemInformation, which writes into a
    // caller-owned block, and two consecutive samples have to coexist so they can be
    // differenced. Both are kept: one native block that only ever grows, and two managed arrays
    // that alternate so the sample handed out last tick is never the one written this tick.
    private IntPtr _processorBuffer;
    private int _processorBufferBytes;
    private CoreTimes[] _timesFirst = [];
    private CoreTimes[] _timesSecond = [];
    private bool _timesUseFirst = true;

    // Per-core percentages are handed straight to the strip, which copies them before keeping
    // anything, so one buffer can be refilled each tick instead of a new array each time.
    private double[] _coreLoadScratch = [];

    // Adapter metadata never changes while the process runs, so it is read once.
    private List<AdapterInventory.Adapter>? _adapters;
    private string? _cpuName;
    private int? _physicalCores;
    private double? _baseFrequencyMhz;
    private bool _identityLoaded;

    // The GPU performance counters are by far the most expensive thing this class does: PDH
    // enumerates one instance per process engine per adapter — several hundred on an ordinary
    // desktop — and the query walks kernel-side state for every one of them. The figures it
    // returns are a slow-moving percentage and a byte total, so re-reading them on every tick
    // spends the most on the least. They are sampled at most this often; between samples the
    // previous reading is reused, so a value can lag by up to the interval.
    private const int GpuCounterIntervalMs = 2000;
    private (Dictionary<string, double> Utilization, Dictionary<string, ulong> Memory)? _gpuCountersSnapshot;
    private long _gpuCountersStamp = long.MinValue;

    // Long-lived helper sessions. Each is null when its source is missing, which is the normal
    // case for at least one of them on any given machine.
    private GpuCounterReader? _gpuCounters;
    private bool _gpuCountersLoaded;

    private NvmlSession? _nvml;
    private bool _nvmlLoaded;

    private PdhQuery? _frequency;
    private bool _frequencyPrimed;
    private bool _frequencyLoaded;

    private List<StorageInventory.Drive>? _drives;
    private bool _drivesLoaded;

    /// <summary>
    /// Captures the counters whose later subtraction produces the utilisation and clock figures.
    /// Call once, then <see cref="Read"/> after the interval that should be measured. Calling it
    /// again simply re-baselines.
    /// </summary>
    public void Prime()
    {
        if (_disposed || !OperatingSystem.IsWindows())
        {
            return;
        }

        lock (_gate)
        {
            var times = ReadProcessorTimes();
            _primed = times.Length > 0 ? times : null;

            var frequency = LoadFrequencyQuery();
            if (frequency is not null && frequency.Collect())
            {
                _frequencyPrimed = true;
            }
        }
    }

    /// <summary>
    /// Reads one full sample. CPU figures describe the interval since the last
    /// <see cref="Prime"/> or <see cref="Read"/>, and the counters are re-baselined so the next
    /// call measures its own interval too.
    /// </summary>
    public HardwareSnapshot Read()
    {
        if (_disposed || !OperatingSystem.IsWindows())
        {
            return new HardwareSnapshot();
        }

        lock (_gate)
        {
            EnsureIdentity();

            var snapshot = new HardwareSnapshot
            {
                Timestamp = DateTimeOffset.Now,
                CpuName = _cpuName,
                CpuCoreCount = _physicalCores,
                CpuThreadCount = Environment.ProcessorCount,
                CpuBaseFrequencyMhz = _baseFrequencyMhz,
            };

            var (total, cores) = TakeProcessorLoad();
            snapshot = snapshot with
            {
                CpuLoadPercent = total,
                CpuCoreLoads = cores,
                CpuFrequencyMhz = ReadEffectiveFrequency() ?? _baseFrequencyMhz,
            };

            // Read once and share: two independent passes would let the utilisation on the
            // graphics row disagree with the temperature derived from the same adapter.
            var gpus = ReadGraphics();

            return snapshot with
            {
                Gpus = gpus,
                Temperatures = ReadTemperatures(gpus),
            };
        }
    }

    // ------------------------------------------------------------------ identity

    /// <summary>
    /// Reads the parts of the machine's identity that never change: the processor name, its
    /// physical core count, its rated clock, and the graphics adapter list.
    /// </summary>
    private void EnsureIdentity()
    {
        if (_identityLoaded)
        {
            return;
        }

        _identityLoaded = true;
        _cpuName = ReadProcessorName();
        _physicalCores = ReadPhysicalCoreCount();
        _baseFrequencyMhz = ReadRatedFrequency();

        try
        {
            _adapters = AdapterInventory.Enumerate();
        }
        catch (Exception)
        {
            _adapters = [];
        }
    }

    private static string? ReadProcessorName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");

            var value = key?.GetValue("ProcessorNameString") as string;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Counts physical cores through <c>GetLogicalProcessorInformation</c>. This replaces the
    /// WMI query that would otherwise need the System.Management package: one entry is returned
    /// per core, not per thread, so the count is the physical one without a per-vendor CPUID.
    /// </summary>
    private static int? ReadPhysicalCoreCount()
    {
        try
        {
            var length = 0u;
            HardwareNative.GetLogicalProcessorInformation(IntPtr.Zero, ref length);

            if (length == 0)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal((int)length);

            try
            {
                if (!HardwareNative.GetLogicalProcessorInformation(buffer, ref length))
                {
                    return null;
                }

                var stride = Marshal.SizeOf<HardwareNative.SYSTEM_LOGICAL_PROCESSOR_INFORMATION>();
                var count = (int)(length / stride);
                var cores = 0;

                for (var i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<HardwareNative.SYSTEM_LOGICAL_PROCESSOR_INFORMATION>(
                        IntPtr.Add(buffer, i * stride));

                    if (entry.Relationship == HardwareNative.RelationProcessorCore)
                    {
                        cores++;
                    }
                }

                return cores > 0 ? cores : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The rated clock, from <c>CallNtPowerInformation</c>. On parts without per-core clock
    /// control every Mhz field carries the rated value, which is exactly what is wanted here —
    /// the <i>live</i> clock comes from the PDH pair instead, because this call does not move
    /// with boost.
    /// </summary>
    private static double? ReadRatedFrequency()
    {
        var count = Environment.ProcessorCount;
        if (count <= 0)
        {
            return null;
        }

        var stride = Marshal.SizeOf<HardwareNative.PROCESSOR_POWER_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(stride * count);

        try
        {
            if (HardwareNative.CallNtPowerInformation(
                    HardwareNative.ProcessorInformation,
                    IntPtr.Zero,
                    0,
                    buffer,
                    (uint)(stride * count)) != 0)
            {
                return null;
            }

            var first = Marshal.PtrToStructure<HardwareNative.PROCESSOR_POWER_INFORMATION>(buffer);

            // MhzLimit tracks the current power cap and can legitimately be 0 on a part that
            // exposes no throttling states, so the rated maximum is the reliable field.
            var rated = first.MaxMhz > 0 ? first.MaxMhz : first.MhzLimit;
            return rated > 0 ? rated : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ------------------------------------------------------------------ processors

    private CoreTimes[] ReadProcessorTimes()
    {
        var count = Environment.ProcessorCount;
        if (count <= 0)
        {
            return [];
        }

        var stride = Marshal.SizeOf<HardwareNative.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
        var bytes = stride * count;

        if (_processorBuffer == IntPtr.Zero || _processorBufferBytes < bytes)
        {
            if (_processorBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_processorBuffer);
            }

            _processorBuffer = Marshal.AllocHGlobal(bytes);
            _processorBufferBytes = bytes;
        }

        try
        {
            if (HardwareNative.NtQuerySystemInformation(
                    HardwareNative.SystemProcessorPerformanceInformation,
                    _processorBuffer,
                    bytes,
                    out _) != 0)
            {
                return [];
            }

            // Alternate between the two retained arrays: the one returned last tick is still
            // held by _primed, so writing into it here would make the difference against itself
            // and report zero everywhere.
            if (_timesFirst.Length != count)
            {
                _timesFirst = new CoreTimes[count];
                _timesSecond = new CoreTimes[count];
            }

            _timesUseFirst = !_timesUseFirst;
            var result = _timesUseFirst ? _timesFirst : _timesSecond;

            // Read straight out of the native block. See ReadProcessorEntries for why this is
            // not a PtrToStructure loop.
            var entries = HardwareNative.ReadProcessorEntries(_processorBuffer, count);

            for (var i = 0; i < count; i++)
            {
                var info = entries[i];

                // KernelTime already contains IdleTime, so busy time is (kernel + user) − idle.
                // Adding them without that subtraction counts every idle tick as work and
                // produces a permanently busy-looking processor.
                result[i] = new CoreTimes(info.IdleTime, info.KernelTime + info.UserTime);
            }

            return result;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private (double? Total, IReadOnlyList<double> Cores) TakeProcessorLoad()
    {
        var current = ReadProcessorTimes();
        var previous = _primed;

        // Re-baseline whatever happens, so the next sample measures its own interval even when
        // this one had nothing to subtract from.
        _primed = current.Length > 0 ? current : null;

        if (previous is null || current.Length == 0)
        {
            return (null, []);
        }

        var count = Math.Min(previous.Length, current.Length);

        // Refilled in place. The strip copies the values before retaining them, so the same
        // buffer can be handed out every tick.
        if (_coreLoadScratch.Length != count)
        {
            _coreLoadScratch = new double[count];
        }

        var cores = _coreLoadScratch;

        long idleSum = 0;
        long totalSum = 0;

        for (var i = 0; i < count; i++)
        {
            var idle = current[i].Idle - previous[i].Idle;
            var total = current[i].Total - previous[i].Total;

            if (total <= 0)
            {
                cores[i] = 0;
                continue;
            }

            idle = Math.Clamp(idle, 0, total);
            cores[i] = Math.Round((double)(total - idle) / total * 100.0, 1);

            idleSum += idle;
            totalSum += total;
        }

        if (totalSum <= 0)
        {
            return (null, cores);
        }

        return (Math.Round((double)(totalSum - idleSum) / totalSum * 100.0, 1), cores);
    }

    private PdhQuery? LoadFrequencyQuery()
    {
        if (_frequencyLoaded)
        {
            return _frequency;
        }

        _frequencyLoaded = true;
        _frequency = PdhQuery.TryCreate(
            @"\Processor Information(_Total)\Processor Frequency",
            @"\Processor Information(_Total)\% Processor Performance");

        return _frequency;
    }

    /// <summary>
    /// Effective clock, from <c>Processor Frequency × % Processor Performance</c>. The first
    /// counter is the rated clock and the second says how far above or below it the part is
    /// running right now, so their product follows single-core boost — which neither
    /// <c>CallNtPowerInformation</c> nor the rated maximum does.
    /// </summary>
    private double? ReadEffectiveFrequency()
    {
        var query = LoadFrequencyQuery();
        if (query is null)
        {
            return null;
        }

        if (!_frequencyPrimed)
        {
            // The first collect only seeds the two samples PDH subtracts, so there is nothing to
            // read yet. Prime reports and this returns null, which the caller reads as "use the
            // rated clock for now".
            _frequencyPrimed = query.Collect();
            return null;
        }

        if (!query.Collect() || !query.TryFormatValues(out var values) || values.Length < 2)
        {
            return null;
        }

        var rated = values[0];
        var performance = values[1] / 100.0;

        if (rated <= 0 || performance <= 0)
        {
            return null;
        }

        var effective = Math.Round(rated * performance, 0);

        // A reading far outside the rated range is a counter artefact rather than a real clock,
        // and showing 20 GHz would undermine every other figure on the card.
        return effective > 0 && effective < rated * 4 ? effective : null;
    }

    // -------------------------------------------------------------------- graphics

    /// <summary>
    /// Builds one <see cref="GpuSample"/> per real adapter by joining the three sources that
    /// describe it: DXGI for identity and capacity, NVML for temperature and NVIDIA load, and the
    /// performance counters for a load figure that also covers AMD and Intel adapters.
    /// </summary>
    private IReadOnlyList<GpuSample> ReadGraphics()
    {
        var adapters = _adapters ?? [];
        var nvml = LoadNvml()?.Read() ?? [];

        // Sampled on a slower clock than everything else on the card: PDH publishes one GPU
        // Engine instance per process engine per adapter, so this single call is a few hundred
        // kernel-side rows and costs more than the rest of the tick combined. See
        // GpuCounterIntervalMs.
        var (utilization, memory) = TakeGpuCounters();

        var results = new List<GpuSample>(adapters.Count);
        var usedNvml = new bool[nvml.Count];

        foreach (var adapter in adapters)
        {
            // The Basic Render Driver is a software rasteriser, not hardware the user owns.
            if (adapter.IsSoftware)
            {
                continue;
            }

            var index = MatchNvml(nvml, usedNvml, adapter.Name);
            var twin = index >= 0 ? nvml[index].Sample : null;

            if (index >= 0)
            {
                usedNvml[index] = true;
            }

            var load = twin?.LoadPercent;
            if (load is null && utilization.TryGetValue(adapter.Luid, out var value))
            {
                load = Math.Round(value, 0);
            }

            var used = twin?.MemoryUsed;
            if (used is null && memory.TryGetValue(adapter.Luid, out var bytes) && bytes > 0)
            {
                used = bytes;
            }

            var total = twin?.MemoryTotal;
            if (total is null && adapter.DedicatedVideoMemory > 0)
            {
                total = adapter.DedicatedVideoMemory;
            }

            // An adapter the enumerator lists twice — a render-only node beside the display
            // node — would otherwise appear as a second graphics card that does not exist.
            if (results.Exists(existing => string.Equals(existing.Name, adapter.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            results.Add(new GpuSample(adapter.Name, load, used, total, twin?.TemperatureCelsius));
        }

        // NVML adapters with no DXGI counterpart still deserve a row: hiding a card that exists
        // is worse than showing one without capacity figures.
        for (var i = 0; i < nvml.Count; i++)
        {
            if (usedNvml[i])
            {
                continue;
            }

            var sample = nvml[i].Sample;

            if (results.Exists(existing => string.Equals(existing.Name, sample.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            results.Add(sample);
        }

        return results;
    }

    private static int MatchNvml(
        List<(GpuSample Sample, int Index)> devices,
        bool[] used,
        string name)
    {
        // Names are the only identifier NVML and DXGI share for a single adapter. An exact match
        // is used first because a machine with two identical cards has two identical names, and
        // pairing them in enumeration order is the best available guess.
        for (var i = 0; i < devices.Count; i++)
        {
            if (!used[i] && string.Equals(devices[i].Sample.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        for (var i = 0; i < devices.Count; i++)
        {
            if (!used[i] &&
                (devices[i].Sample.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                 name.Contains(devices[i].Sample.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }

        return -1;
    }

    private NvmlSession? LoadNvml()
    {
        if (_nvmlLoaded)
        {
            return _nvml;
        }

        _nvmlLoaded = true;

        try
        {
            _nvml = NvmlSession.TryOpen();
        }
        catch (Exception)
        {
            // No NVIDIA driver, or a stale one. The counters still describe the adapter and the
            // card states that the temperature is unavailable.
            _nvml = null;
        }

        return _nvml;
    }

    /// <summary>
    /// The GPU counter figures, re-read only when <see cref="GpuCounterIntervalMs"/> has elapsed
    /// since the last read. Between samples the previous dictionaries are handed back.
    /// </summary>
    /// <remarks>
    /// The dictionaries returned here are owned by the reader and reused on its next read, so a
    /// caller must consume them within the tick. That is already true of <see cref="ReadGraphics"/>,
    /// which only looks values up.
    /// </remarks>
    private (Dictionary<string, double> Utilization, Dictionary<string, ulong> Memory) TakeGpuCounters()
    {
        var counters = LoadGpuCounters();
        if (counters is null)
        {
            return _gpuCountersSnapshot ?? ([], []);
        }

        var now = Environment.TickCount64;

        // TickCount64 wraps every 292 million years, but a subtraction is safe across a wrap
        // where a comparison against a stored stamp would not be.
        if (_gpuCountersSnapshot is { } cached && now - _gpuCountersStamp < GpuCounterIntervalMs)
        {
            return cached;
        }

        try
        {
            var reading = counters.Read();
            _gpuCountersSnapshot = reading;
            _gpuCountersStamp = now;
            return reading;
        }
        catch (Exception)
        {
            // A failed sample keeps whatever was last good rather than blanking the card.
            return _gpuCountersSnapshot ?? ([], []);
        }
    }

    private GpuCounterReader? LoadGpuCounters()
    {
        if (_gpuCountersLoaded)
        {
            return _gpuCounters;
        }

        _gpuCountersLoaded = true;

        try
        {
            _gpuCounters = GpuCounterReader.TryCreate();

            // Rate counters need a pair of collections; this seeds the first one so the second
            // tick has something to subtract from.
            _gpuCounters?.Collect();
        }
        catch (Exception)
        {
            _gpuCounters = null;
        }

        return _gpuCounters;
    }

    // ---------------------------------------------------------------- temperatures

    /// <summary>
    /// Every sensor this process can actually read: the GPU die temperature and one reading per
    /// drive that implements the storage temperature property.
    /// <para>
    /// The processor package sensor is deliberately <b>absent</b> rather than present-but-zero.
    /// Windows exposes no user-mode CPU temperature without an ACPI thermal zone, and this
    /// machine publishes none — so a "CPU 封装" row would be a fabricated number. The UI states
    /// the limitation in words instead, which is the only honest option and keeps every figure on
    /// the card trustworthy.
    /// </para>
    /// </summary>
    private IReadOnlyList<TemperatureSample> ReadTemperatures(IReadOnlyList<GpuSample> gpus)
    {
        var samples = new List<TemperatureSample>();

        foreach (var gpu in gpus)
        {
            if (gpu.TemperatureCelsius is { } celsius)
            {
                samples.Add(new TemperatureSample(gpu.Name, "NVML", celsius));
            }
        }

        foreach (var drive in LoadDrives())
        {
            if (drive.Celsius is { } celsius)
            {
                samples.Add(new TemperatureSample(drive.Name, "存储驱动", celsius));
            }
        }

        return samples;
    }

    private List<StorageInventory.Drive> LoadDrives()
    {
        if (_drivesLoaded)
        {
            return _drives ?? [];
        }

        _drivesLoaded = true;

        try
        {
            // Drive identity and temperature never change for the life of the process, so the
            // handle churn is paid once rather than on every refresh tick.
            _drives = StorageInventory.Enumerate();
        }
        catch (Exception)
        {
            _drives = [];
        }

        return _drives;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _nvml?.Dispose();
        _nvml = null;

        _gpuCounters?.Dispose();
        _gpuCounters = null;

        _frequency?.Dispose();
        _frequency = null;

        if (_processorBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_processorBuffer);
            _processorBuffer = IntPtr.Zero;
            _processorBufferBytes = 0;
        }

        _timesFirst = [];
        _timesSecond = [];
        _coreLoadScratch = [];
    }

    /// <summary>One logical processor's monotonic tick counters.</summary>
    private readonly record struct CoreTimes(long Idle, long Total);
}
