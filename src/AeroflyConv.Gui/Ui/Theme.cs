using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace AeroflyConv.Gui.Ui;

/// <summary>
/// Visual language lifted from AirportStudioNative: near-black navy backdrop, two soft
/// cool-white radial highlights, 1px white 10% borders, translucent "glass" cards and a
/// cyan (0, 0.72, 1.0) accent with a green (0.15, 0.92, 0.55) success tone.
/// </summary>
internal static class Theme
{
    public static readonly Color BaseTop = Color.FromArgb(12, 15, 21);
    public static readonly Color BaseBottom = Color.FromArgb(5, 7, 10);
    public static readonly Color Sidebar = Color.FromArgb(9, 11, 15);

    public static readonly Color CardTop = Color.FromArgb(26, 30, 38);
    public static readonly Color CardBottom = Color.FromArgb(17, 20, 26);
    public static readonly Color CardBorder = Color.FromArgb(30, 255, 255, 255);

    public static readonly Color Text = Color.FromArgb(248, 250, 253);
    public static readonly Color TextMuted = Color.FromArgb(150, 158, 172);
    public static readonly Color TextDim = Color.FromArgb(108, 116, 130);

    /// <summary>D2D1::ColorF(0.0f, 0.72f, 1.0f) accent.</summary>
    public static readonly Color Accent = Color.FromArgb(0, 184, 255);
    public static readonly Color AccentDeep = Color.FromArgb(0, 110, 190);
    public static readonly Color Success = Color.FromArgb(38, 235, 140);
    public static readonly Color Danger = Color.FromArgb(255, 112, 112);

    public static readonly Color InputBack = Color.FromArgb(20, 24, 31);
    public static readonly Color LogBack = Color.FromArgb(8, 10, 14);

    public const int CardRadius = 12;
    public const int ButtonRadius = 8;

    public static readonly Font FontBody = new("Segoe UI", 9.5f);
    public static readonly Font FontSmall = new("Segoe UI", 8.5f);
    public static readonly Font FontTitle = new("Segoe UI Semibold", 19f);
    public static readonly Font FontSection = new("Segoe UI Semibold", 10.5f);
    public static readonly Font FontMono = new("Cascadia Mono", 9f);
    public static readonly Font FontItem = new("Segoe UI", 9.5f);
    public static readonly Font FontItemSelected = new("Segoe UI Semibold", 9.5f);
    public static readonly Font FontHeader = new("Segoe UI Semibold", 8f);
    public static readonly Font FontCardTitle = new("Segoe UI Semibold", 8.5f);

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2f;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Paints the shared app backdrop: vertical base gradient + two radial glows.</summary>
    public static void PaintBackdrop(Graphics g, Rectangle bounds)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using (var baseBrush = new LinearGradientBrush(
                   new Rectangle(bounds.X, bounds.Y, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height)),
                   BaseTop, BaseBottom, LinearGradientMode.Vertical))
        {
            g.FillRectangle(baseBrush, bounds);
        }

        // soft chrome highlight, top-left (radius 520 in the reference)
        using (var glow = RadialGlow(new PointF(bounds.Left + bounds.Width * 0.12f, bounds.Top - bounds.Height * 0.15f),
                   bounds.Width * 0.75f, Color.FromArgb(54, 190, 214, 255)))
        {
            g.FillEllipse(glow, bounds.Left - bounds.Width, bounds.Top - bounds.Height,
                bounds.Width * 2.4f, bounds.Height * 2.1f);
        }

        // faint accent bloom, bottom-right
        using (var glow2 = RadialGlow(new PointF(bounds.Right - bounds.Width * 0.05f, bounds.Bottom + bounds.Height * 0.1f),
                   bounds.Width * 0.5f, Color.FromArgb(30, 0, 184, 255)))
        {
            g.FillEllipse(glow2, bounds.Right - bounds.Width * 0.9f, bounds.Bottom - bounds.Height * 0.7f,
                bounds.Width * 1.4f, bounds.Height * 1.4f);
        }
    }

    private static PathGradientBrush RadialGlow(PointF center, float radius, Color color)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(center.X - radius, center.Y - radius, radius * 2, radius * 2);
        var brush = new PathGradientBrush(path)
        {
            CenterPoint = center,
            CenterColor = color,
            SurroundColors = [Color.FromArgb(0, color)],
        };
        return brush;
    }

    /// <summary>Glass card: subtle top-lit gradient, 1px white border, brighter top edge.</summary>
    public static void PaintCard(Graphics g, RectangleF rect, float radius = CardRadius, bool accentEdge = false)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(rect, radius);
        using (var fill = new LinearGradientBrush(
                   new RectangleF(rect.X, rect.Y, rect.Width, Math.Max(2f, rect.Height)),
                   CardTop, CardBottom, LinearGradientMode.Vertical))
        {
            g.FillPath(fill, path);
        }
        using (var border = new Pen(CardBorder, 1f)) g.DrawPath(border, path);

        // bright hairline on the top edge, like the reference chrome
        using var highlight = new Pen(Color.FromArgb(38, 255, 255, 255), 1f);
        g.DrawLine(highlight, rect.X + radius, rect.Y + 0.5f, rect.Right - radius, rect.Y + 0.5f);

        if (accentEdge)
        {
            using var accent = new Pen(Color.FromArgb(70, Accent), 1f);
            g.DrawPath(accent, path);
        }
    }

    public static void EnableModernWindow(IntPtr handle)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;

        // DWMWA_USE_IMMERSIVE_DARK_MODE (20) / legacy 19 on older builds
        int dark = 1;
        int hr20 = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        if (hr20 != 0) DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        Program.Trace($"dwm dark hr={hr20}");

        // DWMWA_WINDOW_CORNER_PREFERENCE (33) = round corners
        int corner = 2;
        DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int));

        // DWMWA_SYSTEMBACKDROP_TYPE (38): 2 = Mica, 3 = Acrylic (best effort, ignored by older builds)
        int backdrop = 3;
        DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}


