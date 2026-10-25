using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;

namespace MemReduct.Gui;

/// <summary>How a <see cref="FlatButton"/> is filled.</summary>
public enum ButtonVariant
{
    /// <summary>Filled with the accent colour. One per screen, for the main action.</summary>
    Primary,

    /// <summary>Raised surface with a border. The default for everything beside the main action.</summary>
    Secondary,

    /// <summary>No fill until hovered. For presets and other low-emphasis controls.</summary>
    Ghost,
}

/// <summary>
/// A fully owner-drawn button.
/// <para>
/// <see cref="Button"/> with <c>FlatStyle.Flat</c> still lets the system paint a focus
/// rectangle, ignores the border colour on hover, and draws its own grey wash on mouse-over —
/// three details that make a dark theme look broken. Painting the whole face here keeps the
/// hover, press and disabled states under the same palette as everything else.
/// </para>
/// </summary>
public sealed class FlatButton : Control
{
    private bool _hovered;
    private bool _pressed;

    public FlatButton()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable |
            ControlStyles.SupportsTransparentBackColor,
            true);

        BackColor = Color.Transparent;
        Font = Theme.Button;
        Size = new Size(96, 32);
        Cursor = Cursors.Hand;
        TabStop = true;

        // A Control that declares no accessibility information is an opaque pane to a screen
        // reader and to UI Automation: it cannot be located by name and nothing can invoke it.
        // The role plus DoDefaultAction below are what make the drawn button a button.
        AccessibleRole = AccessibleRole.PushButton;
    }

    /// <summary>Which fill the button uses.</summary>
    public ButtonVariant Variant { get; set; } = ButtonVariant.Secondary;

    /// <summary>Corner radius. Defaults to the shared control radius.</summary>
    public int Radius { get; set; } = Theme.ControlRadius;

    // Control.Text is declared [AllowNull], so the override has to accept null too or the
    // compiler flags the narrower contract.
    [AllowNull]
    public override string Text
    {
        get => base.Text;
        set
        {
            base.Text = value ?? string.Empty;
            Invalidate();
        }
    }

    public override bool AutoSize
    {
        get => false;
        set => base.AutoSize = false;
    }

    /// <summary>Raises <see cref="Control.Click"/> as if the button had been pressed.</summary>
    public void PerformClick()
    {
        if (Enabled)
        {
            OnClick(EventArgs.Empty);
        }
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            _pressed = true;
            Invalidate();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            _pressed = false;
            Invalidate();
            PerformClick();
            e.Handled = true;
        }

        base.OnKeyUp(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new FlatButtonAccessibleObject(this);

    /// <summary>
    /// Gives the drawn button an Invoke pattern. Without <see cref="DoDefaultAction"/> a UI
    /// Automation client sees the role but has no way to press it.
    /// </summary>
    private sealed class FlatButtonAccessibleObject : ControlAccessibleObject
    {
        private readonly FlatButton _owner;

        public FlatButtonAccessibleObject(FlatButton owner)
            : base(owner) => _owner = owner;

        public override string? DefaultAction => "按下";

        public override void DoDefaultAction() => _owner.PerformClick();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var (fill, border, text) = ResolveColors();

        Theme.DrawSurface(g, ClientRectangle, fill, border, Radius);

        Theme.DrawText(
            g,
            Text,
            Font,
            text,
            ClientRectangle,
            ContentAlignment.MiddleCenter);

        if (Focused)
        {
            // Inset so the ring reads as "this control has focus" rather than as a second border.
            using var path = Theme.Rounded(Rectangle.Inflate(ClientRectangle, -3, -3), Math.Max(2, Radius - 3));
            using var pen = new Pen(Color.FromArgb(130, Theme.Accent)) { DashStyle = DashStyle.Dot };
            g.DrawPath(pen, path);
        }
    }

    private (Color Fill, Color? Border, Color Text) ResolveColors()
    {
        if (!Enabled)
        {
            return Variant switch
            {
                ButtonVariant.Primary => (Blend(Theme.Accent, Theme.Surface, 0.30), null, Theme.TextMuted),
                _ => (Theme.SurfaceHi, Theme.Border, Theme.TextMuted),
            };
        }

        return Variant switch
        {
            ButtonVariant.Primary => (
                _pressed ? Theme.AccentPressed : _hovered ? Theme.AccentHover : Theme.Accent,
                null,
                Theme.OnAccent),

            ButtonVariant.Ghost => (
                _pressed ? Theme.Pressed : _hovered ? Theme.Hover : Theme.Surface,
                _hovered || _pressed ? Theme.BorderStrong : Theme.Border,
                _hovered ? Theme.TextPrimary : Theme.TextSecondary),

            _ => (
                _pressed ? Theme.Pressed : _hovered ? Theme.Hover : Theme.SurfaceHi,
                _hovered || _pressed ? Theme.BorderStrong : Theme.Border,
                Theme.TextPrimary),
        };
    }

    /// <summary>Mixes <paramref name="amount"/> of <paramref name="towards"/> into <paramref name="from"/>.</summary>
    private static Color Blend(Color from, Color towards, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            from.A,
            (int)Math.Round(from.R + ((towards.R - from.R) * amount)),
            (int)Math.Round(from.G + ((towards.G - from.G) * amount)),
            (int)Math.Round(from.B + ((towards.B - from.B) * amount)));
    }
}
