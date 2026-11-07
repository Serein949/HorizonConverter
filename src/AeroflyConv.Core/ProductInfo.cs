namespace AeroflyConv.Core;

/// <summary>Product naming and version shared by the CLI, the desktop app and the Android app.</summary>
public static class ProductInfo
{
    /// <summary>Chinese product name, used by the phone app.</summary>
    public const string Name = "Aerofly 转换器";

    /// <summary>Latin product name, used by the desktop app and the CLI.</summary>
    public const string NameEn = "Aerofly Converter";

    /// <summary>Release channel shown next to the name.</summary>
    public const string Edition = "Alpha";

    /// <summary>Numeric version (also stamped into the assemblies).</summary>
    public const string Version = "1.21.1";

    /// <summary>"Aerofly 转换器 Alpha"</summary>
    public const string FullName = Name + " " + Edition;

    /// <summary>"Aerofly Converter Alpha"</summary>
    public const string FullNameEn = NameEn + " " + Edition;

    /// <summary>"Aerofly Converter Alpha 1.2" - window titles and banners.</summary>
    public const string Banner = FullNameEn + " " + Version;
}
