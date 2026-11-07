using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace AeroflyConv.Core.Tm;

/// <summary>
/// One node of a "TM" object tree. A node is either a container (children) or a leaf
/// (raw payload). Type/name are 64 bit ids; see <see cref="TmIdTables"/>.
/// </summary>
public sealed class TmNode
{
    public ulong TypeId;
    public ulong NameId;
    /// <summary>Leaf payload (containers keep it empty).</summary>
    public byte[] Payload = [];
    /// <summary>Child nodes (empty for leaves).</summary>
    public List<TmNode> Children = [];
    /// <summary>Raw on-disk header sizes, kept so round trips stay byte identical.</summary>
    public long RawSize;
    public long RawHeaderSize;

    public bool IsContainer => Children.Count > 0 || RawHeaderSize == TmFormats.ChunkHeaderSize;

    public TmNode(ulong typeId, ulong nameId)
    {
        TypeId = typeId;
        NameId = nameId;
    }

    public TmNode Add(TmNode child)
    {
        Children.Add(child);
        return this;
    }

    public TmNode? Child(ulong nameId)
    {
        foreach (TmNode c in Children)
            if (c.NameId == nameId) return c;
        return null;
    }

    public string? StringValue() => TmTree.ReadString(Payload);

    public override string ToString() =>
        $"{TmTree.TypeName(TypeId)}/{TmTree.NameName(NameId)} children={Children.Count} payload={Payload.Length}";
}

/// <summary>
/// Reads and writes Aerofly "TM" object trees (the inflated payload of .tmb/.tsc/.toc/.tmd blobs).
/// </summary>
public static class TmTree
{
    private static readonly Dictionary<ulong, string> TypesById = BuildTypeMap();
    private static readonly Dictionary<ulong, string> NamesById = BuildNameMap();

    private static Dictionary<ulong, string> BuildTypeMap() => BuildMap(TmIdTables.TypeNamesRaw);
    private static Dictionary<ulong, string> BuildNameMap() => BuildMap(TmIdTables.NameIdsRaw);

    private static Dictionary<ulong, string> BuildMap((string Id, string Name)[] entries)
    {
        var map = new Dictionary<ulong, string>(entries.Length);
        foreach (var (id, name) in entries)
            map[BinaryPrimitives.ReadUInt64LittleEndian(Convert.FromHexString(id))] = name;
        return map;
    }

    /// <summary>Parses a 16 char hex id as printed by the reference tables (on-disk byte order).</summary>
    public static ulong Id(string hex) =>
        BinaryPrimitives.ReadUInt64LittleEndian(Convert.FromHexString(hex));

    public static string TypeName(ulong id) =>
        TypesById.TryGetValue(id, out string? n) ? n : "0x" + id.ToString("x16", CultureInfo.InvariantCulture);

    public static string NameName(ulong id) =>
        NamesById.TryGetValue(id, out string? n) ? n : "0x" + id.ToString("x16", CultureInfo.InvariantCulture);

    public static ulong? TypeId(string name) => Lookup(TmIdTables.TypeNamesRaw, name);
    public static ulong? NameId(string name) => Lookup(TmIdTables.NameIdsRaw, name);

    private static ulong? Lookup((string Id, string Name)[] entries, string name)
    {
        foreach (var (id, n) in entries)
            if (n == name) return Id(id);
        return null;
    }

    // ------------------------------------------------------------------ reading
    private static int Step(long size, long headerSize) =>
        (int)Math.Max(Math.Max(size, headerSize), TmFormats.ChunkHeaderSize);

    /// <summary>Parses an inflated TM object blob into a node tree.</summary>
    public static List<TmNode> Parse(ReadOnlySpan<byte> blob) => ParseRange(blob, 0, blob.Length);

    public static List<TmNode> ParseRange(ReadOnlySpan<byte> blob, int start, int end)
    {
        var list = new List<TmNode>();
        int off = start;
        while (off + TmFormats.ChunkHeaderSize <= end)
        {
            ulong typ = BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(off, 8));
            ulong nam = BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(off + 8, 8));
            long size = (long)TmFormats.U64(blob, off + 16);
            long hs = (long)TmFormats.U64(blob, off + 24);
            int step = Step(size, hs);
            if (off + step > end)
            {
                if (size >= TmFormats.ChunkHeaderSize && off + size <= end) step = (int)size;
                else break;
            }

            int payloadLength = Math.Max(0, (int)size - TmFormats.ChunkHeaderSize);
            int available = Math.Max(0, Math.Min(payloadLength, step - TmFormats.ChunkHeaderSize));
            var node = new TmNode(typ, nam) { RawSize = size, RawHeaderSize = hs };

            if (hs == TmFormats.ChunkHeaderSize)
                node.Children = ParseRange(blob, off + TmFormats.ChunkHeaderSize, off + step);
            else
                node.Payload = blob.Slice(off + TmFormats.ChunkHeaderSize, available).ToArray();

            list.Add(node);
            off += step;
        }
        return list;
    }

    /// <summary>Parses a .tmb/.tsc file (inflating it when compressed).</summary>
    public static List<TmNode> ParseFile(string path) =>
        Parse(TmCompress.InflateFile(File.ReadAllBytes(path)));

    public static string? ReadString(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8) return null;
        ulong n = BinaryPrimitives.ReadUInt64LittleEndian(payload);
        if (8 + (long)n > payload.Length) return null;
        return Encoding.ASCII.GetString(payload.Slice(8, (int)n));
    }

    public static byte[] StringPayload(string s)
    {
        byte[] raw = Encoding.ASCII.GetBytes(s);
        // Shipped files store an 8-byte length prefix, the characters, then zero padding
        // up to the next multiple of 8 bytes (string8 / string8u leaves).
        int padded = (8 + raw.Length + 7) & ~7;
        var buf = new byte[padded];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, (ulong)raw.Length);
        raw.CopyTo(buf, 8);
        return buf;
    }

    public static byte[] Int32Payload(int v)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, v);
        return buf;
    }

    public static byte[] UInt32Payload(uint v)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, v);
        return buf;
    }

    public static byte[] Float32Payload(float v)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(buf, v);
        return buf;
    }

    public static byte[] Float64Payload(double v)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(buf, v);
        return buf;
    }

    public static byte[] BoolPayload(bool v) => [v ? (byte)1 : (byte)0];

    public static byte[] Floats32Payload(ReadOnlySpan<float> values)
    {
        var buf = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(i * 4, 4), values[i]);
        return buf;
    }

    public static byte[] UInt32sPayload(ReadOnlySpan<uint> values)
    {
        var buf = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(i * 4, 4), values[i]);
        return buf;
    }

    public static byte[] Int32sPayload(ReadOnlySpan<int> values)
    {
        var buf = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(i * 4, 4), values[i]);
        return buf;
    }

    public static byte[] DoublesPayload(ReadOnlySpan<double> values)
    {
        var buf = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(i * 8, 8), values[i]);
        return buf;
    }

    public static byte[] Vector3sPayload(ReadOnlySpan<float> xyz)
    {
        var buf = new byte[xyz.Length * 4];
        for (int i = 0; i < xyz.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(i * 4, 4), xyz[i]);
        return buf;
    }

    // ------------------------------------------------------------------ writing
    /// <summary>Creates a container node. Children may be added later via <see cref="TmNode.Add"/>.</summary>
    public static TmNode Container(string type, string name, params TmNode[] children)
    {
        var node = new TmNode(TypeId(type) ?? throw new ArgumentException($"unknown type id: {type}"), NameId(name) ?? throw new ArgumentException($"unknown name id: {name}"))
        {
            RawHeaderSize = TmFormats.ChunkHeaderSize,
        };
        node.Children.AddRange(children);
        return node;
    }

    /// <summary>Creates a leaf node with the given payload.</summary>
    public static TmNode Leaf(string type, string name, byte[] payload) =>
        new(TypeId(type) ?? throw new ArgumentException($"unknown type id: {type}"), NameId(name) ?? throw new ArgumentException($"unknown name id: {name}"))
        {
            Payload = payload,
        };

    public static TmNode StringLeaf(string type, string name, string value) =>
        Leaf(type, name, StringPayload(value));

    /// <summary>
    /// Serialises a node list into the TM binary form (uncompressed). Leaf headers use
    /// size = 32 + payload and hdr = 32 + roundup8(payload), matching the shipped files.
    /// </summary>
    public static byte[] Write(ReadOnlySpan<TmNode> nodes)
    {
        var ms = new MemoryStream(1 << 16);
        WriteNodes(ms, nodes);
        return ms.ToArray();
    }

    private static void WriteNodes(Stream s, ReadOnlySpan<TmNode> nodes)
    {
        foreach (TmNode node in nodes)
        {
            if (node.IsContainer)
            {
                var body = new MemoryStream(1 << 12);
                WriteNodes(body, node.Children.ToArray());
                int size = TmFormats.ChunkHeaderSize + (int)body.Length;
                WriteHeader(s, node.TypeId, node.NameId, size, TmFormats.ChunkHeaderSize);
                body.Position = 0;
                body.CopyTo(s);
            }
            else
            {
                byte[] payload = node.Payload;
                int size = TmFormats.ChunkHeaderSize + payload.Length;
                int header = TmFormats.ChunkHeaderSize + RoundUp8(payload.Length);
                WriteHeader(s, node.TypeId, node.NameId, size, header);
                s.Write(payload);
                for (int i = 0; i < header - size; i++) s.WriteByte(0);
            }
        }
    }

    private static int RoundUp8(int v) => v <= 8 ? 8 : (v + 7) & ~7;

    private static void WriteHeader(Stream s, ulong typeId, ulong nameId, long size, long headerSize)
    {
        Span<byte> hdr = stackalloc byte[TmFormats.ChunkHeaderSize];
        hdr.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(hdr[..8], typeId);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(8, 8), nameId);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(16, 8), (ulong)size);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(24, 8), (ulong)headerSize);
        s.Write(hdr);
    }

    /// <summary>
    /// Serialises and LZHAM-compresses a node list into a complete .tmb container, mirroring
    /// the <c>compress_file=true</c> wrapper of the shipped files:
    /// two magic chunks (outer/nested) + SizeUncomp + SizeComp + CrcUncomp + CrcComp + comp_blob.
    /// </summary>
    public static byte[] WriteContainer(ReadOnlySpan<TmNode> nodes, bool compress = true)
    {
        byte[] blob = Write(nodes);
        if (!compress) return WrapPlain(blob);
        return WrapCompressed(blob, TmCompress.Compress(blob));
    }

    private static byte[] WrapCompressed(byte[] uncompressed, byte[] compressBlob)
    {
        // 0x00 outer chunk, 0x20 nested chunk, 0x40..0xdf metadata, 0xe0 comp_blob chunk
        const int headerEnd = 0xE0;
        int compLen = compressBlob.Length;
        long blobSize = TmFormats.ChunkHeaderSize + compLen;
        long blobHs = RoundUp8((int)blobSize);
        long total = headerEnd + blobHs;

        var ms = new MemoryStream((int)total);
        var magic = TmFormats.MagicCompressed;
        WriteRawHeader(ms, magic, TmNames.NameOuter, total, TmFormats.ChunkHeaderSize);
        WriteRawHeader(ms, magic, TmNames.NameNested, total - TmFormats.ChunkHeaderSize, TmFormats.ChunkHeaderSize);
        WriteLeaf(ms, TmNames.TypeUInt64, TmNames.NameSizeUncomp, U64Payload((ulong)uncompressed.Length));
        WriteLeaf(ms, TmNames.TypeUInt64, TmNames.NameSizeComp, U64Payload((ulong)compLen));
        WriteLeaf(ms, TmNames.TypeUInt32, TmNames.NameCrcUncomp, UInt32Payload(TmFormats.Crc32(uncompressed)));
        WriteLeaf(ms, TmNames.TypeUInt32, TmNames.NameCrcComp, UInt32Payload(TmFormats.Crc32(compressBlob)));
        WriteRawHeader(ms, TmNames.TypeListU8, TmNames.NameCompBlob, blobSize, blobHs);
        ms.Write(compressBlob);
        while (ms.Length < total) ms.WriteByte(0);
        return ms.ToArray();
    }

    private static byte[] U64Payload(ulong v)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, v);
        return buf;
    }

    private static void WriteLeaf(Stream s, ulong typeId, ulong nameId, byte[] payload)
    {
        int size = TmFormats.ChunkHeaderSize + payload.Length;
        int hs = RoundUp8(size);
        WriteRawHeader(s, typeId, nameId, size, hs);
        s.Write(payload);
        for (int i = 0; i < hs - size; i++) s.WriteByte(0);
    }

    private static void WriteRawHeader(Stream s, ReadOnlySpan<byte> typeBytes, ulong nameId, long size, long hs)
    {
        Span<byte> hdr = stackalloc byte[TmFormats.ChunkHeaderSize];
        hdr.Clear();
        typeBytes[..8].CopyTo(hdr);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(8, 8), nameId);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(16, 8), (ulong)size);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(24, 8), (ulong)hs);
        s.Write(hdr);
    }

    private static void WriteRawHeader(Stream s, ulong typeId, ulong nameId, long size, long hs)
    {
        Span<byte> hdr = stackalloc byte[TmFormats.ChunkHeaderSize];
        hdr.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(hdr[..8], typeId);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(8, 8), nameId);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(16, 8), (ulong)size);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(24, 8), (ulong)hs);
        s.Write(hdr);
    }

    private static byte[] WrapPlain(byte[] blob)
    {
        var file = new MemoryStream(blob.Length + 16);
        file.Write(TmFormats.MagicPlain);
        file.Write(blob);
        return file.ToArray();
    }
}
