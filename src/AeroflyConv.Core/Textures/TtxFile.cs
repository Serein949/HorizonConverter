using System.Buffers.Binary;
using AeroflyConv.Core.Codecs;
using AeroflyConv.Core.Tm;

namespace AeroflyConv.Core.Textures;

/// <summary>Parsed TTX texture header.</summary>
public sealed class TtxInfo
{
    public required string Format { get; init; }
    public string? Target { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Layers { get; init; } = 1;
    public int Mips { get; init; } = 1;
    public required ReadOnlyMemory<byte> Pixels { get; init; }
}

/// <summary>A decoded texture: top mip as tightly packed RGBA8.</summary>
public sealed class DecodedTexture
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required string Format { get; init; }
    public required int Mips { get; init; }
    public required bool Compressed { get; init; }
    public required byte[] Rgba { get; init; }

    public bool PowerOfTwo => TmFormats.IsPowerOfTwo(Width) && TmFormats.IsPowerOfTwo(Height);
    public bool PngHasAlpha => !TextureFormats.IsOpaqueRgb(Format);
}

/// <summary>TTX texture container reader / decoder / encoder.</summary>
public static class TtxFile
{
    /// <summary>Decompresses <c>compress_file=true</c> files; plain files pass through unchanged.</summary>
    public static byte[] Inflate(ReadOnlySpan<byte> raw) => TmCompress.InflateFile(raw);

    /// <summary>
    /// Parses an already inflated (plain) TTX container. Mirrors the reference walker:
    /// any 8+ byte payload that decodes as a string is a format/target property, any
    /// 4..8 byte payload is the next numeric property (width, height, layers, mips) and
    /// the first bigger payload is the pixel data. IPACS uses several different string
    /// chunk type ids (string8, string8u, stringt8c) so the type id is not used.
    /// </summary>
    public static TtxInfo Parse(ReadOnlySpan<byte> plain)
    {
        if (!TmFormats.HasMagic(plain, TmFormats.MagicPlain))
            throw new AeroflyFormatException("not a TTX texture container");

        string? format = null, target = null;
        int width = 0, height = 0, layers = 0, mips = 0;
        bool haveWidth = false, haveHeight = false, haveLayers = false, haveMips = false;
        ReadOnlyMemory<byte> pixels = default;
        bool havePixels = false;

        int pos = TmFormats.SkipContainers(plain);
        while (pos + TmFormats.ChunkHeaderSize <= plain.Length)
        {
            long size = (long)TmFormats.U64(plain, pos + 16);
            long hs = (long)TmFormats.U64(plain, pos + 24);
            if (size < TmFormats.ChunkHeaderSize) break;

            int available = plain.Length - pos - TmFormats.ChunkHeaderSize;
            if (available < 0) break;
            int payloadLength = (int)Math.Min(size - TmFormats.ChunkHeaderSize, available);

            if (size > 200)
            {
                // the large blob is always the pixel data
                pixels = plain.Slice(pos + TmFormats.ChunkHeaderSize, payloadLength).ToArray();
                havePixels = true;
                break;
            }

            int step = TmFormats.ChunkStep(size, hs);
            if (step < TmFormats.ChunkHeaderSize) break;
            ReadOnlySpan<byte> payload = plain.Slice(
                pos + TmFormats.ChunkHeaderSize,
                Math.Max(0, Math.Min((int)size - TmFormats.ChunkHeaderSize, step - TmFormats.ChunkHeaderSize)));

            if (payload.Length >= 8)
            {
                string? s = TmFormats.TryParseString(payload);
                if (s is not null)
                {
                    if (s.StartsWith("type_", StringComparison.Ordinal)) format = s;
                    else if (s.StartsWith("target_", StringComparison.Ordinal)) target = s;
                    pos += step;
                    continue;
                }
            }

            if (payload.Length is >= 4 and <= 8)
            {
                int value = (int)TmFormats.U32(payload, 0);
                if (!haveWidth) { width = value; haveWidth = true; }
                else if (!haveHeight) { height = value; haveHeight = true; }
                else if (!haveLayers) { layers = value; haveLayers = true; }
                else if (!haveMips) { mips = value; haveMips = true; }
                pos += step;
                continue;
            }

            pixels = payload.ToArray();
            havePixels = true;
            break;
        }

        if (format is null || !haveWidth || !haveHeight || !havePixels)
            throw new AeroflyFormatException(
                $"incomplete texture: fmt={format ?? "?"} {width}x{height} " +
                $"pixels={(havePixels ? pixels.Length : 0)}");

        return new TtxInfo
        {
            Format = format,
            Target = target,
            Width = width,
            Height = height,
            Layers = layers <= 0 ? 1 : layers,
            Mips = mips <= 0 ? 1 : mips,
            Pixels = pixels,
        };
    }

    /// <summary>Full decode of a TTX file (compressed or plain) into RGBA8.</summary>
    public static DecodedTexture Decode(ReadOnlySpan<byte> raw, bool flip = false)
    {
        bool compressed = TmFormats.IsCompressedFile(raw);
        byte[] inner = compressed ? TmCompress.InflateFile(raw) : raw.ToArray();
        TtxInfo info = Parse(inner);
        byte[] rgba = DecodeRgba(info, out string? note);
        if (flip) rgba = MipChain.FlipVertical(rgba, info.Width, info.Height);
        return new DecodedTexture
        {
            Width = info.Width,
            Height = info.Height,
            Format = info.Format,
            Mips = info.Mips,
            Compressed = compressed,
            Rgba = rgba,
        };
    }

    /// <summary>Decodes the pixel payload of a parsed texture into RGBA8.</summary>
    public static byte[] DecodeRgba(TtxInfo info, out string? note)
    {
        note = null;
        string format = info.Format;
        int w = info.Width, h = info.Height;
        ReadOnlySpan<byte> pix = info.Pixels.Span;
        var rgba = new byte[w * h * 4];

        switch (format)
        {
            case "type_rgba":
                pix[..Math.Min(pix.Length, rgba.Length)].CopyTo(rgba);
                return rgba;

            case "type_r":
            {
                int n = Math.Min(pix.Length, w * h);
                for (int i = 0; i < n; i++)
                {
                    byte v = pix[i];
                    int o = i * 4;
                    rgba[o] = v;
                    rgba[o + 1] = v;
                    rgba[o + 2] = v;
                    rgba[o + 3] = 255;
                }
                return rgba;
            }

            case "type_rgb_s3tc_dxt1":
            case "type_rgba_s3tc_dxt1":
                BcCodec.DecodeBc1(pix, w, h, rgba);
                return rgba;

            case "type_rgba_s3tc_dxt3":
                BcCodec.DecodeBc2(pix, w, h, rgba);
                return rgba;

            case "type_rgba_s3tc_dxt5":
                BcCodec.DecodeBc3(pix, w, h, rgba);
                return rgba;

            case "type_rg_rgtc2":
            {
                // BC5 normal map: X/Y stored, Z reconstructed on the unit sphere.
                var rg = new byte[w * h * 2];
                BcCodec.DecodeBc5(pix, w, h, rg);
                DecodeNormalMap(rg, rgba, w * h);
                return rgba;
            }

            case "type_rgb_etc2":
            case "type_rgb_etc1":
                EtcCodec.DecodeEtc2Rgb(pix, w, h, rgba);
                return rgba;

            case "type_rgba_etc2":
                EtcCodec.DecodeEtc2Rgba(pix, w, h, rgba);
                return rgba;

            default:
                if (format.StartsWith("type_rgb_astc_", StringComparison.Ordinal) ||
                    format.StartsWith("type_rgba_astc_", StringComparison.Ordinal))
                {
                    throw new AeroflyFormatException(
                        $"'{format}' (ASTC) is not supported by this converter yet - " +
                        "it is only used by Aerofly mobile builds");
                }
                throw new AeroflyFormatException($"unsupported texture format: {format}");
        }
    }

    /// <summary>BC5 normal map expansion (X, Y -> RGBA with reconstructed Z).</summary>
    private static void DecodeNormalMap(ReadOnlySpan<byte> rg, Span<byte> rgba, int pixelCount)
    {
        for (int i = 0; i < pixelCount; i++)
        {
            int x = rg[i * 2];
            int y = rg[i * 2 + 1];
            float fx = x / 255f * 2f - 1f;
            float fy = y / 255f * 2f - 1f;
            float fz = Math.Max(0f, 1f - fx * fx - fy * fy);
            // Truncate like the reference converter does for the blue channel.
            int z = (int)((MathF.Sqrt(fz) + 1f) * 0.5f * 255f);
            if (z < 0) z = 0;
            if (z > 255) z = 255;

            int o = i * 4;
            rgba[o] = (byte)x;
            rgba[o + 1] = (byte)y;
            rgba[o + 2] = (byte)z;
            rgba[o + 3] = 255;
        }
    }

    // ------------------------------------------------------------------ encode

    public sealed class EncodeResult
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required string Format { get; init; }
        public required int Mips { get; init; }
        public required bool Compressed { get; init; }
        public required byte[] Data { get; init; }
    }

    /// <summary>
    /// Encodes RGBA8 pixels into a shipping-style TTX (full mip chain, LZHAM
    /// <c>compress_file</c> wrapper).
    /// </summary>
    public static EncodeResult Encode(byte[] rgba, int width, int height, string format, bool flip = false,
        bool compress = true, Action<string>? status = null)
    {
        format = TextureFormats.Canonical(format);

        bool hasAlpha = false;
        if (format == TextureFormats.AutoDxt || format is "type_rgb_etc2")
        {
            for (int i = 3; i < rgba.Length; i += 4)
            {
                if (rgba[i] < 255) { hasAlpha = true; break; }
            }
        }

        if (format == TextureFormats.AutoDxt)
            format = hasAlpha ? "type_rgba_s3tc_dxt5" : "type_rgb_s3tc_dxt1";

        if (format == "type_rgb_etc2" && hasAlpha) format = "type_rgba_etc2";

        if (!TextureFormats.Encodable.ContainsKey(format))
            throw new AeroflyFormatException($"unsupported output texture format: {format}");

        if (flip) rgba = MipChain.FlipVertical(rgba, width, height);

        int lastMax = format is "type_rgb_etc2" or "type_rgba_etc2" ? 4 : 1;
        string fmt = format;
        var parts = new List<byte[]>(8);
        int mips = 0;
        foreach (var (level, lw, lh) in MipChain.Levels(rgba, width, height, lastMax))
        {
            parts.Add(EncodeLevel(level, lw, lh, fmt));
            mips++;
        }

        int total = 0;
        foreach (var p in parts) total += p.Length;
        var payload = new byte[total];
        int offset = 0;
        foreach (var p in parts)
        {
            p.CopyTo(payload, offset);
            offset += p.Length;
        }

        byte[] plain = BuildPlainContainer(format, width, height, payload, mips);
        byte[] data;
        if (compress)
        {
            status?.Invoke($"Compressing ({mips} mips, LZHAM) ...");
            ReadOnlySpan<byte> inner = plain.AsSpan(TmFormats.ChunkHeaderSize);
            byte[] blob = TmCompress.Compress(inner);
            data = BuildCompressedContainer(inner, blob);
        }
        else
        {
            data = plain;
        }

        return new EncodeResult
        {
            Width = width,
            Height = height,
            Format = format,
            Mips = mips,
            Compressed = compress,
            Data = data,
        };
    }

    private static byte[] EncodeLevel(byte[] rgba, int w, int h, string format)
    {
        switch (format)
        {
            case "type_rgba":
                return rgba;

            case "type_r":
            {
                var r8 = new byte[w * h];
                for (int i = 0; i < r8.Length; i++)
                {
                    int o = i * 4;
                    // ITU-R BT.601 luma with round-to-nearest (matches Pillow's "L" conversion)
                    r8[i] = (byte)((rgba[o] * 299 + rgba[o + 1] * 587 + rgba[o + 2] * 114 + 500) / 1000);
                }
                return r8;
            }

            case "type_rgb_s3tc_dxt1":
            {
                byte[] padded = MipChain.PadToMultiple(rgba, w, h, 4, out int pw, out int ph);
                var dest = new byte[pw / 4 * (ph / 4) * 8];
                BcCodec.EncodeBc1(padded, pw, ph, dest);
                return dest;
            }

            case "type_rgba_s3tc_dxt5":
            {
                byte[] padded = MipChain.PadToMultiple(rgba, w, h, 4, out int pw, out int ph);
                var dest = new byte[pw / 4 * (ph / 4) * 16];
                BcCodec.EncodeBc3(padded, pw, ph, dest);
                return dest;
            }

            case "type_rgb_etc2":
            case "type_rgba_etc2":
            {
                byte[] padded = MipChain.PadToMultiple(rgba, w, h, 4, out int pw, out int ph);
                int blocks = pw / 4 * (ph / 4);
                if (format == "type_rgb_etc2")
                {
                    var dest = new byte[blocks * 8];
                    EtcCodec.EncodeEtc2Rgb(padded, pw, ph, dest);
                    return dest;
                }
                else
                {
                    var dest = new byte[blocks * 16];
                    EtcCodec.EncodeEtc2Rgba(padded, pw, ph, dest);
                    return dest;
                }
            }

            default:
                throw new AeroflyFormatException($"unsupported output texture format: {format}");
        }
    }

    // ------------------------------------------------------- container building

    private static void WriteU64(Span<byte> dest, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(offset, 8), value);

    private static void WriteU32(Span<byte> dest, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(offset, 4), value);

    private static void WriteId(Span<byte> dest, int offset, ulong id) =>
        BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(offset, 8), id);

    private static int Align8(int n) => (n + 7) & ~7;

    /// <summary>Builds one chunk: type id, name id, size, header size, payload (+padding).</summary>
    private static byte[] Chunk(ulong type, ulong name, ReadOnlySpan<byte> payload, int size, int headerSize)
    {
        var chunk = new byte[headerSize];
        WriteId(chunk, 0, type);
        WriteId(chunk, 8, name);
        WriteU64(chunk, 16, (ulong)size);
        WriteU64(chunk, 24, (ulong)headerSize);
        payload.CopyTo(chunk.AsSpan(TmFormats.ChunkHeaderSize));
        return chunk;
    }

    private static byte[] StringChunk(string value, ulong name)
    {
        byte[] ascii = System.Text.Encoding.ASCII.GetBytes(value);
        int payloadLength = 8 + ascii.Length;
        int inner = Align8(payloadLength);
        int size = TmFormats.ChunkHeaderSize + inner;
        var payload = new byte[inner];
        WriteU64(payload, 0, (ulong)ascii.Length);
        ascii.CopyTo(payload, 8);
        return Chunk(TmNames.TypeString8, name, payload, size, size);
    }

    private static byte[] U32Chunk(int value, ulong name)
    {
        var payload = new byte[8];
        WriteU32(payload, 0, (uint)value);
        return Chunk(TmNames.TypeInt32, name, payload, 36, 40);
    }

    private static byte[] U64Chunk(long value, ulong name)
    {
        var payload = new byte[8];
        WriteU64(payload, 0, (ulong)value);
        return Chunk(TmNames.TypeUInt64, name, payload, 40, 40);
    }

    private static byte[] CrcChunk(uint value, ulong name)
    {
        var payload = new byte[8];
        WriteU32(payload, 0, value);
        return Chunk(TmNames.TypeUInt32, name, payload, 36, 40);
    }

    private static byte[] DataChunk(ReadOnlySpan<byte> payload, ulong name)
    {
        int size = TmFormats.ChunkHeaderSize + payload.Length;
        return Chunk(TmNames.TypeListU8, name, payload, size, Align8(size));
    }

    private static byte[] WrapContainer(ReadOnlySpan<byte> magic, ulong name, ReadOnlySpan<byte> inner)
    {
        var result = new byte[TmFormats.ChunkHeaderSize + inner.Length];
        magic.CopyTo(result);
        WriteId(result, 8, name);
        WriteU64(result, 16, (ulong)result.Length);
        WriteU64(result, 24, TmFormats.ChunkHeaderSize);
        inner.CopyTo(result.AsSpan(TmFormats.ChunkHeaderSize));
        return result;
    }

    /// <summary>Two-level plain container (compress_file=false).</summary>
    public static byte[] BuildPlainContainer(string format, int width, int height, byte[] payload, int mips)
    {
        using var ms = new MemoryStream(TmFormats.ChunkHeaderSize + payload.Length + 256);
        void Append(byte[] chunk) => ms.Write(chunk, 0, chunk.Length);

        Append(StringChunk(format, TmNames.NameType));
        Append(StringChunk("target_2d", TmNames.NameTarget));
        Append(U32Chunk(width, TmNames.NameWidth));
        Append(U32Chunk(height, TmNames.NameHeight));
        Append(U32Chunk(1, TmNames.NameLayers));
        Append(U32Chunk(Math.Max(1, mips), TmNames.NameMips));
        Append(DataChunk(payload, TmNames.NamePixels));

        byte[] nested = WrapContainer(TmFormats.MagicPlain, TmNames.NameNested, ms.ToArray());
        return WrapContainer(TmFormats.MagicPlain, TmNames.NameOuter, nested);
    }

    /// <summary>Two-level compressed container (compress_file=true) around an LZHAM blob.</summary>
    public static byte[] BuildCompressedContainer(ReadOnlySpan<byte> innerUncompressed, byte[] blob)
    {
        uint crcUncomp = TmFormats.Crc32(innerUncompressed);
        uint crcComp = TmFormats.Crc32(blob);
        using var ms = new MemoryStream(blob.Length + 256);

        void Append(byte[] chunk) => ms.Write(chunk, 0, chunk.Length);
        Append(U64Chunk(innerUncompressed.Length, TmNames.NameSizeUncomp));
        Append(U64Chunk(blob.Length, TmNames.NameSizeComp));
        Append(CrcChunk(crcUncomp, TmNames.NameCrcUncomp));
        Append(CrcChunk(crcComp, TmNames.NameCrcComp));
        Append(DataChunk(blob, TmNames.NameCompBlob));

        byte[] nested = WrapContainer(TmFormats.MagicCompressed, TmNames.NameNested, ms.ToArray());
        return WrapContainer(TmFormats.MagicCompressed, TmNames.NameOuter, nested);
    }
}
