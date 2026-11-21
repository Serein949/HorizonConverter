using System.Globalization;
using System.Text;
using AeroflyConv.Core.Aerofly;
using AeroflyConv.Core.Media;
using AeroflyConv.Core.Textures;

namespace AeroflyConv.Core.Modeling;

/// <summary>Options for <see cref="ObjExporter"/>.</summary>
public sealed class ObjExportOptions
{
    /// <summary>Apply each part's 4x4 matrix (shipped parts are usually identity).</summary>
    public bool ApplyTransform = true;
    /// <summary>Convert the referenced .ttx textures to PNG next to the model.</summary>
    public bool ExportTextures = true;
    /// <summary>Flip the V texture coordinate (TTX rows are stored bottom-up, PNG is written upright).</summary>
    public bool FlipV = true;
    /// <summary>Convert the Z-up scene (Aerofly) to Y-up (x, z, −y), the usual game engine convention.</summary>
    public bool YUp;
    /// <summary>Write a part/material/texture report (CSV) next to the model.</summary>
    public bool WriteReport = true;
    /// <summary>Folder used to resolve texture names; defaults to the .tmb folder.</summary>
    public string? SearchRoot;
    /// <summary>Force a livery sub folder instead of picking the best covering one.</summary>
    public string? LiveryFolder;
    /// <summary>
    /// Write the PNGs next to the OBJ (no sub folder) and name them
    /// <c>&lt;material&gt;_basecolor.png</c>, <c>_normal.png</c> … so Substance Painter and
    /// other texturing tools can match them to a texture set by name.
    /// </summary>
    public bool SubstanceMode;
    public Action<string>? Log;
}

/// <summary>Result of an OBJ export.</summary>
public sealed record ObjExportResult(
    string ObjPath,
    string MtlPath,
    int Parts,
    int Triangles,
    int Materials,
    int TexturesWritten,
    IReadOnlyList<string> MissingTextures);

/// <summary>Exports an Aerofly .tmb scene as Wavefront OBJ + MTL (one OBJ group per part).</summary>
public static class ObjExporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static ObjExportResult Export(TmbScene scene, string objPath, ObjExportOptions options)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(objPath)) ?? ".";
        Directory.CreateDirectory(directory);
        string searchRoot = options.SearchRoot ?? directory;
        string baseName = Path.GetFileNameWithoutExtension(objPath);
        string mtlPath = Path.Combine(directory, baseName + ".mtl");
        string textureFolder = options.SubstanceMode
            ? directory
            : Path.Combine(directory, baseName + "_textures");

        // ---- resolve + convert textures -------------------------------------------------
        var textureFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        int written = 0;
        var lookup = BuildTextureIndex(searchRoot, AncestorRoots(searchRoot));
        var textureLog = new List<string>();
        var channels = new[] { "diffuse", "normal", "specular", "reflection", "ambient", "light", "alpha" };

        // Several liveries can sit side by side under the model folder and they all ship the
        // same texture names (fuselage1_color ...). Pick the one folder that covers the most
        // requested names, so a model never ends up wearing two different liveries.
        var requested = new List<string>();
        foreach (TmbMaterial material in scene.Materials)
            foreach (string channel in channels)
            {
                TmbTextureRef? texture = material.Texture(channel);
                if (texture is null || texture.Name.Length == 0) continue;
                if (!requested.Contains(texture.Name, StringComparer.OrdinalIgnoreCase)) requested.Add(texture.Name);
            }

        string preferred = options.LiveryFolder is { Length: > 0 } forced
            ? forced.TrimEnd('\\', '/')
            : lookup.ChooseRoot(searchRoot, requested, out _);
        lookup.ChooseRoot(searchRoot, requested, out int covered);
        if (preferred.Length > 0)
            options.Log?.Invoke($"textures: using {preferred}/ for {covered}/{requested.Count} names");

        if (options.ExportTextures)
        {
            for (int materialIndex = 0; materialIndex < scene.Materials.Count; materialIndex++)
            {
                TmbMaterial material = scene.Materials[materialIndex];
                string materialName = MaterialName(material, materialIndex);
                foreach (string channel in channels)
                {
                    TmbTextureRef? texture = material.Texture(channel);
                    if (texture is null || texture.Name.Length == 0) continue;
                    if (textureFiles.ContainsKey(texture.Name)) continue;
                    (string? file, string kind) = ResolveDetailed(lookup, texture.Name, preferred);
                    if (file is null) continue;
                    textureLog.Add(texture.Name + "," + kind + "," + file);
                    // Substance mode: one flat file per channel, named after the material, so the
                    // texture set in the texturing tool can be matched by name.
                    string fileName = options.SubstanceMode
                        ? materialName + "_" + ChannelSuffix(channel) + ".png"
                        : texture.Name + ".png";
                    string png = Path.Combine(textureFolder, fileName);
                    try
                    {
                        Directory.CreateDirectory(textureFolder);
                        // A stale png from an earlier run (old lookup rules) must not be reused.
                        bool stale = !File.Exists(png) ||
                                     File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(png);
                        if (stale) TtxToPng(file, png);
                        textureFiles[texture.Name] = options.SubstanceMode
                            ? fileName
                            : Path.Combine(Path.GetFileName(textureFolder), fileName);
                        written++;
                    }
                    catch (Exception ex)
                    {
                        options.Log?.Invoke($"texture {texture.Name}: {ex.Message}");
                    }
                }
            }
        }

        foreach (TmbMaterial material in scene.Materials)
        {
            foreach (string channel in channels)
            {
                TmbTextureRef? texture = material.Texture(channel);
                if (texture is null || texture.Name.Length == 0) continue;
                if (!textureFiles.ContainsKey(texture.Name) && !missing.Contains(texture.Name))
                    missing.Add(texture.Name);
            }
        }

        // ---- MTL ------------------------------------------------------------------------
        var mtl = new StringBuilder();
        mtl.AppendLine("# Aerofly material library - generated by Aerofly Converter");
        for (int i = 0; i < scene.Materials.Count; i++)
        {
            TmbMaterial material = scene.Materials[i];
            string name = MaterialName(material, i);
            mtl.AppendLine(Inv, $"newmtl {name}");
            mtl.AppendLine("Kd 1.000000 1.000000 1.000000");
            mtl.AppendLine("Ka 0.000000 0.000000 0.000000");
            mtl.AppendLine("Ks 0.100000 0.100000 0.100000");
            mtl.AppendLine("Ns 20.0");
            mtl.AppendLine("d 1.0");
            mtl.AppendLine("illum 2");
            if (material.ShaderHint.Length > 0) mtl.AppendLine($"# shader_hint {material.ShaderHint}");

            AppendMap(mtl, "map_Kd", material.Texture("diffuse"), textureFiles);
            AppendMap(mtl, "map_Bump", material.Texture("normal"), textureFiles);
            AppendMap(mtl, "map_Ks", material.Texture("specular"), textureFiles);
            AppendMap(mtl, "map_Ke", material.Texture("light"), textureFiles);
            AppendMap(mtl, "map_d", material.Texture("alpha"), textureFiles);
            mtl.AppendLine();
        }
        File.WriteAllText(mtlPath, mtl.ToString(), new UTF8Encoding(false));

        if (options.WriteReport)
        {
            var log = new StringBuilder("texture,match,source\n");
            foreach (string line in textureLog) log.AppendLine(line);
            File.WriteAllText(Path.Combine(directory, baseName + "-textures.csv"), log.ToString(), new UTF8Encoding(false));
        }

        // ---- OBJ ------------------------------------------------------------------------
        var obj = new StringBuilder(1 << 20);
        obj.AppendLine("# Aerofly Converter - Wavefront OBJ");
        obj.AppendLine(Inv, $"# parts {scene.Geometries.Count}, triangles {scene.TriangleCount}, materials {scene.Materials.Count}");
        obj.AppendLine(Inv, $"mtllib {Path.GetFileName(mtlPath)}");

        int vertexBase = 1;
        int uvBase = 1;
        int normalBase = 1;
        int triangles = 0;
        var usedNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var report = new StringBuilder();
        if (options.WriteReport) report.AppendLine("part,material,triangles,textures");

        foreach (TmbGeometry geometry in scene.Geometries)
        {
            string partName = PartName(geometry, usedNames);
            obj.AppendLine(Inv, $"o {partName}");
            obj.AppendLine(Inv, $"g {partName}");

            double[] m = options.ApplyTransform ? geometry.Matrix : Identity;
            foreach (TmbPatch patch in geometry.Patches)
            {
                int vertexCount = patch.VertexSize > 0 ? patch.Vertices.Length / patch.VertexSize : 0;
                if (vertexCount == 0 || patch.Indices.Length < 3) continue;
                int patchBase = vertexBase;
                int patchUvBase = uvBase;
                int patchNormalBase = normalBase;

                // Some patches index vertices 1-based, others 0-based; detect by the maximum index.
                int indexMax = 0;
                foreach (uint raw in patch.Indices)
                {
                    if (raw > int.MaxValue) continue;
                    if ((int)raw > indexMax) indexMax = (int)raw;
                }
                int shift = indexMax >= vertexCount ? 1 : 0;
                (int posOffset, int normalOffset, int uvOffset) = AttributeOffsets(patch);

                for (int v = 0; v < vertexCount; v++)
                {
                    int b = v * patch.VertexSize;
                    if (b + patch.VertexSize > patch.Vertices.Length) break;
                    double x = patch.Vertices[b + posOffset];
                    double y = patch.Vertices[b + posOffset + 1];
                    double z = patch.Vertices[b + posOffset + 2];
                    (x, y, z) = Transform(m, x, y, z);
                    if (options.YUp) (y, z) = (z, -y);
                    obj.AppendLine(Inv, $"v {x:0.######} {y:0.######} {z:0.######}");

                    if (normalOffset >= 0)
                    {
                        double nx = patch.Vertices[b + normalOffset];
                        double ny = patch.Vertices[b + normalOffset + 1];
                        double nz = patch.Vertices[b + normalOffset + 2];
                        (nx, ny, nz) = TransformDirection(m, nx, ny, nz);
                        if (options.YUp) (ny, nz) = (nz, -ny);
                        obj.AppendLine(Inv, $"vn {nx:0.######} {ny:0.######} {nz:0.######}");
                    }

                    if (uvOffset >= 0)
                    {
                        float u = patch.Vertices[b + uvOffset];
                        float w = patch.Vertices[b + uvOffset + 1];
                        if (options.FlipV) { w = 1f - w; u = 1f - u; }
                        obj.AppendLine(Inv, $"vt {u:0.######} {w:0.######}");
                    }
                }

                vertexBase += vertexCount;
                if (normalOffset >= 0) normalBase += vertexCount;
                if (uvOffset >= 0) uvBase += vertexCount;

                TmbMaterial? material = patch.MaterialId >= 0 && patch.MaterialId < scene.Materials.Count
                    ? scene.Materials[patch.MaterialId]
                    : null;
                obj.AppendLine(Inv, $"usemtl {MaterialName(material, patch.MaterialId)}");

                int faces = patch.Indices.Length / 3;
                for (int f = 0; f < faces; f++)
                {
                    int i0 = (int)patch.Indices[f * 3] - shift;
                    int i1 = (int)patch.Indices[f * 3 + 1] - shift;
                    int i2 = (int)patch.Indices[f * 3 + 2] - shift;
                    if (i0 < 0 || i1 < 0 || i2 < 0) continue;
                    if (i0 >= vertexCount || i1 >= vertexCount || i2 >= vertexCount) continue;
                    obj.Append($"f {Face(patchBase, patchUvBase, patchNormalBase, i0, normalOffset >= 0, uvOffset >= 0)} ");
                    obj.Append($"{Face(patchBase, patchUvBase, patchNormalBase, i1, normalOffset >= 0, uvOffset >= 0)} ");
                    obj.AppendLine(Face(patchBase, patchUvBase, patchNormalBase, i2, normalOffset >= 0, uvOffset >= 0));
                }
                triangles += faces;

                if (options.WriteReport)
                {
                    string textures = material is null
                        ? ""
                        : string.Join(" ", material.Textures.Select(t => t.Name));
                    report.AppendLine(Inv, $"\"{partName}\",\"{MaterialName(material, patch.MaterialId)}\",{faces},\"{textures}\"");
                }
            }

        }

        File.WriteAllText(objPath, obj.ToString(), new UTF8Encoding(false));
        if (options.WriteReport)
            File.WriteAllText(Path.Combine(directory, baseName + "-parts.csv"), report.ToString(), new UTF8Encoding(false));

        options.Log?.Invoke($"{scene.Geometries.Count} parts, {triangles:N0} triangles, {scene.Materials.Count} materials, {written} textures");
        return new ObjExportResult(objPath, mtlPath, scene.Geometries.Count, triangles, scene.Materials.Count, written, missing);
    }

    private static readonly double[] Identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    private static string Face(int vertexBase, int uvBase, int normalBase, int index, bool hasNormal, bool hasUv)
    {
        int v = vertexBase + index;
        int vt = uvBase + index;
        int vn = normalBase + index;
        if (hasNormal && hasUv) return $"{v}/{vt}/{vn}";
        if (hasUv) return $"{v}/{vt}";
        if (hasNormal) return $"{v}//{vn}";
        return v.ToString(Inv);
    }

    private static void AppendMap(StringBuilder mtl, string keyword, TmbTextureRef? texture, Dictionary<string, string> files)
    {
        if (texture is null || texture.Name.Length == 0) return;
        if (files.TryGetValue(texture.Name, out string? png))
            mtl.AppendLine(Inv, $"{keyword} {png.Replace('\\', '/')}");
        else
            mtl.AppendLine(Inv, $"# {keyword} {texture.Name}.ttx (not converted)");
    }

    /// <summary>
    /// Reads the vertex layout from the patch itself (vertex_attrib_size / vertex_attrib_offset):
    /// slot 0 is the position, the next 3-float slot the normal, the 2-float slot the UV.
    /// Anything that would not fit inside the stride is ignored, so odd models cannot crash us.
    /// </summary>
    private static (int Pos, int Normal, int Uv) AttributeOffsets(TmbPatch patch)
    {
        int stride = patch.VertexSize > 0 ? patch.VertexSize : 8;
        int[] sizes = patch.AttribSize;
        int[] offsets = patch.AttribOffset;

        int pos = -1, normal = -1, uv = -1;
        for (int i = 0; i < offsets.Length && i < sizes.Length; i++)
        {
            int offset = offsets[i];
            int size = sizes[i];
            if (offset < 0 || size <= 0) continue;
            if (offset + size > stride) continue;
            if (size == 3 && pos < 0) { pos = offset; continue; }
            if (size == 3 && normal < 0) { normal = offset; continue; }
            if (size == 2 && uv < 0) uv = offset;
        }

        if (pos < 0 || pos + 2 >= stride) pos = 0;
        return (pos, normal, uv);
    }

    /// <summary>Aerofly channel name → the suffix texturing tools expect in a texture set.</summary>
    private static string ChannelSuffix(string channel) => channel switch
    {
        "diffuse" => "basecolor",
        "normal" => "normal",
        "specular" => "specular",
        "reflection" => "reflection",
        "ambient" => "ambient",
        "light" => "emissive",
        "alpha" => "opacity",
        _ => channel,
    };

    private static string MaterialName(TmbMaterial? material, int index)
    {
        if (material is null) return $"material_{index}";
        string name = material.Name.Length > 0 ? material.Name : $"material_{index}";
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
        return sb.ToString();
    }

    private static string PartName(TmbGeometry geometry, Dictionary<string, int> used)
    {
        string name = geometry.Name.Length > 0 ? geometry.Name : $"part_{geometry.Id}";
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
        string clean = sb.ToString();
        if (used.TryGetValue(clean, out int count))
        {
            used[clean] = count + 1;
            return $"{clean}_{count + 1}";
        }
        used[clean] = 0;
        return clean;
    }

    private static (double, double, double) Transform(double[] m, double x, double y, double z)
    {
        double rx = m[0] * x + m[1] * y + m[2] * z + m[3];
        double ry = m[4] * x + m[5] * y + m[6] * z + m[7];
        double rz = m[8] * x + m[9] * y + m[10] * z + m[11];
        return (rx, ry, rz);
    }

    private static (double, double, double) TransformDirection(double[] m, double x, double y, double z)
    {
        double rx = m[0] * x + m[1] * y + m[2] * z;
        double ry = m[4] * x + m[5] * y + m[6] * z;
        double rz = m[8] * x + m[9] * y + m[10] * z;
        double len = Math.Sqrt(rx * rx + ry * ry + rz * rz);
        return len > 1e-12 ? (rx / len, ry / len, rz / len) : (0, 1, 0);
    }

    private static void TtxToPng(string ttxPath, string pngPath)
    {
        DecodedTexture texture = TtxFile.Decode(File.ReadAllBytes(ttxPath), flip: true);
        ImageIo.SavePng(pngPath, texture.Rgba, texture.Width, texture.Height, texture.PngHasAlpha);
    }

    /// <summary>The model folder plus its two parent folders (shared textures often live one level up).</summary>
    private static string[] AncestorRoots(string root)
    {
        var roots = new List<string>();
        string? current = Path.GetFullPath(root);
        for (int i = 0; i < 2 && current is not null; i++)
        {
            string? parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) break;
            roots.Add(parent);
            current = parent;
        }
        return roots.ToArray();
    }

    /// <summary>
    /// Index of every .ttx/.png/.dds/.jpg near the model, keyed by base name and ordered by
    /// closeness: the model folder subtree wins over ancestor folders, and .ttx wins over other
    /// formats. Without this the parent "aircraft" folder makes every "ext01_fuselage_color"
    /// collide with unrelated aircraft.
    /// </summary>
    private static TextureIndex BuildTextureIndex(string root, string[] extraRoots)
    {
        var index = new TextureIndex();
        if (Directory.Exists(root)) index.AddFolder(root, 0);

        for (int level = 0; level < extraRoots.Length; level++)
        {
            string folder = extraRoots[level];
            if (!Directory.Exists(folder)) continue;
            int baseRank = (level + 1) * 10;
            index.AddFolderShallow(folder, baseRank);
        }
        return index;
    }

    private sealed class TextureIndex
    {
        private readonly Dictionary<string, List<string>> _byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _rank = new(StringComparer.OrdinalIgnoreCase);

        public void AddFolder(string folder, int rank)
        {
            // .ttx first so a texture and its exported png never fight over the same rank.
            foreach (string file in Enumerate(folder, ".ttx")) Add(file, rank);
            foreach (string file in Enumerate(folder, ".png")) Add(file, rank + 1);
            foreach (string file in Enumerate(folder, ".dds")) Add(file, rank + 2);
            foreach (string file in Enumerate(folder, ".jpg")) Add(file, rank + 3);
        }

        public void AddFolderShallow(string folder, int rank)
        {
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                string ext = Path.GetExtension(file);
                if (ext.Equals(".ttx", StringComparison.OrdinalIgnoreCase)) Add(file, rank);
                else if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase)) Add(file, rank + 1);
                else if (ext.Equals(".dds", StringComparison.OrdinalIgnoreCase)) Add(file, rank + 2);
                else if (ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)) Add(file, rank + 3);
            }
        }

        private static IEnumerable<string> Enumerate(string folder, string ext)
        {
            return Directory.EnumerateFiles(folder, "*" + ext, SearchOption.AllDirectories);
        }

        private void Add(string file, int rank)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (!_byName.TryGetValue(name, out List<string>? list))
            {
                list = new List<string>();
                _byName[name] = list;
            }
            list.Add(file);
            _rank[file] = rank;
        }

        /// <summary>Texture folder (first path segment below the model folder) of a file.</summary>
        private readonly Dictionary<string, string> _root = new(StringComparer.OrdinalIgnoreCase);
        private string _searchRoot = "";

        public void SetSearchRoot(string root) => _searchRoot = Path.GetFullPath(root).TrimEnd('\\', '/');

        public string RootOf(string file)
        {
            if (_root.TryGetValue(file, out string? cached)) return cached;
            string full = Path.GetFullPath(file);
            string root = _searchRoot;
            string result = "";
            if (root.Length > 0 && full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                string rel = full[(root.Length + 1)..];
                int slash = rel.IndexOfAny(['\\', '/']);
                if (slash > 0) result = rel[..slash];
            }
            _root[file] = result;
            return result;
        }

        /// <summary>Picks the livery folder that supplies the most requested texture names.</summary>
        public string ChooseRoot(string searchRoot, IReadOnlyList<string> names, out int covered)
        {
            SetSearchRoot(searchRoot);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in names)
            {
                if (!_byName.TryGetValue(name, out List<string>? list)) continue;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in list)
                {
                    string folder = RootOf(file);
                    if (seen.Add(folder)) counts[folder] = counts.GetValueOrDefault(folder) + 1;
                }
            }

            string best = "";
            covered = 0;
            foreach (var pair in counts)
            {
                if (pair.Value > covered || (pair.Value == covered && string.CompareOrdinal(pair.Key, best) < 0))
                {
                    best = pair.Key;
                    covered = pair.Value;
                }
            }
            return best;
        }

        public string? Exact(string name, string preferred)
        {
            if (!_byName.TryGetValue(name, out List<string>? list) || list.Count == 0) return null;
            if (preferred.Length > 0)
            {
                string? inRoot = null;
                foreach (string file in list)
                {
                    if (!RootOf(file).Equals(preferred, StringComparison.OrdinalIgnoreCase)) continue;
                    if (inRoot is null || _rank[file] < _rank[inRoot] ||
                        (_rank[file] == _rank[inRoot] && file.Length < inRoot.Length)) inRoot = file;
                }
                if (inRoot is not null) return inRoot;
                return null;
            }
            return Best(list);
        }

        /// <summary>All entries that end (or start) with the requested name, best first.</summary>
        public IEnumerable<string> Search(string name, bool suffix, string preferred)
        {
            var hits = new List<string>();
            foreach (var pair in _byName)
            {
                bool match = suffix
                    ? pair.Key.EndsWith(name, StringComparison.OrdinalIgnoreCase) && !pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)
                    : pair.Key.StartsWith(name, StringComparison.OrdinalIgnoreCase) && !pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
                foreach (string file in pair.Value)
                    if (preferred.Length == 0 || RootOf(file).Equals(preferred, StringComparison.OrdinalIgnoreCase))
                        hits.Add(file);
            }
            hits.Sort((a, b) =>
            {
                int cmp = _rank[a].CompareTo(_rank[b]);
                if (cmp != 0) return cmp;
                return a.Length.CompareTo(b.Length);
            });
            return hits;
        }

        private string Best(List<string> list)
        {
            string best = list[0];
            foreach (string file in list)
            {
                if (_rank[file] < _rank[best] || (_rank[file] == _rank[best] && file.Length < best.Length)) best = file;
            }
            return best;
        }
    }

    /// <summary>
    /// Finds the file for a material texture: exact base name first, then suffixes, then a
    /// prefix match (livery folders rename the prefix, e.g. "ext01_fuselage_color" →
    /// "sa_air_china_ext01_fuselage_color").
    /// </summary>
    private static (string? Path, string Kind) ResolveDetailed(TextureIndex index, string name, string preferred)
    {
        string? exact = index.Exact(name, preferred);
        if (exact is not null) return (exact, preferred.Length > 0 ? "exact+livery" : "exact");
        foreach (string file in index.Search(name, suffix: true, preferred)) return (file, "suffix");
        foreach (string file in index.Search(name, suffix: false, preferred)) return (file, "prefix");

        // Nothing under the chosen livery root: fall back to any folder.
        if (preferred.Length > 0)
        {
            string? any = index.Exact(name, "");
            if (any is not null) return (any, "exact-other");
        }

        // Last resort: the longest suffix of the requested name that exists (handles prefixes
        // added by livery packages).
        string[] parts = name.Split('_');
        for (int skip = 1; skip < parts.Length; skip++)
        {
            string candidate = string.Join('_', parts.Skip(skip));
            string? file = index.Exact(candidate, preferred);
            if (file is not null) return (file, "trimmed");
        }
        return (null, "missing");
    }
}
