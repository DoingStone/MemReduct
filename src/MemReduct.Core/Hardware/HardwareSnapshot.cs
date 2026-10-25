namespace MemReduct.Core.Hardware;

/// <summary>
/// One moment of machine-wide hardware activity: how busy the processors and the graphics
/// adapters are, how much video memory is committed, and what the sensors currently read.
/// <para>
/// Every figure is nullable on purpose. A reading is absent for a reason that the caller has to
/// be able to tell apart from a reading of zero — a GPU with no utilisation counter and a GPU
/// sitting idle both look like <c>0</c> if the absence is flattened away, and showing "0 %" for
/// "cannot measure" is the one mistake a monitoring tool must not make.
/// </para>
/// </summary>
public sealed record HardwareSnapshot
{
    /// <summary>When this sample was taken.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>Total processor utilisation, 0–100, or null when it could not be sampled.</summary>
    public double? CpuLoadPercent { get; init; }

    /// <summary>Per-logical-processor utilisation, 0–100 each. Empty when unavailable.</summary>
    public IReadOnlyList<double> CpuCoreLoads { get; init; } = [];

    /// <summary>Effective clock in MHz (base clock scaled by the OS's performance counter).</summary>
    public double? CpuFrequencyMhz { get; init; }

    /// <summary>Rated base clock in MHz.</summary>
    public double? CpuBaseFrequencyMhz { get; init; }

    /// <summary>Descriptive processor name, e.g. <c>AMD Ryzen 5 9600X 6-Core Processor</c>.</summary>
    public string? CpuName { get; init; }

    /// <summary>Physical core count.</summary>
    public int? CpuCoreCount { get; init; }

    /// <summary>Logical processor count.</summary>
    public int? CpuThreadCount { get; init; }

    /// <summary>Graphics adapters, in the order the operating system reports them.</summary>
    public IReadOnlyList<GpuSample> Gpus { get; init; } = [];

    /// <summary>Temperature sensors found on this machine.</summary>
    public IReadOnlyList<TemperatureSample> Temperatures { get; init; } = [];

    /// <summary>Largest current GPU utilisation across adapters, or null when no adapter reports one.</summary>
    public double? GpuLoadPercent
    {
        get
        {
            double? best = null;
            foreach (var gpu in Gpus)
            {
                if (gpu.LoadPercent is { } load && (best is null || load > best))
                {
                    best = load;
                }
            }

            return best;
        }
    }

    /// <summary>Total video memory in use, summed over adapters that report it.</summary>
    public ulong? GpuMemoryUsed
    {
        get
        {
            ulong total = 0;
            var any = false;
            foreach (var gpu in Gpus)
            {
                if (gpu.MemoryUsed is { } used)
                {
                    total += used;
                    any = true;
                }
            }

            return any ? total : null;
        }
    }

    /// <summary>Total video memory installed, summed over adapters that report it.</summary>
    public ulong? GpuMemoryTotal
    {
        get
        {
            ulong total = 0;
            var any = false;
            foreach (var gpu in Gpus)
            {
                if (gpu.MemoryTotal is { } size)
                {
                    total += size;
                    any = true;
                }
            }

            return any ? total : null;
        }
    }

    /// <summary>The hottest sensor reading in the sample, or null when nothing reports a temperature.</summary>
    public double? HottestTemperature
    {
        get
        {
            double? best = null;
            foreach (var sensor in Temperatures)
            {
                if (sensor.Celsius is { } value && (best is null || value > best))
                {
                    best = value;
                }
            }

            return best;
        }
    }
}

/// <summary>One graphics adapter's contribution to a <see cref="HardwareSnapshot"/>.</summary>
public sealed record GpuSample(
    string Name,
    double? LoadPercent,
    ulong? MemoryUsed,
    ulong? MemoryTotal,
    double? TemperatureCelsius)
{
    /// <summary>Video memory use as a percentage of installed memory, or null when either is unknown.</summary>
    public double? MemoryLoadPercent => MemoryTotal is { } total && total > 0 && MemoryUsed is { } used
        ? (double)used / total * 100.0
        : null;
}

/// <summary>
/// A named temperature reading. <paramref name="Source"/> says which subsystem produced it, so
/// a tooltip can explain a reading the user cannot otherwise account for.
/// </summary>
public sealed record TemperatureSample(string Name, string Source, double? Celsius);
