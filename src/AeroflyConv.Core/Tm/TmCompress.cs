using System.Buffers.Binary;
using AeroflyConv.Core.Lzham;

namespace AeroflyConv.Core.Tm;

/// <summary>
/// Reads and writes the <c>tmcompress</c> wrapper (64 byte header + LZHAM stream) used by
/// <c>compress_file=true</c> containers.
/// </summary>
public static class TmCompress
{
    public static bool IsTmBlob(ReadOnlySpan<byte> blob) =>
        blob.Length >= TmNames.TmCompressHeaderSize &&
        BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4, 4)) == TmNames.TmCompressMagic;

    /// <summary>Builds the 64 byte header that precedes a raw LZHAM stream.</summary>
    public static byte[] BuildHeader(long uncompressed, long compressed, int dictLog2 = TmNames.LzhamDictLog2,
        int tableRate = TmNames.LzhamTableRate)
    {
        var header = new byte[TmNames.TmCompressHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), (uint)TmNames.TmCompressHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), TmNames.TmCompressMagic);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8, 8), (ulong)uncompressed);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16, 8), (ulong)compressed);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(40, 8), TmNames.TmCompressConst);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(48, 4), (uint)dictLog2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(52, 4), (uint)tableRate);
        return header;
    }

    /// <summary>Compresses a payload into a complete tmcompress blob (header + LZHAM stream).</summary>
    public static byte[] Compress(ReadOnlySpan<byte> payload)
    {
        byte[] stream = LzhamCodec.CompressRaw(payload, TmNames.LzhamDictLog2, TmNames.LzhamTableRate);
        byte[] header = BuildHeader(payload.Length, TmNames.TmCompressHeaderSize + stream.Length);
        var blob = new byte[header.Length + stream.Length];
        header.CopyTo(blob, 0);
        stream.CopyTo(blob, header.Length);
        return blob;
    }

    /// <summary>Inflates a tmcompress blob (64 byte header + LZHAM stream).</summary>
    public static byte[] Inflate(ReadOnlySpan<byte> blob)
    {
        if (!IsTmBlob(blob))
            throw new AeroflyFormatException("not a tmcompress blob (bad magic)");
        long expected = (long)BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(8, 8));
        ReadOnlySpan<byte> stream = blob[TmNames.TmCompressHeaderSize..];
        return LzhamCodec.Decompress(stream, expected is > 0 and < int.MaxValue ? (int)expected : -1);
    }

    /// <summary>
    /// Returns the uncompressed container bytes for a possibly LZHAM-compressed file.
    /// Plain files are returned unchanged (copied).
    /// </summary>
    public static byte[] InflateFile(ReadOnlySpan<byte> raw)
    {
        if (!TmFormats.IsCompressedFile(raw))
            return raw.ToArray();

        if (!TryFindCompressBlob(raw, out int offset, out int length))
            throw new AeroflyFormatException(
                "compressed container does not contain an LZHAM data blob");

        return Inflate(raw.Slice(offset, length));
    }

    /// <summary>Locates the LZHAM blob inside a compressed container (chunk named comp_blob).</summary>
    private static bool TryFindCompressBlob(ReadOnlySpan<byte> data, out int offset, out int length)
    {
        offset = 0;
        length = 0;
        int pos = TmFormats.SkipContainers(data);
        int bestOffset = -1, bestLength = 0;
        while (pos + TmFormats.ChunkHeaderSize <= data.Length)
        {
            ulong name = TmFormats.U64(data, pos + 8);
            long size = (long)TmFormats.U64(data, pos + 16);
            long hs = (long)TmFormats.U64(data, pos + 24);
            if (size < TmFormats.ChunkHeaderSize || pos + size > data.Length) break;

            int payloadLength = (int)size - TmFormats.ChunkHeaderSize;
            if (name == TmNames.NameCompBlob &&
                payloadLength >= TmNames.TmCompressHeaderSize)
            {
                offset = pos + TmFormats.ChunkHeaderSize;
                length = payloadLength;
                return true;
            }
            if (payloadLength > bestLength)
            {
                bestOffset = pos + TmFormats.ChunkHeaderSize;
                bestLength = payloadLength;
            }

            int step = TmFormats.ChunkStep(size, hs);
            if (step < TmFormats.ChunkHeaderSize) break;
            pos += step;
        }

        if (bestOffset >= 0 && IsTmBlob(data.Slice(bestOffset, bestLength)))
        {
            offset = bestOffset;
            length = bestLength;
            return true;
        }
        return false;
    }
}
