using System.Drawing;
using System.Drawing.Imaging;

namespace AeroflyConv.Desktop;

/// <summary>
/// Windows image decoding through GDI+ (part of the OS, MIT-licensed wrapper).
/// Supports PNG / JPEG / BMP / GIF / TIFF; WebP and TGA are not available here.
/// </summary>
public static class DesktopImageDecoder
{
    public static void Install() => AeroflyConv.Core.Media.ImageIo.Decoder = Decode;

    public static (byte[] Rgba, int Width, int Height) Decode(string path)
    {
        using var source = new Bitmap(path);
        int width = source.Width, height = source.Height;
        using var canvas = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(canvas))
        {
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        }

        BitmapData data = canvas.LockBits(new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rgba = new byte[width * height * 4];
            int stride = data.Stride;
            for (int y = 0; y < height; y++)
            {
                IntPtr row = IntPtr.Add(data.Scan0, y * stride);
                // GDI+ gives BGRA; the converter wants RGBA.
                System.Runtime.InteropServices.Marshal.Copy(row, rgba, y * width * 4, width * 4);
                for (int i = y * width * 4; i < (y + 1) * width * 4; i += 4)
                    (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
            }
            return (rgba, width, height);
        }
        finally
        {
            canvas.UnlockBits(data);
        }
    }
}
