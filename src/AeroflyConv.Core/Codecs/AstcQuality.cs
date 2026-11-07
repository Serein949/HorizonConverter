namespace AeroflyConv.Core.Codecs;

/// <summary>
/// Speed/quality trade-off for <see cref="AstcEncoder"/>. Only affects encoding;
/// decoding is identical for every tier.
/// </summary>
public enum AstcQuality
{
    /// <summary>
    /// Roughly 2x faster than <see cref="Standard"/> for ~0.2 dB less.
    /// Picks fewer weight grids and does a single refinement pass.
    /// </summary>
    Fast = 0,

    /// <summary>
    /// Default. Every candidate grid gets a cheap score, the best few are refined
    /// twice with a +-2 level weight search.
    /// </summary>
    Standard = 1,

    /// <summary>
    /// Refines every candidate grid with a wider weight search. ~1.8x slower
    /// than <see cref="Standard"/> for ~0.1-0.3 dB more.
    /// </summary>
    High = 2,
}

/// <summary>Human readable helpers for <see cref="AstcQuality"/>.</summary>
public static class AstcQualityNames
{
    /// <summary>Stable identifiers used by the CLI, the GUI and the Android app.</summary>
    public static readonly string[] Ids = { "fast", "standard", "high" };

    /// <summary>Chinese labels for the Android UI.</summary>
    public static readonly string[] LabelsZh = { "快速", "标准", "高质量" };

    /// <summary>Parses <c>fast</c>/<c>standard</c>/<c>high</c> (case-insensitive).</summary>
    public static bool TryParse(string? text, out AstcQuality quality)
    {
        quality = AstcQuality.Standard;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        switch (text.Trim().ToLowerInvariant())
        {
            case "fast":
            case "0":
                quality = AstcQuality.Fast;
                return true;
            case "standard":
            case "normal":
            case "1":
                quality = AstcQuality.Standard;
                return true;
            case "high":
            case "best":
            case "2":
                quality = AstcQuality.High;
                return true;
            default:
                return false;
        }
    }
}
