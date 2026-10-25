using System.Text;

namespace Ildump;

/// <summary>
/// Reads the .NET single-file bundle manifest (the ".net core bundle" marker) from an
/// executable and lists the embedded files, so an inner assembly can be carved out.
/// </summary>
internal static class Bundle
{
    public static int Run(string[] args)
    {
        var filter = args.Length > 1 ? args[1] : null;
        var bytes = File.ReadAllBytes(Path.GetFullPath(args[0]));
        var marker = Encoding.UTF8.GetBytes(".net core bundle");
        var at = IndexOf(bytes, marker, 0, 4 * 1024 * 1024);
        if (at < 0)
        {
            Console.Error.WriteLine("no single-file bundle marker found");
            return 3;
        }

        Console.WriteLine($"bundle marker at 0x{at:X}");

        var offset = at + marker.Length;
        long major = BitConverter.ToInt32(bytes, offset);
        offset += 4;
        long minor = BitConverter.ToInt32(bytes, offset);
        offset += 4;
        long count = BitConverter.ToInt32(bytes, offset);
        offset += 4;
        var headerOffset = BitConverter.ToInt64(bytes, offset);

        Console.WriteLine($"version {major}.{minor}, {count} embedded files, header at 0x{headerOffset:X}");

        var p = (int)headerOffset;
        for (long i = 0; i < count; i++)
        {
            var fileOffset = BitConverter.ToInt64(bytes, p); p += 8;
            var size = BitConverter.ToInt64(bytes, p); p += 8;
            var compressed = BitConverter.ToUInt32(bytes, p); p += 4;

            // The 8.0 manifest writes a 4-byte type and 8 zero bytes before the name.
            var type = bytes[p]; p += 1;
            p += 7;
            var nameEnd = Array.IndexOf(bytes, (byte)0, p);
            var name = Encoding.UTF8.GetString(bytes, p, nameEnd - p);
            p = nameEnd + 1;

            if (filter is null || name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"{name}\ttype={type}\tsize={size}\tcompressed={compressed}\toffset={fileOffset}");
            }
        }

        return 0;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start, int limit)
    {
        for (var i = start; i <= Math.Min(haystack.Length, limit) - needle.Length; i++)
        {
            var j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j])
            {
                j++;
            }

            if (j == needle.Length)
            {
                return i;
            }
        }

        return -1;
    }
}
