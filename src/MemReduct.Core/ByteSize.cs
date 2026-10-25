namespace MemReduct.Core;

/// <summary>Human-readable byte formatting shared by the CLI, the GUI and the logs.</summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Formats a byte count as e.g. <c>1.24 GB</c>. Negative values keep their sign.</summary>
    public static string Format(long bytes, int decimals = 2)
    {
        var negative = bytes < 0;
        var value = negative ? -(double)bytes : bytes;
        var unit = 0;

        while (value >= 1024.0 && unit < Units.Length - 1)
        {
            value /= 1024.0;
            unit++;
        }

        var text = unit == 0
            ? $"{value:0} {Units[unit]}"
            : $"{value.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture)} {Units[unit]}";

        return negative ? "-" + text : text;
    }

    /// <summary>Formats a signed delta with an explicit sign, e.g. <c>+412.50 MB</c>.</summary>
    public static string FormatDelta(long bytes)
    {
        if (bytes > 0)
        {
            return "+" + Format(bytes);
        }

        return Format(bytes);
    }

    /// <summary>Formats a duration compactly, e.g. <c>812 毫秒</c> or <c>2.41 秒</c>.</summary>
    public static string FormatDuration(TimeSpan span)
    {
        return span.TotalSeconds >= 1.0
            ? $"{span.TotalSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} 秒"
            : $"{span.TotalMilliseconds:0} 毫秒";
    }
}
