using System.Drawing.Drawing2D;

namespace MemReduct.Gui;

/// <summary>
/// The colour table the menu system reads from.
/// <para>
/// <see cref="ProfessionalColorTable"/> exposes around thirty separate colours, and any one
/// left at its default arrives as a piece of the light theme. They are all set here, including
/// the image margin and the three gradient triplets, because a menu that is dark except for
/// one pale strip looks worse than one that was never themed at all.
/// </para>
/// </summary>
internal sealed class DarkColorTable : ProfessionalColorTable
{
    public override Color MenuStripGradientBegin => Theme.Canvas;
    public override Color MenuStripGradientEnd => Theme.Canvas;

    public override Color ToolStripDropDownBackground => Theme.Surface;
    public override Color ImageMarginGradientBegin => Theme.Surface;
    public override Color ImageMarginGradientMiddle => Theme.Surface;
    public override Color ImageMarginGradientEnd => Theme.Surface;

    public override Color MenuItemSelected => Theme.Hover;
    public override Color MenuItemSelectedGradientBegin => Theme.Hover;
    public override Color MenuItemSelectedGradientEnd => Theme.Hover;

    public override Color MenuItemPressedGradientBegin => Theme.SurfaceHi;
    public override Color MenuItemPressedGradientEnd => Theme.SurfaceHi;
    public override Color MenuItemPressedGradientMiddle => Theme.SurfaceHi;

    // A visible item border would draw a box around whatever the pointer is over.
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuBorder => Theme.BorderStrong;

    public override Color SeparatorDark => Theme.Border;
    public override Color SeparatorLight => Theme.Border;

    public override Color ButtonSelectedHighlight => Theme.Hover;
    public override Color ButtonSelectedBorder => Theme.BorderStrong;
    public override Color ButtonSelectedGradientBegin => Theme.Hover;
    public override Color ButtonSelectedGradientEnd => Theme.Hover;
    public override Color ButtonPressedGradientBegin => Theme.Pressed;
    public override Color ButtonPressedGradientEnd => Theme.Pressed;
    public override Color ButtonCheckedGradientBegin => Theme.SurfaceHi;
    public override Color ButtonCheckedGradientEnd => Theme.SurfaceHi;
    public override Color ButtonCheckedHighlight => Theme.SurfaceHi;
    public override Color ButtonCheckedHighlightBorder => Theme.Border;
}

/// <summary>
/// Draws menus in the application palette.
/// <para>
/// Beyond colour, this replaces the system check mark with a drawn one. The stock glyph is a
/// dark bitmap that all but disappears on a dark menu — a check-marked setting would then read
/// as unchecked, which is worse than not drawing it at all.
/// </para>
/// </summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    /// <summary>
    /// One instance for the whole process. A renderer is a pure drawing strategy that holds no
    /// per-strip state, so sharing one keeps the menu bar, every drop-down and the tray menu
    /// painting identically without allocating a copy per strip.
    /// </summary>
    public static DarkMenuRenderer Shared { get; } = new();

    public DarkMenuRenderer()
        : base(new DarkColorTable())
    {
        // The gradient edge is a light-theme flourish that renders as a bright rim here.
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(
            e.ToolStrip is MenuStrip ? Theme.Canvas : Theme.Surface);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // The menu bar itself has no border; it sits flush on the window background. Drop-downs
        // get one so they separate from whatever they opened over.
        if (e.ToolStrip is MenuStrip)
        {
            return;
        }

        var bounds = e.AffectedBounds;
        using var pen = new Pen(Theme.BorderStrong);
        e.Graphics.DrawRectangle(pen, 0, 0, bounds.Width - 1, bounds.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var item = e.Item;
        if (!item.Enabled)
        {
            return;
        }

        var bounds = new Rectangle(Point.Empty, item.Size);

        if (item.Selected || item.Pressed)
        {
            using var brush = new SolidBrush(item.Pressed ? Theme.SurfaceHi : Theme.Hover);
            e.Graphics.FillRectangle(brush, bounds);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item?.Enabled == false
            ? Theme.TextMuted
            : e.Item?.Selected == true ? Theme.TextPrimary : Theme.TextSecondary;

        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // A tick drawn from two strokes, sized to the item's image slot.
        var box = e.ImageRectangle;
        var cx = box.X + (box.Width / 2) - 4;
        var cy = box.Y + (box.Height / 2);

        using var pen = new Pen(Theme.Accent, 2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        g.DrawLines(
            pen,
            new[]
            {
                new Point(cx, cy),
                new Point(cx + 3, cy + 4),
                new Point(cx + 9, cy - 4),
            });
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Selected == true ? Theme.TextPrimary : Theme.TextMuted;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var mid = e.Item!.Bounds.Height / 2;
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawLine(pen, e.Item.Bounds.Left + 30, mid, e.Item.Bounds.Right - 8, mid);
    }
}
