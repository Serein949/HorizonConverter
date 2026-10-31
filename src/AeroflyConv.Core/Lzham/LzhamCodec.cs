using System.Buffers.Binary;

namespace AeroflyConv.Core.Lzham;

/// <summary>
/// Pure managed LZHAM codec (as used by Aerofly FS <c>tmcompress</c> payloads). Decoding is a faithful
/// port of the reference C++ decompressor; encoding emits valid raw (stored) blocks.
/// </summary>
public static class LzhamCodec
{
    private const int TmHeaderSize = 64;
    private const long MaxOutputBytes = 1_500_000_000L;

    /// <summary>True if the buffer plausibly starts with an LZHAM stream header (6E CD).</summary>
    public static bool LooksLikeLzham(ReadOnlySpan<byte> data) =>
        data.Length >= 2 && data[0] == 0x6E && data[1] == 0xCD;

    /// <summary>
    /// Decompress a raw LZHAM stream (starting at the 6E CD bytes) into a new array.
    /// <paramref name="expectedSize"/> is a hint (from the TM header).
    /// </summary>
    public static byte[] Decompress(ReadOnlySpan<byte> lzhamStream, int expectedSize = -1) =>
        DecompressCore(lzhamStream, -1, 20, expectedSize);

    /// <summary>
    /// Decompress only the LZHAM part of a 64-byte-tmcompress-header blob
    /// (64 byte header followed by the raw LZHAM stream).
    /// </summary>
    public static byte[] DecompressTmBlob(ReadOnlySpan<byte> tmBlob)
    {
        if (tmBlob.Length < TmHeaderSize)
            throw new InvalidDataException("LZHAM: tmcompress blob is shorter than its 64 byte header");

        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(tmBlob.Slice(0, 4));
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(tmBlob.Slice(4, 4));
        if (headerSize != TmHeaderSize || magic != LzhamTables.TmCompressMagic)
            throw new InvalidDataException("LZHAM: bad tmcompress header");

        ulong uncompressed = BinaryPrimitives.ReadUInt64LittleEndian(tmBlob.Slice(8, 8));
        ulong compressed = BinaryPrimitives.ReadUInt64LittleEndian(tmBlob.Slice(16, 8));
        int dictLog2 = (int)BinaryPrimitives.ReadUInt32LittleEndian(tmBlob.Slice(48, 4));
        int tableRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(tmBlob.Slice(52, 4));

        int streamLength = tmBlob.Length - TmHeaderSize;
        if (compressed >= TmHeaderSize && compressed <= (ulong)tmBlob.Length)
            streamLength = (int)compressed - TmHeaderSize;

        if (dictLog2 < LzhamTables.MinDictSizeLog2 || dictLog2 > LzhamTables.MaxDictSizeLog2)
            throw new InvalidDataException("LZHAM: unsupported dictionary log2 in tmcompress header");

        int expected = -1;
        if (uncompressed > 0 && uncompressed < (ulong)MaxOutputBytes)
            expected = (int)uncompressed;

        return DecompressCore(tmBlob.Slice(TmHeaderSize, streamLength), dictLog2, tableRate, expected);
    }

    /// <summary>
    /// Compress using only LZHAM "raw / uncompressed blocks". The returned buffer is a complete,
    /// valid LZHAM stream (zlib-style 6E CD header, raw blocks, EOF block, adler32).
    /// </summary>
    public static byte[] CompressRaw(ReadOnlySpan<byte> data, int dictLog2 = 21, int tableUpdateRate = 20) =>
        LzhamRawEncoder.Compress(data, dictLog2, tableUpdateRate);

    /// <summary>
    /// Same as <see cref="CompressRaw"/> but wraps the stream in the 64-byte tmcompress header.
    /// </summary>
    public static byte[] CompressTmBlob(ReadOnlySpan<byte> data, int dictLog2 = 21, int tableUpdateRate = 20)
    {
        byte[] stream = LzhamRawEncoder.Compress(data, dictLog2, tableUpdateRate);

        var blob = new byte[TmHeaderSize + stream.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0, 4), TmHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4, 4), LzhamTables.TmCompressMagic);
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(8, 8), (ulong)data.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(16, 8), (ulong)blob.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(40, 8), 0x17F34DF32797945CUL);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(48, 4), (uint)dictLog2);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(52, 4), (uint)tableUpdateRate);

        stream.CopyTo(blob, TmHeaderSize);
        return blob;
    }

    private static unsafe byte[] DecompressCore(ReadOnlySpan<byte> stream, int dictLog2Override, int tableRate, int expectedSize)
    {
        if (stream.Length < 2)
            throw new InvalidDataException("LZHAM: stream too short for its zlib-style header");

        int cmf = stream[0];
        int flg = stream[1];

        if (((cmf << 8) + flg) % 31 != 0 || (cmf & 15) != 14)
            throw new InvalidDataException("LZHAM: bad zlib-style stream header");

        if ((flg & 32) != 0)
            throw new InvalidDataException("LZHAM: preset dictionaries (FDICT) are not supported");

        int dictFromHeader = (cmf >> 4) + 15;
        if (dictFromHeader < LzhamTables.MinDictSizeLog2 || dictFromHeader > LzhamTables.MaxDictSizeLog2)
            throw new InvalidDataException("LZHAM: unsupported dictionary size in stream header");

        // The tmcompress header carries the authoritative dictionary log2 (like LZHAM's decompress params);
        // a bare stream only has the zlib-style CMF nibble.
        int dictLog2 = dictLog2Override >= 0 ? dictLog2Override : dictFromHeader;

        int initialCapacity = expectedSize > 0 ? expectedSize : EstimateCapacity(stream.Length);
        long capacity = Math.Clamp((long)initialCapacity, 16, MaxOutputBytes);

        fixed (byte* pStream = stream)
        {
            for (int attempt = 0; ; attempt++)
            {
                var decoder = new LzhamDecoder(dictLog2, tableRate, 2, stream.Length);
                try
                {
                    return decoder.Decode(pStream, (int)capacity);
                }
                catch (LzhamOutputOverflowException ex)
                {
                    if (attempt >= 30 || ex.Required > MaxOutputBytes || capacity >= MaxOutputBytes)
                        throw new InvalidDataException("LZHAM: decompressed data is larger than the supported maximum");

                    long next = Math.Max(ex.Required, capacity * 2);
                    capacity = Math.Min(next, MaxOutputBytes);
                }
            }
        }
    }

    private static int EstimateCapacity(int compressedLength)
    {
        long estimate = (long)compressedLength * 4 + 1024;
        if (estimate > 64L * 1024 * 1024)
            estimate = 64L * 1024 * 1024;
        return (int)Math.Clamp(estimate, 16, int.MaxValue);
    }
}
