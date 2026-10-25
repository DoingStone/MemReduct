using System.Runtime.InteropServices;
using System.Text;

namespace MemReduct.Core.Hardware;

/// <summary>
/// Reads fixed-disk identity and temperature straight from the storage stack.
/// <para>
/// Both readings come from the same device handle and the same IOCTL, so a drive either
/// answers both or neither. Drives that do not implement the temperature property are skipped
/// rather than reported as 0 °C — a drive running at zero degrees is not a thing, and showing it
/// would teach the user to distrust every other reading on the card.
/// </para>
/// </summary>
internal static class StorageInventory
{
    /// <summary>STORAGE_DEVICE_DESCRIPTOR is what a StorageDeviceProperty query returns.</summary>
    private const uint StorageDeviceProperty = 0;

    // Layout of STORAGE_TEMPERATURE_DATA_DESCRIPTOR, established from the real payload rather
    // than the header names: the reserved area between the warning threshold and the entry array
    // is 12 bytes, so the descriptor is 24 bytes overall and each entry is 16.
    private const int HeaderBytes = 24;
    private const int CountOffset = 12;
    private const int EntryBytes = 16;
    private const int TemperatureOffset = 2;

    /// <summary>
    /// One entry per physical drive that answered. <paramref name="Index"/> is the
    /// <c>\\.\PhysicalDriveN</c> number, which is what other tools show as the disk number.
    /// </summary>
    internal sealed record Drive(int Index, string Name, double? Celsius);

    /// <summary>
    /// Enumerates drives 0..15. <paramref name="maxDrive"/> is deliberately small: physical drive
    /// numbers are dense from zero, and probing further would cost a handle open per empty slot.
    /// </summary>
    internal static List<Drive> Enumerate(int maxDrive = 16)
    {
        var drives = new List<Drive>();

        if (!OperatingSystem.IsWindows())
        {
            return drives;
        }

        for (var index = 0; index < maxDrive; index++)
        {
            var handle = OpenDrive(index);
            if (handle == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                var name = ReadName(handle) ?? $"磁盘 {index}";
                var celsius = ReadTemperature(handle);
                drives.Add(new Drive(index, name, celsius));
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        return drives;
    }

    private static IntPtr OpenDrive(int index)
    {
        try
        {
            // Access is deliberately 0: the properties below are metadata and need no read
            // right, and requesting one on a drive that is mounted read-only fails the open.
            var handle = HardwareNative.CreateFileW(
                @"\\.\PhysicalDrive" + index,
                0,
                FileShareReadWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);

            return handle == new IntPtr(-1) ? IntPtr.Zero : handle;
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    private static void CloseHandle(IntPtr handle) => _ = HardwareNative.CloseHandle(handle);

    private static string? ReadName(IntPtr handle)
    {
        const int bufferSize = 512;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        var query = Marshal.AllocHGlobal(12);

        try
        {
            // STORAGE_PROPERTY_QUERY { PropertyId, QueryType (PropertyStandardQuery = 0), Additional[1] }
            for (var offset = 0; offset < 12; offset++)
            {
                Marshal.WriteByte(query, offset, 0);
            }

            Marshal.WriteInt32(query, 0, (int)StorageDeviceProperty);

            if (!HardwareNative.DeviceIoControl(
                    handle,
                    HardwareNative.IOCTL_STORAGE_QUERY_PROPERTY,
                    query,
                    12,
                    buffer,
                    bufferSize,
                    out var returned,
                    IntPtr.Zero) || returned < 36)
            {
                return null;
            }

            var vendor = ReadString(buffer, returned, Marshal.ReadInt32(buffer, 12));
            var product = ReadString(buffer, returned, Marshal.ReadInt32(buffer, 16));

            var combined = string.Join(
                " ",
                new[] { vendor, product }.Where(part => !string.IsNullOrWhiteSpace(part)));

            return string.IsNullOrWhiteSpace(combined) ? null : Collapse(combined);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(query);
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Reads the drive's own temperature sensor. Returns null for drives that refuse the
    /// property, which is most USB bridges and some older SATA firmware.
    /// </summary>
    private static double? ReadTemperature(IntPtr handle)
    {
        const int bufferSize = 512;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        var query = Marshal.AllocHGlobal(12);

        try
        {
            for (var offset = 0; offset < 12; offset++)
            {
                Marshal.WriteByte(query, offset, 0);
            }

            Marshal.WriteInt32(query, 0, (int)HardwareNative.StorageDeviceTemperatureProperty);

            if (!HardwareNative.DeviceIoControl(
                    handle,
                    HardwareNative.IOCTL_STORAGE_QUERY_PROPERTY,
                    query,
                    12,
                    buffer,
                    bufferSize,
                    out var returned,
                    IntPtr.Zero) || returned < HeaderBytes + EntryBytes)
            {
                return null;
            }

            // STORAGE_TEMPERATURE_DATA_DESCRIPTOR is 24 bytes, not the 16 its documented header
            // fields suggest — the reserved area is larger than the WORD pair the headers name,
            // which the real payload confirms (168 B for 9 entries, 104 B for 5). Count is a WORD
            // at offset 12 and the entries follow the header, each 16 bytes with the temperature
            // at entry offset +2. Reading the first temperature at the documented offset 18 lands
            // inside the reserved padding and yields 0, which is why every drive used to report
            // "no sensor".
            var count = Marshal.ReadInt16(buffer, CountOffset);
            if (count <= 0)
            {
                return null;
            }

            for (var i = 0; i < count; i++)
            {
                var entry = HeaderBytes + (i * EntryBytes);
                if (entry + TemperatureOffset + sizeof(short) > returned)
                {
                    break;
                }

                var celsius = Marshal.ReadInt16(buffer, entry + TemperatureOffset);

                // A drive publishes several sensors (package, controller, ambient). The first
                // valid one is what the OS itself surfaces; 0 and the -32768 marker both mean the
                // sensor is absent rather than reporting a cold case.
                if (celsius is > 0 and <= 150)
                {
                    return celsius;
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(query);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ReadString(IntPtr buffer, uint returned, int offset)
    {
        if (offset <= 0 || offset >= returned)
        {
            return string.Empty;
        }

        var bytes = new List<byte>(64);
        for (var i = offset; i < returned; i++)
        {
            var value = Marshal.ReadByte(buffer, i);
            if (value == 0)
            {
                break;
            }

            bytes.Add(value);
        }

        return Encoding.ASCII.GetString([.. bytes]).Trim();
    }

    /// <summary>
    /// Collapses the padding that storage descriptors are full of: vendor fields are fixed-width
    /// and arrive as <c>ATA        GLOWAY   Basic256GNVMe</c>.
    /// </summary>
    private static string Collapse(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
        }

        return builder.ToString().Trim();
    }

    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
}
