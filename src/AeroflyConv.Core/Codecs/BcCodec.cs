using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AeroflyConv.Core.Codecs;

/// <summary>
/// BC1/BC2/BC3/BC4/BC5 block decoders and BC1/BC3 encoders.
///
/// Decode semantics are byte-exact with the decoders used by the reference converter:
///  * BC1/BC3/BC4/BC5: Perfare/Texture2DDecoder (the "texture2ddecoder" python module).
///  * BC2: the pure-python _decode_dxt3() in the reference ttx_converter.py (DXT3 always uses
///    the 4-colour palette and the 255/31, 255/63 channel expansion).
///
/// The decoders write packed little-endian RGBA pixels (byte order R,G,B,A), which is what the
/// rest of the application expects.  Output spans are tightly packed rows.
///
/// texture2ddecoder returns BGRA.  For BC4 the value is placed in its R byte (bgra[2]) and for
/// BC5 the X block goes to R (bgra[2]) and the Y block to G (bgra[1]); the unused bytes are zero
/// and alpha is 255.  Our BC4/BC5 output therefore matches bgra[2::4] (BC4) and the interleaved
/// (bgra[2], bgra[1]) pair (BC5), which is the X,Y order required by the API.
/// </summary>
public static unsafe class BcCodec
{
    // 5->8 and 6->8 bit expansion used by BC1: bit replication (matches rgb565_le()).
    private static readonly byte[] Exp5 = BuildExpansion(5);
    private static readonly byte[] Exp6 = BuildExpansion(6);
    // Expansion used by the python DXT3 decoder: v * 255 / 31 (63).
    private static readonly byte[] Mul5 = BuildMul(31);
    private static readonly byte[] Mul6 = BuildMul(63);

    private static byte[] BuildExpansion(int bits)
    {
        int n = 1 << bits;
        var t = new byte[n];
        int shift = 8 - bits;
        for (int i = 0; i < n; i++)
        {
            t[i] = (byte)((i << shift) | (i >> (bits - shift)));
        }
        return t;
    }

    private static byte[] BuildMul(int max)
    {
        var t = new byte[max + 1];
        for (int i = 0; i <= max; i++)
        {
            t[i] = (byte)(i * 255 / max);
        }
        return t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Pack(byte r, byte g, byte b)
        => (uint)r | ((uint)g << 8) | ((uint)b << 16) | 0xFF000000u;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Pack565(int q)
        => Pack(Exp5[(q >> 11) & 31], Exp6[(q >> 5) & 63], Exp5[q & 31]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt(ref byte dst, int byteOffset, uint value)
        => Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, byteOffset), value);

    // ---------------------------------------------------------------------------------------
    // BC1
    // ---------------------------------------------------------------------------------------

    public static void DecodeBc1(ReadOnlySpan<byte> data, int width, int height, Span<byte> rgba)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        int blocksW = (width + 3) >> 2;
        int blocksH = (height + 3) >> 2;
        if (data.Length < blocksW * blocksH * 8)
        {
            throw new ArgumentException("BC1 data is shorter than the block count requires.", nameof(data));
        }
        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("RGBA destination is too small.", nameof(rgba));
        }

        unsafe
        {
            fixed (byte* srcFixed = data)
            fixed (byte* dstFixed = rgba)
            {
                byte* src = srcFixed;
                uint* pal = stackalloc uint[4];
                int rowStride = width * 4;

                for (int by = 0; by < blocksH; by++)
                {
                    int y0 = by * 4;
                    int rows = height - y0 < 4 ? height - y0 : 4;
                    for (int bx = 0; bx < blocksW; bx++)
                    {
                        int x0 = bx * 4;
                        int cols = width - x0 < 4 ? width - x0 : 4;
                        uint bits = (uint)(src[4] | (src[5] << 8) | (src[6] << 16) | (src[7] << 24));
                        BuildBc1Palette(src, pal, useFourColorsOnly: false, divScale: false);
                        src += 8;

                        for (int row = 0; row < rows; row++)
                        {
                            ref byte dst = ref dstFixed[(y0 + row) * rowStride + x0 * 4];
                            uint b = bits >> (row * 8);
                            for (int x = 0; x < cols; x++)
                            {
                                WriteUInt(ref dst, x * 4, pal[b & 3]);
                                b >>= 2;
                            }
                        }
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // BC2 (DXT3)
    // ---------------------------------------------------------------------------------------

    public static void DecodeBc2(ReadOnlySpan<byte> data, int width, int height, Span<byte> rgba)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        int blocksW = (width + 3) >> 2;
        int blocksH = (height + 3) >> 2;
        if (data.Length < blocksW * blocksH * 16)
        {
            throw new ArgumentException("BC2 data is shorter than the block count requires.", nameof(data));
        }
        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("RGBA destination is too small.", nameof(rgba));
        }

        unsafe
        {
            fixed (byte* srcFixed = data)
            fixed (byte* dstFixed = rgba)
            {
                byte* src = srcFixed;
                uint* pal = stackalloc uint[4];
                int rowStride = width * 4;

                for (int by = 0; by < blocksH; by++)
                {
                    int y0 = by * 4;
                    int rows = height - y0 < 4 ? height - y0 : 4;
                    for (int bx = 0; bx < blocksW; bx++)
                    {
                        int x0 = bx * 4;
                        int cols = width - x0 < 4 ? width - x0 : 4;

                        // The python reference assigns palette index 0..3 with the 4-colour formula
                        // unconditionally, and expands 565 with v*255/31 (v*255/63 for green).
                        int q0 = src[8] | (src[9] << 8);
                        int q1 = src[10] | (src[11] << 8);
                        uint c0 = Pack(Mul5[(q0 >> 11) & 31], Mul6[(q0 >> 5) & 63], Mul5[q0 & 31]);
                        uint c1 = Pack(Mul5[(q1 >> 11) & 31], Mul6[(q1 >> 5) & 63], Mul5[q1 & 31]);
                        uint cr0 = c0 & 0xFF, cg0 = (c0 >> 8) & 0xFF, cb0 = (c0 >> 16) & 0xFF;
                        uint cr1 = c1 & 0xFF, cg1 = (c1 >> 8) & 0xFF, cb1 = (c1 >> 16) & 0xFF;
                        pal[0] = c0;
                        pal[1] = c1;
                        pal[2] = Pack((byte)((2 * cr0 + cr1) / 3), (byte)((2 * cg0 + cg1) / 3), (byte)((2 * cb0 + cb1) / 3));
                        pal[3] = Pack((byte)((cr0 + 2 * cr1) / 3), (byte)((cg0 + 2 * cg1) / 3), (byte)((cb0 + 2 * cb1) / 3));

                        uint bits = (uint)(src[12] | (src[13] << 8) | (src[14] << 16) | (src[15] << 24));

                        for (int row = 0; row < rows; row++)
                        {
                            ref byte dst = ref dstFixed[(y0 + row) * rowStride + x0 * 4];
                            uint b = bits >> (row * 8);
                            for (int x = 0; x < cols; x++)
                            {
                                int pixel = row * 4 + x;
                                byte packed = src[pixel >> 1];
                                int nib = (pixel & 1) == 0 ? (packed & 0xF) : (packed >> 4);
                                WriteUInt(ref dst, x * 4, (pal[b & 3] & 0x00FFFFFFu) | ((uint)(nib * 17) << 24));
                                b >>= 2;
                            }
                        }

                        src += 16;
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // BC3 (DXT5)
    // ---------------------------------------------------------------------------------------

    public static void DecodeBc3(ReadOnlySpan<byte> data, int width, int height, Span<byte> rgba)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        int blocksW = (width + 3) >> 2;
        int blocksH = (height + 3) >> 2;
        if (data.Length < blocksW * blocksH * 16)
        {
            throw new ArgumentException("BC3 data is shorter than the block count requires.", nameof(data));
        }
        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("RGBA destination is too small.", nameof(rgba));
        }

        unsafe
        {
            fixed (byte* srcFixed = data)
            fixed (byte* dstFixed = rgba)
            {
                byte* src = srcFixed;
                uint* pal = stackalloc uint[4];
                byte* apal = stackalloc byte[8];
                int rowStride = width * 4;

                for (int by = 0; by < blocksH; by++)
                {
                    int y0 = by * 4;
                    int rows = height - y0 < 4 ? height - y0 : 4;
                    for (int bx = 0; bx < blocksW; bx++)
                    {
                        int x0 = bx * 4;
                        int cols = width - x0 < 4 ? width - x0 : 4;

                        BuildAlpha8Palette(src, apal);
                        ulong abits = ReadUInt64(src + 2);
                        uint cbits = (uint)(src[12] | (src[13] << 8) | (src[14] << 16) | (src[15] << 24));
                        BuildBc1Palette(src + 8, pal, useFourColorsOnly: false, divScale: false);
                        src += 16;

                        ulong a = abits;
                        for (int row = 0; row < rows; row++)
                        {
                            ref byte dst = ref dstFixed[(y0 + row) * rowStride + x0 * 4];
                            uint b = cbits >> (row * 8);
                            for (int x = 0; x < cols; x++)
                            {
                                byte alpha = apal[(int)(a & 7)];
                                a >>= 3;
                                WriteUInt(ref dst, x * 4, (pal[b & 3] & 0x00FFFFFFu) | ((uint)alpha << 24));
                                b >>= 2;
                            }
                        }
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // BC4 / BC5
    // ---------------------------------------------------------------------------------------

    public static void DecodeBc4(ReadOnlySpan<byte> data, int width, int height, Span<byte> r8)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        int blocksW = (width + 3) >> 2;
        int blocksH = (height + 3) >> 2;
        if (data.Length < blocksW * blocksH * 8)
        {
            throw new ArgumentException("BC4 data is shorter than the block count requires.", nameof(data));
        }
        if (r8.Length < width * height)
        {
            throw new ArgumentException("R8 destination is too small.", nameof(r8));
        }

        unsafe
        {
            fixed (byte* srcFixed = data)
            fixed (byte* dstFixed = r8)
            {
                byte* src = srcFixed;
                byte* pal = stackalloc byte[8];

                for (int by = 0; by < blocksH; by++)
                {
                    int y0 = by * 4;
                    int rows = height - y0 < 4 ? height - y0 : 4;
                    for (int bx = 0; bx < blocksW; bx++)
                    {
                        int x0 = bx * 4;
                        int cols = width - x0 < 4 ? width - x0 : 4;

                        BuildAlpha8Palette(src, pal);
                        ulong bits = ReadUInt64(src + 2);
                        src += 8;

                        ulong b = bits;
                        for (int row = 0; row < rows; row++)
                        {
                            ref byte dst = ref dstFixed[(y0 + row) * width + x0];
                            for (int x = 0; x < cols; x++)
                            {
                                Unsafe.Add(ref dst, x) = pal[(int)(b & 7)];
                                b >>= 3;
                            }
                        }
                    }
                }
            }
        }
    }

    public static void DecodeBc5(ReadOnlySpan<byte> data, int width, int height, Span<byte> rg)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        int blocksW = (width + 3) >> 2;
        int blocksH = (height + 3) >> 2;
        if (data.Length < blocksW * blocksH * 16)
        {
            throw new ArgumentException("BC5 data is shorter than the block count requires.", nameof(data));
        }
        if (rg.Length < width * height * 2)
        {
            throw new ArgumentException("RG destination is too small.", nameof(rg));
        }

        unsafe
        {
            fixed (byte* srcFixed = data)
            fixed (byte* dstFixed = rg)
            {
                byte* src = srcFixed;
                byte* palX = stackalloc byte[8];
                byte* palY = stackalloc byte[8];

                for (int by = 0; by < blocksH; by++)
                {
                    int y0 = by * 4;
                    int rows = height - y0 < 4 ? height - y0 : 4;
                    for (int bx = 0; bx < blocksW; bx++)
                    {
                        int x0 = bx * 4;
                        int cols = width - x0 < 4 ? width - x0 : 4;

                        BuildAlpha8Palette(src, palX);
                        BuildAlpha8Palette(src + 8, palY);
                        ulong bx3 = ReadUInt64(src + 2);
                        ulong by3 = ReadUInt64(src + 10);
                        src += 16;

                        for (int row = 0; row < rows; row++)
                        {
                            int rowOff = (y0 + row) * width * 2 + x0 * 2;
                            for (int x = 0; x < cols; x++)
                            {
                                int i = row * 4 + x;
                                dstFixed[rowOff + x * 2] = palX[(int)((bx3 >> (3 * i)) & 7)];
                                dstFixed[rowOff + x * 2 + 1] = palY[(int)((by3 >> (3 * i)) & 7)];
                            }
                        }
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Shared block helpers
    // ---------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadUInt64(byte* p)
        => (ulong)p[0] | ((ulong)p[1] << 8) | ((ulong)p[2] << 16) | ((ulong)p[3] << 24)
         | ((ulong)p[4] << 32) | ((ulong)p[5] << 40) | ((ulong)p[6] << 48) | ((ulong)p[7] << 56);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BuildAlpha8Palette(byte* src, byte* pal)
    {
        byte a0 = src[0];
        byte a1 = src[1];
        pal[0] = a0;
        pal[1] = a1;
        if (a0 > a1)
        {
            pal[2] = (byte)((6 * a0 + a1) / 7);
            pal[3] = (byte)((5 * a0 + 2 * a1) / 7);
            pal[4] = (byte)((4 * a0 + 3 * a1) / 7);
            pal[5] = (byte)((3 * a0 + 4 * a1) / 7);
            pal[6] = (byte)((2 * a0 + 5 * a1) / 7);
            pal[7] = (byte)((a0 + 6 * a1) / 7);
        }
        else
        {
            pal[2] = (byte)((4 * a0 + a1) / 5);
            pal[3] = (byte)((3 * a0 + 2 * a1) / 5);
            pal[4] = (byte)((2 * a0 + 3 * a1) / 5);
            pal[5] = (byte)((a0 + 4 * a1) / 5);
            pal[6] = 0;
            pal[7] = 255;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BuildBc1Palette(byte* src, uint* pal, bool useFourColorsOnly, bool divScale)
    {
        int q0 = src[0] | (src[1] << 8);
        int q1 = src[2] | (src[3] << 8);
        uint c0, c1;
        if (divScale)
        {
            c0 = Pack(Mul5[(q0 >> 11) & 31], Mul6[(q0 >> 5) & 63], Mul5[q0 & 31]);
            c1 = Pack(Mul5[(q1 >> 11) & 31], Mul6[(q1 >> 5) & 63], Mul5[q1 & 31]);
        }
        else
        {
            c0 = Pack565(q0);
            c1 = Pack565(q1);
        }

        pal[0] = c0;
        pal[1] = c1;

        uint r0 = c0 & 0xFF, g0 = (c0 >> 8) & 0xFF, b0 = (c0 >> 16) & 0xFF;
        uint r1 = c1 & 0xFF, g1 = (c1 >> 8) & 0xFF, b1 = (c1 >> 16) & 0xFF;

        if (useFourColorsOnly || q0 > q1)
        {
            pal[2] = Pack((byte)((2 * r0 + r1) / 3), (byte)((2 * g0 + g1) / 3), (byte)((2 * b0 + b1) / 3));
            pal[3] = Pack((byte)((r0 + 2 * r1) / 3), (byte)((g0 + 2 * g1) / 3), (byte)((b0 + 2 * b1) / 3));
        }
        else
        {
            pal[2] = Pack((byte)((r0 + r1) / 2), (byte)((g0 + g1) / 2), (byte)((b0 + b1) / 2));
            pal[3] = 0xFF000000u;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Encoding (implemented in BcEncoder.cs)
    // ---------------------------------------------------------------------------------------

    public static void EncodeBc1(ReadOnlySpan<byte> rgba, int width, int height, Span<byte> dest)
        => BcEncoder.EncodeBc1(rgba, width, height, dest);

    public static void EncodeBc3(ReadOnlySpan<byte> rgba, int width, int height, Span<byte> dest)
        => BcEncoder.EncodeBc3(rgba, width, height, dest);
}
