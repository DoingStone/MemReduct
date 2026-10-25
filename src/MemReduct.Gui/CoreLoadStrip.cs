namespace MemReduct.Gui;

/// <summary>
/// One thin bar per logical processor, drawn side by side.
/// <para>
/// A single averaged CPU figure hides the thing people actually look for: one core pinned at
/// 100 % while the other eleven idle reads as 8 % overall. Drawing a bar per processor makes that
/// visible without printing a column of numbers, and the fill colour still comes from the shared
/// load band so a hot core is hot in the same sense as the memory bar above it.
/// </para>
/// </summary>
public sealed class CoreLoadStrip : Control
{
    private double[] _values = [];

    public CoreLoadStrip()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);

        Height = 22;
        BackColor = Color.Transparent;
    }

    /// <summary>
    /// The colour behind the bars. Set explicitly rather than read from the parent, because a
    /// transparent layout panel reports <see cref="Color.Transparent"/> and filling with that
    /// leaves the previous frame on screen — the same reason <see cref="LoadBar"/> carries it.
    /// </summary>
    public Color SurfaceColor { get; set; } = Theme.Surface;

    /// <summary>Per-processor load, 0–100 each. Out-of-range readings are clamped.</summary>
    public void SetValues(IReadOnlyList<double> values)
    {
        var count = values.Count;

        // Skips the repaint when nothing moved. This runs on every refresh tick, and a redraw
        // here costs a dozen rounded paths; on an idle machine the values rarely change at the
        // resolution the caller rounds to.
        //
        // The comparison is made against the stored sample directly rather than against a copy,
        // because the copy was allocated before the comparison and thrown away whenever nothing
        // had moved — which is the common case. It compares the clamped value the paint will
        // actually use, so a reading that clamps onto its previous number still counts as
        // unchanged.
        if (count == _values.Length)
        {
            var unchanged = true;
            for (var i = 0; i < count; i++)
            {
                if (Math.Abs(Math.Clamp(values[i], 0, 100) - _values[i]) > 0.005)
                {
                    unchanged = false;
                    break;
                }
            }

            if (unchanged)
            {
                return;
            }
        }

        // Only a change in processor count needs a new array; an ordinary frame writes in place.
        if (count != _values.Length)
        {
            _values = new double[count];
        }

        for (var i = 0; i < count; i++)
        {
            _values[i] = Math.Clamp(values[i], 0, 100);
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // Paint the surface behind the bars first: without this the previous frame shows through
        // wherever a bar has since got shorter.
        using (var background = new SolidBrush(SurfaceColor))
        {
            g.FillRectangle(background, ClientRectangle);
        }

        if (_values.Length == 0 || Width <= 0 || Height <= 1)
        {
            return;
        }

        // A pixel of track between bars, so the processor count stays readable when most of them
        // are idle and every bar is nearly empty.
        const int gap = 2;
        var barWidth = Math.Max(1, (Width - (gap * (_values.Length - 1))) / _values.Length);
        var radius = Math.Max(2, Math.Min(barWidth, Height - 1) / 2);
        var trackHeight = Math.Max(1, Height - 1);

        for (var i = 0; i < _values.Length; i++)
        {
            var x = i * (barWidth + gap);
            var track = new Rectangle(x, 0, barWidth, trackHeight);

            Theme.DrawSurface(g, track, Theme.SurfaceSunken, Theme.Border, radius);

            // Anchored to the bottom so the bar grows upward like a level meter rather than
            // dropping from the top like a progress bar filling.
            var filled = (int)Math.Round(trackHeight * (_values[i] / 100.0));
            if (filled < 2)
            {
                continue;
            }

            var bar = new Rectangle(x, track.Bottom - filled, barWidth, filled);
            using var path = Theme.Rounded(bar, radius);
            using var brush = new SolidBrush(Theme.ForLoad(_values[i]));
            g.FillPath(brush, path);
        }
    }
}
