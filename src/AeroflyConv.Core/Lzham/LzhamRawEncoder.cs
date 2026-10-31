using System.Buffers.Binary;

namespace AeroflyConv.Core.Lzham;

/// <summary>Simple MSB-first bit writer mirroring <c>symbol_codec</c>'s put_bits/assemble_output_buf.</summary>
internal sealed class LzhamBitWriter
{
    private byte[] _buf;
    private int _len;
    private ulong _bitBuf;
    private int _bitCount = 64; // number of free bits in _bitBuf, counted like the C++ codec

    public LzhamBitWriter(int capacity = 4096)
    {
        _buf = new byte[Math.Max(64, capacity)];
    }

    private void Ensure(int extra)
    {
        if (_len + extra <= _buf.Length)
            return;

        int newSize = _buf.Length * 2;
        while (newSize < _len + extra)
            newSize *= 2;
        Array.Resize(ref _buf, newSize);
    }

    public void WriteByteRaw(byte b)
    {
        Ensure(1);
        _buf[_len++] = b;
    }

    public void PutBits(uint bits, int numBits)
    {
        if (numBits <= 0)
            return;

        if (numBits > 32)
            throw new ArgumentOutOfRangeException(nameof(numBits));

        ulong v = bits;
        if (numBits < 32)
            v &= (1UL << numBits) - 1;

        _bitCount -= numBits;
        _bitBuf |= v << _bitCount;

        while (_bitCount <= 56)
        {
            Ensure(1);
            _buf[_len++] = (byte)(_bitBuf >> 56);
            _bitBuf <<= 8;
            _bitCount += 8;
        }
    }

    public void AlignToByte()
    {
        int bitsIn = 64 - _bitCount;
        if ((bitsIn & 7) != 0)
            PutBits(0, 8 - (bitsIn & 7));
    }

    public void FlushBits()
    {
        PutBits(0, 7);
    }

    /// <summary>Appends bytes verbatim. Must only be called when the writer is byte aligned.</summary>
    public void WriteBytesRaw(ReadOnlySpan<byte> data)
    {
        // Flush any complete bytes still held in the bit buffer.
        while (_bitCount <= 56)
        {
            Ensure(1);
            _buf[_len++] = (byte)(_bitBuf >> 56);
            _bitBuf <<= 8;
            _bitCount += 8;
        }

        Ensure(data.Length);
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;
    }

    public byte[] ToArray()
    {
        var result = new byte[_len];
        Buffer.BlockCopy(_buf, 0, result, 0, _len);
        return result;
    }
}

internal static class Adler32
{
    private const uint Base = 65521;
    private const int NMax = 5552;

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint a = 1;
        uint b = 0;
        int i = 0;
        int len = data.Length;

        while (i < len)
        {
            int n = Math.Min(len - i, NMax);
            int end = i + n;

            // Unrolled by 8.
            while (i + 8 <= end)
            {
                a += data[i]; b += a;
                a += data[i + 1]; b += a;
                a += data[i + 2]; b += a;
                a += data[i + 3]; b += a;
                a += data[i + 4]; b += a;
                a += data[i + 5]; b += a;
                a += data[i + 6]; b += a;
                a += data[i + 7]; b += a;
                i += 8;
            }
            while (i < end)
            {
                a += data[i++];
                b += a;
            }

            a %= Base;
            b %= Base;
        }

        return (b << 16) | a;
    }
}

/// <summary>
/// Writes a valid LZHAM stream using only "raw" blocks (plus the terminating EOF block). No LZ match
/// finder is used; the bytes are stored verbatim, but the block headers and terminating adler32 are
/// arithmetic/bit coded exactly like the reference compressor.
/// </summary>
internal static class LzhamRawEncoder
{
    // Keep individual raw blocks comfortably below the 24-bit (16 MiB) block length field.
    private const int RawBlockSize = 1 << 22;

    public static byte[] Compress(ReadOnlySpan<byte> data, int dictLog2, int tableUpdateRate)
    {
        if (dictLog2 < LzhamTables.MinDictSizeLog2 || dictLog2 > LzhamTables.MaxDictSizeLog2)
            throw new ArgumentOutOfRangeException(nameof(dictLog2), "LZHAM dictionary log2 must be in [15,29]");
        if (tableUpdateRate < 0 || tableUpdateRate > 20)
            throw new ArgumentOutOfRangeException(nameof(tableUpdateRate), "LZHAM table update rate must be in [0,20]");

        // zlib-style header: CM = 14 (LZHAM), CINFO = dict_size_log2 - 15, FLEVEL = 3 (uber), FDICT = 0.
        int cmf = 14 | ((dictLog2 - 15) << 4);
        int flg = 3 << 6;
        int check = ((cmf << 8) + flg) % 31;
        if (check != 0)
            flg += 31 - check;

        var writer = new LzhamBitWriter(Math.Max(64, data.Length + (data.Length / 64) + 64));
        writer.WriteByteRaw((byte)cmf);
        writer.WriteByteRaw((byte)flg);

        int pos = 0;
        while (pos < data.Length)
        {
            int chunkLen = Math.Min(RawBlockSize, data.Length - pos);
            uint lenMinusOne = (uint)(chunkLen - 1);

            writer.PutBits(LzhamTables.RawBlock, LzhamTables.BlockHeaderBits);
            writer.PutBits(lenMinusOne, 24);
            writer.PutBits(((lenMinusOne & 0xFF) ^ ((lenMinusOne >> 8) & 0xFF) ^ ((lenMinusOne >> 16) & 0xFF)) & 0xFF, 8);

            writer.AlignToByte();
            writer.WriteBytesRaw(data.Slice(pos, chunkLen));

            pos += chunkLen;
        }

        // EOF block + adler32 of the uncompressed data.
        writer.PutBits(LzhamTables.EofBlock, LzhamTables.BlockHeaderBits);
        writer.AlignToByte();
        writer.PutBits(Adler32.Compute(data), 32);
        writer.FlushBits();

        return writer.ToArray();
    }
}
