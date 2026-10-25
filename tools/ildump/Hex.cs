namespace Ildump;

/// <summary>Hex dump of a file region, for reading a binary header by hand.</summary>
internal static class Hex
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ildump hex <file> <offset> [length]");
            return 2;
        }

        var path = Path.GetFullPath(args[0]);
        var offset = args[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt64(args[1], 16)
            : long.Parse(args[1]);
        var length = args.Length > 2 ? int.Parse(args[2]) : 512;

        using var fs = File.OpenRead(path);
        fs.Position = offset;
        var buffer = new byte[length];
        var read = fs.Read(buffer, 0, length);

        for (var i = 0; i < read; i += 16)
        {
            var count = Math.Min(16, read - i);
            var hex = string.Join(' ', buffer[i..(i + count)].Select(b => b.ToString("X2")));
            var text = new string(buffer[i..(i + count)]
                .Select(b => b >= 32 && b < 127 ? (char)b : '.')
                .ToArray());

            Console.WriteLine($"0x{offset + i:X8}  {hex,-48}  {text}");
        }

        return 0;
    }
}
