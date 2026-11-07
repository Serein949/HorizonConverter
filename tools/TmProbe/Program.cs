using System.Buffers.Binary;
using AeroflyConv.Core.Tm;

namespace TmProbe;

internal static class Program
{
    private static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        switch (cmd)
        {
            case "crc" when args.Length > 1:
                DumpWrapper(File.ReadAllBytes(args[1]));
                return 0;
            case "rt" when args.Length > 1:
                return RoundTrip(args[1], args.Length > 2 ? args[2] : null);
            case "tree" when args.Length > 1:
                return Tree(args[1], args.Length > 2 ? int.Parse(args[2]) : 3);
            case "ids" when args.Length > 1:
                return DumpIds(args[1], args.Length > 2 ? args[2] : null);
            case "gfx" when args.Length > 1:
                return GfxStats(args[1]);
            case "types" when args.Length > 1:
                return TypeStats(args[1]);
            case "obj" when args.Length > 1:
                return ObjInfo(args[1]);
            case "patch" when args.Length > 1:
                return PatchStats(args[1]);
            case "tt" when args.Length > 1:
                return TextRoundTrip(args[1], args.Length > 2 ? args[2] : null);
            case "schema" when args.Length > 2:
                return Schema(args[1], args[2]);
            case "names" when args.Length > 1:
                return Names(args[1], args.Length > 2 ? args[2] : null);
            case "hex" when args.Length > 1:
                return HexDump(args[1], args.Length > 2 ? int.Parse(args[2]) : 64);

            case "bounds" when args.Length > 1:
                return BoundsOf(args[1]);

            case "str" when args.Length > 1:
                return Strings(args[1], args.Length > 2 ? args[2] : null);

            case "mine" when args.Length > 2:
                return Miner.Run(args[1], args[2]);
            default:
                Console.WriteLine("usage:");
                Console.WriteLine("  TmProbe crc  <file>            show compress_file wrapper fields");
                Console.WriteLine("  TmProbe rt   <file> [out.tmb]  parse -> rewrite -> compare bytes");
                Console.WriteLine("  TmProbe tree <file> [depth]    print node tree with ids");
                Console.WriteLine("  TmProbe tt   <file.tmd> [out]  text tree round trip");
                return 1;
        }
    }

    private static void DumpWrapper(byte[] raw)
    {
        Console.WriteLine($"file length : {raw.Length} (0x{raw.Length:x})");
        int pos = 0;
        while (pos + 32 <= raw.Length && pos < 0x100)
        {
            ulong type = BinaryPrimitives.ReadUInt64BigEndian(raw.AsSpan(pos, 8));
            ulong name = BinaryPrimitives.ReadUInt64BigEndian(raw.AsSpan(pos + 8, 8));
            long size = (long)TmFormats.U64(raw, pos + 16);
            long hs = (long)TmFormats.U64(raw, pos + 24);
            Console.WriteLine($"+0x{pos:x4} type={TmTree.TypeName(type)} name={TmTree.NameName(name)} size={size} hs={hs}");
            if (size < 32) break;
            if (size == 36 && hs == 40) { pos += 40; continue; }
            if (hs > 32 && size == 40) { pos += (int)hs; continue; }
            if (hs == 32) { pos += 32; continue; }
            break;
        }

        byte[] blob = TmCompress.InflateFile(raw);
        Console.WriteLine($"inflated    : {blob.Length} (0x{blob.Length:x}) crc32=0x{TmFormats.Crc32(blob):x8}");
        if (TmFormats.IsCompressedFile(TmFormats.Deobfuscate(raw)))
        {
            // locate comp blob to crc the compressed stream too
            byte[] norm = TmFormats.Deobfuscate(raw);
            int p2 = 0;
            while (p2 + 32 <= norm.Length)
            {
                ulong nm = TmFormats.U64(norm, p2 + 8);
                if (nm == TmNames.NameCompBlob)
                {
                    long sz = (long)TmFormats.U64(norm, p2 + 16);
                    byte[] comp = norm.AsSpan(p2 + 32, (int)sz - 32).ToArray();
                    Console.WriteLine($"comp len={comp.Length} crc32=0x{TmFormats.Crc32(comp):x8} (expect file field 0xd5fe7b11)");
                    break;
                }
                long hsz = (long)TmFormats.U64(norm, p2 + 24);
                int step = (int)Math.Max(hsz, 32);
                if (step <= 0) break;
                p2 += step;
            }
        }
    }

    private static int RoundTrip(string path, string? outPath)
    {
        byte[] raw = File.ReadAllBytes(path);
        byte[] original = TmCompress.InflateFile(raw);
        List<TmNode> tree = TmTree.Parse(original);
        byte[] rewritten = TmTree.Write(tree.ToArray());
        Console.WriteLine($"inflated {original.Length} bytes -> {tree.Count} root node(s)");
        Console.WriteLine($"rewritten {rewritten.Length} bytes");

        int diffs = 0, firstDiff = -1;
        int n = Math.Min(original.Length, rewritten.Length);
        for (int i = 0; i < n; i++)
        {
            if (original[i] == rewritten[i]) continue;
            if (firstDiff < 0) firstDiff = i;
            if (diffs < 5)
                Console.WriteLine($"  diff at 0x{i:x}: orig={original[i]:x2} new={rewritten[i]:x2}");
            diffs++;
        }
        if (original.Length != rewritten.Length)
            Console.WriteLine($"  length differs: orig={original.Length} new={rewritten.Length}");
        Console.WriteLine(diffs == 0 && original.Length == rewritten.Length
            ? "ROUND TRIP: byte identical"
            : $"ROUND TRIP: {diffs} byte(s) differ, first at 0x{firstDiff:x}");

        byte[] container = TmTree.WriteContainer(tree.ToArray(), compress: true);
        byte[] back = TmCompress.InflateFile(container);
        bool ok = back.AsSpan().SequenceEqual(original);
        Console.WriteLine($"container: {container.Length} bytes, re-inflate {(ok ? "OK" : "MISMATCH")}");
        if (outPath is not null)
        {
            File.WriteAllBytes(outPath, container);
            Console.WriteLine($"wrote {outPath}");
        }
        return diffs == 0 && ok ? 0 : 2;
    }

    private static int Tree(string path, int depth)
    {
        byte[] blob = TmCompress.InflateFile(File.ReadAllBytes(path));
        List<TmNode> nodes = TmTree.Parse(blob);
        foreach (TmNode node in nodes) Print(node, 0, depth);
        return 0;
    }

    private static void Print(TmNode node, int indent, int depth)
    {
        string pad = new(' ', indent * 2);
        string extra = node.IsContainer ? $"{node.Children.Count} children" : $"len={node.Payload.Length}";
        Console.WriteLine($"{pad}{TmTree.TypeName(node.TypeId)} [{TmTree.NameName(node.NameId)}] {extra}");
        if (depth <= 0) return;
        foreach (TmNode c in node.Children) Print(c, indent + 1, depth - 1);
    }

    private static int TextRoundTrip(string path, string? outPath)
    {
        List<TmTextNode> roots = TmTextTree.ParseFile(path);
        int count = 0;
        foreach (TmTextNode r in roots) count += Count(r);
        string text = TmTextTree.ToText(roots);
        List<TmTextNode> again = TmTextTree.Parse(text);
        int count2 = 0;
        foreach (TmTextNode r in again) count2 += Count(r);
        Console.WriteLine($"{Path.GetFileName(path)}: {roots.Count} root(s), {count} node(s)");
        Console.WriteLine($"re-parsed  : {again.Count} root(s), {count2} node(s)");
        int mismatches = Compare(roots, again);
        Console.WriteLine(mismatches == 0 && count == count2 ? "TEXT ROUND TRIP: OK" : $"TEXT ROUND TRIP: {mismatches} mismatch(es)");
        if (outPath is not null)
        {
            File.WriteAllText(outPath, text);
            Console.WriteLine($"wrote {outPath} ({text.Length} chars)");
        }
        return mismatches == 0 && count == count2 ? 0 : 2;
    }

    private static int Count(TmTextNode n)
    {
        int c = 1;
        foreach (TmTextNode ch in n.Children) c += Count(ch);
        return c;
    }

    private static int Compare(List<TmTextNode> a, List<TmTextNode> b)
    {
        if (a.Count != b.Count) return Math.Abs(a.Count - b.Count);
        int diff = 0;
        for (int i = 0; i < a.Count; i++) diff += Compare(a[i], b[i]);
        return diff;
    }

    private static int Compare(TmTextNode a, TmTextNode b)
    {
        int diff = 0;
        if (a.Type != b.Type || a.Name != b.Name || a.Value != b.Value)
        {
            Console.WriteLine($"  node differs: <[{a.Type}][{a.Name}][{a.Value}]> vs <[{b.Type}][{b.Name}][{b.Value}]>");
            diff++;
        }
        diff += Compare(a.Children, b.Children);
        return diff;
    }

    private static TmNode? DeepFind(TmNode node, ulong nameId)
    {
        if (node.NameId == nameId) return node;
        foreach (TmNode c in node.Children)
        {
            TmNode? hit = DeepFind(c, nameId);
            if (hit is not null) return hit;
        }
        return null;
    }

    private static int PatchStats(string path)
    {
        List<TmNode> roots = TmTree.ParseFile(path);
        ulong N(string n) => TmTree.NameId(n) ?? throw new ArgumentException(n);

        TmNode? geoList = DeepFind(roots[0], N("geometry_list"));
        if (geoList is null) { Console.WriteLine("no geometry_list"); return 1; }
        Console.WriteLine($"geometries: {geoList.Children.Count}");
        int shown = 0;
        foreach (TmNode geo in geoList.Children)
        {
            string name = geo.Child(N("name"))?.StringValue() ?? "?";
            TmNode? patches = geo.Child(N("patch_list"));
            if (patches is null) continue;
            foreach (TmNode patch in patches.Children)
            {
                int I32(string n) => patch.Child(N(n))?.Payload is { Length: 4 } p ? BitConverter.ToInt32(p) : -1;
                int numFaces = I32("num_faces");
                int vfSize = Math.Max(1, I32("vf_size"));
                int vertFloats = patch.Child(N("vertices"))?.Payload.Length / 4 ?? 0;
                int idx = patch.Child(N("index_list"))?.Payload.Length / 4 ?? 0;
                int strip = patch.Child(N("strip_index_list"))?.Payload.Length / 4 ?? 0;
                int matId = I32("material_id");
                int attribs = patch.Child(N("vertex_attrib_size"))?.Payload.Length / 4 ?? 0;
                Console.WriteLine($"{name,-26} faces={numFaces,5} verts={vertFloats / vfSize,6} idx={idx,6} strip={strip,6} mat={matId,3} nattr={attribs}");
                if (++shown >= 14) return 0;
            }
        }
        return 0;
    }

    private static int GfxStats(string path)
    {
        var roots = AeroflyConv.Core.Tm.TmTextTree.ParseFile(path);
        string[] kinds = { "rigidbodygraphics", "hingedbodygraphics", "movinggraphics", "rotatingbodygraphics",
                           "linkagegraphics", "graphics_translation", "graphics_rotation", "graphics_input" };
        foreach (var root in roots)
            foreach (var node in root.Descendants())
            {
                if (Array.IndexOf(kinds, node.Type) < 0) continue;
                var parts = new List<string>();
                foreach (var child in node.Children)
                    parts.Add($"{child.Type}/{child.Name}={child.Value}");
                Console.WriteLine($"{node.Type}/{node.Name}  {string.Join("  ", parts)}");
            }
        return 0;
    }

    private static int TypeStats(string path)
    {
        var roots = AeroflyConv.Core.Tm.TmTextTree.ParseFile(path);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var root in roots)
            foreach (var node in root.Descendants())
                counts[node.Type] = counts.GetValueOrDefault(node.Type) + 1;
        foreach (var pair in counts.OrderByDescending(p => p.Value))
            Console.WriteLine($"{pair.Value,6}  {pair.Key}");
        return 0;
    }

    private static int ObjInfo(string path)
    {
        AeroflyConv.Core.XPlane.Obj8File obj = AeroflyConv.Core.XPlane.Obj8File.ParseFile(path);
        var (minX, minY, minZ, maxX, maxY, maxZ) = obj.Bounds();
        Console.WriteLine($"{Path.GetFileName(path)}: {obj.Vertices.Count} verts, {obj.Indices.Count} indices, {obj.Runs.Count} runs, {obj.TriangleCount} tris");
        Console.WriteLine($"  bounds X[{minX:0.###},{maxX:0.###}] Y[{minY:0.###},{maxY:0.###}] Z[{minZ:0.###},{maxZ:0.###}]");
        Console.WriteLine($"  texture={obj.Texture} lit={obj.TextureLit} normal={obj.TextureNormal}");
        Console.WriteLine($"  lights={obj.Lights.Count} unknown={obj.UnknownCommands.Count}");
        foreach (string u in obj.UnknownCommands.Take(4)) Console.WriteLine($"   ? {u}");
        var groups = obj.Runs.GroupBy(r => r.Anim.Key).OrderByDescending(g => g.Sum(r => r.IndexCount));
        foreach (var g in groups)
        {
            int tris = g.Sum(r => r.IndexCount) / 3;
            string[] refs = g.First().Anim.Datarefs;
            string label = refs.Length == 0 ? "(static)" : string.Join(", ", refs);
            int minIdx = int.MaxValue, maxIdx = -1;
            foreach (var r in g)
                for (int i = 0; i < r.IndexCount; i++)
                {
                    int idx = obj.Indices[r.IndexOffset + i];
                    minIdx = Math.Min(minIdx, idx);
                    maxIdx = Math.Max(maxIdx, idx);
                }
            Console.WriteLine($"  {tris,7} tris  {label}   idx[{minIdx},{maxIdx}]");
            if (refs.Length > 0)
                foreach (var c in g.First().Anim.Commands) Console.WriteLine($"           {c}");
        }
        return 0;
    }

    private static int DumpIds(string tmbPath, string? outPath)
    {
        List<TmNode> binary = TmTree.ParseFile(tmbPath);
        string txtPath = tmbPath + ".txt";
        if (!File.Exists(txtPath)) { Console.WriteLine("missing " + txtPath); return 1; }
        List<TmTextNode> text = TmTextTree.ParseFile(txtPath);

        var pairs = new Dictionary<(ulong, ulong), (string Type, string Name, int Count)>();
        int visited = 0, mismatched = 0;
        void Walk(TmNode b, TmTextNode t)
        {
            visited++;
            string tn = TmTree.TypeName(b.TypeId);
            string nn = TmTree.NameName(b.NameId);
            // text names drop the list_/pointer_list_/array_ prefix: compare loosely
            if (!tn.Contains(t.Type) && !t.Type.Contains(tn.TrimStart('*'))) mismatched++;
            var key = (b.TypeId, b.NameId);
            if (pairs.TryGetValue(key, out (string Type, string Name, int Count) prev))
                pairs[key] = (prev.Type, prev.Name, prev.Count + 1);
            else
                pairs[key] = (tn, t.Name, 1);

            int n = Math.Min(b.Children.Count, t.Children.Count);
            for (int i = 0; i < n; i++) Walk(b.Children[i], t.Children[i]);
        }
        TmTextNode textRoot = text[0];
        while (textRoot.Type == "file" && textRoot.Children.Count == 1) textRoot = textRoot.Children[0];
        Walk(binary[0], textRoot);
        int textCount = 0;
        void CountText(TmTextNode n) { textCount++; foreach (TmTextNode c in n.Children) CountText(c); }
        foreach (TmTextNode r in text) CountText(r);
        int binCount = 0;
        void CountBin(TmNode n) { binCount++; foreach (TmNode c in n.Children) CountBin(c); }
        foreach (TmNode r in binary) CountBin(r);
        Console.WriteLine($"binary roots={binary.Count} nodes={binCount}; text roots={text.Count} nodes={textCount}");
        Console.WriteLine($"visited {visited} binary nodes, {mismatched} type mismatches, {pairs.Count} distinct id pairs");
        void ShowFirst(TmTextNode n, int d)
        {
            if (d > 6) return;
            Console.WriteLine($"{new string(' ', d * 2)}{n.Type} [{n.Name}] [{n.Value}] ({n.Children.Count} children)");
            foreach (TmTextNode c in n.Children.Take(2)) ShowFirst(c, d + 1);
        }
        ShowFirst(text[0], 0);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("// <auto-generated> learned from real Aerofly TMB files </auto-generated>");
        sb.AppendLine("namespace AeroflyConv.Core.Tm;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Type/field id pairs observed in shipped .tmb files.</summary>");
        sb.AppendLine("public static class TmbFieldIds");
        sb.AppendLine("{");
        sb.AppendLine("    public static readonly (ulong TypeId, ulong NameId, string Type, string Name)[] Pairs =");
        sb.AppendLine("    [");
        foreach (var kv in pairs.OrderByDescending(p => p.Value.Count))
            sb.AppendLine($"        (0x{kv.Key.Item1:x16}UL, 0x{kv.Key.Item2:x16}UL, \"{kv.Value.Type}\", \"{kv.Value.Name}\"), // {kv.Value.Count}");
        sb.AppendLine("    ];");
        sb.AppendLine("}");
        string code = sb.ToString();
        if (outPath is null)
        {
            foreach (System.ComponentModel.Win32Exception _ in Array.Empty<System.ComponentModel.Win32Exception>()) { }
            foreach (string line in code.Split('\n').Take(400)) Console.WriteLine(line.TrimEnd());
        }
        else
        {
            File.WriteAllText(outPath, code);
            Console.WriteLine("wrote " + outPath);
        }
        return pairCheck(pairs, 0);
    }

    private static int pairCheck(Dictionary<(ulong, ulong), (string Type, string Name, int Count)> pairs, int x) => 0;

    /// <summary>Compares the node schema of a generated .tmb against a reference aircraft .tmb.</summary>
    private static int Schema(string path, string referencePath)
    {
        TmNode? mine = LoadScene(path);
        TmNode? reference = LoadScene(referencePath);
        if (mine is null || reference is null)
        {
            Console.WriteLine("no tmxglscene root found");
            return 2;
        }

        int failures = 0;
        failures += Compare("scene", mine.Children, reference.Children, repeat: 1);

        TmNode? myMaterial = First(mine, "material_list");
        TmNode? refMaterial = First(reference, "material_list");
        TmNode? myGeometry = First(mine, "geometry_list");
        TmNode? refGeometry = First(reference, "geometry_list");

        failures += Compare("material", myMaterial?.Children, refMaterial?.Children, repeat: 1);
        if (myMaterial is not null && refMaterial is not null)
        {
            TmNode? m = myMaterial.Children.FirstOrDefault();
            TmNode? r = refMaterial.Children.FirstOrDefault();
            failures += Compare("material element", m?.Children, r?.Children, repeat: 1);
            failures += Compare("texture_list", First(m, "texture_list")?.Children, First(r, "texture_list")?.Children, repeat: 1);
            failures += Compare("texture element", First(First(m, "texture_list"), "texture_list")?.Children,
                First(First(r, "texture_list"), "texture_list")?.Children, repeat: 1);
        }

        failures += Compare("geometry_list", myGeometry?.Children, refGeometry?.Children, repeat: 1);
        if (myGeometry is not null && refGeometry is not null)
        {
            TmNode? m = myGeometry.Children.FirstOrDefault();
            TmNode? r = refGeometry.Children.FirstOrDefault();
            failures += Compare("geometry element", m?.Children, r?.Children, repeat: 1);
            failures += Compare("mesh_collision", First(m, "mesh_collision")?.Children, First(r, "mesh_collision")?.Children, repeat: 1);
            failures += Compare("patch_list", First(m, "patch_list")?.Children, First(r, "patch_list")?.Children, repeat: 1);
            TmNode? mp = First(First(m, "patch_list"), "patch_list");
            TmNode? rp = First(First(r, "patch_list"), "patch_list");
            failures += Compare("patch element", mp?.Children, rp?.Children, repeat: 1);
        }

        Console.WriteLine(failures == 0 ? "SCHEMA: matches reference" : $"SCHEMA: {failures} mismatch(es)");
        failures += CompareFields(mine, reference);
        return failures == 0 ? 0 : 2;
    }

    /// <summary>Compares type id and payload width of the small scalar/string fields.</summary>
    private static int CompareFields(TmNode mine, TmNode reference)
    {
        string[] fields =
        [
            "id", "name", "tag", "matrix", "repeat_s", "repeat_t", "uvscaling",
            "vf_size", "vertex_attrib_size", "vertex_attrib_offset", "material_id",
            "channel", "shader_hint", "point_list", "face_indices_list", "num_faces", "vertices"
        ];
        int failures = 0;
        foreach (string field in fields)
        {
            TmNode? a = FindLeaf(mine, field);
            TmNode? b = FindLeaf(reference, field);
            if (a is null || b is null) continue;
            bool sameType = a.TypeId == b.TypeId;
            bool sameWidth = a.Payload.Length == b.Payload.Length;
            if (sameType && sameWidth)
            {
                Console.WriteLine($"  ok   field {field}: {TmTree.TypeName(a.TypeId)} payload={a.Payload.Length}");
                continue;
            }
            Console.WriteLine($"  FAIL field {field}: mine {TmTree.TypeName(a.TypeId)}/{a.Payload.Length}B vs reference {TmTree.TypeName(b.TypeId)}/{b.Payload.Length}B");
            failures++;
        }
        return failures;
    }

    private static TmNode? FindLeaf(TmNode node, string name)
    {
        if (!node.IsContainer && TmTree.NameName(node.NameId) == name) return node;
        foreach (TmNode c in node.Children)
        {
            TmNode? found = FindLeaf(c, name);
            if (found is not null) return found;
        }
        return null;
    }

    private static TmNode? LoadScene(string p)
    {
        byte[] blob = TmCompress.InflateFile(File.ReadAllBytes(p));
        List<TmNode> nodes = TmTree.Parse(blob);
        TmNode? root = nodes.Count > 0 ? nodes[0] : null;
        while (root is not null && !root.IsContainer) root = null;
        return root is null ? null : DeepScene(root);
    }

    private static TmNode? DeepScene(TmNode n)
    {
        if (TmTree.TypeName(n.TypeId) == "tmxglscene") return n;
        foreach (TmNode c in n.Children)
        {
            TmNode? found = DeepScene(c);
            if (found is not null) return found;
        }
        return null;
    }

    private static TmNode? First(TmNode? parent, string name)
    {
        if (parent is null) return null;
        foreach (TmNode c in parent.Children)
            if (TmTree.NameName(c.NameId) == name && c.IsContainer) return c;
        return null;
    }

    private static int Compare(string label, List<TmNode>? mine, List<TmNode>? reference, int repeat)
    {
        static string Signature(List<TmNode>? list)
        {
            if (list is null) return "<missing>";
            if (list.Count == 0) return "<empty>";
            var names = list.Select(c => $"{TmTree.TypeName(c.TypeId)}/{TmTree.NameName(c.NameId)}").ToList();
            bool uniform = names.Distinct().Count() == 1;
            return uniform ? $"{names[0]} x{names.Count}" : string.Join(", ", names);
        }

        static string Shape(List<TmNode>? list)
        {
            if (list is null) return "<missing>";
            if (list.Count == 0) return "<empty>";
            var names = list.Select(c => $"{TmTree.TypeName(c.TypeId)}/{TmTree.NameName(c.NameId)}").Distinct().ToList();
            return names.Count == 1 ? names[0] : string.Join(", ", names);
        }

        string a = Signature(mine);
        string b = Signature(reference);
        if (Shape(mine) == Shape(reference))
        {
            Console.WriteLine($"  ok   {label}: {a}   (reference {b})");
            return 0;
        }
        Console.WriteLine($"  FAIL {label}");
        Console.WriteLine($"        generated: {a}");
        Console.WriteLine($"        reference: {b}");
        return 1;
    }

    /// <summary>Lists material and geometry names of a .tmb scene.</summary>
    private static int Names(string path, string? outPath)
    {
        TmNode? scene = LoadScene(path);
        if (scene is null)
        {
            Console.WriteLine("no tmxglscene root");
            return 2;
        }

        var sb = new System.Text.StringBuilder();
        List<TmNode> materials = First(scene, "material_list")?.Children ?? new List<TmNode>();
        List<TmNode> geometries = First(scene, "geometry_list")?.Children ?? new List<TmNode>();
        sb.AppendLine($"materials {materials.Count}");
        foreach (TmNode m in materials) sb.AppendLine($"  mat {ChildText(m, "name")}");
        sb.AppendLine($"geometries {geometries.Count}");
        foreach (TmNode g in geometries)
        {
            int patches = First(g, "patch_list")?.Children.Count ?? 0;
            sb.AppendLine($"  geo {ChildText(g, "name")} patches={patches}");
        }

        string text = sb.ToString();
        if (outPath is null) Console.Write(text);
        else
        {
            File.WriteAllText(outPath, text);
            Console.WriteLine($"wrote {outPath} ({materials.Count} materials, {geometries.Count} geometries)");
        }
        return 0;
    }

    /// <summary>Prints raw payload prefixes of string-typed leaves so their encoding can be studied.</summary>
    private static int Strings(string path, string? nameFilter)
    {
        byte[] blob = TmCompress.InflateFile(File.ReadAllBytes(path));
        List<TmNode> nodes = TmTree.Parse(blob);
        var seen = new HashSet<string>();
        foreach (TmNode root in nodes) WalkStrings(root, nameFilter, seen);
        return 0;
    }

    private static void WalkStrings(TmNode node, string? filter, HashSet<string> seen)
    {
        string type = TmTree.TypeName(node.TypeId);
        string name = TmTree.NameName(node.NameId);
        if (!node.IsContainer && type.StartsWith("string", StringComparison.Ordinal) &&
            (filter is null || name == filter))
        {
            string key = type + "/" + name;
            if (seen.Add(key))
            {
                int take = Math.Min(node.Payload.Length, 24);
                string hex = Convert.ToHexString(node.Payload.AsSpan(0, take));
                string ascii = System.Text.Encoding.ASCII.GetString(node.Payload.AsSpan(0, take)).Replace('\0', '.');
                Console.WriteLine($"{type}/{name} len={node.Payload.Length} hex={hex} ascii={ascii}");
            }
        }
        foreach (TmNode c in node.Children) WalkStrings(c, filter, seen);
    }

    private static string ChildText(TmNode parent, string name)    {
        foreach (TmNode c in parent.Children)
            if (TmTree.NameName(c.NameId) == name)
                return System.Text.Encoding.ASCII.GetString(c.Payload).TrimEnd('\0');
        return "";
    }

    private static int BoundsOf(string path)
    {
        TmNode? scene = LoadScene(path);
        if (scene is null) { Console.WriteLine("no tmxglscene root"); return 2; }
        List<TmNode> geometries = First(scene, "geometry_list")?.Children ?? new List<TmNode>();
        double[] gmin = [1e9, 1e9, 1e9], gmax = [-1e9, -1e9, -1e9];
        foreach (TmNode g in geometries)
        {
            double[] lmin = [1e9, 1e9, 1e9], lmax = [-1e9, -1e9, -1e9];
            double[] m = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
            TmNode? mnode = g.Children.FirstOrDefault(c => c.Children.Count == 16);
            if (mnode is not null)
                for (int i = 0; i < 16; i++) m[i] = BitConverter.ToDouble(mnode.Children[i].Payload);

            long verts = 0;
            double[] umin = [1e9, 1e9], umax = [-1e9, -1e9];
            foreach (TmNode patch in First(g, "patch_list")?.Children ?? new List<TmNode>())
            {
                TmNode? vs = patch.Children.FirstOrDefault(c => TmTree.NameName(c.NameId) == "vertices");
                if (vs is null) continue;
                ReadOnlySpan<byte> vp = vs.Children.Count > 0 ? default : vs.Payload;
                int n = vs.Children.Count > 0 ? vs.Children.Count : vp.Length / 4;
                ReadOnlySpan<byte> bytes = vp;
                verts += n / 8;
                for (int i = 0; i + 7 < n; i += 8)
                {
                    double x = vs.Children.Count > 0 ? BitConverter.ToSingle(vs.Children[i].Payload) : BitConverter.ToSingle(bytes.Slice(i * 4));
                    double y = vs.Children.Count > 0 ? BitConverter.ToSingle(vs.Children[i + 1].Payload) : BitConverter.ToSingle(bytes.Slice((i + 1) * 4));
                    double z = vs.Children.Count > 0 ? BitConverter.ToSingle(vs.Children[i + 2].Payload) : BitConverter.ToSingle(bytes.Slice((i + 2) * 4));
                    double tx = m[0] * x + m[4] * y + m[8] * z + m[12];
                    double ty = m[1] * x + m[5] * y + m[9] * z + m[13];
                    double tz = m[2] * x + m[6] * y + m[10] * z + m[14];
                    double uu = vs.Children.Count > 0 ? BitConverter.ToSingle(vs.Children[i + 6].Payload) : BitConverter.ToSingle(bytes.Slice((i + 6) * 4));
                    double vv = vs.Children.Count > 0 ? BitConverter.ToSingle(vs.Children[i + 7].Payload) : BitConverter.ToSingle(bytes.Slice((i + 7) * 4));
                    if (uu < umin[0]) umin[0] = uu; if (uu > umax[0]) umax[0] = uu;
                    if (vv < umin[1]) umin[1] = vv; if (vv > umax[1]) umax[1] = vv;
                    double[] v = [tx, ty, tz];
                    for (int a = 0; a < 3; a++) { if (v[a] < lmin[a]) lmin[a] = v[a]; if (v[a] > lmax[a]) lmax[a] = v[a]; if (v[a] < gmin[a]) gmin[a] = v[a]; if (v[a] > gmax[a]) gmax[a] = v[a]; }
                }
            }
            Console.WriteLine($"  {ChildText(g, "name"),-24} verts={verts,7} uv[{umin[0]:F2},{umax[0]:F2}]x[{umin[1]:F2},{umax[1]:F2}] x[{lmin[0],7:F2},{lmax[0],7:F2}] y[{lmin[1],7:F2},{lmax[1],7:F2}] z[{lmin[2],7:F2},{lmax[2],7:F2}]");
        }
        Console.WriteLine($"TOTAL x[{gmin[0]:F2},{gmax[0]:F2}] y[{gmin[1]:F2},{gmax[1]:F2}] z[{gmin[2]:F2},{gmax[2]:F2}]");
        return 0;
    }

    private static int HexDump(string path, int count)
    {
        byte[] raw = File.ReadAllBytes(path);
        byte[] inflated = AeroflyConv.Core.Tm.TmCompress.InflateFile(raw);
        Console.WriteLine($"{Path.GetFileName(path)} raw={raw.Length} inflated={inflated.Length}");
        int take = Math.Min(count, inflated.Length);
        Console.WriteLine(Convert.ToHexString(inflated.AsSpan(0, take)));
        return 0;
    }
}
