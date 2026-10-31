using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace AeroflyConv.Gui.Ui;

/// <summary>
/// Glass-styled drop-down selector. A plain WinForms ComboBox cannot be themed reliably
/// (it always paints a system-coloured face), so the closed state is drawn here and the
/// list is a dark-rendered ContextMenuStrip.
/// </summary>
internal sealed class GlassSelect : Control
{
    private readonly List<string> _items = [];
    private int _selected = -1;
    private bool _hover;
    private ContextMenuStrip? _menu;

    public event EventHandler? SelectedIndexChanged;

    public GlassSelect()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font = Theme.FontBody;
        Height = 30;
        Cursor = Cursors.Hand;
    }

    public string[] Items => _items.ToArray();

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            int index = value < 0 || value >= _items.Count ? -1 : value;
            if (index == _selected) return;
            _selected = index;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string? SelectedItem => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

    public void SetItems(IEnumerable<string> items, int selected = 0)
    {
        _items.Clear();
        _items.AddRange(items);
        _selected = _items.Count == 0 ? -1 : Math.Clamp(selected, 0, _items.Count - 1);
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || _items.Count == 0) return;
        ShowMenu();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter || (e.Alt && e.KeyCode == Keys.Down))
        {
            ShowMenu();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Down && _selected < _items.Count - 1) SelectedIndex++;
        else if (e.KeyCode == Keys.Up && _selected > 0) SelectedIndex--;
        base.OnKeyDown(e);
    }

    private void ShowMenu()
    {
        _menu?.Dispose();
        _menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            BackColor = Color.FromArgb(22, 26, 33),
            ForeColor = Theme.Text,
            Font = Theme.FontBody,
            ShowImageMargin = false,
            Padding = new Padding(4, 6, 4, 6),
            DropShadowEnabled = true,
        };

        for (int i = 0; i < _items.Count; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem(_items[i])
            {
                ForeColor = index == _selected ? Theme.Accent : Theme.Text,
                BackColor = Color.Transparent,
                Padding = new Padding(6, 4, 6, 4),
                AutoSize = false,
                Width = Math.Max(240, Width),
                Height = 26,
            };
            item.Click += (_, _) => SelectedIndex = index;
            _menu.Items.Add(item);
        }

        _menu.Show(this, new Point(0, Height + 2));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        try { PaintSelect(e); }
        catch (Exception ex) { Program.LogCrash("GlassSelect.Paint", ex); }
    }

    private void PaintSelect(PaintEventArgs e)
    {
        if (Width <= 2 || Height <= 2) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float s = DeviceDpi / 96f;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.RoundedRect(rect, 7 * s);
        using (var fill = new SolidBrush(Color.FromArgb(Enabled ? 232 : 160, 20, 24, 31)))
            g.FillPath(fill, path);
        using (var border = new Pen(Color.FromArgb(Enabled ? (_hover ? 84 : 40) : 20,
                   _hover ? Theme.Accent : Color.White), 1f))
            g.DrawPath(border, path);

        string text = SelectedItem ?? (_items.Count == 0 ? "\u2014" : _items[0]);
        using var brush = new SolidBrush(Enabled ? Theme.Text : Theme.TextDim);
        var textRect = new RectangleF(11 * s, 0, Math.Max(1f, Width - 36 * s), Math.Max(1f, Height));
        using var format = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(text, Font, brush, textRect, format);

        // chevron
        float cx = Width - 16 * s, cy = Height / 2f;
        using var pen = new Pen(Color.FromArgb(Enabled ? 200 : 100, Theme.TextMuted), 1.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        g.DrawLines(pen, [new PointF(cx - 4 * s, cy - 2 * s), new PointF(cx, cy + 3 * s), new PointF(cx + 4 * s, cy - 2 * s)]);
    }
}

/// <summary>Dark renderer for the drop-down menus.</summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColors()) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(2, 1, e.Item.Width - 4, e.Item.Height - 2);
        using var path = Theme.RoundedRect(rect, 6f);
        if (e.Item.Selected)
        {
            using var fill = new SolidBrush(Color.FromArgb(46, Theme.Accent));
            g.FillPath(fill, path);
        }
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var fill = new SolidBrush(Color.FromArgb(20, 24, 30));
        g.FillRectangle(fill, e.AffectedBounds);
        using var border = new Pen(Color.FromArgb(16, 255, 255, 255));
        g.DrawRectangle(border, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Color.FromArgb(40, Theme.Accent);
        public override Color MenuItemBorder => Color.FromArgb(60, Theme.Accent);
        public override Color ToolStripDropDownBackground => Color.FromArgb(20, 24, 30);
        public override Color ImageMarginGradientBegin => Color.FromArgb(20, 24, 30);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(20, 24, 30);
        public override Color ImageMarginGradientEnd => Color.FromArgb(20, 24, 30);
    }
}



