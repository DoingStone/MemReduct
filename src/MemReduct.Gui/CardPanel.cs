namespace MemReduct.Gui;

/// <summary>
/// A rounded card with an optional title strip and a body that hosts child controls.
/// <para>
/// Depth in this interface is carried by surface colour and a hairline border rather than by
/// shadows: a shadow at these sizes is either invisible or a grey smear, while a lighter fill
/// against a darker canvas reads correctly at any scale.
/// </para>
/// <para>
/// Children go into <see cref="Body"/>, not into the card itself. The body is inset below the
/// title strip and inside the card padding, so a child can dock to fill it and never has to
/// know where the title ended.
/// </para>
/// </summary>
public sealed class CardPanel : Panel
{
    private string _title = string.Empty;
    private string _hint = string.Empty;
    private string _titleSuffix = string.Empty;

    public CardPanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw,
            true);

        BackColor = Theme.Canvas;
        ForeColor = Theme.TextPrimary;

        // Opaque, not transparent. A transparent child asks its parent to paint the area behind
        // it, and the parent here draws its face in OnPaint rather than OnPaintBackground — so
        // transparency would reveal the card's BackColor, not its rounded surface. An opaque
        // body of the same colour is both correct and one less indirection.
        Body = new Panel
        {
            BackColor = Theme.Surface,
            Location = new Point(Theme.CardPadding, Theme.CardPadding + Theme.CardTitleHeight),
        };

        Controls.Add(Body);
        Resize += (_, _) => LayoutBody();
    }

    /// <summary>The control children should be added to.</summary>
    public Panel Body { get; }

    /// <summary>Heading drawn at the top-left of the card. Empty hides the strip.</summary>
    public string Title
    {
        get => _title;
        set
        {
            _title = value ?? string.Empty;
            LayoutBody();
            Invalidate();
        }
    }

    /// <summary>
    /// A muted value drawn at the right end of the title strip — the place to put a live
    /// figure that belongs to the whole card rather than to one row inside it.
    /// </summary>
    public string Hint
    {
        get => _hint;
        set
        {
            _hint = value ?? string.Empty;
            Invalidate();
        }
    }

    /// <summary>
    /// A short accent-coloured tag drawn immediately after the title, for a state the card as
    /// a whole is in (for example 已关闭). Empty draws nothing.
    /// </summary>
    public string TitleSuffix
    {
        get => _titleSuffix;
        set
        {
            _titleSuffix = value ?? string.Empty;
            Invalidate();
        }
    }

    /// <summary>Colour of <see cref="TitleSuffix"/>.</summary>
    public Color TitleSuffixColor { get; set; } = Theme.TextMuted;

    private void LayoutBody()
    {
        var top = Theme.CardPadding + (string.IsNullOrEmpty(_title) ? 0 : Theme.CardTitleHeight);

        Body.Location = new Point(Theme.CardPadding, top);
        Body.Size = new Size(
            Math.Max(0, Width - (Theme.CardPadding * 2)),
            Math.Max(0, Height - top - Theme.CardPadding));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.DrawSurface(e.Graphics, ClientRectangle, Theme.Surface, Theme.Border);

        if (string.IsNullOrEmpty(_title))
        {
            return;
        }

        var titleBounds = new Rectangle(
            Theme.CardPadding,
            Theme.CardPadding - 4,
            0,
            Theme.CardTitleHeight);

        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var titleSize = g.MeasureString(_title, Theme.SectionTitle);
        Theme.DrawText(
            g,
            _title,
            Theme.SectionTitle,
            Theme.TextSecondary,
            new Rectangle(titleBounds.X, titleBounds.Y, (int)titleSize.Width + 4, titleBounds.Height));

        var cursor = titleBounds.X + (int)titleSize.Width + 8;

        if (!string.IsNullOrEmpty(_titleSuffix))
        {
            var suffixSize = g.MeasureString(_titleSuffix, Theme.Caption);
            Theme.DrawText(
                g,
                _titleSuffix,
                Theme.Caption,
                TitleSuffixColor,
                new Rectangle(cursor, titleBounds.Y + 2, (int)suffixSize.Width + 4, titleBounds.Height));

            cursor += (int)suffixSize.Width + 8;
        }

        if (!string.IsNullOrEmpty(_hint))
        {
            var left = cursor;
            var right = Width - Theme.CardPadding;
            if (right > left)
            {
                Theme.DrawText(
                    g,
                    _hint,
                    Theme.Caption,
                    Theme.TextMuted,
                    new Rectangle(left, titleBounds.Y, right - left, titleBounds.Height),
                    ContentAlignment.MiddleRight);
            }
        }

        // A hairline under the title separates the heading from the data without needing a
        // second fill colour.
        using var pen = new Pen(Theme.Border);
        g.DrawLine(
            pen,
            Theme.CardPadding,
            Theme.CardPadding + Theme.CardTitleHeight - 8,
            Width - Theme.CardPadding,
            Theme.CardPadding + Theme.CardTitleHeight - 8);
    }
}
