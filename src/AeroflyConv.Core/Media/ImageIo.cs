using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AeroflyConv.Core.Media;

/// <summary>Image input (ImageSharp) and fast PNG output (<see cref="PngWriter"/>).</summary>
public static class ImageIo
{
    public static readonly string[] SupportedExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tga", ".webp"];

    /// <summary>Loads an image as tightly packed RGBA8 (top row first).</summary>
    public static (byte[] Rgba, int Width, int Height) LoadRgba(string path)
    {
        try
        {
            using Image<Rgba32> image = Image.Load<Rgba32>(path);
            int w = image.Width, h = image.Height;
            var rgba = new byte[w * h * 4];
            image.CopyPixelDataTo(rgba);
            return (rgba, w, h);
        }
        catch (UnknownImageFormatException)
        {
            throw new AeroflyFormatException($"unsupported image format: {Path.GetFileName(path)}");
        }
        catch (InvalidImageContentException ex)
        {
            throw new AeroflyFormatException($"cannot read image {Path.GetFileName(path)}: {ex.Message}", ex);
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
