using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using AeroflyConv.Core.Tm;

namespace AeroflyConv.Core.Media;

public enum PngQuality
{
    /// <summary>zlib level 1 - fastest, a few percent larger (default).</summary>
    Fast,
    /// <summary>zlib level 6 - small files, ~2x slower.</summary>
    Balanced,
    /// <summary>zlib level 9 with full filter search.</summary>
    Best,
}

/// <summary>
/// Fast 8-bit PNG writer (RGB / RGBA, non interlaced) with per-row adaptive filtering.
/// ImageSharp is ~4-5x slower here because of its generic pipeline; textures are large
/// and this is the main cost of TTX -> PNG.
/// </summary>
public static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static void Write(string path, ReadOnlySpan<byte> rgba, int width, int height,
        bool withAlpha, PngQuality quality = PngQuality.Fast)
    {
        using FileStream file = File.Create(path, 1 << 16);
        Write(file, rgba, width, height, withAlpha, quality);
    }

    public static void Write(Stream output, ReadOnlySpan<byte> rgba, int width, int height,
        bool withAlpha, PngQuality quality = PngQuality.Fast)
    {
        int bpp = withAlpha ? 4 : 3;
        int stride = width * bpp;

        output.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), height);
        ihdr[8] = 8;                          // bit depth
        ihdr[9] = (byte)(withAlpha ? 6 : 2);  // colour type: RGBA / RGB
        ihdr[10] = 0;                         // deflate
        ihdr[11] = 0;                         // adaptive filtering
        ihdr[12] = 0;                         // no interlace
        WriteChunk(output, "IHDR"u8, ihdr);

        using var compressed = new MemoryStream(1 << 20);
        var level = quality switch
        {
            PngQuality.Balanced => CompressionLevel.Optimal,
            PngQuality.Best => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Fastest,
        };

        using (var zlib = new ZLibStream(compressed, level, leaveOpen: true))
        {
            FilterRows(zlib, rgba, width, height, bpp, stride, quality == PngQuality.Best);
        }

        ReadOnlySpan<byte> idat = compressed.GetBuffer().AsSpan(0, (int)compressed.Length);
        WriteChunk(output, "IDAT"u8, idat);
        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    private static void FilterRows(Stream sink, ReadOnlySpan<byte> rgba, int width, int height,
        int bpp, int stride, bool exhaustive)
    {
        var current = new byte[stride];
        var previous = new byte[stride];
        var candidates = new byte[4][];
        for (int i = 0; i < 4; i++) candidates[i] = new byte[stride];

        Span<byte> header = stackalloc byte[1];
        for (int y = 0; y < height; y++)
        {
            int rowBase = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int s = rowBase + x * 4;
                int d = x * bpp;
                current[d] = rgba[s];
                current[d + 1] = rgba[s + 1];
                current[d + 2] = rgba[s + 2];
                if (bpp == 4) current[d + 3] = rgba[s + 3];
            }

            int chosen = SelectFilter(current, previous, bpp, stride, exhaustive, candidates);
            // candidate index 3 is Paeth, whose PNG filter type is 4.
            header[0] = (byte)(chosen == 3 ? 4 : chosen);
            sink.Write(header);
            sink.Write(candidates[chosen], 0, stride);

            (previous, current) = (current, previous);
        }
    }

    /// <summary>Minimum-sum-of-absolute-differences filter heuristic (cheap, libpng-like).</summary>
    private static int SelectFilter(byte[] cur, byte[] prev, int bpp, int stride, bool exhaustive,
        byte[][] candidates)
    {
        // 0 = None, 1 = Sub, 2 = Up, 4 = Paeth
        Span<long> scores = stackalloc long[4];
        scores.Clear();
        byte[] none = candidates[0], sub = candidates[1], up = candidates[2], paeth = candidates[3];

        for (int i = 0; i < stride; i++)
        {
            int a = i >= bpp ? cur[i - bpp] : 0;
            int b = prev[i];
            int c = i >= bpp ? prev[i - bpp] : 0;
            byte v = cur[i];

            byte fNone = v;
            byte fSub = (byte)(v - a);
            byte fUp = (byte)(v - b);
            int p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            int pr = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
            byte fPaeth = (byte)(v - pr);

            none[i] = fNone;
            sub[i] = fSub;
            up[i] = fUp;
            paeth[i] = fPaeth;

            scores[0] += SignedAbs(fNone);
            scores[1] += SignedAbs(fSub);
            scores[2] += SignedAbs(fUp);
            scores[3] += SignedAbs(fPaeth);
        }

        int best = 0;
        for (int i = 1; i < 4; i++)
            if (scores[i] < scores[best]) best = i;
        return best;
    }

    private static int SignedAbs(byte value) => value < 128 ? value : 256 - value;

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(head[..4], data.Length);
        type.CopyTo(head[4..8]);
        output.Write(head);

        if (data.Length > 0)
        {
            output.Write(data);
            uint crc = TmFormats.Crc32(type);
            // continue the CRC over the payload
            crc = Crc32Continue(crc, data);
            Span<byte> crcBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
            output.Write(crcBytes);
        }
        else
        {
            Span<byte> crcBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBytes, TmFormats.Crc32(type));
            output.Write(crcBytes);
        }
    }

    private static uint[]? _crcTable;

    private static uint[] CrcTable => _crcTable ??= BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>Continues a standard CRC-32 (as returned by <see cref="TmFormats.Crc32"/>) over more data.</summary>
    private static uint Crc32Continue(uint previous, ReadOnlySpan<byte> data)
    {
        uint crc = previous ^ 0xFFFFFFFFu;
        uint[] table = CrcTable;
        foreach (byte b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
