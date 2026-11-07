using AeroflyConv.Core.Conversion;
using AeroflyConv.Core.Media;
using AeroflyConv.Core.Textures;
using AeroflyConv.Core.XPlane;

namespace AeroflyConv.Core.Aerofly;

/// <summary>Writes Aerofly .ttx textures from the X-Plane images referenced by a converted model.</summary>
public static class TtxExporter
{
    /// <summary>Formats Aerofly accepts for each material channel.</summary>
    private static string FormatFor(string channel, bool hasAlpha) => channel switch
    {
        "normal" or "bump" => "type_rgba_s3tc_dxt5",
        _ => hasAlpha ? "type_rgba_s3tc_dxt5" : "type_rgb_s3tc_dxt1",
    };

    public static List<string> Export(IEnumerable<TextureSource> sources, string outDir, bool flipV,
        Action<string>? log = null)
    {
        var written = new List<string>();
        Directory.CreateDirectory(outDir);

        foreach (TextureSource source in sources)
        {
            string target = Path.Combine(outDir, source.TargetName + ".ttx");
            try
            {
                (byte[] rgba, int width, int height, string origin) = Load(source.SourcePath);
                bool hasAlpha = source.AllowAlpha && !channelIgnoresAlpha(source.Channel) && ImageIo.HasUsefulAlpha(rgba);
                string format = FormatFor(source.Channel, hasAlpha);

                if (!IsPowerOfTwo(width) || !IsPowerOfTwo(height))
                {
                    log?.Invoke($"{source.TargetName}: {width}x{height} is not a power of two");
                }

                var encoded = TtxFile.Encode(rgba, width, height, format, flipV, compress: true);
                File.WriteAllBytes(target, encoded.Data);
                written.Add(target);
                log?.Invoke($"{source.TargetName}.ttx  {width}x{height} {format} <- {Path.GetFileName(source.SourcePath)} ({origin})");
            }
            catch (Exception ex)
            {
                log?.Invoke($"{source.TargetName}.ttx FAILED: {ex.Message} ({source.SourcePath})");
            }
        }
        return written;
    }

    private static bool channelIgnoresAlpha(string channel) => channel is "normal" or "bump";

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    private static (byte[] Rgba, int Width, int Height, string Origin) Load(string path)
    {
        if (path.Length == 0) return (Flat(), 8, 8, "generated");
        if (DdsFile.IsDds(path))
        {
            DdsImage image = DdsFile.ParseFile(path);
            return (image.Rgba, image.Width, image.Height, image.Format);
        }
        (byte[] rgba, int width, int height) = ImageIo.LoadRgba(path);
        return (rgba, width, height, Path.GetExtension(path).TrimStart('.').ToLowerInvariant());
    }

    /// <summary>Neutral grey for objects that carry no texture at all in X-Plane.</summary>
    private static byte[] Flat()
    {
        var rgba = new byte[8 * 8 * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 200;
            rgba[i + 1] = 200;
            rgba[i + 2] = 200;
            rgba[i + 3] = 255;
        }
        return rgba;
    }
}
