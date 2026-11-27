namespace AeroflyConv.Core;

/// <summary>Product naming and version shared by the CLI, the desktop app and the Android app.</summary>
public static class ProductInfo
{
    /// <summary>Product name shown on the phone app and in the window title.</summary>
    public const string Name = "Horizon Converter";

    /// <summary>Latin product name (kept for callers that want the ASCII form).</summary>
    public const string NameEn = "Horizon Converter";

    /// <summary>Numeric version (also stamped into the assemblies).</summary>
    public const string Version = "1.0";

    /// <summary>"Horizon Converter" - single channel, no Alpha/Public split.</summary>
    public const string FullName = Name;

    /// <summary>"Horizon Converter"</summary>
    public const string FullNameEn = NameEn;

    /// <summary>"Horizon Converter 1.0" - window title and CLI banner.</summary>
    public const string Banner = NameEn + " " + Version;

    /// <summary>"Horizon Converter 1.0" - phone builds share the same banner now.</summary>
    public const string PhoneBanner = Name + " " + Version;
}
