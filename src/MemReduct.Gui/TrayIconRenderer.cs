using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace MemReduct.Gui;

/// <summary>
/// Draws the notification-area icon: a solid square, coloured by memory load, with the load
/// number centred in it.
/// <para>
/// The tray is the only place this tool is visible without being opened, so the icon carries
/// the reading instead of showing a static logo. Three decisions follow from that:
/// </para>
/// <list type="bullet">
/// <item>
/// The colour is the whole square rather than a bar inside a frame. At 16 px a frame plus a
/// partial fill spends most of its pixels on the frame, and the part that carries the value —
/// the fill — ends up too small to read at a glance.
/// </item>
/// <item>
/// The number is drawn from a hand-made 3x5 pixel font, not with <c>Graphics.DrawString</c>.
/// A real font at 16 px renders as grey antialiased mush; a 3x5 bitmap font stays crisp
/// because every glyph pixel lands on exactly one icon pixel.
/// </item>
/// <item>
/// No percent sign. A trailing glyph costs a whole glyph cell of width, and width is what
/// caps the scale: at 16 px "49%" fits only at 1x, while "49" fits at 2x and is therefore
/// twice as tall and twice as wide. Legibility of the number is worth more than the unit,
/// which the tooltip already spells out as "内存占用 49%".
/// </item>
/// </list>
/// </summary>
internal static class TrayIconRenderer
{
    /// <summary>Glyph cell size of the built-in bitmap font.</summary>
    private const int GlyphWidth = 3;
    private const int GlyphHeight = 5;

    /// <summary>
    /// One text colour for every band. Measured against the palette below with the WCAG
    /// relative-luminance formula, the worst band is red at 4.70:1 and the best is yellow at
    /// 9.3:1 — so a single near-black stays legible on all of them and no per-band switching
    /// is needed.
    /// </summary>
    private static readonly Color TextColor = Color.FromArgb(17, 24, 39);

    /// <summary>The 3x5 font. Each glyph is five rows of three columns; a <c>#</c> is ink.</summary>
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = ["###", "# #", "# #", "# #", "###"],
        ['1'] = [" # ", "## ", " # ", " # ", "###"],
        ['2'] = ["###", "  #", "###", "#  ", "###"],
        ['3'] = ["###", "  #", "###", "  #", "###"],
        ['4'] = ["# #", "# #", "###", "  #", "  #"],
        ['5'] = ["###", "#  ", "###", "  #", "###"],
        ['6'] = ["###", "#  ", "###", "# #", "###"],
        ['7'] = ["###", "  #", "  #", "  #", "  #"],
        ['8'] = ["###", "# #", "###", "# #", "###"],
        ['9'] = ["###", "# #", "###", "  #", "###"],
    };

    /// <summary>
    /// Renders <paramref name="loadPercent"/> as a solid square icon
    /// <paramref name="size"/> pixels on a side. The caller owns the returned
    /// <see cref="Icon"/> and must dispose it.
    /// </summary>
    internal static Icon Create(double loadPercent, int size)
    {
        var percent = (int)Math.Round(Math.Clamp(loadPercent, 0, 100));

        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            // No smoothing anywhere: this is flat colour plus a bitmap font, and any
            // antialiasing at 16 px turns both the edges and the glyphs into grey mush.
            g.SmoothingMode = SmoothingMode.None;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.Clear(Color.Transparent);

            using (var fill = new SolidBrush(FillColor(percent)))
            {
                g.FillRectangle(fill, 0, 0, size, size);
            }

            DrawCentred(g, percent.ToString(CultureInfo.InvariantCulture), size);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);

            // Clone so the result owns managed memory and the native handle can be released
            // immediately; holding the HICON would leak one GDI object per redraw.
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>Width in pixels of <paramref name="text"/> drawn at <paramref name="scale"/>.</summary>
    private static int WidthAt(string text, int scale) =>
        (((GlyphWidth + 1) * text.Length) - 1) * scale;

    /// <summary>
    /// Draws <paramref name="text"/> as centred as an integer pixel grid allows, at the
    /// largest integer scale that still leaves a pixel of margin on every side. Integer
    /// scaling matters: at 16 px the scale is 1 or 2, and a fractional one would blur every
    /// glyph edge.
    /// </summary>
    private static void DrawCentred(Graphics g, string text, int size)
    {
        var scale = 1;
        while (WidthAt(text, scale + 1) <= size - 2 && (GlyphHeight * (scale + 1)) <= size - 2)
        {
            scale++;
        }

        var width = WidthAt(text, scale);
        var height = GlyphHeight * scale;
        var originX = (size - width) / 2;
        var originY = (size - height) / 2;

        using var ink = new SolidBrush(TextColor);
        for (var i = 0; i < text.Length; i++)
        {
            if (!Glyphs.TryGetValue(text[i], out var rows))
            {
                continue;
            }

            var cellX = originX + (i * (GlyphWidth + 1) * scale);
            for (var row = 0; row < GlyphHeight; row++)
            {
                for (var column = 0; column < GlyphWidth; column++)
                {
                    if (rows[row][column] != '#')
                    {
                        continue;
                    }

                    g.FillRectangle(
                        ink,
                        cellX + (column * scale),
                        originY + (row * scale),
                        scale,
                        scale);
                }
            }
        }
    }

    /// <summary>
    /// Green below 60%, yellow to 80%, orange to 90%, red above — the same thresholds the load
    /// bar and the available-memory figure use, so the tray never disagrees with them. The
    /// four hues are kept far apart on purpose: the icon is 16 px, and neighbouring bands that
    /// are merely different shades read as the same colour at that size.
    /// </summary>
    private static Color FillColor(int percent) => percent switch
    {
        >= 90 => Color.FromArgb(239, 68, 68),
        >= 80 => Color.FromArgb(249, 115, 22),
        >= 60 => Color.FromArgb(234, 179, 8),
        _ => Color.FromArgb(34, 197, 94),
    };

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
