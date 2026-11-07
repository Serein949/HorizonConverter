using System.Globalization;

namespace AeroflyConv.Core.XPlane;

/// <summary>
/// Minimal reader for X-Plane .acf files: `P key value` lines plus the object table
/// (<c>_obja/N/...</c>) that lists the OBJ8 models attached to the aircraft.
/// </summary>
public sealed class AcfFile
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string Path { get; private init; } = "";
    public string Version { get; private set; } = "";

    /// <summary>One attached OBJ8 object.</summary>
    public sealed class AttachedObject
    {
        public int Index;
        public string File = "";
        /// <summary>Resolved absolute path (empty when the file is missing).</summary>
        public string FullPath = "";
        public bool IsInternal;
        public int Flags;
        public double X, Y, Z;
    }

    public readonly List<AttachedObject> Objects = [];

    public static AcfFile ParseFile(string path)
    {
        var acf = new AcfFile { Path = path };
        acf.Parse(File.ReadAllLines(path));
        return acf;
    }

    public static AcfFile Parse(string text, string name = "")
    {
        var acf = new AcfFile { Path = name };
        acf.Parse(text.Split('\n'));
        return acf;
    }

    private void Parse(IEnumerable<string> lines)
    {
        foreach (string raw in lines)
        {
            ReadOnlySpan<char> line = raw.AsSpan().Trim();
            if (line.IsEmpty) continue;
            if (line[0] == 'I' || line[0] == 'A')
            {
                // "I" / "A" header lines of the property block
                if (line.Length > 2) Version = line[2..].ToString();
                continue;
            }
            if (line[0] != 'P') continue;
            ReadOnlySpan<char> body = line[1..].TrimStart();
            int sp = body.IndexOf(' ');
            if (sp < 0) continue;
            string key = body[..sp].ToString();
            string value = body[(sp + 1)..].Trim().ToString();
            _values[key] = value;
        }

        // Collect _obja/N/... entries.
        var byIndex = new SortedDictionary<int, AttachedObject>();
        foreach ((string key, string value) in _values)
        {
            if (!key.StartsWith("_obja/", StringComparison.Ordinal)) continue;
            int slash = key.IndexOf('/', 6);
            if (slash < 0) continue;
            if (!int.TryParse(key.AsSpan(6, slash - 6), out int index)) continue;
            if (!byIndex.TryGetValue(index, out AttachedObject? obj))
                byIndex[index] = obj = new AttachedObject { Index = index };

            string field = key[(slash + 1)..];
            switch (field)
            {
                case "_v10_att_file_stl":
                case "_v10_att_file_v0":
                case "obj_path":
                    obj.File = value;
                    break;
                case "_v10_is_internal":
                    obj.IsInternal = value != "0";
                    break;
                case "_obj_flags":
                    if (int.TryParse(value, out int flags)) obj.Flags = flags;
                    break;
                case "_v10_att_x_acf_prt_ref":
                    obj.X = Num(value);
                    break;
                case "_v10_att_y_acf_prt_ref":
                    obj.Y = Num(value);
                    break;
                case "_v10_att_z_acf_prt_ref":
                    obj.Z = Num(value);
                    break;
            }
        }

        string baseDir = Path.Length > 0 ? (System.IO.Path.GetDirectoryName(Path) ?? "") : "";
        foreach (AttachedObject obj in byIndex.Values)
        {
            if (obj.File.Length == 0) continue;
            string rel = obj.File.Replace('/', System.IO.Path.DirectorySeparatorChar);
            // X-Plane resolves attached object paths against the aircraft folder, but most aircraft
            // list them relative to their objects/ sub folder, so both roots (and the objects/ root
            // with the ../ prefix stripped) have to be tried.
            string objectsDir = System.IO.Path.Combine(baseDir, "objects");
            string[] candidates =
            [
                System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, rel)),
                System.IO.Path.GetFullPath(System.IO.Path.Combine(objectsDir, rel)),
                System.IO.Path.GetFullPath(System.IO.Path.Combine(objectsDir, System.IO.Path.GetFileName(rel))),
                System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, System.IO.Path.GetFileName(rel))),
            ];
            obj.FullPath = candidates.FirstOrDefault(File.Exists) ?? "";
            Objects.Add(obj);
        }
    }

    private static double Num(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public double GetDouble(string key, double fallback = 0) =>
        _values.TryGetValue(key, out string? v) &&
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            ? d
            : fallback;

    public int GetInt(string key, int fallback = 0) =>
        _values.TryGetValue(key, out string? v) &&
        int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)
            ? i
            : fallback;

    public string GetString(string key, string fallback = "") => _values.GetValueOrDefault(key) ?? fallback;

    public IEnumerable<string> Keys => _values.Keys;

    /// <summary>Number of wings described in the ACF (<c>_wing/N</c> entries).</summary>
    public int WingCount => CountGroups("_wing/");

    public int EngineCount => CountGroups("_engine/");

    private int CountGroups(string prefix)
    {
        var seen = new HashSet<int>();
        foreach (string key in _values.Keys)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            int slash = key.IndexOf('/', prefix.Length);
            if (slash < 0) continue;
            if (int.TryParse(key.AsSpan(prefix.Length, slash - prefix.Length), out int i)) seen.Add(i);
        }
        return seen.Count == 0 ? 0 : seen.Max() + 1;
    }
}
