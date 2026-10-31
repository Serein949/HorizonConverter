using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace AeroflyConv.Gui.Ui;

/// <summary>Sidebar brand header: "Aerofly" in white, " Converter" in the accent colour.</summary>
internal sealed class BrandHeader : Control
{
    public BrandHeader()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Height = 96;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float s = DeviceDpi / 96f;

        using var font = new Font("Segoe UI Semibold", 17f);
        const string first = "Aerofly";
        const string second = "Converter";
        Size firstSize = TextRenderer.MeasureText(g, first, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        using var white = new SolidBrush(Theme.Text);
        using var accent = new SolidBrush(Theme.Accent);
        g.DrawString(first, font, white, 22 * s, 20 * s);
        g.DrawString(second, font, accent, 22 * s + firstSize.Width + 4 * s, 20 * s);

        using var tagFont = new Font("Segoe UI", 8.5f);
        using var dim = new SolidBrush(Theme.TextDim);
        g.DrawString("TTX  \u00b7  TSB  \u00b7  TTC  \u00b7  TM", tagFont, dim, 24 * s, 52 * s);
    }
}



