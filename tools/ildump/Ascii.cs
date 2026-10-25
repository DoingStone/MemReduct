using System.Text;

namespace Ildump;

/// <summary>
/// Searches a binary for readable ASCII runs containing a keyword. Materialises the whole
/// file once, which is far cheaper than a page-by-page walk when the caller only needs to
/// know whether a symbol is present.
/// </summary>
internal static class Ascii
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ildump ascii <file> <keyword> [maxHits] [contextBytes]");
            return 2;
        }

        var path = Path.GetFullPath(args[0]);
        var keyword = args[1];
        var maxHits = args.Length > 2 ? int.Parse(args[2]) : 20;
        var context = args.Length > 3 ? int.Parse(args[3]) : 48;

        var bytes = File.ReadAllBytes(path);
        var text = Encoding.Latin1.GetString(bytes);
        var needle = keyword;
        var hits = 0;
        var at = 0;

        while (hits < maxHits)
        {
            var index = text.IndexOf(needle, at, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            var from = Math.Max(0, index - context);
            var to = Math.Min(text.Length, index + needle.Length + context);
            var window = new string(text[from..to]
                .Select(c => c >= 32 && c < 127 ? c : '.')
                .ToArray());

            Console.WriteLine($"0x{index:X8}  {window}");
            hits++;
            at = index + 1;
        }

        Console.WriteLine($"{hits} hit(s) for '{keyword}' in {path} ({bytes.Length:N0} bytes)");
        return hits > 0 ? 0 : 3;
    }
}
