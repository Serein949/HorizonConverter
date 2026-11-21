namespace AeroflyConv.Core.Media;

/// <summary>
/// Image input. Decoding itself is supplied by the host platform (Windows uses GDI+,
/// Android uses BitmapFactory) so the converter core carries no third-party image codec.
/// </summary>
public static class ImageIo
{
    /// <summary>Extensions the converter accepts; the platform decoder decides what it can read.</summary>
    public static readonly string[] SupportedExtensions =
    [
        ".png", ".jpg", ".jpeg", ".bmp", ".gif",
    ];

    /// <summary>
    /// Decodes an image file. The host registers <see cref="Decoder"/> once at startup
    /// (see the CLI / GUI / Android entry points).
    /// </summary>
    public static Func<string, (byte[] Rgba, int Width, int Height)>? Decoder;

    public static (byte[] Rgba, int Width, int Height) LoadRgba(string path)
    {
        Func<string, (byte[] Rgba, int Width, int Height)>? decoder = Decoder;
        if (decoder is null)
            throw new AeroflyFormatException("no image decoder registered (host did not call ImageIo.Decoder)");

        try
        {
            (byte[] rgba, int width, int height) = decoder(path);
            if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
                throw new AeroflyFormatException($"decoder returned an empty image for {Path.GetFileName(path)}");
            return (rgba, width, height);
        }
        catch (AeroflyFormatException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AeroflyFormatException(
                $"cannot read image {Path.GetFileName(path)}: {ex.Message}", ex);
        }
    }

    public static bool HasUsefulAlpha(ReadOnlySpan<byte> rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] < 255) return true;
        return false;
    }

    /// <summary>Writes a PNG; opaque formats are written as 24-bit RGB like the reference tool.</summary>
    public static void SavePng(string path, ReadOnlySpan<byte> rgba, int width, int height, bool withAlpha,
        PngQuality quality = PngQuality.Fast) =>
        PngWriter.Write(path, rgba, width, height, withAlpha, quality);
}
