using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using AeroflyConv.Core.Conversion;

namespace AeroflyConv.Gui.Ui;

/// <summary>One row of the sidebar navigation (a category header or a conversion mode).</summary>
internal sealed class NavEntry
{
    public required string Text { get; init; }
    public ConversionMode Mode { get; init; }
    public bool IsHeader { get; init; }
    public string? Hint { get; init; }
}

/// <summary>Sidebar navigation list with category headers, hover and selection glow.</summary>
internal sealed class NavList : Control
{
    private readonly List<NavEntry> _entries = [];
    private int _hover = -1;
    private int _selected = -1;

    public event EventHandler? SelectionChanged;

    public NavList()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font = Theme.FontBody;
        Cursor = Cursors.Hand;
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value == _selected || value < 0 || value >= _entries.Count || _entries[value].IsHeader) return;
            _selected = value;
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Selects the n-th non-header entry (used by the screenshot test hook).</summary>
    public void SelectIndex(int index)
    {
        int seen = 0;
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].IsHeader) continue;
            if (seen++ == index) { SelectedIndex = i; return; }
        }
    }

    public NavEntry? Selected => _selected >= 0 && _selected < _entries.Count ? _entries[_selected] : null;

    public void SetEntries(IEnumerable<NavEntry> entries)
    {
        _entries.Clear();
        _entries.AddRange(entries);
        _selected = _entries.FindIndex(e => !e.IsHeader);
        Height = MeasureHeight();
        Invalidate();
    }

    public int MeasureHeight()
    {
        float s = DeviceDpi / 96f;
        int h = 0;
        foreach (NavEntry e in _entries) h += (int)((e.IsHeader ? 28 : 36) * s);
        return h + 4;
    }

    private (int Index, Rectangle Bounds)? HitTest(Point p)
    {
        float s = DeviceDpi / 96f;
        int y = 2;
        for (int i = 0; i < _entries.Count; i++)
        {
            int h = (int)((_entries[i].IsHeader ? 28 : 36) * s);
            if (!_entries[i].IsHeader && p.Y >= y && p.Y < y + h)
                return (i, new Rectangle(0, y, Width, h));
            y += h;
        }
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hit = HitTest(e.Location);
        int index = hit?.Index ?? -1;
        if (index != _hover)
        {
            _hover = index;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        var hit = HitTest(e.Location);
        if (hit is not null) SelectedIndex = hit.Value.Index;
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        try { PaintNav(e); }
        catch (Exception ex) { Program.LogCrash("NavList.Paint", ex); }
    }

    private void PaintNav(PaintEventArgs e)
    {
        if (Width <= 3 || Height <= 3) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float s = DeviceDpi / 96f;

        int y = 2;
        for (int i = 0; i < _entries.Count; i++)
        {
            NavEntry entry = _entries[i];
            int h = (int)((entry.IsHeader ? 28 : 36) * s);
            var row = new RectangleF(0, y, Width, h);

            if (entry.IsHeader)
            {
                using var headerBrush = new SolidBrush(Theme.TextDim);
                g.DrawString(entry.Text.ToUpperInvariant(), Theme.FontHeader, headerBrush, 14 * s, y + 10 * s);
            }
            else
            {
                bool selected = i == _selected;
                bool hover = i == _hover;
                var body = new RectangleF(row.X + 6 * s, row.Y + 2 * s, row.Width - 12 * s, h - 4 * s);

                if ((selected || hover) && body.Width > 4 && body.Height > 4)
                {
                    using var path = Theme.RoundedRect(body, 8 * s);
                    if (selected)
                    {
                        using var fill = new LinearGradientBrush(
                            new RectangleF(body.X, body.Y, Math.Max(2f, body.Width), Math.Max(2f, body.Height)),
                            Color.FromArgb(46, Theme.Accent), Color.FromArgb(8, Theme.Accent),
                            LinearGradientMode.Horizontal);
                        g.FillPath(fill, path);
                        using var border = new Pen(Color.FromArgb(70, Theme.Accent), 1f);
                        g.DrawPath(border, path);
                        using var bar = new SolidBrush(Theme.Accent);
                        g.FillRectangle(bar, body.X + 1, body.Y + 7 * s, 3 * s, body.Height - 14 * s);
                    }
                    else
                    {
                        using var fill = new SolidBrush(Color.FromArgb(18, 255, 255, 255));
                        g.FillPath(fill, path);
                    }
                }

                using var textBrush = new SolidBrush(selected ? Theme.Text : Color.FromArgb(206, 214, 226));
                Font font = selected ? Theme.FontItemSelected : Theme.FontItem;
                g.DrawString(entry.Text, font, textBrush, 22 * s, row.Y + (h - font.GetHeight(g)) / 2f);
            }

            y += h;
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        float s = DeviceDpi / 96f;
        int w = Width;
        if (w > 0)
        {
            // keep rows fully visible even before the first layout pass
            Height = MeasureHeight();
        }
    }
}






