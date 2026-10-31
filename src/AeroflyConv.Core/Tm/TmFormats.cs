using System.Buffers.Binary;

namespace AeroflyConv.Core.Tm;

/// <summary>
/// Low level handling of Aerofly "TM" chunk containers (TTX textures, TSB sounds,
/// TOC/TSC/WAD/TMB/TSL scenery tables).
///
/// A container is a flat sequence of 32 byte chunk headers:
///   +0  type id  (8 bytes)
///   +8  name id  (8 bytes)
///   +16 size     (u64, total bytes incl. header)
///   +24 hdr size (u64, may be larger than size = padding)
///   +32 payload  (size - 32 bytes, rounded up to hdr size)
/// </summary>
public static class TmFormats
{
    public static ReadOnlySpan<byte> MagicCompressed => [0xB5, 0xFE, 0x24, 0xC7, 0xB7, 0x3C, 0xEB, 0xB7];
    public static ReadOnlySpan<byte> MagicPlain => [0xA6, 0x97, 0xE9, 0xAC, 0xF9, 0xFC, 0xC1, 0x7C];
    public static ReadOnlySpan<byte> MagicSound => [0x5F, 0x34, 0x99, 0x6E, 0xF3, 0x11, 0x96, 0x9D];

    /// <summary>
    /// Second <c>compress_file</c> magic used by the FS4 livery files written into the user folder.
    /// Differs from <see cref="MagicCompressed"/> only in the first two bytes.
    /// </summary>
    public static ReadOnlySpan<byte> MagicCompressedAlt => [0xA5, 0xA4, 0x24, 0xC7, 0xB7, 0x3C, 0xEB, 0xB7];

    /// <summary>
    /// User-folder livery textures are stored XOR-obfuscated in their first 0x40 bytes (the two
    /// wrapper headers) with <see cref="ObfuscationKey"/>. This is the on-disk signature.
    /// </summary>
    public static ReadOnlySpan<byte> MagicObfuscated => [0xFF, 0xFE, 0x7E, 0x9D, 0xED, 0x66, 0xB1, 0xED];

    public const byte ObfuscationKey = 0x5A;
    public const int ObfuscatedHeaderLength = 0x40;

    /// <summary>Undoes the livery obfuscation; returns a copy of <paramref name="raw"/> otherwise.</summary>
    public static byte[] Deobfuscate(ReadOnlySpan<byte> raw)
    {
        if (!HasMagic(raw, MagicObfuscated) || raw.Length < ObfuscatedHeaderLength)
            return raw.ToArray();

        byte[] copy = raw.ToArray();
        for (int i = 0; i < ObfuscatedHeaderLength; i++)
            copy[i] ^= ObfuscationKey;
        return copy;
    }

    public const int ChunkHeaderSize = 32;

    public static ulong U64(ReadOnlySpan<byte> b, int off) =>
        BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(off, 8));

    public static uint U32(ReadOnlySpan<byte> b, int off) =>
        BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off, 4));

    public static bool HasMagic(ReadOnlySpan<byte> data, ReadOnlySpan<byte> magic) =>
        data.Length >= 8 && data[..8].SequenceEqual(magic);

    /// <summary>True for a <c>compress_file=true</c> (LZHAM) flavour, either magic variant.</summary>
    public static bool IsCompressedFile(ReadOnlySpan<byte> data) =>
        HasMagic(data, MagicCompressed) || HasMagic(data, MagicCompressedAlt);

    /// <summary>True for an uncompressed (<c>compress_file=false</c>) container.</summary>
    public static bool IsPlainFile(ReadOnlySpan<byte> data) => HasMagic(data, MagicPlain);

    /// <summary>Step in bytes for a chunk with the given size / header size.</summary>
    public static int ChunkStep(long size, long headerSize)
    {
        // u32 properties are stored with size=36 but a 40 byte header.
        int step = size == 36 && headerSize == 40 ? 40 : (int)size;
        return step;
    }

    /// <summary>
    /// Skips leading wrapper containers (header size 32 that span the rest of the
    /// buffer) and returns the offset of the first property chunk.
    /// </summary>
    public static int SkipContainers(ReadOnlySpan<byte> data)
    {
        int off = 0;
        while (off + ChunkHeaderSize <= data.Length)
        {
            long size = (long)U64(data, off + 16);
            long hs = (long)U64(data, off + 24);
            if (hs == 32 && (size == data.Length - off || size == data.Length))
            {
                off += ChunkHeaderSize;
                continue;
            }
            break;
        }
        return off;
    }

    /// <summary>
    /// Finds the single large data chunk of a container ("the payload"), which is how
    /// the LZHAM blob and PCM buffers are stored. Mirrors the reference walker.
    /// </summary>
    public static bool TryFindPayload(ReadOnlySpan<byte> data, out int offset, out int length)
    {
        offset = 0;
        length = 0;
        if (data.Length < 0x100) return false;
        int pos = 0x40;
        while (pos + ChunkHeaderSize <= data.Length)
        {
            long size = (long)U64(data, pos + 16);
            long hs = (long)U64(data, pos + 24);
            if (size > 200)
            {
                if (pos + size > data.Length || size < 32) return false;
                offset = pos + ChunkHeaderSize;
                length = (int)size - ChunkHeaderSize;
                return true;
            }
            int step = size == 36 && hs == 40 ? 40 : (int)size;
            if (step < ChunkHeaderSize) return false;
            pos += step;
        }
        return false;
    }

    /// <summary>Parses a "string8" property payload (u64 length + ASCII bytes).</summary>
    public static string? TryParseString(ReadOnlySpan<byte> payload, int maxLength = 64)
    {
        if (payload.Length < 8) return null;
        ulong n = U64(payload, 0);
        if (n == 0 || n > (ulong)maxLength) return null;
        if (8 + (int)n > payload.Length) return null;
        ReadOnlySpan<byte> raw = payload.Slice(8, (int)n);
        foreach (byte b in raw)
            if (b < 32 || b >= 127) return null;
        return System.Text.Encoding.ASCII.GetString(raw);
    }

    public static bool IsPowerOfTwo(int v) => v > 0 && (v & (v - 1)) == 0;

    public static string PowerOfTwoWarning(int w, int h) =>
        $"WARNING: {w}x{h} is not power-of-two - Aerofly textures are expected to be " +
        "power-of-two (e.g. 1024x1024, 1024x512)";

    // ---------------------------------------------------------------- CRC-32
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>Standard zlib/IEEE CRC-32 (identical to Python's zlib.crc32).</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}

