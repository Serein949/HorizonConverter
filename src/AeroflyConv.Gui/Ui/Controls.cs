using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace AeroflyConv.Gui.Ui;

/// <summary>Rounded glass card used to group the file list / log.</summary>
internal sealed class GlassCard : Panel
{
    public GlassCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Padding = new Padding(14, 34, 14, 14);
    }

    public string? Title { get; set; }
    public string? Subtitle { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        try { PaintCardSurface(e); }
        catch (Exception ex) { Program.LogCrash("GlassCard.Paint", ex); }
    }

    private void PaintCardSurface(PaintEventArgs e)
    {
        if (Width <= 4 || Height <= 4) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float s = DeviceDpi / 96f;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Theme.PaintCard(g, rect, Theme.CardRadius * s);

        if (Title is not null)
        {
            using var brush = new SolidBrush(Theme.TextDim);
            g.DrawString(Title.ToUpperInvariant(), Theme.FontCardTitle, brush, 16 * s, 10 * s);
        }
        if (Subtitle is not null)
        {
            using var brush = new SolidBrush(Theme.TextDim);
            g.DrawString(Subtitle, Theme.FontSmall, brush, 16 * s, 22 * s);
        }
    }
}

/// <summary>Rounded button: accent-filled ("primary") or translucent glass.</summary>
internal sealed class GlassButton : Control
{
    private bool _hover;
    private bool _down;

    public GlassButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font = new Font("Segoe UI Semibold", 9f);
        Cursor = Cursors.Hand;
        AutoSize = false;
        Height = 36;
    }

    public bool Primary { get; set; }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _down = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _down = true;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _down = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        Invalidate();
        base.OnTextChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float s = DeviceDpi / 96f;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        float radius = Theme.ButtonRadius * s;
        using var path = Theme.RoundedRect(rect, radius);

        bool enabled = Enabled;
        if (Primary)
        {
            Color top = enabled ? Lighten(Theme.Accent, _hover ? 34 : 12) : Color.FromArgb(44, 52, 62);
            Color bottom = enabled ? Theme.AccentDeep : Color.FromArgb(34, 40, 48);
            if (_down) top = Lighten(top, -18);
            using var fill = new LinearGradientBrush(
                new RectangleF(rect.X, rect.Y, Math.Max(2f, rect.Width), Math.Max(2f, rect.Height)),
                top, bottom, LinearGradientMode.Vertical);
            g.FillPath(fill, path);

            if (enabled)
            {
                using var glow = new Pen(Color.FromArgb(_hover ? 120 : 70, Theme.Accent), 1f);
                g.DrawPath(glow, path);
            }
        }
        else
        {
            using var fill = new LinearGradientBrush(
                new RectangleF(rect.X, rect.Y, Math.Max(2f, rect.Width), Math.Max(2f, rect.Height)),
                Color.FromArgb(enabled ? 30 : 16, 255, 255, 255),
                Color.FromArgb(enabled ? 10 : 6, 255, 255, 255),
                LinearGradientMode.Vertical);
            g.FillPath(fill, path);
            using var border = new Pen(Color.FromArgb(enabled ? (_hover ? 56 : 34) : 18, 255, 255, 255), 1f);
            g.DrawPath(border, path);
        }

        Color textColor = !enabled
            ? Color.FromArgb(120, 130, 142)
            : Primary ? Color.FromArgb(6, 14, 20) : Theme.Text;

        using var brush = new SolidBrush(textColor);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(Text, Font, brush, rect, format);
    }

    private static Color Lighten(Color c, int delta) => Color.FromArgb(
        c.A,
        Math.Clamp(c.R + delta, 0, 255),
        Math.Clamp(c.G + delta, 0, 255),
        Math.Clamp(c.B + delta, 0, 255));
}

/// <summary>Rounded progress bar with a cyan gradient fill and a soft glow.</summary>
internal sealed class GlowProgressBar : Control
{
    private int _value;
    private int _maximum = 100;

    public GlowProgressBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Height = 10;
        Maximum = 100;
    }

    public int Maximum
    {
        get => _maximum;
        set
        {
            _maximum = Math.Max(1, value);
            Invalidate();
        }
    }

    public int Value
    {
        get => _value;
        set
        {
            int clamped = Math.Clamp(value, 0, _maximum);
            if (clamped == _value) return;
            _value = clamped;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        float h = Math.Max(4f, Height - 2f);
        var track = new RectangleF(1f, 1f, Math.Max(2f, Width - 2f), h);
        using (var path = Theme.RoundedRect(track, h / 2f))
        using (var fill = new SolidBrush(Color.FromArgb(26, 32, 40)))
        {
            g.FillPath(fill, path);
            using var border = new Pen(Color.FromArgb(26, 255, 255, 255), 1f);
            g.DrawPath(border, path);
        }

        if (_value <= 0) return;
        float fraction = Math.Clamp(_value / (float)_maximum, 0f, 1f);
        float w = Math.Max(h, track.Width * fraction);
        var bar = new RectangleF(track.X, track.Y, w, track.Height);
        using (var path = Theme.RoundedRect(bar, h / 2f))
        {
            using (var glow = new SolidBrush(Color.FromArgb(40, Theme.Accent)))
            {
                var g2 = new RectangleF(bar.X - 1, bar.Y - 1, bar.Width + 2, bar.Height + 2);
                using var glowPath = Theme.RoundedRect(g2, h / 2f + 1);
                g.FillPath(glow, glowPath);
            }
            using var fill = new LinearGradientBrush(
                new RectangleF(bar.X, bar.Y, Math.Max(2f, bar.Width), Math.Max(2f, bar.Height)),
                Color.FromArgb(0, 214, 255), Theme.AccentDeep, LinearGradientMode.Horizontal);
            g.FillPath(fill, path);
        }
    }
}

/// <summary>Dark owner-drawn ComboBox so the format selector matches the theme.</summary>
internal sealed class GlassCombo : ComboBox
{
    public GlassCombo()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        BackColor = Theme.InputBack;
        ForeColor = Theme.Text;
        Font = Theme.FontBody;
        ItemHeight = 24;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? Color.FromArgb(40, Theme.Accent) : Theme.InputBack))
            e.Graphics.FillRectangle(back, e.Bounds);
        using var text = new SolidBrush(selected ? Theme.Text : Color.FromArgb(210, 218, 230));
        var rect = new RectangleF(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height);
        using var format = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        e.Graphics.DrawString(Items[e.Index]?.ToString(), Font, text, rect, format);
    }
}

/// <summary>Dark owner-drawn list box for the queued files.</summary>
internal sealed class GlassListBox : ListBox
{
    public GlassListBox()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        DrawMode = DrawMode.OwnerDrawFixed;
        BorderStyle = BorderStyle.None;
        // A native list box cannot be truly transparent; use a solid tone sampled from the card.
        BackColor = Color.FromArgb(19, 23, 29);
        ForeColor = Theme.Text;
        Font = Theme.FontBody;
        ItemHeight = 24;
        IntegralHeight = false;
        SelectionMode = SelectionMode.MultiExtended;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? Color.FromArgb(46, Theme.Accent) : Color.FromArgb(0, 0, 0, 0)))
            e.Graphics.FillRectangle(back, e.Bounds);
        if (selected)
        {
            using var bar = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(bar, e.Bounds.X, e.Bounds.Y + 4, 3, e.Bounds.Height - 8);
        }
        using var text = new SolidBrush(selected ? Theme.Text : Color.FromArgb(205, 214, 226));
        using var format = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisPath };
        var rect = new RectangleF(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 14, e.Bounds.Height);
        e.Graphics.DrawString(Items[e.Index]?.ToString(), Font, text, rect, format);
    }
}









