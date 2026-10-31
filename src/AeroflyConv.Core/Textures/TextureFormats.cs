using System.Collections.Frozen;

namespace AeroflyConv.Core.Textures;

/// <summary>Metadata for the texture formats found in Aerofly TTX / TTC files.</summary>
public static class TextureFormats
{
    /// <summary>Formats that can be written from an image, with a UI label.</summary>
    public static readonly FrozenDictionary<string, string> Encodable = new Dictionary<string, string>
    {
        ["type_rgba"] = "RGBA (lossless)",
        ["type_r"] = "Grayscale R8",
        ["type_rgb_s3tc_dxt1"] = "DXT1 (BC1, no alpha)",
        ["type_rgba_s3tc_dxt5"] = "DXT5 (BC3, alpha)",
        ["type_rgb_etc2"] = "ETC2 RGB (mobile)",
        ["type_rgba_etc2"] = "ETC2 RGBA (mobile, alpha)",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, string> Aliases = new Dictionary<string, string>
    {
        ["astc6x6"] = "type_rgb_astc_6x6",
        ["astc_6x6"] = "type_rgb_astc_6x6",
        ["etc2"] = "type_rgb_etc2",
        ["dxt1"] = "type_rgb_s3tc_dxt1",
        ["dxt5"] = "type_rgba_s3tc_dxt5",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Formats where "auto" means DXT5 if the image has alpha, else DXT1.</summary>
    public const string AutoDxt = "dxt_auto";

    public static string Canonical(string format) =>
        Aliases.TryGetValue(format, out var mapped) ? mapped : format;

    /// <summary>Opaque GPU RGB formats are written as 24-bit PNG (no alpha channel).</summary>
    public static bool IsOpaqueRgb(string format) =>
        format is "type_rgb_s3tc_dxt1" or "type_rgb_etc2" or "type_rgb_etc1" ||
        format.StartsWith("type_rgb_astc_", StringComparison.Ordinal);

    /// <summary>Bytes occupied by one compressed block (0 for uncompressed formats).</summary>
    public static int BytesPerBlock(string format) => format switch
    {
        "type_rgb_s3tc_dxt1" or "type_rgba_s3tc_dxt1" => 8,
        "type_rgba_s3tc_dxt3" => 16,
        "type_rgba_s3tc_dxt5" => 16,
        "type_rg_rgtc2" => 16,
        "type_rgb_etc2" or "type_rgb_etc1" => 8,
        "type_rgba_etc2" => 16,
        _ when format.StartsWith("type_rgb_astc_", StringComparison.Ordinal) => 16,
        _ when format.StartsWith("type_rgba_astc_", StringComparison.Ordinal) => 16,
        _ => 0,
    };

    /// <summary>ASTC block size parsed from <c>type_rgb_astc_6x6</c> (0 for non-ASTC formats).</summary>
    public static (int W, int H) AstcBlockSize(string format)
    {
        int idx = format.StartsWith("type_rgb_astc_", StringComparison.Ordinal)
            ? "type_rgb_astc_".Length
            : format.StartsWith("type_rgba_astc_", StringComparison.Ordinal)
                ? "type_rgba_astc_".Length
                : 0;
        if (idx == 0) return (0, 0);
        string rest = format[idx..];
        int x = rest.IndexOf('x');
        if (x <= 0) return (0, 0);
        return int.TryParse(rest[..x], out int w) && int.TryParse(rest[(x + 1)..], out int h)
            ? (w, h)
            : (0, 0);
    }

    public static bool IsSupportedDecode(string format) => format switch
    {
        "type_rgba" or "type_r" => true,
        "type_rgb_s3tc_dxt1" or "type_rgba_s3tc_dxt1" => true,
        "type_rgba_s3tc_dxt3" or "type_rgba_s3tc_dxt5" => true,
        "type_rg_rgtc2" => true,
        "type_rgb_etc2" or "type_rgb_etc1" or "type_rgba_etc2" => true,
        _ when format.StartsWith("type_rgb_astc_", StringComparison.Ordinal) => true,
        _ when format.StartsWith("type_rgba_astc_", StringComparison.Ordinal) => true,
        _ => false,
    };
}

