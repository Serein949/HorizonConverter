using System.Buffers.Binary;
using System.IO.Compression;
using AeroflyConv.Core.Codecs;
using AeroflyConv.Core.Tm;

namespace AeroflyConv.Core.Textures;

/// <summary>
/// Aerofly scenery <c>.ttc</c> textures: a 256 byte header followed by zlib, LZHAM
/// (tmcompress) or raw GPU blocks. <c>compress=false</c> converter output may instead be
/// a TTX twin saved with a <c>.ttc</c> extension - that case is handled too.
/// </summary>
public static class TtcFile
{
    public const uint Magic = 0x303A;
    public const uint Version = 0x100;
    public const int HeaderSize = 256;

    public const int FormatR8 = 0;
    public const int FormatDxt1 = 10;
    public const int FormatDxt5 = 12;
    public const int FormatEtc2 = 21;

    public static string FormatName(int id) => id switch
    {
        FormatR8 => "r8",
        FormatDxt1 => "dxt1",
        FormatDxt5 => "dxt5",
        FormatEtc2 => "etc2",
        _ => $"unknown({id})",
    };

    public static int FormatId(string name) => name switch
    {
        "r8" => FormatR8,
        "dxt1" => FormatDxt1,
        "dxt5" => FormatDxt5,
        "etc2" => FormatEtc2,
        _ => throw new AeroflyFormatException($"unsupported TTC format: {name}"),
    };

    public static string ToTtxFormat(int id) => id switch
    {
        FormatR8 => "type_r",
        FormatDxt1 => "type_rgb_s3tc_dxt1",
        FormatDxt5 => "type_rgba_s3tc_dxt5",
        FormatEtc2 => "type_rgb_etc2",
        _ => throw new AeroflyFormatException($"unknown TTC format id {id}"),
    };

    public static bool IsNativeTtc(ReadOnlySpan<byte> data) =>
        data.Length >= 8 &&
        BinaryPrimitives.ReadUInt32LittleEndian(data[..4]) == Magic &&
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)) == Version;

    public static bool IsTtxTwin(ReadOnlySpan<byte> data) =>
        TmFormats.HasMagic(data, TmFormats.MagicPlain) || TmFormats.HasMagic(data, TmFormats.MagicCompressed);

    public sealed class TtcInfo
    {
        public required int Extra { get; init; }
        public required int PayloadLength { get; init; }
        public required int Uncompressed { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int Mips { get; init; }
        public required int FormatId { get; init; }
        public required string Format { get; init; }
        public required string PayloadMethod { get; init; }
        public required byte[] Pixels { get; init; }
    }

    public static TtcInfo Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new AeroflyFormatException("TTC file too short for its 256-byte header");
        if (!IsNativeTtc(data))
            throw new AeroflyFormatException("not a native TTC file");

        int extra = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, 4));
        int payload = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12, 4));
        int uncompressed = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16, 4));
        int width = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20, 4));
        int height = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(24, 4));
        int mips = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(28, 4));
        int formatId = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(32, 4));

        if (HeaderSize + payload != data.Length)
            throw new AeroflyFormatException(
                $"TTC payload length mismatch: header {payload}, file {data.Length - HeaderSize}");
        if (formatId is not (FormatR8 or FormatDxt1 or FormatDxt5 or FormatEtc2))
        {
            if (formatId is 0x1234_5671 or 0x1234_5672)
                throw new AeroflyFormatException(
                    "this .ttc is a shipped scenery map tile (format marker 0x1234567x). " +
                    "That tile variant is undocumented and is not supported by the reference " +
                    "converter either - only converter-made scenery TTCs (DXT1/DXT5/ETC2/R8) are.");
            throw new AeroflyFormatException($"unknown TTC format id {formatId}");
        }

        ReadOnlySpan<byte> body = data[HeaderSize..];
        byte[] pixels = InflatePayload(body, uncompressed, out string method);
        if (uncompressed > 0 && pixels.Length != uncompressed)
            throw new AeroflyFormatException(
                $"inflated TTC size {pixels.Length} != header {uncompressed}");

        return new TtcInfo
        {
            Extra = extra,
            PayloadLength = payload,
            Uncompressed = uncompressed,
            Width = width,
            Height = height,
            Mips = mips <= 0 ? 1 : mips,
            FormatId = formatId,
            Format = FormatName(formatId),
            PayloadMethod = method,
            Pixels = pixels,
        };
    }

    private static byte[] InflatePayload(ReadOnlySpan<byte> body, int uncompressed, out string method)
    {
        if (body.Length == 0) throw new AeroflyFormatException("empty TTC payload");

        if (TmCompress.IsTmBlob(body))
        {
            method = "lzham";
            return TmCompress.Inflate(body);
        }
        if (body[0] == 0x78)
        {
            method = "zlib";
            using var input = new MemoryStream(body.ToArray(), writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(Math.Max(uncompressed, 1024));
            zlib.CopyTo(output);
            return output.ToArray();
        }
        if (body.Length == uncompressed)
        {
            method = "raw";
            return body.ToArray();
        }
        throw new AeroflyFormatException("unrecognised TTC payload (not zlib, LZHAM or raw GPU data)");
    }

    /// <summary>Decodes a native TTC or a TTX twin into RGBA (top mip).</summary>
    public static DecodedTexture Decode(ReadOnlySpan<byte> data, bool flip = true)
    {
        if (!IsNativeTtc(data))
        {
            if (IsTtxTwin(data)) return TtxFile.Decode(data, flip);
            throw new AeroflyFormatException("not a native TTC or TTX-twin .ttc file");
        }

        TtcInfo info = Parse(data);
        var ttxInfo = new TtxInfo
        {
            Format = ToTtxFormat(info.FormatId),
            Width = info.Width,
            Height = info.Height,
            Mips = info.Mips,
            Pixels = info.Pixels,
        };
        byte[] rgba = TtxFile.DecodeRgba(ttxInfo, out _);
        if (flip) rgba = MipChain.FlipVertical(rgba, info.Width, info.Height);
        return new DecodedTexture
        {
            Width = info.Width,
            Height = info.Height,
            Format = info.Format,
            Mips = info.Mips,
            Compressed = info.PayloadMethod == "lzham",
            Rgba = rgba,
        };
    }

    // ------------------------------------------------------------------ encode

    public sealed class EncodeResult
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required string Format { get; init; }
        public required int Mips { get; init; }
        public required int Extra { get; init; }
        public required string PayloadMethod { get; init; }
        public required byte[] Data { get; init; }
    }

    public static (int BlockW, int BlockH, int Bytes) BlockLayout(string format) => format switch
    {
        "r8" => (1, 1, 1),
        "dxt1" => (4, 4, 8),
        "dxt5" => (4, 4, 16),
        "etc2" => (4, 4, 8),
        _ => throw new AeroflyFormatException($"unsupported TTC format: {format}"),
    };

    public static string NormalizeFormat(string format)
    {
        string f = format.ToLowerInvariant()
            .Replace("type_rgb_s3tc_dxt1", "dxt1")
            .Replace("type_rgba_s3tc_dxt5", "dxt5");
        return f switch
        {
            "dxt" or "bc1" => "dxt1",
            "bc3" => "dxt5",
            "etc" => "etc2",
            "mask" or "r" or "type_r" => "r8",
            _ => f,
        };
    }

    /// <summary>Encodes RGBA8 into a native TTC (zlib payload, full mip chain by default).</summary>
    public static EncodeResult Encode(byte[] rgba, int width, int height, string format,
        bool flip = true, bool mipmaps = true, int? extra = null, string? sourcePath = null)
    {
        string fmt = NormalizeFormat(format);
        if (flip) rgba = MipChain.FlipVertical(rgba, width, height);
        if (fmt != "r8") rgba = MipChain.PadToMultiple(rgba, width, height, 4, out width, out height);

        (int bw, int bh, _) = BlockLayout(fmt);
        bool allBlack = true;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i] != 0 || rgba[i + 1] != 0 || rgba[i + 2] != 0) { allBlack = false; break; }
        }
        uint preview = allBlack ? 0xFFFFFFFFu : 0u;

        int mips = mipmaps ? MipChain.Count(width, height) : 1;
        var parts = new List<byte[]>(mips);
        byte[] cur = rgba;
        int cw = width, ch = height;
        for (int i = 0; i < mips; i++)
        {
            parts.Add(EncodeLevel(cur, cw, ch, fmt));
            if (i + 1 < mips)
            {
                cur = MipChain.Halve(cur, cw, ch, out int nw, out int nh);
                cw = nw;
                ch = nh;
            }
        }

        int total = 0;
        foreach (var p in parts) total += p.Length;
        var pixels = new byte[total];
        int offset = 0;
        foreach (var p in parts)
        {
            p.CopyTo(pixels, offset);
            offset += p.Length;
        }

        int expected = GpuMipBytes(fmt, width, height, mips);
        if (pixels.Length != expected)
            throw new AeroflyFormatException(
                $"GPU payload {pixels.Length} != expected {expected} for {width}x{height} mips={mips}");

        int extraValue = extra ?? ZoomFromName(sourcePath);
        byte[] data = Build(zipPayload: true, pixels, width, height, mips, fmt, extraValue, preview);
        return new EncodeResult
        {
            Width = width,
            Height = height,
            Format = fmt,
            Mips = mips,
            Extra = extraValue,
            PayloadMethod = "zlib",
            Data = data,
        };
    }

    public static int GpuMipBytes(string fmt, int width, int height, int mips)
    {
        (int bw, int bh, int bytes) = BlockLayout(fmt);
        int total = 0, cw = width, ch = height;
        for (int i = 0; i < mips; i++)
        {
            total += ((cw + bw - 1) / bw) * ((ch + bh - 1) / bh) * bytes;
            cw = Math.Max(1, cw / 2);
            ch = Math.Max(1, ch / 2);
        }
        return total;
    }

    private static byte[] EncodeLevel(byte[] rgba, int w, int h, string fmt)
    {
        if (fmt == "r8") return EncodeR8Level(rgba, w, h);

        // Block formats need whole 4x4 blocks; small mip levels are padded by edge clamping.
        byte[] padded = MipChain.PadToMultiple(rgba, w, h, 4, out int pw, out int ph);
        int blocks = pw / 4 * (ph / 4);
        switch (fmt)
        {
            case "dxt1":
            {
                var dest = new byte[blocks * 8];
                BcCodec.EncodeBc1(padded, pw, ph, dest);
                return dest;
            }
            case "dxt5":
            {
                var dest = new byte[blocks * 16];
                BcCodec.EncodeBc3(padded, pw, ph, dest);
                return dest;
            }
            case "etc2":
            {
                var dest = new byte[blocks * 8];
                EtcCodec.EncodeEtc2Rgb(padded, pw, ph, dest);
                return dest;
            }
            default:
                throw new AeroflyFormatException($"unsupported TTC format: {fmt}");
        }
    }

    /// <summary>R8 mask: alpha when it varies, otherwise luminance (matches the reference).</summary>
    private static byte[] EncodeR8Level(byte[] rgba, int w, int h)
    {
        bool hasAlpha = false;
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] < 255) { hasAlpha = true; break; }

        var dest = new byte[w * h];
        for (int i = 0; i < dest.Length; i++)
        {
            int o = i * 4;
            dest[i] = hasAlpha
                ? rgba[o + 3]
                : (byte)((rgba[o] * 299 + rgba[o + 1] * 587 + rgba[o + 2] * 114 + 500) / 1000);
        }
        return dest;
    }

    public static byte[] Build(bool zipPayload, byte[] pixels, int width, int height, int mips,
        string format, int extra, uint preview)
    {
        byte[] body;
        if (zipPayload)
        {
            using var output = new MemoryStream(pixels.Length / 2 + 256);
            using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(pixels, 0, pixels.Length);
            body = output.ToArray();
        }
        else
        {
            body = pixels;
        }

        var data = new byte[HeaderSize + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), (uint)extra);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), (uint)pixels.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20, 4), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24, 4), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28, 4), (uint)mips);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32, 4), (uint)FormatId(format));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36, 4), preview);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40, 4), preview);
        body.CopyTo(data.AsSpan(HeaderSize));
        return data;
    }

    /// <summary>Scenery tile zoom level from a <c>map_&lt;zoom&gt;_x_y.ttc</c> style name.</summary>
    public static int ZoomFromName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        var match = System.Text.RegularExpressions.Regex.Match(
            path.Replace('/', '\\'), @"(?:^|\\)map_(\d+)_", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }
}
