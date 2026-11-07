using System.Globalization;
using System.Text;
using AeroflyConv.Core.Aerofly;
using AeroflyConv.Core.XPlane;

namespace AeroflyConv.Core.Conversion;

/// <summary>Options controlling the OBJ8 to .tmb conversion.</summary>
public sealed class XpConvertOptions
{
    /// <summary>Flip texture V coordinates (X-Plane uses v down, Aerofly v up).</summary>
    public bool FlipV = true;
    /// <summary>Aerofly culls the opposite winding of X-Plane OBJ8.</summary>
    public bool FlipWinding = true;
    /// <summary>Uniform scale applied to positions and pivots.</summary>
    public double Scale = 1.0;
    /// <summary>Triangle budget for the generated collision hull of each geometry.</summary>
    public int CollisionTriangleBudget = 1200;
    /// <summary>Skip objects flagged as cockpit interiors.</summary>
    public bool SkipInterior;
    /// <summary>Merge static geometry per source object instead of per object+material.</summary>
    public bool MergeStatic = true;
    /// <summary>Emit per group conversion decisions as warnings, for diagnosis.</summary>
    public bool Debug;
}

/// <summary>An animated part extracted from X-Plane OBJ8 animation stacks.</summary>
public sealed class AnimatedPart
{
    /// <summary>Name of the geometry this part drives (matches a TMB geometry name).</summary>
    public string Name = "";
    /// <summary>Normalised role: LeftAileron, RightAileron, Elevator, Rudder, LeftFlap, ...</summary>
    public string Role = "";
    /// <summary>Pivot in Aerofly space (metres, aircraft frame).</summary>
    public double[] Pivot = new double[3];
    /// <summary>Rotation axis in Aerofly space (unit length).</summary>
    public double[] Axis = new double[3];
    /// <summary>X-Plane datarefs that drove this part.</summary>
    public List<string> Datarefs = [];
    /// <summary>Maximum deflection in degrees observed on the driving dataref.</summary>
    public double MaxDegrees;
    /// <summary>Centroid of the part in Aerofly space, used for side detection.</summary>
    public double[] Centroid = new double[3];
}

/// <summary>A source texture that must be converted into .ttx files.</summary>
public sealed class TextureSource
{
    public string BaseName = "";
    public string Channel = "diffuse";
    public string SourcePath = "";
    /// <summary>Name of the .ttx file (without extension) referenced by the material.</summary>
    public string TargetName = "";
    /// <summary>Only glass may carry alpha; elsewhere alpha turns solid skins transparent.</summary>
    public bool AllowAlpha;
}

/// <summary>Result of converting one X-Plane aircraft folder into Aerofly data.</summary>
public sealed class ModelConversionResult
{
    public TmbScene Scene = new();
    public readonly List<AnimatedPart> Parts = [];
    public readonly List<TextureSource> Textures = [];
    public readonly List<string> Warnings = [];
    public int ObjectsRead;
    public int ObjectsSkipped;
    public int Triangles;
}

/// <summary>Converts X-Plane OBJ8 objects into an Aerofly <see cref="TmbScene"/>.</summary>
public static class XpModelConverter
{
    private static readonly (string Needle, string Role)[] RoleRules =
    [
        ("wing/aileron", "Aileron"),
        ("wing/flap", "Flap"),
        ("wing/elevator", "Elevator"),
        ("wing/rudder", "Rudder"),
        ("controls/elevator_trim", "ElevatorTrim"),
        ("controls/rudder_trim", "RudderTrim"),
        ("controls/aileron_trim", "AileronTrim"),
        ("gear/deploy_ratio", "Gear"),
        ("engine/prop_rotation_angle", "Propeller"),
        ("engines/prop_rotation_angle", "Propeller"),
    ];

    public static ModelConversionResult Convert(AcfFile acf, XpConvertOptions? options = null)
    {
        options ??= new XpConvertOptions();
        var result = new ModelConversionResult();
        var materials = new Dictionary<string, TmbMaterial>(StringComparer.Ordinal);
        var geometries = new Dictionary<string, TmbGeometry>(StringComparer.Ordinal);
        var geometryIds = new Dictionary<string, int>(StringComparer.Ordinal);
        int nextGeometryId = 1000;
        var unbound = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var obj in acf.Objects)
        {
            if (obj.FullPath.Length == 0)
            {
                result.Warnings.Add($"missing object file: {obj.File}");
                result.ObjectsSkipped++;
                continue;
            }
            if (options.SkipInterior && obj.IsInternal)
            {
                result.ObjectsSkipped++;
                continue;
            }

            Obj8File file;
            try
            {
                file = Obj8File.ParseFile(obj.FullPath);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{Path.GetFileName(obj.FullPath)}: {ex.Message}");
                result.ObjectsSkipped++;
                continue;
            }
            result.ObjectsRead++;

            string objectName = Sanitize(Path.GetFileNameWithoutExtension(obj.FullPath));
            bool interior = obj.IsInternal || LooksInterior(objectName);

            foreach (var group in file.Runs.GroupBy(r => (r.Texture, AnimKey(r.Anim))))
            {
                Obj8Run first = group.First();
                List<int> triangles = CollectTriangles(file, group);
                if (triangles.Count == 0) continue;

                string controlRole = ControlRole(first.Anim);
                string geometryName;
                if (controlRole.Length > 0)
                {
                    // side is derived from the centroid once the vertices are known: park in a temp key
                    geometryName = controlRole;
                }
                else
                {
                    geometryName = objectName;
                }

                string materialKey = MaterialKey(first.Texture, file.TextureNormal, file.TextureLit);
                if (!materials.TryGetValue(materialKey, out TmbMaterial? material))
                {
                    material = BuildMaterial(first, interior);
                    materials[materialKey] = material;
                    RegisterTextures(first, file, material, result);
                }

                int materialId = result.Scene.AddMaterial(material);
                if (options.Debug)
                {
                    double dy = 0, minX = 1e9, maxX = -1e9;
                    foreach (int tri in triangles)
                    {
                        dy += file.Vertices[tri].X;
                        minX = Math.Min(minX, file.Vertices[tri].X);
                        maxX = Math.Max(maxX, file.Vertices[tri].X);
                    }
                    dy = -dy / Math.Max(1, triangles.Count);
                    result.Warnings.Add($"[debug] {objectName} role='{controlRole}' tris={triangles.Count / 3} " +
                                        $"centroidY={dy:0.###} xpX=[{minX:0.##},{maxX:0.##}] run={first.Line}");
                }
                (float[] vertices, uint[] indices) = BuildPatch(file, triangles, options);

                if (geometryName == controlRole && controlRole.Length > 0)
                {
                    // decide the side from the geometry's own centroid and merge into that part
                    double cy = 0;
                    for (int i = 0; i < vertices.Length; i += 8) cy += vertices[i + 1];
                    cy /= Math.Max(1, vertices.Length / 8);
                    // Only the ailerons and flaps exist twice per wing; elevator and rudder are shared.
                    string side = controlRole is "Aileron" or "Flap" && Math.Abs(cy) > 0.05
                        ? cy > 0 ? "Left" : "Right"
                        : "";
                    geometryName = side + controlRole;
                }

                if (!geometries.TryGetValue(geometryName, out TmbGeometry? geometry))
                {
                    geometry = new TmbGeometry { Id = nextGeometryId++, Name = geometryName };
                    geometries[geometryName] = geometry;
                    geometryIds[geometryName] = geometry.Id;
                    result.Scene.Geometries.Add(geometry);
                }

                geometry.Patches.Add(new TmbPatch
                {
                    NumFaces = indices.Length / 3,
                    MaterialId = materialId,
                    Vertices = vertices,
                    Indices = indices,
                });

                if (first.Anim.IsAnimated && controlRole.Length > 0)
                {
                    AddAnimatedPart(result, geometry, first.Anim, vertices, geometryName, controlRole);
                }
                else if (first.Anim.IsAnimated)
                {
                    // Report unbound animations once per object instead of once per run.
                    if (!unbound.TryGetValue(objectName, out HashSet<string>? datarefs))
                        unbound[objectName] = datarefs = new HashSet<string>(StringComparer.Ordinal);
                    foreach (string dataref in first.Anim.Datarefs) datarefs.Add(dataref);
                }
            }
        }

        foreach (var pair in unbound.OrderBy(p => p.Key, StringComparer.Ordinal))
            result.Warnings.Add($"{pair.Key}: unbound animations ({pair.Value.Count}): " +
                                string.Join(", ", pair.Value.OrderBy(d => d, StringComparer.Ordinal)));

        foreach (TmbGeometry geometry in result.Scene.Geometries)
            BuildCollision(geometry, options.CollisionTriangleBudget);

        result.Triangles = result.Scene.TriangleCount;
        return result;
    }

    private static string AnimKey(Obj8AnimStack stack) => stack.Key;

    private static string RoleOf(string dataref)
    {
        foreach ((string needle, string role) in RoleRules)
            if (dataref.Contains(needle, StringComparison.Ordinal)) return role;
        return "";
    }

    /// <summary>Trim tabs are secondary: a surface's own hinge wins over the trim tab wrapped around it.</summary>
    private static int RolePriority(string role) => role.EndsWith("Trim", StringComparison.Ordinal) ? 1 : 0;

    /// <summary>A control surface found inside an animation stack.</summary>
    private readonly record struct ControlHit(Obj8AnimCommand Command, string Role, double PX, double PY, double PZ);

    /// <summary>
    /// Picks the animation command that drives a control surface. X-Plane nests transforms, so the
    /// innermost rotation that names a control is the surface hinge; the pivot is the sum of the
    /// translations that precede it (the trans(-p)/rotate/trans(+p) sandwich).
    /// </summary>
    private static ControlHit? SelectControl(Obj8AnimStack stack)
    {
        ControlHit? best = null;
        int bestPriority = int.MaxValue;
        double px = 0, py = 0, pz = 0;

        foreach (Obj8AnimCommand command in stack.Commands)
        {
            if (command.Kind == Obj8AnimKind.Translate)
            {
                px += command.X;
                py += command.Y;
                pz += command.Z;
                continue;
            }
            if (command.Kind != Obj8AnimKind.Rotate || command.Dataref == "none") continue;

            string role = RoleOf(command.Dataref);
            if (role.Length == 0) continue;
            int priority = RolePriority(role);
            // innermost wins within the same priority, so keep overwriting on <=
            if (priority <= bestPriority)
            {
                bestPriority = priority;
                best = new ControlHit(command, role, px, py, pz);
            }
        }
        return best;
    }

    private static string ControlRole(Obj8AnimStack stack) => SelectControl(stack)?.Role ?? "";

    private static List<int> CollectTriangles(Obj8File file, IEnumerable<Obj8Run> runs)
    {
        var triangles = new List<int>();
        foreach (Obj8Run run in runs)
        {
            int offset = run.IndexOffset;
            int count = Math.Min(run.IndexCount, file.Indices.Count - offset);
            for (int i = 0; i + 2 < count; i += 3)
            {
                int a = file.Indices[offset + i];
                int b = file.Indices[offset + i + 1];
                int c = file.Indices[offset + i + 2];
                if ((uint)a >= (uint)file.Vertices.Count ||
                    (uint)b >= (uint)file.Vertices.Count ||
                    (uint)c >= (uint)file.Vertices.Count) continue;
                if (a == b || b == c || a == c) continue;
                triangles.Add(a);
                triangles.Add(b);
                triangles.Add(c);
            }
        }
        return triangles;
    }

    /// <summary>Welds the referenced vertices and converts them into Aerofly space.</summary>
    private static (float[] Vertices, uint[] Indices) BuildPatch(Obj8File file, List<int> triangles,
        XpConvertOptions options)
    {
        var map = new Dictionary<(int, int, int, int, int, int, int, int), uint>(triangles.Count);
        var vertices = new List<float>(triangles.Count * 8);
        var indices = new List<uint>(triangles.Count);

        foreach (int index in triangles)
        {
            Obj8Vertex v = file.Vertices[index];
            float u = v.U;
            float w = options.FlipV ? 1f - v.V : v.V;
            var key = (Bits(v.X), Bits(v.Y), Bits(v.Z), Bits(v.Nx), Bits(v.Ny), Bits(v.Nz), Bits(u), Bits(w));
            if (!map.TryGetValue(key, out uint id))
            {
                id = (uint)(vertices.Count / 8);
                map[key] = id;
                double scale = options.Scale;
                vertices.Add((float)(-v.Z * scale));
                vertices.Add((float)(-v.X * scale));
                vertices.Add((float)(v.Y * scale));
                // X-Plane declares a triangle front-facing when its vertices run clockwise; the
                // Aerofly renderer wants the opposite, so the winding is reversed below. The
                // transform itself is a proper rotation, so the normals are carried over unchanged.
                vertices.Add((float)(-v.Nz));
                vertices.Add((float)(-v.Nx));
                vertices.Add((float)(v.Ny));
                vertices.Add(u);
                vertices.Add(w);
            }
            indices.Add(id);
        }
        if (options.FlipWinding)
            for (int i = 0; i + 2 < indices.Count; i += 3)
                (indices[i], indices[i + 2]) = (indices[i + 2], indices[i]);
        return (vertices.ToArray(), indices.ToArray());
    }

    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

    private static string MaterialKey(string texture, string normal, string lit) =>
        $"{texture}|{normal}|{lit}";

    private static TmbMaterial BuildMaterial(Obj8Run run, bool interior)
    {
        string baseName = Sanitize(Path.GetFileNameWithoutExtension(run.Texture));
        if (baseName.Length == 0) baseName = "Default";
        var material = new TmbMaterial
        {
            Name = baseName,
            ShaderHint = run.BlendGlass ? "glass exterior" : interior ? "pbr interior" : "pbr exterior",
        };
        material.Textures.Add(new TmbTextureRef { Channel = "diffuse", Name = baseName + "_color" });
        return material;
    }

    private static void RegisterTextures(Obj8Run run, Obj8File file, TmbMaterial material,
        ModelConversionResult result)
    {
        string dir = Path.GetDirectoryName(file.Path) ?? "";
        string baseName = Sanitize(Path.GetFileNameWithoutExtension(run.Texture));
        if (baseName.Length == 0) baseName = "Default";

        AddTexture(result, material, baseName, "diffuse", Path.Combine(dir, run.Texture), baseName + "_color", run.BlendGlass);
        if (file.TextureNormal.Length > 0)
            AddTexture(result, material, baseName, "normal", Path.Combine(dir, file.TextureNormal), baseName + "_normal");
        if (file.TextureLit.Length > 0)
            AddTexture(result, material, baseName, "illumination", Path.Combine(dir, file.TextureLit), baseName + "_illumination");
    }

    private static void AddTexture(ModelConversionResult result, TmbMaterial material, string baseName,
        string channel, string sourcePath, string targetName, bool allowAlpha = false)
    {
        if (material.Texture(channel) is null)
            material.Textures.Add(new TmbTextureRef { Channel = channel, Name = targetName });

        if (!File.Exists(sourcePath)) sourcePath = ResolveTextureVariant(sourcePath);
        if (!File.Exists(sourcePath))
        {
            // X-Plane objects without a TEXTURE command get a flat generated material instead of a hole
            result.Warnings.Add($"{channel} texture missing: {sourcePath} (using flat grey)");
            sourcePath = "";
        }
        if (result.Textures.Any(t => t.TargetName == targetName)) return;
        result.Textures.Add(new TextureSource
        {
            BaseName = baseName,
            Channel = channel,
            SourcePath = sourcePath,
            TargetName = targetName,
            AllowAlpha = allowAlpha,
        });
    }

    /// <summary>X-Plane often references a .png while only the .dds sibling ships (or vice versa).</summary>
    private static string ResolveTextureVariant(string path)
    {
        string stem = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path));
        foreach (string extension in new[] { ".dds", ".png", ".jpg", ".tga", ".DDS", ".PNG" })
            if (File.Exists(stem + extension)) return stem + extension;
        return path;
    }

    private static void AddAnimatedPart(ModelConversionResult result, TmbGeometry geometry, Obj8AnimStack stack,
        float[] vertices, string geometryName, string role)
    {
        // The trans(-p)/rotate/trans(+p) sandwich means the translations before the chosen rotate sum
        // to exactly the pivot, so SelectControl captured them at that point of the stack.
        if (SelectControl(stack) is not ControlHit hit)
        {
            result.Warnings.Add($"{geometryName}: no rotating command found, geometry stays static");
            return;
        }
        Obj8AnimCommand rotate = hit.Command;
        double px = hit.PX, py = hit.PY, pz = hit.PZ;

        double ax = -rotate.Z, ay = -rotate.X, az = rotate.Y;
        double length = Math.Sqrt(ax * ax + ay * ay + az * az);
        if (length < 1e-9) length = 1;
        ax /= length;
        ay /= length;
        az /= length;

        double cx = 0, cy = 0, cz = 0;
        int count = vertices.Length / 8;
        for (int i = 0; i < vertices.Length; i += 8)
        {
            cx += vertices[i];
            cy += vertices[i + 1];
            cz += vertices[i + 2];
        }
        if (count > 0)
        {
            cx /= count;
            cy /= count;
            cz /= count;
        }

        AnimatedPart? existing = result.Parts.FirstOrDefault(p => p.Name == geometryName);
        if (existing is not null) return;

        result.Parts.Add(new AnimatedPart
        {
            Name = geometryName,
            Role = role,
            Pivot = [-pz, -px, py],
            Axis = [ax, ay, az],
            Datarefs = [.. stack.Datarefs],
            MaxDegrees = Math.Max(Math.Abs(rotate.V1), Math.Abs(rotate.V2)),
            Centroid = [cx, cy, cz],
        });
    }

    private static void BuildCollision(TmbGeometry geometry, int budget)
    {
        var points = new Dictionary<(int, int, int), uint>();
        int total = geometry.Patches.Sum(p => p.NumFaces);
        int step = Math.Max(1, total / Math.Max(1, budget));

        int seen = 0;
        foreach (TmbPatch patch in geometry.Patches)
        {
            for (int f = 0; f < patch.NumFaces; f++, seen++)
            {
                if (seen % step != 0) continue;
                for (int k = 0; k < 3; k++)
                {
                    uint index = patch.Indices[f * 3 + k];
                    int at = (int)index * patch.VertexSize;
                    var key = (Bits(patch.Vertices[at]), Bits(patch.Vertices[at + 1]), Bits(patch.Vertices[at + 2]));
                    if (!points.TryGetValue(key, out uint id))
                    {
                        id = (uint)(geometry.CollisionPoints.Count / 3);
                        points[key] = id;
                        geometry.CollisionPoints.Add(patch.Vertices[at]);
                        geometry.CollisionPoints.Add(patch.Vertices[at + 1]);
                        geometry.CollisionPoints.Add(patch.Vertices[at + 2]);
                    }
                    geometry.CollisionIndices.Add(id);
                }
            }
        }
    }

    private static bool LooksInterior(string name) =>
        name.Contains("cockpit", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("panel", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("interior", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("cabin", StringComparison.OrdinalIgnoreCase);

    /// <summary>Strips the extension and non-alphanumeric characters, upper-camel-cases the result.</summary>
    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        bool upper = true;
        foreach (char c in name)
        {
            if (!char.IsLetterOrDigit(c))
            {
                upper = true;
                continue;
            }
            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        return sb.Length == 0 ? "Object" : sb.ToString();
    }

    public static string FormatInvariant(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);
}
