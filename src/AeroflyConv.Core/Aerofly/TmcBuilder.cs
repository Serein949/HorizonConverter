using AeroflyConv.Core.Tm;

namespace AeroflyConv.Core.Aerofly;

/// <summary>Builds the .tmc model-information file of a converted aircraft.</summary>
public static class TmcBuilder
{
    /// <summary>
    /// Clones the model information of an Aerofly aircraft and renames it. Contact spheres,
    /// interior sphere and version stay untouched: they describe the airframe whose flight model
    /// the .tmd keeps.
    /// </summary>
    public static string Build(List<TmTextNode> templateRoots, string displayName, string icao)
    {
        TmTextNode? info = templateRoots
            .SelectMany(r => r.Descendants("modelinformation"))
            .FirstOrDefault();

        if (info is null)
            throw new InvalidDataException("template .tmc has no modelinformation node");

        Set(info, "string8", "DisplayName", displayName);
        Set(info, "string8", "DisplayNameFull", displayName);
        Set(info, "string8t8c", "ICAO", icao);
        return TmTextTree.ToText(templateRoots);
    }

    private static void Set(TmTextNode parent, string type, string name, string value)
    {
        TmTextNode? child = parent.Child(name);
        if (child is null) parent.Add(type, name, value);
        else child.Value = value;
    }
}
