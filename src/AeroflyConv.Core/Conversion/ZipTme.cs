using System.IO.Compression;

namespace AeroflyConv.Core.Conversion;

/// <summary>
/// ZIP &lt;-&gt; TME. A <c>.tme</c> is a ZIP with the folder layout the Aerofly mobile
/// loader expects.
/// </summary>
public static class ZipTme
{
    public const string TreeRoot = "dlc_aircraft_dr400/aircraft/dr400/european_air_charter";
    private const string FlatRoot = "european_air_charter/";

    public static void ZipToTme(string input, string output)
    {
        using var zin = ZipFile.OpenRead(input);
        using var zout = ZipFile.Open(output, ZipArchiveMode.Create);
        foreach (ZipArchiveEntry entry in zin.Entries)
        {
            string name = entry.FullName;
            if (name.StartsWith(FlatRoot, StringComparison.Ordinal))
                name = TreeRoot + "/" + name[FlatRoot.Length..];

            ZipArchiveEntry dest = zout.CreateEntry(name, CompressionLevel.Optimal);
            using Stream source = entry.Open();
            using Stream target = dest.Open();
            source.CopyTo(target);
        }
    }

    public static void TmeToZip(string input, string output)
    {
        string prefix = TreeRoot + "/";
        using var zin = ZipFile.OpenRead(input);
        using var zout = ZipFile.Open(output, ZipArchiveMode.Create);
        foreach (ZipArchiveEntry entry in zin.Entries)
        {
            string name = entry.FullName.StartsWith(prefix, StringComparison.Ordinal)
                ? FlatRoot + entry.FullName[prefix.Length..]
                : entry.FullName;

            ZipArchiveEntry dest = zout.CreateEntry(name, CompressionLevel.Optimal);
            using Stream source = entry.Open();
            using Stream target = dest.Open();
            source.CopyTo(target);
        }
    }
}
