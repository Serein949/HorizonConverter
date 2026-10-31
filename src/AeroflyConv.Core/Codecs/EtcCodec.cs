using System.Runtime.CompilerServices;

namespace AeroflyConv.Core.Codecs;

/// <summary>
/// ETC1 / ETC2 RGB / ETC2 RGBA (EAC alpha) decoders, plus ETC2 encoders.
///
/// Decode semantics are byte-exact with texture2ddecoder's decode_etc1 / decode_etc2 /
/// decode_etc2a8 (Perfare/Texture2DDecoder, etc.cpp).  The ETC2 RGBA block layout is
/// 8 bytes of EAC alpha followed by 8 bytes of ETC2 RGB - this is what both etcpak's
/// compress_etc2_rgba and texture2ddecoder's decode_etc2a8 use (the "8 RGB + 8 alpha"
/// wording in the API sketch would not decode with the reference).
/// </summary>
public static class EtcCodec
{
    // ETC1 / ETC2 modifier table (small, large).
    private static readonly int[] Modifier = [2, 8, 5, 17, 9, 29, 13, 42, 18, 60, 24, 80, 33, 106, 47, 183];
    // Indexed [2][8] flattened: Etc1Modifier[table*2 + which].
    private static readonly int[] Distance = [3, 6, 11, 16, 23, 32, 41, 64];

    private static readonly sbyte[,] AlphaMod =
    {
        { -3, -6, -9, -15, 2, 5, 8, 14 },
        { -3, -7, -10, -13, 2, 6, 9, 12 },
        { -2, -5, -8, -13, 1, 4, 7, 12 },
        { -2, -4, -6, -13, 1, 3, 5, 12 },
        { -3, -6, -8, -12, 2, 5, 7, 11 },
        { -3, -7, -9, -11, 2, 6, 8, 10 },
        { -4, -7, -8, -11, 3, 6, 7, 10 },
        { -3, -5, -8, -11, 2, 4, 7, 10 },
        { -2, -6, -8, -10, 1, 5, 7, 9 },
        { -2, -5, -8, -10, 1, 4, 7, 9 },
        { -2, -4, -8, -10, 1, 3, 7, 9 },
        { -2, -5, -7, -10, 1, 4, 6, 9 },
        { -3, -4, -7, -10, 2, 3, 6, 9 },
        { -1, -2, -3, -10, 0, 1, 2, 9 },
        { -4, -6, -8, -9, 3, 5, 7, 8 },
        { -3, -5, -7, -9, 2, 4, 6, 8 },
    };

    private static readonly byte[] WriteOrder = [0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15];
    private static readonly byte[] WriteOrderRev = [15, 11, 7, 3, 14, 10, 6, 2, 13, 9, 5, 1, 12, 8, 4, 0];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clamp255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Pack(int r, int g, int b)
        => (uint)Clamp255(r) | ((uint)Clamp255(g) << 8) | ((uint)Clamp255(b) << 16) | 0xFF000000u;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt(ref byte dst, int byteOffset, uint value)
        => Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, byteOffset), value);

    // ---------------------------------------------------------------------------------------
    // Public decode API
    // ---------------------------------------------------------------------------------------

    public static void DecodeEtc2Rgb(ReadOnlySpan<byte> data, int width, int height, Span<byte> rgba)
        => DecodeRgbCore(data, width, height, rgba, etc2: true);

    public static void DecodeEtc1(ReadOnlySpan<byte> data, int width, int height, Span<byte> rgba)
        => DecodeRgbCore(data, width, height, rgba, etc2: false);

    private static unsafe void DecodeRgbCore(ReadOnlySpan<byte> data, int width, int height, Span<byte> rgba, bool etc2)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        int blocksW = (width + 3) >> 2;
        int blocksH = (height + 3) >> 2;
        if (data.Length < blocksW * blocksH * 8)
        {
            throw new ArgumentException("ETC data is shorter than the block count requires.", nameof(data));
        }
        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("RGBA destination is too small.", nameof(rgba));
        }

        fixed (byte* srcFixed = data)
        fixed (byte* dstFixed = rgba)
        {
            byte* src = srcFixed;
            uint* buf = stackalloc uint[16];

            for (int by = 0; by < blocksH; by++)
            {
                for (int bx = 0; bx < blocksW; bx++)
                {
                    if (etc2)
                    {
                        DecodeEtc2Block(src, buf);
                    }
                    else
                    {
                        DecodeEtc1Block(src, buf);
                    }
                    CopyBlock(buf, dstFixed, bx, by, width, height);
                    src += 8;
                }
            }
        }
    }

    public static unsafe void DecodeEtc2Rgba(ReadOnlySpan<byte> data, int width, int height, Span<byte> rgba)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        int blocksW = (width + 3) >> 2;
        int blocksH = (height + 3) >> 2;
        if (data.Length < blocksW * blocksH * 16)
        {
            throw new ArgumentException("ETC2 RGBA data is shorter than the block count requires.", nameof(data));
        }
        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("RGBA destination is too small.", nameof(rgba));
        }

        fixed (byte* srcFixed = data)
        fixed (byte* dstFixed = rgba)
        {
            byte* src = srcFixed;
            uint* buf = stackalloc uint[16];

            for (int by = 0; by < blocksH; by++)
            {
                for (int bx = 0; bx < blocksW; bx++)
                {
                    DecodeEtc2Block(src + 8, buf);
                    DecodeEtc2AlphaBlock(src, buf);
                    CopyBlock(buf, dstFixed, bx, by, width, height);
                    src += 16;
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Block decoders
    // ---------------------------------------------------------------------------------------

    private static unsafe void DecodeEtc1Block(byte* s, uint* buf)
    {
        int d3 = s[3];
        int flip = d3 & 1;
        int code0 = d3 >> 5;
        int code1 = (d3 >> 2) & 7;
        int j = (s[6] << 8) | s[7];
        int k = (s[4] << 8) | s[5];

        int r0, g0, b0, r1, g1, b1;
        if ((d3 & 2) != 0)
        {
            int r = s[0] & 0xF8;
            int dr = ((s[0] << 3) & 0x18) - ((s[0] << 3) & 0x20);
            int g = s[1] & 0xF8;
            int dg = ((s[1] << 3) & 0x18) - ((s[1] << 3) & 0x20);
            int b = s[2] & 0xF8;
            int db = ((s[2] << 3) & 0x18) - ((s[2] << 3) & 0x20);

            r0 = r | (r >> 5);
            g0 = g | (g >> 5);
            b0 = b | (b >> 5);
            int vr = (r + dr) & 0xFF;
            int vg = (g + dg) & 0xFF;
            int vb = (b + db) & 0xFF;
            r1 = vr | (vr >> 5);
            g1 = vg | (vg >> 5);
            b1 = vb | (vb >> 5);
        }
        else
        {
            r0 = ((s[0] & 0xF0) | (s[0] >> 4)) & 0xFF;
            g0 = ((s[1] & 0xF0) | (s[1] >> 4)) & 0xFF;
            b0 = ((s[2] & 0xF0) | (s[2] >> 4)) & 0xFF;
            r1 = ((s[0] & 0x0F) | ((s[0] << 4) & 0xF0)) & 0xFF;
            g1 = ((s[1] & 0x0F) | ((s[1] << 4) & 0xF0)) & 0xFF;
            b1 = ((s[2] & 0x0F) | ((s[2] << 4) & 0xF0)) & 0xFF;
        }

        for (int i = 0; i < 16; i++)
        {
            int sb = flip == 0 ? (i < 8 ? 0 : 1) : ((i & 3) < 2 ? 0 : 1);
            int table = sb == 0 ? code0 : code1;
            int m = Modifier[table * 2 + ((j >> i) & 1)];
            if (((k >> i) & 1) != 0)
            {
                m = -m;
            }
            int r = sb == 0 ? r0 : r1;
            int g = sb == 0 ? g0 : g1;
            int b = sb == 0 ? b0 : b1;
            buf[WriteOrder[i]] = Pack(r + m, g + m, b + m);
        }
    }

    private static unsafe void DecodeEtc2Block(byte* s, uint* buf)
    {
        int d3 = s[3];
        int j = (s[6] << 8) | s[7];
        int k = (s[4] << 8) | s[5];

        int r = s[0] & 0xF8;
        int dr = ((s[0] << 3) & 0x18) - ((s[0] << 3) & 0x20);
        int g = s[1] & 0xF8;
        int dg = ((s[1] << 3) & 0x18) - ((s[1] << 3) & 0x20);
        int b = s[2] & 0xF8;
        int db = ((s[2] << 3) & 0x18) - ((s[2] << 3) & 0x20);

        if ((d3 & 2) == 0)
        {
            DecodeIndividual(s, buf, d3, j, k);
            return;
        }

        if (r + dr < 0 || r + dr > 255)
        {
            DecodeT(s, buf, d3, j, k);
        }
        else if (g + dg < 0 || g + dg > 255)
        {
            DecodeH(s, buf, d3, j, k);
        }
        else if (b + db < 0 || b + db > 255)
        {
            DecodePlanar(s, buf);
        }
        else
        {
            DecodeDifferential(s, buf, d3, j, k, r, dr, g, dg, b, db);
        }
    }

    private static unsafe void DecodeIndividual(byte* s, uint* buf, int d3, int j, int k)
    {
        int flip = d3 & 1;
        int code0 = d3 >> 5;
        int code1 = (d3 >> 2) & 7;

        int r0 = ((s[0] & 0xF0) | (s[0] >> 4)) & 0xFF;
        int g0 = ((s[1] & 0xF0) | (s[1] >> 4)) & 0xFF;
        int b0 = ((s[2] & 0xF0) | (s[2] >> 4)) & 0xFF;
        int r1 = ((s[0] & 0x0F) | ((s[0] << 4) & 0xF0)) & 0xFF;
        int g1 = ((s[1] & 0x0F) | ((s[1] << 4) & 0xF0)) & 0xFF;
        int b1 = ((s[2] & 0x0F) | ((s[2] << 4) & 0xF0)) & 0xFF;

        for (int i = 0; i < 16; i++)
        {
            int sb = flip == 0 ? (i < 8 ? 0 : 1) : ((i & 3) < 2 ? 0 : 1);
            int table = sb == 0 ? code0 : code1;
            int m = Modifier[table * 2 + ((j >> i) & 1)];
            if (((k >> i) & 1) != 0)
            {
                m = -m;
            }
            int cr = sb == 0 ? r0 : r1;
            int cg = sb == 0 ? g0 : g1;
            int cb = sb == 0 ? b0 : b1;
            buf[WriteOrder[i]] = Pack(cr + m, cg + m, cb + m);
        }
    }

    private static unsafe void DecodeDifferential(byte* s, uint* buf, int d3, int j, int k,
        int r, int dr, int g, int dg, int b, int db)
    {
        int flip = d3 & 1;
        int code0 = d3 >> 5;
        int code1 = (d3 >> 2) & 7;

        int r0 = r | (r >> 5);
        int g0 = g | (g >> 5);
        int b0 = b | (b >> 5);
        int vr = r + dr;
        int vg = g + dg;
        int vb = b + db;
        int r1 = vr | (vr >> 5);
        int g1 = vg | (vg >> 5);
        int b1 = vb | (vb >> 5);

        for (int i = 0; i < 16; i++)
        {
            int sb = flip == 0 ? (i < 8 ? 0 : 1) : ((i & 3) < 2 ? 0 : 1);
            int table = sb == 0 ? code0 : code1;
            int m = Modifier[table * 2 + ((j >> i) & 1)];
            if (((k >> i) & 1) != 0)
            {
                m = -m;
            }
            int cr = sb == 0 ? r0 : r1;
            int cg = sb == 0 ? g0 : g1;
            int cb = sb == 0 ? b0 : b1;
            buf[WriteOrder[i]] = Pack(cr + m, cg + m, cb + m);
        }
    }

    private static unsafe void DecodeT(byte* s, uint* buf, int d3, int j, int k)
    {
        int c0r = ((s[0] << 3) & 0xC0) | ((s[0] << 4) & 0x30) | ((s[0] >> 1) & 0x0C) | (s[0] & 3);
        int c0g = (s[1] & 0xF0) | (s[1] >> 4);
        int c0b = (s[1] & 0x0F) | ((s[1] << 4) & 0xF0);
        int c1r = (s[2] & 0xF0) | (s[2] >> 4);
        int c1g = (s[2] & 0x0F) | ((s[2] << 4) & 0xF0);
        int c1b = (s[3] & 0xF0) | (s[3] >> 4);
        int d = Distance[((s[3] >> 1) & 6) | (s[3] & 1)];

        uint p0 = Pack(c0r, c0g, c0b);
        uint p1 = Pack(c1r + d, c1g + d, c1b + d);
        uint p2 = Pack(c1r, c1g, c1b);
        uint p3 = Pack(c1r - d, c1g - d, c1b - d);

        for (int i = 0; i < 16; i++)
        {
            int index = (((k >> i) & 1) << 1) | ((j >> i) & 1);
            uint c = index switch { 0 => p0, 1 => p1, 2 => p2, _ => p3 };
            buf[WriteOrder[i]] = c;
        }
    }

    private static unsafe void DecodeH(byte* s, uint* buf, int d3, int j, int k)
    {
        int c0r = ((s[0] << 1) & 0xF0) | ((s[0] >> 3) & 0x0F);
        int c0g = ((s[0] << 5) & 0xE0) | (s[1] & 0x10);
        c0g |= c0g >> 4;
        int c0b = (s[1] & 8) | ((s[1] << 1) & 6) | (s[2] >> 7);
        c0b |= c0b << 4;
        c0b &= 0xFF;
        int c1r = ((s[2] << 1) & 0xF0) | ((s[2] >> 3) & 0x0F);
        int c1g = ((s[2] << 5) & 0xE0) | ((s[3] >> 3) & 0x10);
        c1g |= c1g >> 4;
        int c1b = ((s[3] << 1) & 0xF0) | ((s[3] >> 3) & 0x0F);

        int d = (s[3] & 4) | ((s[3] << 1) & 2);
        if (c0r > c1r ||
            (c0r == c1r && (c0g > c1g || (c0g == c1g && c0b >= c1b))))
        {
            d++;
        }
        d = Distance[d];

        uint p0 = Pack(c0r + d, c0g + d, c0b + d);
        uint p1 = Pack(c0r - d, c0g - d, c0b - d);
        uint p2 = Pack(c1r + d, c1g + d, c1b + d);
        uint p3 = Pack(c1r - d, c1g - d, c1b - d);

        for (int i = 0; i < 16; i++)
        {
            int index = (((k >> i) & 1) << 1) | ((j >> i) & 1);
            uint c = index switch { 0 => p0, 1 => p1, 2 => p2, _ => p3 };
            buf[WriteOrder[i]] = c;
        }
    }

    private static unsafe void DecodePlanar(byte* s, uint* buf)
    {
        int c0r = ((s[0] << 1) & 0xFC) | ((s[0] >> 5) & 3);
        int c0g = ((s[0] << 7) & 0x80) | (s[1] & 0x7E) | (s[0] & 1);
        int c0b = ((s[1] << 7) & 0x80) | ((s[2] << 2) & 0x60) | ((s[2] << 3) & 0x18) | ((s[3] >> 5) & 4);
        c0b = (c0b | (c0b >> 6)) & 0xFF;
        int c1r = ((s[3] << 1) & 0xF8) | ((s[3] << 2) & 4) | ((s[3] >> 5) & 3);
        int c1g = (s[4] & 0xFE) | (s[4] >> 7);
        int c1b = ((s[4] << 7) & 0x80) | ((s[5] >> 1) & 0x7C);
        c1b = (c1b | (c1b >> 6)) & 0xFF;
        int c2r = ((s[5] << 5) & 0xE0) | ((s[6] >> 3) & 0x1C) | ((s[5] >> 1) & 3);
        int c2g = ((s[6] << 3) & 0xF8) | ((s[7] >> 5) & 6) | ((s[6] >> 4) & 1);
        int c2b = ((s[7] << 2) | ((s[7] >> 4) & 3)) & 0xFF;
        c2b = (c2b | (c2b >> 6)) & 0xFF;

        for (int i = 0; i < 16; i++)
        {
            int x = i & 3;
            int y = i >> 2;
            int rr = Clamp255((x * (c1r - c0r) + y * (c2r - c0r) + 4 * c0r + 2) >> 2);
            int gg = Clamp255((x * (c1g - c0g) + y * (c2g - c0g) + 4 * c0g + 2) >> 2);
            int bb = Clamp255((x * (c1b - c0b) + y * (c2b - c0b) + 4 * c0b + 2) >> 2);
            buf[i] = Pack(rr, gg, bb);
        }
    }

    private static unsafe void DecodeEtc2AlphaBlock(byte* s, uint* buf)
    {
        if ((s[1] & 0xF0) != 0)
        {
            int mult = s[1] >> 4;
            int ti = s[1] & 0xF;
            ulong l = ((ulong)s[0] << 56) | ((ulong)s[1] << 48) | ((ulong)s[2] << 40) | ((ulong)s[3] << 32)
                    | ((ulong)s[4] << 24) | ((ulong)s[5] << 16) | ((ulong)s[6] << 8) | s[7];
            for (int i = 0; i < 16; i++)
            {
                int index = (int)((l >> (3 * i)) & 7);
                int pos = WriteOrderRev[i];
                int a = Clamp255(s[0] + mult * AlphaMod[ti, index]);
                buf[pos] = (buf[pos] & 0x00FFFFFFu) | ((uint)a << 24);
            }
        }
        else
        {
            uint a = (uint)s[0] << 24;
            for (int i = 0; i < 16; i++)
            {
                buf[i] = (buf[i] & 0x00FFFFFFu) | a;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void CopyBlock(uint* buf, byte* dst, int bx, int by, int width, int height)
    {
        int x0 = bx * 4;
        int y0 = by * 4;
        int rows = height - y0 < 4 ? height - y0 : 4;
        int cols = width - x0 < 4 ? width - x0 : 4;
        int rowStride = width * 4;
        for (int y = 0; y < rows; y++)
        {
            ref byte d = ref dst[(y0 + y) * rowStride + x0 * 4];
            for (int x = 0; x < cols; x++)
            {
                WriteUInt(ref d, x * 4, buf[y * 4 + x]);
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Public encode API (implemented in EtcEncoder.cs)
    // ---------------------------------------------------------------------------------------

    public static void EncodeEtc2Rgb(ReadOnlySpan<byte> rgba, int width, int height, Span<byte> dest)
        => EtcEncoder.EncodeRgb(rgba, width, height, dest);

    public static void EncodeEtc2Rgba(ReadOnlySpan<byte> rgba, int width, int height, Span<byte> dest)
        => EtcEncoder.EncodeRgba(rgba, width, height, dest);
}
