using System.Globalization;

namespace AeroflyConv.Core.XPlane;

/// <summary>One entry of the OBJ8 vertex table: position, normal, texture coordinate.</summary>
public struct Obj8Vertex
{
    public float X, Y, Z;
    public float Nx, Ny, Nz;
    public float U, V;
}

public enum Obj8AnimKind
{
    Rotate,
    Translate,
    Hide,
    Show,
}

/// <summary>A single animation command of an OBJ8 animation stack.</summary>
public sealed class Obj8AnimCommand
{
    public Obj8AnimKind Kind;
    /// <summary>Axis (rotate) or translation delta (trans) in object space.</summary>
    public float X, Y, Z;
    /// <summary>Angle range in degrees (rotate) or value range (hide/show).</summary>
    public float V1, V2;
    /// <summary>Dataref value range that maps onto <see cref="V1"/>..<see cref="V2"/>.</summary>
    public float V3, V4;
    /// <summary>Dataref driving the animation, "none" for static commands.</summary>
    public string Dataref = "none";
    /// <summary>Second driver of a rotate command, "none" when unused.</summary>
    public string Dataref2 = "none";

    public override string ToString() =>
        Kind switch
        {
            Obj8AnimKind.Rotate => $"rotate({X:0.####},{Y:0.####},{Z:0.####})[{V1:0.###},{V2:0.###}]{Dataref}",
            Obj8AnimKind.Translate => $"trans({X:0.####},{Y:0.####},{Z:0.####}){Dataref}",
            Obj8AnimKind.Hide => $"hide[{V1:0.###},{V2:0.###}]{Dataref}",
            _ => $"show[{V1:0.###},{V2:0.###}]{Dataref}",
        };
}

/// <summary>Immutable description of the animation stack active while triangles were emitted.</summary>
public sealed class Obj8AnimStack
{
    public static readonly Obj8AnimStack Empty = new([]);

    public Obj8AnimStack(Obj8AnimCommand[] commands)
    {
        Commands = commands;
        Key = string.Join('|', commands.Select(c => c.ToString()));
        Datarefs = commands
            .Where(c => c.Dataref != "none")
            .Select(c => c.Dataref)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public Obj8AnimCommand[] Commands { get; }
    /// <summary>Datarefs that actually drive this stack.</summary>
    public string[] Datarefs { get; }
    /// <summary>Stable textual identity, used to group triangles.</summary>
    public string Key { get; }
    public bool IsAnimated => Datarefs.Length > 0;
}

/// <summary>A contiguous triangle run emitted by one TRIS command with the render state in effect.</summary>
public sealed class Obj8Run
{
    public int IndexOffset;
    public int IndexCount;
    public string Texture = "";
    public Obj8AnimStack Anim = Obj8AnimStack.Empty;
    public float LightLevel;
    public bool BlendGlass;
    public bool NoCull;
    public bool NoShadow;
    /// <summary>Source line number, for diagnostics.</summary>
    public int Line;
}

/// <summary>Mutable render state while walking the file.</summary>
internal sealed class Obj8State
{
    public float LightLevel;
    public bool BlendGlass;
    public bool NoCull;
    public bool NoShadow;

    public Obj8State Clone() => (Obj8State)MemberwiseClone();
}

/// <summary>Parsed OBJ8 file: vertex table plus triangle runs with material and animation state.</summary>
public sealed class Obj8File
{
    public string Path = "";
    public string Texture = "";
    public string TextureLit = "";
    public string TextureNormal = "";
    public readonly List<Obj8Vertex> Vertices = [];
    public readonly List<int> Indices = [];
    public readonly List<Obj8Run> Runs = [];
    /// <summary>LIGHT_PARAM / LIGHT_NAMED lines, kept for reference.</summary>
    public readonly List<string> Lights = [];
    public readonly List<string> UnknownCommands = [];

    public static Obj8File ParseFile(string path) => Parse(File.ReadAllText(path), path);

    public static Obj8File Parse(string text, string path = "")
    {
        var file = new Obj8File { Path = path };
        var stack = new AnimFrames();
        var state = new Obj8State();
        int lineNo = 0;

        // One scratch buffer per parse: a stackalloc inside the loop would grow the stack per line.
        Span<float> scalars = stackalloc float[10];
        Span<int> intScratch = stackalloc int[2];

        foreach (string rawLine in text.Split('\n'))
        {
            lineNo++;
            // Laminar's own aircraft separate tokens with tabs, third-party models use spaces.
            string normalised = rawLine.Contains('\t') ? rawLine.Replace('\t', ' ') : rawLine;
            ReadOnlySpan<char> line = normalised.AsSpan().Trim();
            if (line.IsEmpty || line[0] == '#') continue;

            int sp = line.IndexOf(' ');
            ReadOnlySpan<char> cmd = sp < 0 ? line : line[..sp];
            ReadOnlySpan<char> rest = sp < 0 ? default : line[(sp + 1)..].Trim();

            switch (cmd)
            {
                case "A":
                case "800":
                case "OBJ":
                case "POINT_COUNTS":
                case "NORMAL_METALNESS":
                case "GLOBAL_specular":
                case "GLOBAL_cockpit_lit":
                case "GLOBAL_no_shadow":
                case "ATTR_shiny_rat":
                case "ATTR_draw_disable":
                case "ATTR_draw_enable":
                case "ATTR_solid_camera":
                case "ANIM_rotate_end":
                case "ANIM_trans_begin":
                case "ANIM_trans_end":
                case "ANIM_rotate_key":
                case "ANIM_trans_key":
                    break;

                case "TEXTURE":
                    file.Texture = rest.ToString();
                    break;
                case "TEXTURE_LIT":
                    file.TextureLit = rest.ToString();
                    break;
                case "TEXTURE_NORMAL":
                    file.TextureNormal = rest.ToString();
                    break;

                case "VT":
                {
                    Span<float> vals = scalars;
                    if (!TryParseFloats(rest, vals, 8, out int got) || got < 8)
                    {
                        file.UnknownCommands.Add($"line {lineNo}: bad VT");
                        break;
                    }
                    file.Vertices.Add(new Obj8Vertex
                    {
                        X = vals[0], Y = vals[1], Z = vals[2],
                        Nx = vals[3], Ny = vals[4], Nz = vals[5],
                        U = vals[6], V = vals[7],
                    });
                    break;
                }

                case "IDX10":
                    // IDX10 is simply "at most ten plain indices on this line", identical to two IDX lines.
                case "IDX":
                {
                    int pos = 0;
                    while (pos < rest.Length)
                    {
                        int next = rest[pos..].IndexOf(' ');
                        ReadOnlySpan<char> tok = next < 0 ? rest[pos..] : rest.Slice(pos, next);
                        pos += next < 0 ? rest.Length : next + 1;
                        if (tok.IsEmpty) continue;
                        file.Indices.Add(int.Parse(tok, CultureInfo.InvariantCulture));
                    }
                    break;
                }

                case "TRIS":
                {
                    Span<int> two = intScratch;
                    if (!TryParseInts(rest, two, 2, out int got) || got < 2)
                    {
                        file.UnknownCommands.Add($"line {lineNo}: bad TRIS");
                        break;
                    }
                    file.Runs.Add(new Obj8Run
                    {
                        IndexOffset = two[0],
                        IndexCount = two[1],
                        Texture = file.Texture,
                        Anim = stack.Count == 0 ? Obj8AnimStack.Empty : new Obj8AnimStack(stack.Flatten()),
                        LightLevel = state.LightLevel,
                        BlendGlass = state.BlendGlass,
                        NoCull = state.NoCull,
                        NoShadow = state.NoShadow,
                        Line = lineNo,
                    });
                    break;
                }

                case "ATTR_light_level":
                {
                    Span<float> vals = scalars;
                    if (TryParseFloats(rest, vals, 2, out _)) state.LightLevel = Math.Max(vals[0], vals[1]);
                    break;
                }

                case "BLEND_GLASS":
                    state.BlendGlass = true;
                    break;
                case "ATTR_no_blend":
                    state.BlendGlass = false;
                    break;
                case "ATTR_cull":
                    state.NoCull = false;
                    break;
                case "ATTR_no_cull":
                    state.NoCull = true;
                    break;
                case "ATTR_shadow":
                    state.NoShadow = false;
                    break;
                case "ATTR_no_shadow":
                    state.NoShadow = true;
                    break;

                case "ANIM_begin":
                    stack.Push();
                    break;
                case "ANIM_end":
                    stack.Pop();
                    break;

                case "ANIM_trans":
                {
                    Span<float> vals = scalars;
                    int n = TryParseFloats(rest, vals, 8, out int got) ? got : 0;
                    stack.Add(new Obj8AnimCommand
                    {
                        Kind = Obj8AnimKind.Translate,
                        X = n > 0 ? vals[0] : 0,
                        Y = n > 1 ? vals[1] : 0,
                        Z = n > 2 ? vals[2] : 0,
                        Dataref = DatarefToken(rest, 0),
                    });
                    break;
                }

                case "ANIM_rotate":
                {
                    Span<float> vals = scalars;
                    int n = TryParseFloats(rest, vals, 10, out int got) ? got : 0;
                    stack.Add(new Obj8AnimCommand
                    {
                        Kind = Obj8AnimKind.Rotate,
                        X = n > 0 ? vals[0] : 0,
                        Y = n > 1 ? vals[1] : 0,
                        Z = n > 2 ? vals[2] : 0,
                        V1 = n > 3 ? vals[3] : 0,
                        V2 = n > 4 ? vals[4] : 0,
                        V3 = n > 5 ? vals[5] : 0,
                        V4 = n > 6 ? vals[6] : 0,
                        Dataref = DatarefToken(rest, 0),
                        Dataref2 = DatarefToken(rest, 1),
                    });
                    break;
                }

                case "ANIM_rotate_begin":
                {
                    Span<float> vals = scalars;
                    int n = TryParseFloats(rest, vals, 3, out int got) ? got : 0;
                    stack.Add(new Obj8AnimCommand
                    {
                        Kind = Obj8AnimKind.Rotate,
                        X = n > 0 ? vals[0] : 0,
                        Y = n > 1 ? vals[1] : 0,
                        Z = n > 2 ? vals[2] : 0,
                        Dataref = DatarefToken(rest, 0),
                    });
                    break;
                }

                case "ANIM_hide":
                case "ANIM_show":
                {
                    Span<float> vals = scalars;
                    int n = TryParseFloats(rest, vals, 2, out int got) ? got : 0;
                    stack.Add(new Obj8AnimCommand
                    {
                        Kind = cmd[5] == 'h' ? Obj8AnimKind.Hide : Obj8AnimKind.Show,
                        V1 = n > 0 ? vals[0] : 0,
                        V2 = n > 1 ? vals[1] : 0,
                        Dataref = DatarefToken(rest, 0),
                    });
                    break;
                }

                case "LIGHT_PARAM":
                case "LIGHT_CUSTOM":
                case "LIGHT_NAMED":
                    file.Lights.Add(line.ToString());
                    break;

                default:
                    if (!cmd.IsEmpty) file.UnknownCommands.Add($"line {lineNo}: {line.ToString()}");
                    break;
            }
        }

        return file;
    }

    /// <summary>
    /// Animation commands nest: ANIM_begin opens a frame and ANIM_end closes it. Commands of an inner
    /// frame apply on top of the outer ones, so a frame is flattened into its parent when it closes.
    /// </summary>
    private sealed class AnimFrames
    {
        private readonly List<List<Obj8AnimCommand>> _frames = [new()];

        public int Count => _frames.Sum(f => f.Count);

        public void Push() => _frames.Add([]);

        public void Pop()
        {
            if (_frames.Count <= 1) return;
            List<Obj8AnimCommand> top = _frames[^1];
            _frames.RemoveAt(_frames.Count - 1);
            _frames[^1].AddRange(top);
        }

        public void Add(Obj8AnimCommand command) => _frames[^1].Add(command);

        public Obj8AnimCommand[] Flatten() => _frames.SelectMany(f => f).ToArray();
    }

    /// <summary>
    /// Returns the n-th non numeric token of an animation command line. Datarefs always follow the
    /// numeric arguments, and the argument counts differ between commands (ANIM_trans carries eight,
    /// ANIM_rotate seven, ANIM_hide two), so skipping the numbers is more robust than counting them.
    /// </summary>
    private static string DatarefToken(ReadOnlySpan<char> text, int tokenIndex)
    {
        int index = 0, pos = 0;
        while (pos < text.Length)
        {
            int sp = text[pos..].IndexOf(' ');
            ReadOnlySpan<char> tok = sp < 0 ? text[pos..] : text.Slice(pos, sp);
            pos += sp < 0 ? text.Length : sp + 1;
            if (tok.IsEmpty) continue;
            if (float.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) continue;
            if (index++ == tokenIndex) return tok.ToString();
        }
        return "none";
    }

    private static bool TryParseFloats(ReadOnlySpan<char> text, Span<float> dest, int max, out int count)
    {
        count = 0;
        int pos = 0;
        while (pos < text.Length && count < max)
        {
            int sp = text[pos..].IndexOf(' ');
            ReadOnlySpan<char> tok = sp < 0 ? text[pos..] : text.Slice(pos, sp);
            pos += sp < 0 ? text.Length : sp + 1;
            if (tok.IsEmpty) continue;
            if (!float.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) break;
            dest[count++] = v;
        }
        return count > 0;
    }

    private static bool TryParseInts(ReadOnlySpan<char> text, Span<int> dest, int max, out int count)
    {
        count = 0;
        int pos = 0;
        while (pos < text.Length && count < max)
        {
            int sp = text[pos..].IndexOf(' ');
            ReadOnlySpan<char> tok = sp < 0 ? text[pos..] : text.Slice(pos, sp);
            pos += sp < 0 ? text.Length : sp + 1;
            if (tok.IsEmpty) continue;
            if (!int.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) break;
            dest[count++] = v;
        }
        return count > 0;
    }

    /// <summary>Axis-aligned bounds of the vertex table, in OBJ local space.</summary>
    public (float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ) Bounds()
    {
        if (Vertices.Count == 0) return (0, 0, 0, 0, 0, 0);
        Obj8Vertex f = Vertices[0];
        float minX = f.X, minY = f.Y, minZ = f.Z, maxX = f.X, maxY = f.Y, maxZ = f.Z;
        foreach (Obj8Vertex v in Vertices)
        {
            if (v.X < minX) minX = v.X;
            if (v.Y < minY) minY = v.Y;
            if (v.Z < minZ) minZ = v.Z;
            if (v.X > maxX) maxX = v.X;
            if (v.Y > maxY) maxY = v.Y;
            if (v.Z > maxZ) maxZ = v.Z;
        }
        return (minX, minY, minZ, maxX, maxY, maxZ);
    }

    public int TriangleCount => Runs.Sum(r => r.IndexCount) / 3;
}
