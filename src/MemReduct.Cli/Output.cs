using System.Text;

namespace MemReduct.Cli;

/// <summary>Minimal colored console helpers shared by the CLI commands.</summary>
internal static class Output
{
    private static bool _colorEnabled = true;

    internal static void Init()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // Redirected output can reject an encoding change; harmless.
        }

        _colorEnabled = !Console.IsOutputRedirected
                       && !string.Equals(Environment.GetEnvironmentVariable("NO_COLOR"), "1", StringComparison.Ordinal)
                       && !string.Equals(Environment.GetEnvironmentVariable("NO_COLOR"), "true", StringComparison.OrdinalIgnoreCase);
    }

    internal static void Write(string text) => Console.Write(text);

    internal static void Line(string text = "") => Console.WriteLine(text);

    internal static void Colored(string text, ConsoleColor color)
    {
        if (!_colorEnabled)
        {
            Console.Write(text);
            return;
        }

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = previous;
    }

    internal static void LineColored(string text, ConsoleColor color)
    {
        Colored(text, color);
        Console.WriteLine();
    }

    internal static void Header(string title)
    {
        Line();
        LineColored(title, ConsoleColor.Cyan);
        LineColored(new string('-', Math.Max(title.Length, 40)), ConsoleColor.DarkGray);
    }

    // Prefixes are padded to the same 7 display columns so Chinese and ASCII rows line up.
    internal static void Success(string text) => LineColored("  成功 " + text, ConsoleColor.Green);

    internal static void Failure(string text) => LineColored("  失败 " + text, ConsoleColor.Red);

    internal static void Warning(string text) => LineColored("  警告 " + text, ConsoleColor.Yellow);

    internal static void Info(string text) => LineColored("       " + text, ConsoleColor.Gray);

    internal static void Field(string label, string value, ConsoleColor valueColor = ConsoleColor.White)
    {
        Console.Write("  ");
        Console.Write(PadDisplay(label, 22));
        Colored(value, valueColor);
        Console.WriteLine();
    }

    /// <summary>
    /// Pads to a target <i>display</i> width, counting East Asian wide characters as two
    /// columns. Plain <c>PadRight</c> counts characters, so a Chinese label would otherwise
    /// push its value out of alignment with ASCII labels sharing the same column.
    /// </summary>
    internal static string PadDisplay(string text, int width)
    {
        var columns = 0;
        foreach (var ch in text)
        {
            columns += IsWide(ch) ? 2 : 1;
        }

        return columns >= width ? text : text + new string(' ', width - columns);
    }

    private static bool IsWide(char ch) =>
        ch >= 0x1100 && (
            ch <= 0x115F ||                                   // Hangul Jamo
            ch is (char)0x2329 or (char)0x232A ||
            (ch >= 0x2E80 && ch <= 0xA4CF && ch != 0x303F) || // CJK radicals … Yi
            (ch >= 0xAC00 && ch <= 0xD7A3) ||                 // Hangul syllables
            (ch >= 0xF900 && ch <= 0xFAFF) ||                 // CJK compatibility ideographs
            (ch >= 0xFE30 && ch <= 0xFE6F) ||                 // CJK compatibility forms
            (ch >= 0xFF00 && ch <= 0xFF60) ||                 // Fullwidth forms
            (ch >= 0xFFE0 && ch <= 0xFFE6));

    /// <summary>Renders a fixed-width load meter, e.g. <c>[####------] 42%</c>.</summary>
    internal static string Bar(double percent, int width = 28)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        var filled = (int)Math.Round(clamped / 100.0 * width);
        return "[" + new string('#', filled) + new string('-', width - filled) + $"] {clamped,5:F1}%";
    }

    internal static ConsoleColor LoadColor(double percent) => percent switch
    {
        >= 90 => ConsoleColor.Red,
        >= 75 => ConsoleColor.Yellow,
        _ => ConsoleColor.Green,
    };
}
