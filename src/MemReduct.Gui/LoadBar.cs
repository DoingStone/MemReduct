using System.Drawing.Drawing2D;

namespace MemReduct.Gui;

/// <summary>
/// A slim, colour-coded memory-load bar.
/// <para>
/// A stock <see cref="ProgressBar"/> renders in the system accent colour, which says nothing
/// about whether the reading is healthy. This draws its own track and picks the fill colour
/// from the load, so the state is readable at a glance from across the room.
/// </para>
/// </summary>
public sealed class LoadBar : Control
{
    private double _percent;

    public LoadBar()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);

        Height = 10;
        BackColor = Color.Transparent;
    }

    /// <summary>
    /// The colour behind the track. Set explicitly rather than read from the parent, because a
    /// transparent layout panel reports <see cref="Color.Transparent"/> and filling with that
    /// is a no-op that leaves the previous frame's bar on screen.
    /// </summary>
    public Color SurfaceColor { get; set; } = Theme.Surface;

    /// <summary>Current load, 0–100. Values outside the range are clamped.</summary>
    public double Percent
    {
        get => _percent;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (Math.Abs(clamped - _percent) < 0.005)
            {
                return;
            }

            _percent = clamped;
            Invalidate();
        }
    }

    /// <summary>Fill colour for the current load, taken from the shared band palette.</summary>
    public Color FillColor => Theme.ForLoad(_percent);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Paint the surface behind the bar first: the control is transparent, and without this
        // the previous frame's bar shows through wherever the new one is shorter.
        using (var background = new SolidBrush(SurfaceColor))
        {
            g.FillRectangle(background, ClientRectangle);
        }

        var radius = Math.Max(2, Height / 2);
        var track = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));

        Theme.DrawSurface(g, track, Theme.SurfaceSunken, Theme.Border, radius);

        var filled = (int)Math.Round(track.Width * (_percent / 100.0));
        if (filled < 3)
        {
            return;
        }

        var bar = new Rectangle(0, 0, filled, track.Height);

        // A slight top-to-bottom lightening keeps a long bar from reading as a flat slab. The
        // two stops are close together on purpose: a strong gradient looks like a loading
        // animation and implies progress toward something, which this is not.
        var baseColor = FillColor;
        using var barPath = Theme.Rounded(bar, radius);
        using (var brush = new LinearGradientBrush(
            bar,
            Lighten(baseColor, 0.12),
            baseColor,
            LinearGradientMode.Vertical))
        {
            g.FillPath(brush, barPath);
        }
    }

    private static Color Lighten(Color color, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            color.A,
            (int)Math.Round(color.R + ((255 - color.R) * amount)),
            (int)Math.Round(color.G + ((255 - color.G) * amount)),
            (int)Math.Round(color.B + ((255 - color.B) * amount)));
    }
}
