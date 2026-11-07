using System.Globalization;
using System.Text;
using AeroflyConv.Core.Conversion;
using AeroflyConv.Core.Tm;

namespace AeroflyConv.Core.Aerofly;

/// <summary>Options for building an Aerofly .tmd (dynamic objects / animation binding) file.</summary>
public sealed class TmdBuildOptions
{
    /// <summary>Keep the template's flight model, cameras and systems; only graphics get rebound.</summary>
    public bool KeepFlightModel = true;
    /// <summary>Name of the rigid body the static geometry is attached to.</summary>
    public string BodyName = "Fuselage";
}

/// <summary>Result of a .tmd build.</summary>
public sealed class TmdBuildResult
{
    public string Text = "";
    public List<string> Log = [];
    public List<string> UnboundGeometry = [];
    public int BoundGeometry;
    public int ReboundParts;
}

/// <summary>
/// Rebinds the graphics half of an existing Aerofly .tmd onto a converted X-Plane model.
/// The flight model, cameras and input chains of the template are kept, so the converted model
/// flies on a known-good airframe while its geometry and animation pivots come from X-Plane.
/// </summary>
public static class TmdBuilder
{
    private static readonly string[] GraphicsTypes =
    [
        "rigidbodygraphics", "hingedbodygraphics", "movinggraphics", "rotatingbodygraphics",
        "linkagegraphics", "torsionlinkgraphics", "graphics_translation", "graphics_rotation",
        "graphics_input", "graphics_wheel", "graphics_propeller_blade", "propellergraphics",
        "graphics_conelight", "graphics_pointlight", "graphics_human", "graphics_linear_interpolation",
        "graphics_mapping_linear", "graphics_mix", "graphics_product", "graphics_flasher",
        "graphics_input_color", "graphics_spotlight",
    ];

    public static TmdBuildResult Build(List<TmTextNode> templateRoots, ModelConversionResult model,
        TmdBuildOptions? options = null)
    {
        options ??= new TmdBuildOptions();
        var result = new TmdBuildResult();

        var geometryNames = new HashSet<string>(model.Scene.Geometries.Select(g => g.Name), StringComparer.Ordinal);
        var parts = new Dictionary<string, AnimatedPart>(StringComparer.Ordinal);
        foreach (AnimatedPart part in model.Parts)
            parts[part.Name] = part;

        var used = new HashSet<string>(StringComparer.Ordinal);
        var drawn = new HashSet<string>(StringComparer.Ordinal);
        var remove = new List<TmTextNode>();
        TmTextNode? container = null;

        var templateNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (TmTextNode root in templateRoots)
            foreach (TmTextNode node in root.Descendants())
                templateNames.Add(node.Name);

        // hard references ([string8][Geometry][name]) inside collision hulls / blades must resolve,
        // otherwise the model manager aborts the whole .tmd load, so map them onto our geometry
        RebindHardGeometryRefs(templateRoots, geometryNames, model, used, result);

        foreach (TmTextNode root in templateRoots)
        {
            foreach (TmTextNode node in root.Descendants())
            {
                if (Array.IndexOf(GraphicsTypes, node.Type) < 0) continue;

                TmTextNode? list = node.Child("GeometryList");
                TmTextNode? single = node.Child("Geometry");
                if (list is not null || single is not null)
                {
                    var kept = new List<string>();
                    foreach (string word in list?.ParseWords() ?? [])
                    {
                        // a geometry may only be drawn by one graphics object; reusing it is fatal
                        if (!geometryNames.Contains(word) || drawn.Contains(word)) continue;
                        kept.Add(word);
                        drawn.Add(word);
                        used.Add(word);
                    }

                    string? singleName = single?.Value.Trim();
                    if (singleName is { Length: > 0 })
                    {
                        if (geometryNames.Contains(singleName) && !drawn.Contains(singleName))
                        {
                            drawn.Add(singleName);
                            used.Add(singleName);
                        }
                        else
                        {
                            // hard reference already taken by an earlier object: this object has nothing left
                            result.Log.Add($"drop graphics '{node.Type}/{node.Name}' (geometry '{singleName}' already drawn)");
                            remove.Add(node);
                            continue;
                        }
                    }

                    if (kept.Count == 0 && singleName is not { Length: > 0 })
                    {
                        // an empty graphics object makes the engine abort the whole .tmd load
                        var free = new HashSet<string>(geometryNames.Where(n => !drawn.Contains(n)), StringComparer.Ordinal);
                        string alias = ResolveAlias(node.Name, free, [], allowFallback: false);
                        if (alias.Length > 0)
                        {
                            kept.Add(alias);
                            drawn.Add(alias);
                            used.Add(alias);
                            result.Log.Add($"alias graphics '{node.Type}/{node.Name}' -> '{alias}'");
                        }
                        else
                        {
                            result.Log.Add($"drop graphics '{node.Type}/{node.Name}' (no geometry)");
                            remove.Add(node);
                            continue;
                        }
                    }

                    if (list is not null && kept.Count > 0)
                        list.Value = " " + string.Join(" ", kept) + " ";
                    result.BoundGeometry += kept.Count;
                }

                if (node.Type == "hingedbodygraphics" && parts.TryGetValue(node.Name, out AnimatedPart? part))
                {
                    SetVector(node, "Pivot", part.Pivot);
                    SetVector(node, "Axis", part.Axis);
                    SetScalar(node, "Scaling", 1.0);
                    result.ReboundParts++;
                    result.Log.Add($"rebound {node.Type}/{node.Name} pivot={Format(part.Pivot)} axis={Format(part.Axis)}");
                }
                else if (node.Type == "rotatingbodygraphics" && parts.TryGetValue(node.Name, out AnimatedPart? spinning))
                {
                    SetVector(node, "Pivot", spinning.Pivot);
                    SetVector(node, "Axis", spinning.Axis);
                    SetScalar(node, "Scaling", 1.0);
                    result.ReboundParts++;
                    result.Log.Add($"rebound {node.Type}/{node.Name} pivot={Format(spinning.Pivot)}");
                }

                if (container is null && node.Type == "rigidbodygraphics" && node.Name.EndsWith("Stuff", StringComparison.Ordinal))
                    container = node;
            }
        }

        foreach (TmTextNode dropped in remove) dropped.Remove();

        container ??= templateRoots
            .SelectMany(r => r.Descendants("rigidbodygraphics"))
            .FirstOrDefault(n => n.Name == "FuselageStuff");
        container ??= templateRoots
            .SelectMany(r => r.Descendants("rigidbodygraphics"))
            .FirstOrDefault();

        // every converted geometry the template does not already reference is attached to the body
        foreach (TmbGeometry geometry in model.Scene.Geometries)
        {
            if (used.Contains(geometry.Name) || drawn.Contains(geometry.Name)) continue;
            result.UnboundGeometry.Add(geometry.Name);
            if (container is null) continue;
            if (templateNames.Contains(geometry.Name))
            {
                // the template already has an object with this name; adding another would be a duplicate
                result.Log.Add($"skip duplicate object {geometry.Name}");
                continue;
            }

            var node = new TmTextNode("rigidbodygraphics", geometry.Name);
            node.Add("string8", "GeometryList", " " + geometry.Name + " ");
            node.Add("uint32", "PositionID", options.BodyName + ".R");
            node.Add("uint32", "OrientationID", options.BodyName + ".Q");
            // graphics objects are siblings inside pointer_list_tmgraphics, never nested in another object
            (container.Parent ?? container).Add(node);
            result.BoundGeometry++;
        }

        result.Text = TmTextTree.ToText(templateRoots);
        return result;
    }

    /// <summary>
    /// Maps every hard [string8][Geometry][name] reference of the template onto a geometry that the
    /// converted model actually contains. A dangling reference makes the engine abort the .tmd load.
    /// </summary>
    private static void RebindHardGeometryRefs(List<TmTextNode> roots, HashSet<string> geometryNames,
        ModelConversionResult model, HashSet<string> used, TmdBuildResult result)
    {
        List<TmbGeometry> byArea = model.Scene.Geometries
            .OrderByDescending(g => g.Patches.Sum(p => (long)p.NumFaces))
            .ToList();

        foreach (TmTextNode root in roots)
        {
            foreach (TmTextNode node in root.Descendants())
            {
                if (node.Type != "string8" || node.Name != "Geometry") continue;

                string name = node.Value.Trim();
                if (name.Length == 0) continue;
                if (geometryNames.Contains(name))
                {
                    used.Add(name);
                    continue;
                }

                string alias = ResolveAlias(name, geometryNames, byArea);
                if (alias.Length == 0)
                {
                    result.Log.Add($"unresolved geometry reference '{name}'");
                    continue;
                }

                result.Log.Add($"alias geometry '{name}' -> '{alias}'");
                node.Value = alias;
                used.Add(alias);
            }
        }
    }

    private static readonly string[] AliasKeywords =
        ["Aileron", "Flap", "Elevator", "Rudder", "Propeller", "Glass", "Glas", "Wing", "Fuselage", "Gear"];

    private static string ResolveAlias(string missing, HashSet<string> names, List<TmbGeometry> byArea,
                                       bool allowFallback = true)
    {
        foreach (string keyword in AliasKeywords)
        {
            bool hit = keyword is "Glass" or "Glas"
                ? missing.Contains("Glas", StringComparison.OrdinalIgnoreCase)
                : missing.Contains(keyword, StringComparison.OrdinalIgnoreCase);
            if (!hit) continue;

            // prefer the matching side (LeftX / RightX) when the reference names one
            string? side = missing.StartsWith("Left", StringComparison.OrdinalIgnoreCase) ? "Left"
                : missing.StartsWith("Right", StringComparison.OrdinalIgnoreCase) ? "Right"
                : null;

            if (side is not null)
            {
                string? sided = names.FirstOrDefault(n => n.Contains(side, StringComparison.OrdinalIgnoreCase) &&
                                                           n.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                if (sided is not null) return sided;
            }

            string? match = names.FirstOrDefault(n => n.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            if (keyword is "Glas" or "Glass")
                match ??= names.FirstOrDefault(n => n.Contains("Glas", StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return allowFallback && byArea.Count > 0 ? byArea[0].Name : "";
    }

    private static void SetVector(TmTextNode node, string childName, double[] values)
    {
        TmTextNode? child = node.Child(childName);
        string value = " " + Format(values) + " ";
        if (child is null) node.Add("tmvector3d", childName, value);
        else child.Value = value;
    }

    private static void SetScalar(TmTextNode node, string childName, double value)
    {
        string text = value.ToString("G9", CultureInfo.InvariantCulture);
        TmTextNode? child = node.Child(childName);
        if (child is null) node.Add("float64", childName, text);
        else child.Value = text;
    }

    private static string Format(double[] values) =>
        string.Join(" ", values.Select(v => v.ToString("G9", CultureInfo.InvariantCulture)));
}
