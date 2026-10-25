using System.Text;

namespace MemReduct.Core.Hardware;

/// <summary>
/// A live NVML session over the installed NVIDIA adapters, or null when there is no NVIDIA
/// driver to talk to.
/// <para>
/// NVML supplies the two readings DXGI does not carry — the GPU's own temperature and its
/// <i>dedicated</i> video memory in use — and it does so without a kernel driver of our own or
/// any third-party tool being installed. It talks to one vendor only, deliberately: reading an
/// AMD or Intel adapter's temperature has no comparable user-mode API, so those adapters fall
/// back to load and capacity without a temperature, which the UI states plainly instead of
/// inventing a number.
/// </para>
/// </summary>
internal sealed class NvmlSession : IDisposable
{
    private readonly uint _count;
    private bool _disposed;

    // Filled once per adapter: the driver reports the same model string for the life of the
    // process, so re-reading and re-decoding it every tick was pure garbage.
    private readonly string?[] _names;

    private NvmlSession(uint count)
    {
        _count = count;
        _names = new string?[count];
    }

    /// <summary>Initialises NVML, or returns null when no NVIDIA driver is present.</summary>
    public static NvmlSession? TryOpen()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            if (HardwareNative.NvmlInit() != 0)
            {
                return null;
            }
        }
        catch (DllNotFoundException)
        {
            // No NVIDIA driver installed. Every other card lands here, which is expected.
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            // A driver too old to expose the _v2 entry points. Not worth a special case.
            return null;
        }

        try
        {
            if (HardwareNative.NvmlDeviceGetCount(out var count) != 0 || count == 0)
            {
                HardwareNative.nvmlShutdown();
                return null;
            }

            return new NvmlSession(count);
        }
        catch (Exception)
        {
            HardwareNative.nvmlShutdown();
            return null;
        }
    }

    /// <summary>Reads every NVIDIA adapter. Adapters that fail are skipped individually.</summary>
    public List<(GpuSample Sample, int Index)> Read()
    {
        var results = new List<(GpuSample, int)>();

        if (_disposed)
        {
            return results;
        }

        for (uint index = 0; index < _count; index++)
        {
            try
            {
                if (HardwareNative.NvmlDeviceGetHandleByIndex(index, out var device) != 0 || device == IntPtr.Zero)
                {
                    continue;
                }

                var name = ResolveName(device, (int)index) ?? $"NVIDIA GPU {index}";

                double? load = HardwareNative.nvmlDeviceGetUtilizationRates(device, out var utilization) == 0
                    ? utilization.Gpu
                    : null;

                ulong? used = null;
                ulong? total = null;
                if (HardwareNative.nvmlDeviceGetMemoryInfo(device, out var memory) == 0 && memory.Total > 0)
                {
                    used = memory.Used;
                    total = memory.Total;
                }

                double? temperature = HardwareNative.nvmlDeviceGetTemperature(
                    device,
                    HardwareNative.NVML_TEMPERATURE_GPU,
                    out var celsius) == 0
                    ? celsius
                    : null;

                results.Add((new GpuSample(name, load, used, total, temperature), (int)index));
            }
            catch (Exception)
            {
                // One misbehaving adapter must not blank the whole card.
            }
        }

        return results;
    }

    /// <summary>
    /// Adapter names never change while the process runs, so each one is read once and then
    /// reused. Reading them per tick allocated a 128-byte buffer and decoded a UTF-8 string for
    /// every adapter on every refresh, for a value that is fixed for the life of the driver.
    /// </summary>
    private string? ResolveName(IntPtr device, int index)
    {
        if (index < _names.Length && _names[index] is { } cached)
        {
            return cached;
        }

        var name = ReadName(device);
        if (name is null)
        {
            return null;
        }

        if (index < _names.Length)
        {
            _names[index] = name;
        }

        return name;
    }

    private static string? ReadName(IntPtr device)
    {
        var buffer = new byte[128];

        if (HardwareNative.nvmlDeviceGetName(device, buffer, (uint)buffer.Length) != 0)
        {
            return null;
        }

        // NVML returns a NUL-terminated UTF-8 string in a fixed buffer.
        var length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
        {
            length = buffer.Length;
        }

        var name = Encoding.UTF8.GetString(buffer, 0, length).Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            HardwareNative.nvmlShutdown();
        }
        catch (Exception)
        {
            // Shutting down a loaded library is not worth surfacing.
        }
    }
}
