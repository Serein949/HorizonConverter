using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using AeroflyConv.Core.Tm;

namespace AeroflyConv.Core.Tm;

/// <summary>
/// Generic text dump of Aerofly TM containers (.toc/.tsc/.wad/.tmb/.tsl) - a flat
/// type/name/value tree identical in layout to the reference converter's output.
/// </summary>
public static class TmTextDump
{
    private const int HexBlobLine = 32;
    private const int MaxInlineBlob = 64;
    private static readonly string[] ListPrefixes = ["pointer_list_", "list_", "array_"];

    private static readonly FrozenDictionary<ulong, string> TypeNames = Build(TmIdTables.TypeNamesRaw);
    private static readonly FrozenDictionary<ulong, string> NameIds = Build(TmIdTables.NameIdsRaw);

    private static FrozenDictionary<ulong, string> Build((string Id, string Name)[] entries)
    {
        var dict = new Dictionary<ulong, string>(entries.Length);
        foreach (var (id, name) in entries)
        {
            ulong key = BinaryPrimitives.ReadUInt64BigEndian(Convert.FromHexString(id));
            dict[key] = name;
        }
        return dict.ToFrozenDictionary();
    }

    /// <summary>Reads an 8 byte id in file order (so its hex form matches the id tables).</summary>
    private static ulong Id(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset, 8));

    private static string Hex(ulong id) => id.ToString("x16", CultureInfo.InvariantCulture);

    private static string Label(FrozenDictionary<ulong, string> table, ulong raw, string prefix)
    {
        if (table.TryGetValue(raw, out string? name)) return name;
        string hex = Hex(raw);
        return prefix + hex[..8];
    }

    private static int Step(long size, long headerSize) =>
        (int)Math.Max(Math.Max(size, headerSize), 32);

    /// <summary>Slice that never throws on truncated containers (clamps to the buffer end).</summary>
    private static ReadOnlySpan<byte> SafeSlice(ReadOnlySpan<byte> data, int offset, int length)
    {
        if (offset >= data.Length || length <= 0) return ReadOnlySpan<byte>.Empty;
        int available = data.Length - offset;
        return data.Slice(offset, Math.Min(length, available));
    }

    private static float F32(ReadOnlySpan<byte> b, int offset) =>
        BinaryPrimitives.ReadSingleLittleEndian(b.Slice(offset, 4));

    private static double F64(ReadOnlySpan<byte> b, int offset) =>
        BinaryPrimitives.ReadDoubleLittleEndian(b.Slice(offset, 8));

    /// <summary>Python <c>format(x, ".15g")</c> equivalent (plus integer shortening).</summary>
    public static string FormatNumber(double x, bool isF32 = false)
    {
        if (isF32) x = (float)x;
        if (double.IsNaN(x)) return "nan";
        if (double.IsPositiveInfinity(x)) return "inf";
        if (double.IsNegativeInfinity(x)) return "-inf";
        if (x == Math.Floor(x) && Math.Abs(x) < 1e15)
            return ((long)x).ToString(CultureInfo.InvariantCulture);
        return x.ToString("G15", CultureInfo.InvariantCulture).ToLowerInvariant();
    }

    private static string ItemType(string parentType)
    {
        foreach (string prefix in ListPrefixes)
            if (parentType.StartsWith(prefix, StringComparison.Ordinal))
                return parentType[prefix.Length..];
        return "element";
    }

    /// <summary>Parses "string8"-style payloads; returns null when it does not look like text.</summary>
    private static string? LookLikeString(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8) return null;
        ulong n = BinaryPrimitives.ReadUInt64LittleEndian(payload);
        if (n > 1_000_000 || 8 + (long)n > payload.Length) return null;
        if (n == 0) return "";
        ReadOnlySpan<byte> raw = payload.Slice(8, (int)n);
        foreach (byte b in raw)
        {
            if (!((b >= 32 && b < 127) || b is 9 or 10 or 13)) return null;
        }
        return Encoding.ASCII.GetString(raw);
    }

    private static string PromoteType(string typeName, ReadOnlySpan<byte> payload)
    {
        if (typeName == "float32" && payload.Length > 4 && payload.Length % 4 == 0) return "list_float32";
        if (typeName == "uint32" && payload.Length > 4 && payload.Length % 4 == 0) return "list_uint32";
        if (typeName == "int32" && payload.Length > 4 && payload.Length % 4 == 0) return "array_int32";
        if (typeName == "float64" && payload.Length > 8 && payload.Length % 8 == 0) return "list_float64";
        if (typeName == "list_uint8" && payload.Length == 1) return "uint8";
        return typeName;
    }

    private static (string Value, List<string>? Extra) FormatBlob(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) return ("", null);
        if (payload.Length <= MaxInlineBlob) return (Convert.ToHexString(payload).ToLowerInvariant(), null);
        var rows = new List<string>((payload.Length + HexBlobLine - 1) / HexBlobLine);
        for (int i = 0; i < payload.Length; i += HexBlobLine)
        {
            ReadOnlySpan<byte> slice = payload.Slice(i, Math.Min(HexBlobLine, payload.Length - i));
            rows.Add(Convert.ToHexString(slice).ToLowerInvariant());
        }
        return (payload.Length.ToString(CultureInfo.InvariantCulture), rows);
    }

    private static string JoinDoubles(ReadOnlySpan<byte> payload, int count, int stride)
    {
        var sb = new StringBuilder(count * 12);
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(FormatNumber(F64(payload, i * stride)));
        }
        return sb.ToString();
    }

    private static string JoinFloats(ReadOnlySpan<byte> payload, int count, int stride)
    {
        var sb = new StringBuilder(count * 10);
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(FormatNumber(F32(payload, i * stride), true));
        }
        return sb.ToString();
    }

    private static (string Value, List<string>? Extra) LeafValue(string typeName, ReadOnlySpan<byte> payload)
    {
        switch (typeName)
        {
            case "bool":
                return ((payload.Length > 0 && payload[0] != 0) ? "true" : "false", null);

            case "float32" when payload.Length >= 4:
                return (FormatNumber(F32(payload, 0), true), null);
            case "float64" when payload.Length >= 8:
                return (FormatNumber(F64(payload, 0)), null);

            case "int8" when payload.Length > 0:
                return (((sbyte)payload[0]).ToString(CultureInfo.InvariantCulture), null);
            case "uint8" when payload.Length > 0:
                return (payload[0].ToString(CultureInfo.InvariantCulture), null);
            case "uint16" when payload.Length >= 2:
                return (BinaryPrimitives.ReadUInt16LittleEndian(payload).ToString(CultureInfo.InvariantCulture), null);
            case "int32" when payload.Length >= 4:
                return (BinaryPrimitives.ReadInt32LittleEndian(payload).ToString(CultureInfo.InvariantCulture), null);
            case "uint32" when payload.Length >= 4:
                return (BinaryPrimitives.ReadUInt32LittleEndian(payload).ToString(CultureInfo.InvariantCulture), null);
            case "uint64" when payload.Length >= 8:
                return (BinaryPrimitives.ReadUInt64LittleEndian(payload).ToString(CultureInfo.InvariantCulture), null);

            case "vector2_float64" when payload.Length >= 16:
                return (JoinDoubles(payload, 2, 8), null);
            case "vector3_float64" when payload.Length >= 24:
                return (JoinDoubles(payload, 3, 8), null);
            case "vector4_float32" when payload.Length >= 16:
                return (JoinFloats(payload, 4, 4), null);

            case "list_float32" or "raw_f32" when payload.Length >= 4:
            {
                int count = payload.Length / 4;
                return (JoinFloats(payload, count, 4), null);
            }
            case "list_float64" when payload.Length >= 8:
                return (JoinDoubles(payload, payload.Length / 8, 8), null);
            case "list_uint32" when payload.Length >= 4:
            {
                int count = payload.Length / 4;
                var sb = new StringBuilder(count * 6);
                for (int i = 0; i < count; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(i * 4, 4)).ToString(CultureInfo.InvariantCulture));
                }
                return (sb.ToString(), null);
            }
            case "array_int32" when payload.Length >= 4:
            {
                int count = payload.Length / 4;
                var sb = new StringBuilder(count * 6);
                for (int i = 0; i < count; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4)).ToString(CultureInfo.InvariantCulture));
                }
                return (sb.ToString(), null);
            }
            case "list_vector3_float32" when payload.Length >= 12:
            {
                int groups = payload.Length / 12;
                var sb = new StringBuilder(groups * 24);
                for (int i = 0; i < groups; i++)
                {
                    int o = i * 12;
                    sb.Append('(')
                      .Append(FormatNumber(F32(payload, o), true)).Append(' ')
                      .Append(FormatNumber(F32(payload, o + 4), true)).Append(' ')
                      .Append(FormatNumber(F32(payload, o + 8), true)).Append(") ");
                }
                return (sb.ToString(), null);
            }
            case "matrix4_float64" when payload.Length >= 128:
                return (JoinDoubles(payload, 16, 8), null);

            case "list_uint8":
                if (payload.Length <= 1)
                    return ((payload.Length > 0 ? payload[0] : 0).ToString(CultureInfo.InvariantCulture), null);
                return FormatBlob(payload);
        }

        if (typeName.StartsWith("string", StringComparison.Ordinal))
        {
            string? text = LookLikeString(payload);
            return (text ?? "", null);
        }

        string? generic = LookLikeString(payload);
        if (generic is not null) return (generic, null);
        if (payload.Length == 1) return (payload[0].ToString(CultureInfo.InvariantCulture), null);
        if (payload.Length == 4) return (FormatNumber(F32(payload, 0), true), null);
        if (payload.Length == 8) return (FormatNumber(F64(payload, 0)), null);
        if (payload.Length > 0 && payload.Length % 8 == 0 && payload.Length <= 128)
            return (JoinDoubles(payload, payload.Length / 8, 8), null);
        if (payload.Length > 0 && payload.Length % 4 == 0 && payload.Length <= 64)
            return (JoinFloats(payload, payload.Length / 4, 4), null);
        return FormatBlob(payload);
    }

    /// <summary>Recovers num_vertices from len(vertices)/vf_size when the stored count is 0.</summary>
    private static int? DerivedNumVertices(ReadOnlySpan<byte> data, int start, int end)
    {
        int off = start;
        int? vfSize = null;
        int? nfloat = null;
        int? stored = null;

        while (off + 32 <= end)
        {
            ulong nam = Id(data, off + 8);
            long size = (long)TmFormats.U64(data, off + 16);
            long hs = (long)TmFormats.U64(data, off + 24);
            int step = Step(size, hs);
            if (off + step > end)
            {
                if (size >= 32 && off + size <= end) step = (int)size;
                else break;
            }
            int payloadLength = Math.Max(0, (int)size - 32);
            ReadOnlySpan<byte> payload = SafeSlice(data, off + 32, Math.Min(payloadLength, step - 32));
            string field = NameIds.TryGetValue(nam, out string? n) ? n : "";

            if (field == "num_vertices")
                stored = payload.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(payload) : 0;
            else if (field == "vf_size" && payload.Length >= 4)
                vfSize = BinaryPrimitives.ReadInt32LittleEndian(payload);
            else if (field == "vertices" && payload.Length >= 4)
                nfloat = payload.Length / 4;

            off += step;
        }

        if (stored.HasValue && stored.Value != 0) return null;
        if (vfSize is null or <= 0 || nfloat is null) return null;
        if (nfloat.Value % vfSize.Value != 0) return null;
        return nfloat.Value / vfSize.Value;
    }

    private static void FillNumVertices(List<string> lines, int indent, int count)
    {
        string pad = new(' ', indent);
        string oldLine = $"{pad}<[uint32][num_vertices][0]>";
        string newLine = $"{pad}<[uint32][num_vertices][{count.ToString(CultureInfo.InvariantCulture)}]>";
        for (int i = 0; i < lines.Count; i++)
            if (lines[i] == oldLine) lines[i] = newLine;
    }

    private static void EmitNode(ReadOnlySpan<byte> data, int start, int end, int indent,
        ulong? parentType, ulong? parentName, List<string> lines)
    {
        int off = start;
        string pad = new(' ', indent);
        int itemIndex = 0;

        while (off + 32 <= end)
        {
            ulong typ = Id(data, off);
            ulong nam = Id(data, off + 8);
            long size = (long)TmFormats.U64(data, off + 16);
            long hs = (long)TmFormats.U64(data, off + 24);
            int step = Step(size, hs);
            if (off + step > end)
            {
                if (size >= 32 && off + size <= end) step = (int)size;
                else
                {
                    lines.Add($"{pad}// truncated object at 0x{off:x} size={size} hs={hs}");
                    break;
                }
            }

            int payloadLength = Math.Max(0, (int)size - 32);
            ReadOnlySpan<byte> payload = SafeSlice(data, off + 32, Math.Min(payloadLength, step - 32));

            string typeName = Label(TypeNames, typ, "type_");
            string fieldName = Label(NameIds, nam, "name_");

            bool isListItem = parentType is not null && parentName is not null &&
                              typ == parentType.Value && nam == parentName.Value;
            if (isListItem)
            {
                string parentLabel = Label(TypeNames, parentType!.Value, "type_");
                typeName = ItemType(parentLabel);
                fieldName = "element";
            }

            typeName = PromoteType(typeName, payload);
            if (hs == 32 && !isListItem && typeName is "uint32" or "uint8" or "float32")
                typeName = "list_" + typeName;

            if (hs == 32)
            {
                string value = isListItem ? itemIndex.ToString(CultureInfo.InvariantCulture) : "";
                if (isListItem) itemIndex++;
                lines.Add($"{pad}<[{typeName}][{fieldName}][{value}]");

                var childLines = new List<string>();
                EmitNode(data, off + 32, off + step, indent + 4, typ, nam, childLines);
                int? derived = DerivedNumVertices(data, off + 32, off + step);
                if (derived is not null) FillNumVertices(childLines, indent + 4, derived.Value);
                lines.AddRange(childLines);
                lines.Add($"{pad}>");
            }
            else
            {
                (string value, List<string>? extra) = LeafValue(typeName, payload);
                if (extra is null)
                {
                    lines.Add($"{pad}<[{typeName}][{fieldName}][{value}]>");
                }
                else
                {
                    lines.Add($"{pad}<[{typeName}][{fieldName}][{value}]");
                    string extraPad = new(' ', indent + 4);
                    foreach (string row in extra) lines.Add(extraPad + row);
                    lines.Add($"{pad}>");
                }
            }

            off += step;
        }
    }

    /// <summary>Inflates a TM container and renders the generic text tree.</summary>
    public static string DumpText(ReadOnlySpan<byte> raw, out int inflatedBytes, bool wrapFile = true)
    {
        byte[] blob = TmCompress.InflateFile(raw);
        inflatedBytes = blob.Length;
        if (blob.Length < 32)
            throw new AeroflyFormatException("inflated blob is too small to be a TM object");

        int rootIndent = wrapFile ? 4 : 0;
        var lines = new List<string>(1024);
        if (wrapFile) lines.Add("<[file][][]");
        EmitNode(blob, 0, blob.Length, rootIndent, null, null, lines);
        if (wrapFile) lines.Add(">");
        lines.Add("");
        return string.Join('\n', lines);
    }

    /// <summary>Number of <c>&lt;[xref][element][</c> occurrences (placement count).</summary>
    public static int CountXrefs(string text)
    {
        const string needle = "<[xref][element][";
        int count = 0, start = 0;
        while (start < text.Length)
        {
            int idx = text.IndexOf(needle, start, StringComparison.Ordinal);
            if (idx < 0) break;
            count++;
            start = idx + needle.Length;
        }
        return count;
    }
}
