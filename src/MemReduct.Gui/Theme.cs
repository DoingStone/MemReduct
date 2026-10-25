using System.Drawing.Drawing2D;

namespace MemReduct.Gui;

/// <summary>Which palette the interface is drawn in.</summary>
public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>
/// The single source of truth for colour, type and geometry.
/// <para>
/// Everything visual reads from here so the window cannot drift into a dozen slightly
/// different greys. The names are semantic (surface, border, muted) rather than literal,
/// because a palette described as "the dark grey one" stops meaning anything the moment a
/// second dark grey appears.
/// </para>
/// <para>
/// The values are properties rather than readonly fields because the palette and the type
/// scale are both switchable at runtime. Nothing may cache them: a control reads
/// <see cref="Surface"/> while painting, and <see cref="Apply"/> re-creates every font, so a
/// control that copied a font into a field would keep the old size after a scale change.
/// Changing either one therefore means rebuilding the window, which is what
/// <c>MainForm.RebuildUi</c> does.
/// </para>
/// </summary>
internal static class Theme
{
    private static double _scale = 1.0;

    /// <summary>The current palette.</summary>
    public static AppTheme Mode { get; private set; } = AppTheme.Dark;

    /// <summary>The current type scale. 1.0 is the designed size.</summary>
    public static double Scale => _scale;

    /// <summary>Scales a designed pixel measurement to the current type scale.</summary>
    public static int S(int value) => (int)Math.Round(value * _scale);

    // ------------------------------------------------------------------ surfaces

    /// <summary>The window background, behind every card.</summary>
    public static Color Canvas { get; private set; }

    /// <summary>A card face.</summary>
    public static Color Surface { get; private set; }

    /// <summary>A raised element on a card: inputs, secondary buttons, table headers.</summary>
    public static Color SurfaceHi { get; private set; }

    /// <summary>A recessed area: the unfilled part of a track, the results table body.</summary>
    public static Color SurfaceSunken { get; private set; }

    /// <summary>Hover state for anything drawn on <see cref="SurfaceHi"/>.</summary>
    public static Color Hover { get; private set; }

    /// <summary>Pressed state for anything drawn on <see cref="SurfaceHi"/>.</summary>
    public static Color Pressed { get; private set; }

    /// <summary>Every second row of the results table.</summary>
    public static Color RowAlternate { get; private set; }

    // ------------------------------------------------------------------- strokes

    /// <summary>Hairline around cards and inputs.</summary>
    public static Color Border { get; private set; }

    /// <summary>A border that has to hold its own against a lighter fill.</summary>
    public static Color BorderStrong { get; private set; }

    /// <summary>The rule under a table row.</summary>
    public static Color RowRule { get; private set; }

    // ---------------------------------------------------------------------- text
    // Three tiers only. A fourth would be indistinguishable from the third on most displays.

    public static Color TextPrimary { get; private set; }
    public static Color TextSecondary { get; private set; }
    public static Color TextMuted { get; private set; }

    // -------------------------------------------------------------------- accent

    public static Color Accent { get; private set; }
    public static Color AccentHover { get; private set; }
    public static Color AccentPressed { get; private set; }

    /// <summary>Text drawn on top of <see cref="Accent"/>.</summary>
    public static Color OnAccent { get; private set; }

    // ------------------------------------------------------------------- signals
    // Outcome colours. These are about what happened, not about how much memory is in use,
    // which is why they are named separately from the load bands even where the value matches.

    /// <summary>A successful outcome.</summary>
    public static Color Success { get; private set; }

    /// <summary>Something worth noticing that is not a failure.</summary>
    public static Color Warning { get; private set; }

    /// <summary>A failure, or a refusal that needs the user's attention.</summary>
    public static Color Danger { get; private set; }

    // ---------------------------------------------------------------- load bands
    // Shared by the load bar, the tray icon and the load caption, so one reading never shows
    // up in two different colours. The hues are deliberately far apart: at 16 px, two
    // neighbouring oranges are one orange.

    private static Color _band0 = Color.FromArgb(52, 211, 153);  // under 60 %
    private static Color _band1 = Color.FromArgb(234, 179, 8);   // 60–79 %
    private static Color _band2 = Color.FromArgb(249, 115, 22);  // 80–89 %
    private static Color _band3 = Color.FromArgb(239, 68, 68);   // 90 % and up

    /// <summary>The band colour for a memory load, 0–100.</summary>
    public static Color ForLoad(double percent) => percent >= 90 ? _band3
        : percent >= 80 ? _band2
        : percent >= 60 ? _band1
        : _band0;

    // ---------------------------------------------------------------------- type
    // Re-created by Apply, never cached by a control. Every control that needs a font borrows
    // one of these; nothing in the UI constructs a Font per paint or per row, because those are
    // GDI handles and an earlier revision leaked one for every statistic it drew.

    /// <summary>Chinese UI text needs a CJK face. Segoe UI substitutes glyph-by-glyph, which
    /// leaves a mixed Chinese/ASCII string with inconsistent metrics and baseline.</summary>
    public const string FontFamily = "Microsoft YaHei UI";

    public static Font Body { get; private set; } = null!;
    public static Font BodyBold { get; private set; } = null!;
    public static Font Caption { get; private set; } = null!;
    public static Font SectionTitle { get; private set; } = null!;
    public static Font StatValue { get; private set; } = null!;
    public static Font Button { get; private set; } = null!;
    public static Font AppTitle { get; private set; } = null!;

    /// <summary>The hero load figure. Large enough to read from across a desk.</summary>
    public static Font Display { get; private set; } = null!;

    /// <summary>Its unit suffix, set small so the digits keep the weight.</summary>
    public static Font DisplayUnit { get; private set; } = null!;

    // ------------------------------------------------------------------ geometry
    // Corner radii, gaps and paddings are scaled with the type, so a larger interface has
    // proportionally larger rounding rather than the same corner on a bigger box.

    /// <summary>Corner radius for cards.</summary>
    public static int CardRadius => S(14);

    /// <summary>Corner radius for buttons, inputs and switches.</summary>
    public static int ControlRadius => S(10);

    /// <summary>Gap between sibling cards.</summary>
    public static int Gap => S(10);

    /// <summary>Inset from a card's edge to its content.</summary>
    public static int CardPadding => S(16);

    /// <summary>Height of a card's title strip.</summary>
    public static int CardTitleHeight => S(32);

    /// <summary>
    /// Height of one line of <paramref name="font"/>, measured from the font instead of guessed.
    /// A label's box is taller than the em size suggests — ascent and descent of a CJK glyph
    /// reach past it — so a fixed height that looks right for Latin text cuts the top and bottom
    /// off 中文. Measuring here keeps every caption row correct as the type scale and the monitor
    /// DPI change.
    /// </summary>
    public static int LineHeight(Font font)
    {
        // Rounded up, plus a little slack: the renderer's measured box is the tightest fit and
        // a clipped glyph is far worse than two spare pixels.
        return TextRenderer.MeasureText("国Ag", font).Height + S(2);
    }

    /// <summary>
    /// A row tall enough for <paramref name="font"/>, but never shorter than
    /// <paramref name="designedHeight"/>, so the designed rhythm survives while the glyph box
    /// always fits inside it.
    /// </summary>
    public static int RowHeight(Font font, int designedHeight)
    {
        return Math.Max(S(designedHeight), LineHeight(font));
    }

    // ------------------------------------------------------------------- palette

    /// <summary>The scale steps offered in the 界面缩放 menu.</summary>
    public static readonly (string Label, double Scale)[] ScaleSteps =
    [
        ("紧凑", 0.90),
        ("标准", 1.00),
        ("较大", 1.15),
        ("特大", 1.30),
    ];

    /// <summary>Switches palette and type scale together, then re-creates the fonts.</summary>
    public static void Apply(AppTheme mode, double scale)
    {
        Mode = mode;
        _scale = Math.Clamp(scale, 0.75, 2.0);

        if (mode == AppTheme.Light)
        {
            Canvas = Color.FromArgb(238, 241, 246);
            Surface = Color.FromArgb(255, 255, 255);
            SurfaceHi = Color.FromArgb(243, 245, 249);
            SurfaceSunken = Color.FromArgb(250, 251, 253);
            Hover = Color.FromArgb(226, 233, 242);
            Pressed = Color.FromArgb(214, 223, 235);
            RowAlternate = Color.FromArgb(246, 248, 251);

            Border = Color.FromArgb(216, 222, 232);
            BorderStrong = Color.FromArgb(190, 200, 214);
            RowRule = Color.FromArgb(233, 237, 243);

            TextPrimary = Color.FromArgb(24, 32, 44);
            TextSecondary = Color.FromArgb(88, 100, 117);

            // Darkened from (139,149,164): that value cleared the 3:1 bar for incidental marks but
            // only reached 3.03:1 on white, and captions are real text at 8.5 pt. This reaches
            // 5.24:1 on white and 4.63:1 on the canvas.
            TextMuted = Color.FromArgb(98, 109, 126);

            // The accent is darkened for the light palette: the dark-theme green is chosen to
            // glow against near-black and turns pale and low-contrast on white.
            Accent = Color.FromArgb(13, 148, 96);
            AccentHover = Color.FromArgb(16, 170, 110);
            AccentPressed = Color.FromArgb(10, 126, 82);
            OnAccent = Color.FromArgb(255, 255, 255);

            Success = Color.FromArgb(5, 150, 105);
            Warning = Color.FromArgb(180, 83, 9);
            Danger = Color.FromArgb(220, 38, 38);

            _band0 = Color.FromArgb(16, 185, 129);
            _band1 = Color.FromArgb(217, 119, 6);
            _band2 = Color.FromArgb(234, 88, 12);
            _band3 = Color.FromArgb(220, 38, 38);
        }
        else
        {
            // The card has to sit visibly above the canvas. The previous pair (20,26,35) on
            // (11,15,22) measured 1.10:1, which is below the threshold where a dark surface reads
            // as raised at all — the cards dissolved into the background and the whole window
            // looked like one flat sheet. Lifting the surface to (24,32,45) puts 1.17:1 between
            // them while keeping the near-black the palette is built around.
            Canvas = Color.FromArgb(11, 15, 22);
            Surface = Color.FromArgb(24, 32, 45);
            SurfaceHi = Color.FromArgb(32, 42, 58);
            SurfaceSunken = Color.FromArgb(14, 19, 27);
            Hover = Color.FromArgb(44, 57, 75);
            Pressed = Color.FromArgb(18, 24, 33);
            RowAlternate = Color.FromArgb(29, 38, 53);

            // Border had to move most of all. At (35,44,59) it measured 1.24:1 against the card,
            // so the card outlines were effectively invisible and every inset well and chip
            // boundary disappeared with them. (52,66,86) reaches 1.60:1 — still a hairline, but
            // one that defines an edge — and BorderStrong (74,92,116) gives inputs and focus
            // targets a 2.40:1 outline.
            Border = Color.FromArgb(52, 66, 86);
            BorderStrong = Color.FromArgb(74, 92, 116);

            // Row rules follow the same reasoning: 1.05:1 was invisible, so the results table
            // read as one undivided block. (34,44,58) lands at 1.16:1.
            RowRule = Color.FromArgb(34, 44, 58);

            TextPrimary = Color.FromArgb(230, 237, 247);
            TextSecondary = Color.FromArgb(147, 161, 181);

            // Lightened from (100,116,139): that value read as 3.67:1 on a card, which is fine for
            // a rule or an icon but not for 8.5 pt caption text. Against the lifted surface this
            // reaches 4.53:1 on a card, 5.16:1 on a sunken well and 5.31:1 on the canvas.
            TextMuted = Color.FromArgb(120, 136, 158);

            Accent = Color.FromArgb(43, 196, 138);
            AccentHover = Color.FromArgb(56, 214, 155);
            AccentPressed = Color.FromArgb(30, 163, 113);
            OnAccent = Color.FromArgb(6, 32, 24);

            Success = Color.FromArgb(52, 211, 153);
            Warning = Color.FromArgb(245, 176, 65);
            Danger = Color.FromArgb(239, 68, 68);

            _band0 = Color.FromArgb(52, 211, 153);
            _band1 = Color.FromArgb(234, 179, 8);
            _band2 = Color.FromArgb(249, 115, 22);
            _band3 = Color.FromArgb(239, 68, 68);
        }

        Body = Make(10F);
        BodyBold = Make(10F, FontStyle.Bold);
        Caption = Make(8.5F);
        SectionTitle = Make(10.5F, FontStyle.Bold);
        StatValue = Make(10.5F);
        Button = Make(10F, FontStyle.Bold);
        AppTitle = Make(16F, FontStyle.Bold);
        Display = Make(30F, FontStyle.Bold);
        DisplayUnit = Make(12F, FontStyle.Bold);
    }

    private static Font Make(float size, FontStyle style = FontStyle.Regular) =>
        new(FontFamily, (float)Math.Round(size * _scale, 2), style);

    // ------------------------------------------------------------------- drawing

    /// <summary>
    /// A rounded rectangle path. Falls back to a plain rectangle when the radius cannot fit,
    /// because <see cref="GraphicsPath.AddArc"/> with an oversized diameter throws.
    /// </summary>
    public static GraphicsPath Rounded(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();

        // Clamp rather than bail. A caller asking for "half the height" — the natural way to say
        // "make this a pill" — overshoots by one pixel whenever the height is odd, because the
        // diameter is then height + 1. Bailing to AddRectangle in that case silently turns a pill
        // into a square, which is exactly what happened to the switch track at 37x19 with radius
        // PillHeight/2 = 10. The largest radius that fits is the honest reading of the request.
        var limit = Math.Min(rect.Width, rect.Height) / 2;
        if (radius > limit)
        {
            radius = limit;
        }

        var diameter = radius * 2;

        if (diameter <= 0 || diameter > rect.Width || diameter > rect.Height)
        {
            path.AddRectangle(rect);
            return path;
        }

        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Fills and optionally outlines a rounded rectangle.
    /// <para>
    /// The bounds are deflated by one pixel before drawing: a stroke straddles the path, so
    /// outlining the full rectangle would clip its outer half against the control edge and
    /// leave the card looking thinner on the top and left than on the bottom and right.
    /// </para>
    /// <para>
    /// <paramref name="radius"/> defaults to -1, meaning "use the current card radius", rather
    /// than naming <see cref="CardRadius"/> directly: a default parameter value is baked in at
    /// compile time, so it could not follow a scale change.
    /// </para>
    /// </summary>
    public static void DrawSurface(
        Graphics g,
        Rectangle bounds,
        Color fill,
        Color? border = null,
        int radius = -1)
    {
        if (bounds.Width <= 1 || bounds.Height <= 1)
        {
            return;
        }

        if (radius < 0)
        {
            radius = CardRadius;
        }

        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        using (var path = Rounded(rect, radius))
        {
            using (var brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
            }

            if (border is { } stroke)
            {
                using var pen = new Pen(stroke);
                g.DrawPath(pen, path);
            }
        }

        g.SmoothingMode = previous;
    }

    /// <summary>
    /// Draws left-aligned text vertically centred in <paramref name="bounds"/>, with no
    /// wrapping and no padding of its own.
    /// </summary>
    public static void DrawText(
        Graphics g,
        string text,
        Font font,
        Color color,
        Rectangle bounds,
        ContentAlignment alignment = ContentAlignment.MiddleLeft)
    {
        using var brush = new SolidBrush(color);

        var format = new StringFormat(StringFormatFlags.NoWrap)
        {
            Alignment = alignment switch
            {
                ContentAlignment.MiddleRight or ContentAlignment.TopRight or ContentAlignment.BottomRight
                    => StringAlignment.Far,
                ContentAlignment.MiddleCenter or ContentAlignment.TopCenter or ContentAlignment.BottomCenter
                    => StringAlignment.Center,
                _ => StringAlignment.Near,
            },
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
        };

        g.DrawString(text, font, brush, bounds, format);
        format.Dispose();
    }
}
