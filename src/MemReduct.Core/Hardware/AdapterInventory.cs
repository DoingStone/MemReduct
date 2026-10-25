using System.Runtime.InteropServices;

namespace MemReduct.Core.Hardware;

/// <summary>
/// Enumerates the graphics adapters through DXGI and caches what it learns.
/// <para>
/// DXGI is used rather than WMI for three reasons: it reports the real dedicated video memory
/// (<c>Win32_VideoController.AdapterRAM</c> is a 32-bit field that wraps at 4 GB, so every
/// modern card reads as exactly 4 GB or less), it lists only adapters that are present right
/// now, and it returns the adapter LUID that the performance counters are keyed by.
/// </para>
/// </summary>
internal static class AdapterInventory
{
    /// <summary>One graphics adapter as DXGI describes it.</summary>
    internal sealed record Adapter(
        string Name,
        string Luid,
        ulong DedicatedVideoMemory,
        uint VendorId,
        bool IsSoftware);

    /// <summary>
    /// Enumerates every adapter, or returns an empty list when DXGI is unavailable (a machine
    /// without a graphics stack, or a session where the call is denied).
    /// </summary>
    internal static List<Adapter> Enumerate()
    {
        var results = new List<Adapter>();

        if (!OperatingSystem.IsWindows())
        {
            return results;
        }

        var iid = HardwareNative.IidDxgiFactory1;
        IntPtr factory;

        try
        {
            if (HardwareNative.CreateDXGIFactory1(ref iid, out factory) != 0 || factory == IntPtr.Zero)
            {
                return results;
            }
        }
        catch (Exception)
        {
            // dxgi.dll missing or blocked. The card falls back to counters without names.
            return results;
        }

        try
        {
            var factoryVtable = Marshal.ReadIntPtr(factory);
            var enumAdapters = (HardwareNative.EnumAdapters1Delegate)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(factoryVtable, HardwareNative.Vtable_EnumAdapters1 * IntPtr.Size),
                typeof(HardwareNative.EnumAdapters1Delegate));

            for (uint index = 0; index < 16; index++)
            {
                if (enumAdapters(factory, index, out var adapter) != 0 || adapter == IntPtr.Zero)
                {
                    break;
                }

                try
                {
                    var description = ReadDescription(adapter);
                    if (description is not { } desc)
                    {
                        continue;
                    }

                    var name = desc.Description?.TrimEnd('\0').Trim() ?? string.Empty;
                    var luid = $"0x{desc.AdapterLuid.HighPart:X8}{desc.AdapterLuid.LowPart:X8}";

                    results.Add(new Adapter(
                        string.IsNullOrWhiteSpace(name) ? luid : name,
                        luid,
                        desc.DedicatedVideoMemory.ToUInt64(),
                        desc.VendorId,
                        (desc.Flags & HardwareNative.DXGI_ADAPTER_FLAG_SOFTWARE) != 0));
                }
                finally
                {
                    // Every successful EnumAdapters1 hands over a reference.
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }

        return results;
    }

    private static HardwareNative.DXGI_ADAPTER_DESC1? ReadDescription(IntPtr adapter)
    {
        try
        {
            var vtable = Marshal.ReadIntPtr(adapter);
            var getDesc = (HardwareNative.GetDesc1Delegate)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(vtable, HardwareNative.Vtable_GetDesc1 * IntPtr.Size),
                typeof(HardwareNative.GetDesc1Delegate));

            return getDesc(adapter, out var description) == 0 ? description : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Release(IntPtr comObject)
    {
        try
        {
            var vtable = Marshal.ReadIntPtr(comObject);
            var release = (HardwareNative.ReleaseDelegate)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(vtable, HardwareNative.Vtable_Release * IntPtr.Size),
                typeof(HardwareNative.ReleaseDelegate));

            release(comObject);
        }
        catch (Exception)
        {
            // Releasing is best-effort; a leak here is a handle, not a crash.
        }
    }
}
