using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;

namespace MemReduct.Gui;

/// <summary>
/// A pill switch with a label, standing in for <see cref="CheckBox"/>.
/// <para>
/// A checkbox draws a small square whose checked state is a glyph the user has to decode.
/// A switch shows the state as the position of the knob, which is readable without reading —
/// and it gives the settings rows a consistent 20-pixel height to lay out against, where a
/// check box's glyph box and its text baseline disagree by a pixel or two.
/// </para>
/// </summary>
public sealed class Switch : Control
{
    // Scaled properties, not constants. A fixed pill next to 130% type reads as a control from
    // a different application, and the label would sit on top of the pill.
    private static int PillWidth => Theme.S(46);
    private static int PillHeight => Theme.S(24);
    private static int KnobInset => Theme.S(3);
    private static int LabelGap => Theme.S(10);

    private bool _checked;
    private bool _hovered;

    public Switch()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);

        BackColor = Color.Transparent;
        Font = Theme.Body;
        ForeColor = Theme.TextPrimary;
        Cursor = Cursors.Hand;
        TabStop = true;
        Height = PillHeight;

        // Matches FlatButton: an owner-drawn control has to declare what it is before a screen
        // reader or UI Automation client can find it or change it.
        AccessibleRole = AccessibleRole.CheckButton;
    }

    /// <summary>Flips <see cref="Checked"/>.</summary>
    public void Toggle() => Checked = !Checked;

    protected override AccessibleObject CreateAccessibilityInstance() => new SwitchAccessibleObject(this);

    /// <summary>Exposes the on/off state and a Toggle action to UI Automation.</summary>
    private sealed class SwitchAccessibleObject : ControlAccessibleObject
    {
        private readonly Switch _owner;

        public SwitchAccessibleObject(Switch owner)
            : base(owner) => _owner = owner;

        public override string? DefaultAction => "切换";

        public override AccessibleStates State =>
            _owner.Checked ? base.State | AccessibleStates.Checked : base.State;

        public override void DoDefaultAction() => _owner.Toggle();
    }

    /// <summary>Raised after <see cref="Checked"/> changes, whether by click or by code.</summary>
    public event EventHandler? CheckedChanged;

    /// <summary>Whether the switch is on.</summary>
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value)
            {
                return;
            }

            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Text drawn to the right of the pill.</summary>
    // Control.Text is declared [AllowNull], so the override has to accept null too.
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

    /// <summary>Width of the pill plus the gap plus the label.</summary>
    public int PreferredWidth
    {
        get
        {
            var label = string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(Text, Font).Width;
            return PillWidth + (label > 0 ? LabelGap + label : 0);
        }
    }

    public override Size GetPreferredSize(Size proposedSize) =>
        new(PreferredWidth, PillHeight);

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location))
        {
            // Focus first so the keyboard continues from whatever was just clicked, which is
            // what every other control in the window does.
            Focus();
            Checked = !Checked;
        }

        base.OnMouseUp(e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            Checked = !Checked;
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
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        ForeColor = Enabled ? Theme.TextPrimary : Theme.TextMuted;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var on = _checked && Enabled;
        var pill = new Rectangle(0, (Height - PillHeight) / 2, PillWidth - 1, PillHeight - 1);

        var track = on
            ? _hovered ? Theme.AccentHover : Theme.Accent
            : _hovered && Enabled ? Theme.Hover : Theme.SurfaceSunken;

        Theme.DrawSurface(
            g,
            pill,
            track,
            on ? null : _hovered && Enabled ? Theme.BorderStrong : Theme.Border,
            PillHeight / 2);

        // The knob travels the full inner width. Its x is derived from the state rather than
        // animated: a moving knob on a settings row draws the eye away from the value the row
        // is reporting.
        //
        // Size and position come from the pill rectangle, not from PillHeight. The pill is
        // PillHeight - 1 tall so that its border lands on a pixel, and a knob sized off
        // PillHeight is a pixel taller than the space inside it: it used to sit at
        // pill.Y + KnobInset with 3px of gap above and 2px below, which reads as a knob that
        // is not centred.
        var knobSize = pill.Height - (KnobInset * 2);
        var knobX = on
            ? pill.Right - KnobInset - knobSize
            : pill.X + KnobInset;

        var knobRect = new Rectangle(
            knobX,
            pill.Y + ((pill.Height - knobSize) / 2),
            knobSize,
            knobSize);
        using (var knob = new SolidBrush(on ? Color.FromArgb(255, 255, 255) : Theme.TextMuted))
        using (var knobPath = Theme.Rounded(knobRect, knobSize / 2))
        {
            g.FillPath(knob, knobPath);
        }

        if (!string.IsNullOrEmpty(Text))
        {
            var labelBounds = new Rectangle(
                PillWidth + LabelGap,
                0,
                Math.Max(0, Width - PillWidth - LabelGap),
                Height);

            Theme.DrawText(
                g,
                Text,
                Font,
                Enabled ? ForeColor : Theme.TextMuted,
                labelBounds);
        }

        // Keyboard focus is shown as a ring around the pill only, so it never collides with
        // the label's descenders.
        if (Focused)
        {
            using var pen = new Pen(Color.FromArgb(120, Theme.Accent)) { DashStyle = DashStyle.Dot };
            var ring = Rectangle.Inflate(pill, 2, 2);
            using var path = Theme.Rounded(ring, ring.Height / 2);
            g.DrawPath(pen, path);
        }
    }
}
