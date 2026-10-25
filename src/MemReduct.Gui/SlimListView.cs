using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace MemReduct.Gui;

/// <summary>
/// A details <see cref="ListView"/> painted in the application palette.
/// <para>
/// The stock control is the one place a dark theme visibly breaks: its column header is drawn
/// by the system in the light theme's grey, so a dark window ends up with a bright bar across
/// the results table. Setting <c>HeaderStyle = Nonclickable</c> does not change that — it only
/// stops the header responding to clicks. Owner-drawing the header, the rows and the sub-items
/// is the only way to bring it into the palette.
/// </para>
/// </summary>
public sealed class SlimListView : ListView
{
    /// <summary>Extra space added to a measured column header so titles are not clipped.</summary>
    private const int HeaderPadding = 13;

    private const int LvmGetHeader = 0x101F;

    private bool _adjustingColumns;

    public SlimListView()
    {
        View = View.Details;
        FullRowSelect = true;
        MultiSelect = false;
        HideSelection = false;
        GridLines = false;
        OwnerDraw = true;
        BorderStyle = BorderStyle.None;
        HeaderStyle = ColumnHeaderStyle.Nonclickable;
        BackColor = Theme.SurfaceSunken;
        ForeColor = Theme.TextPrimary;
        Font = Theme.Body;

        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDarkTheme();

        // The columns are added while the control is still unparented, so the first fit runs with
        // no handle and gives up — see FitColumns. By the time the handle exists the control is
        // already at its final size, so no later ClientSizeChanged arrives to trigger another
        // pass, and the table would keep the placeholder widths from Columns.Add forever: the
        // last column stops short of the control's right edge, leaving the header window to paint
        // the gap in the system theme — a white block across a dark window. Fit once here, after
        // the handle exists, to settle the widths.
        try
        {
            BeginInvoke(() => AutoSizeColumns());
        }
        catch (InvalidOperationException)
        {
            // The window was torn down before the message could be queued; nothing to size.
        }
    }

    /// <summary>
    /// Asks the system for the dark variants of the parts this control does not paint.
    /// <para>
    /// Owner-drawing covers the rows, the sub-items and the column headers, but the scrollbars
    /// and the header's own background are separate windows drawn by the current system theme,
    /// so they stay light on a dark window. The explorer dark theme is the supported way to ask
    /// for the dark variants; there is no managed API for it.
    /// </para>
    /// </summary>
    private void ApplyDarkTheme()
    {
        SetWindowTheme(Handle, "DarkMode_Explorer", null);

        var header = SendMessage(Handle, LvmGetHeader, IntPtr.Zero, IntPtr.Zero);
        if (header != IntPtr.Zero)
        {
            SetWindowTheme(header, "DarkMode_Explorer", null);
        }
    }

    /// <summary>
    /// Re-fits the columns whenever the client area changes size.
    /// <para>
    /// This is what keeps the horizontal scrollbar away. A vertical scrollbar appearing narrows
    /// the client area, which would leave the columns wider than the space and summon a
    /// horizontal one underneath them — and that in turn eats the height the last row needed.
    /// Re-fitting on the client-size change settles it in one extra pass.
    /// </para>
    /// </summary>
    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);

        if (_adjustingColumns || Columns.Count == 0)
        {
            return;
        }

        _adjustingColumns = true;
        try
        {
            AutoSizeColumns();
        }
        finally
        {
            _adjustingColumns = false;
        }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? subAppName, string? subIdList);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Sizes every column to its widest row, then gives the last one whatever is left.
    /// <para>
    /// Last column first: it is the only one that can absorb slack, and measuring it before
    /// the others would make it compete with them for width.
    /// </para>
    /// </summary>
    public void AutoSizeColumns(int lastColumnMinimum = 220)
    {
        // This runs from WndProc by way of OnClientSizeChanged, so it can arrive while the
        // control is still being built or while RebuildUi is tearing the old one down: the
        // columns are added one at a time, and a control mid-disposal no longer has an ambient
        // font to fall back on. A layout recalculation must never be the thing that throws —
        // it surfaced as a NullReferenceException inside WM_WINDOWPOSCHANGED, which the user
        // saw as a fatal-error dialog on a plain window resize.
        try
        {
            FitColumns(lastColumnMinimum);
        }
        catch (Exception ex) when (ex is NullReferenceException
                                      or ObjectDisposedException
                                      or InvalidOperationException
                                      or ArgumentException
                                      or ExternalException)
        {
            // Nothing a layout pass can fail on is worth killing the window over. The sizes are
            // recomputed on the next resize, on the next row, and after the next clean run, so
            // abandoning this pass costs at most a frame of slightly stale column widths — the
            // alternative is a fatal-error dialog over a resize.
        }
    }

    /// <summary>
    /// The fitting itself. Kept separate so <see cref="AutoSizeColumns"/> can be the one place
    /// that decides a failure here is not fatal — see the comment there.
    /// </summary>
    private void FitColumns(int lastColumnMinimum)
    {
        if (Columns.Count == 0 || IsDisposed || Disposing)
        {
            return;
        }

        var font = Font;
        if (font is null)
        {
            return;
        }

        // Fill the client area exactly, with no allowance for the scrollbar. ClientSize already
        // excludes a scrollbar that is showing, so reserving its width as well would leave a
        // strip of the header unpainted — and the header window paints its own background there,
        // which on a dark window is a white block in the corner. The scrollbar only appears after
        // the rows are added, so AddResultRow re-fits once the list is populated; that is what
        // keeps the columns from overflowing into a horizontal scrollbar.
        var available = ClientSize.Width;

        // Measure each column's own content, but never below its header. The handle is checked
        // rather than assumed: this method is reachable from OnClientSizeChanged, which the
        // layout engine can raise before the native list view exists, and CreateGraphics with
        // no handle to bind to is exactly the kind of thing that throws mid-resize.
        if (!IsHandleCreated)
        {
            return;
        }

        // Taken once: the count can change while the rows below are enumerated, and every loop
        // here indexes by this number, so re-reading Columns.Count part-way would let the array
        // and the collection disagree.
        var count = Columns.Count;
        var widths = new int[count];

        using (var g = CreateGraphics())
        {
            for (var i = 0; i < count; i++)
            {
                var header = Columns[i];
                if (header is null)
                {
                    continue;
                }

                widths[i] = (int)Math.Ceiling(g.MeasureString(header.Text ?? string.Empty, font).Width)
                    + HeaderPadding;

                foreach (ListViewItem item in Items)
                {
                    if (i >= item.SubItems.Count)
                    {
                        continue;
                    }

                    var text = item.SubItems[i]?.Text;
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    var measured = (int)Math.Ceiling(g.MeasureString(text, font).Width) + HeaderPadding + 8;
                    widths[i] = Math.Max(widths[i], measured);
                }
            }
        }

        var fixedTotal = 0;
        for (var i = 0; i < count - 1; i++)
        {
            fixedTotal += widths[i];
        }

        widths[^1] = Math.Max(lastColumnMinimum, available - fixedTotal);

        // If the request still overflows, take the excess out of the last column only.
        var overflow = fixedTotal + widths[^1] - available;
        if (overflow > 0)
        {
            widths[^1] = Math.Max(120, widths[^1] - overflow);
        }

        for (var i = 0; i < count; i++)
        {
            // The column can go away between the measurement above and the write here — the
            // count is only a snapshot — and the measurement loop already skips a null column,
            // so the write has to tolerate one too. This was the last unguarded dereference in
            // the method.
            var column = Columns[i];
            if (column is null)
            {
                continue;
            }

            column.Width = widths[i];
        }

        // Changing a column width only invalidates that column, which leaves the dividers of the
        // previous layout painted in the client area wherever no row covers them. Repaint the
        // whole control so the empty body stays a single flat surface.
        Invalidate();
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // The header window spans the whole control, the scrollbar column included, while the
        // client area the columns are laid out in stops short of it. Whatever the columns do not
        // cover keeps the system header background — a light block in the corner of a dark
        // window, sitting right above the scrollbar. The last column therefore paints all the way
        // to the control's right edge instead of to its own.
        var band = e.ColumnIndex == Columns.Count - 1
            ? new Rectangle(e.Bounds.X, e.Bounds.Y, Width - e.Bounds.X, e.Bounds.Height)
            : e.Bounds;

        using (var fill = new SolidBrush(Theme.SurfaceHi))
        {
            g.FillRectangle(fill, band);
        }

        // Centre the text rather than sitting it on the baseline: the rows below draw their
        // values vertically centred, and mismatched alignments make the table look misaligned
        // even when every column is exact.
        var padding = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
        Theme.DrawText(g, e.Header?.Text ?? string.Empty, Theme.Caption, Theme.TextSecondary, padding);

        using var pen = new Pen(Theme.Border);
        g.DrawLine(pen, band.Left, band.Bottom - 1, band.Right, band.Bottom - 1);
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        // Background only. e.DrawDefault stays false so OnDrawSubItem does the text, which is
        // what lets each column be coloured independently of the row.
        var background = e.Item!.Selected
            ? Theme.Hover
            : e.ItemIndex % 2 == 1 ? Theme.RowAlternate : Theme.SurfaceSunken;

        using var brush = new SolidBrush(background);
        e.Graphics.FillRectangle(brush, e.Bounds);
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var color = e.Item!.Selected ? Theme.TextPrimary : e.Item.ForeColor;
        var bounds = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 14), e.Bounds.Height);

        Theme.DrawText(g, e.SubItem!.Text, Font, color, bounds);

        using var pen = new Pen(Theme.RowRule);
        g.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
    }

    private const int WmPaint = 0x000F;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg != WmPaint || Items.Count > 0)
        {
            return;
        }

        // A themed list view paints a divider at every column boundary across its whole client
        // area. Rows cover them, because OnDrawItem fills each row rectangle — but an empty list
        // leaves them standing as stray vertical lines under the header, and there is no draw
        // notification for that region. It is painted over instead.
        var header = SendMessage(Handle, LvmGetHeader, IntPtr.Zero, IntPtr.Zero);
        var top = 0;

        if (header != IntPtr.Zero && GetWindowRect(header, out var rect))
        {
            top = rect.Bottom - rect.Top;
        }

        var body = new Rectangle(0, top, ClientSize.Width, ClientSize.Height - top);
        if (body.Height <= 0 || body.Width <= 0)
        {
            return;
        }

        using var g = Graphics.FromHwnd(Handle);
        using var brush = new SolidBrush(BackColor);
        g.FillRectangle(brush, body);
    }
}
