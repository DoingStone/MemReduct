using System.Text;

namespace Ildump;

/// <summary>
/// Builds an index of byte offsets for every occurrence of a literal in a file, using
/// chunked reads and vectorised search, so a scan over a large executable stays cheap.
/// </summary>
internal static class Scan
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ildump scan <file> <literal> [contextBytes]");
            return 2;
        }

        var path = Path.GetFullPath(args[0]);
        var needle = Encoding.UTF8.GetBytes(args[1]);
        var context = args.Length > 2 ? int.Parse(args[2]) : 32;
        var size = new FileInfo(path).Length;

        // Keep one needle-length of overlap so a literal straddling a boundary is still found.
        const int chunk = 8 * 1024 * 1024;
        var buffer = new byte[chunk + needle.Length];
        using var fs = File.OpenRead(path);
        long position = 0;
        var found = 0;

        while (position < size)
        {
            fs.Position = position;
            var read = fs.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            var span = new ReadOnlySpan<byte>(buffer, 0, read);
            var searchFrom = 0;
            while (true)
            {
                var index = span[searchFrom..].IndexOf(needle);
                if (index < 0)
                {
                    break;
                }

                var i = searchFrom + index;
                var absolute = position + i;
                var from = Math.Max(0, i - context);
                var to = Math.Min(read, i + needle.Length + context);
                var text = new string(span[from..to]
                    .ToArray()
                    .Select(b => b >= 32 && b < 127 ? (char)b : '.')
                    .ToArray());

                Console.WriteLine($"0x{absolute:X8}  {text}");
                found++;
                searchFrom = i + 1;
            }

            position += read - needle.Length;
        }

        Console.WriteLine($"{found} match(es) in {path} ({size:N0} bytes)");
        return found > 0 ? 0 : 3;
    }
}
