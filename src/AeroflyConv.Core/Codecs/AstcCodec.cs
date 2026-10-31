using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AeroflyConv.Core.Codecs;

/// <summary>
/// Pure C# ASTC LDR decoder.
/// <para>
/// The bit layouts and arithmetic mirror the reference decoder used as ground truth
/// (Perfare's Texture2DDecoder <c>astc.cpp</c>, wrapped by the Python
/// <c>texture2ddecoder</c> package) so that output is byte-identical.
/// </para>
/// </summary>
public static class AstcCodec
{
    /// <summary>True if this ASTC block size is supported by the decoder (4..12 in each dimension).</summary>
    public static bool SupportsBlockSize(int blockWidth, int blockHeight) =>
        blockWidth is >= 4 and <= 12 && blockHeight is >= 4 and <= 12;

    /// <summary>Decodes one or more ASTC blocks into tightly packed RGBA8 (top row first).</summary>
    /// <param name="data">block-compressed data, 16 bytes per block, rows of ceil(width/bw) blocks</param>
    /// <param name="width">image width in pixels (may not be a multiple of bw)</param>
    /// <param name="height">image height in pixels (may not be a multiple of bh)</param>
    /// <param name="blockWidth">ASTC block width, e.g. 6</param>
    /// <param name="blockHeight">ASTC block height, e.g. 6</param>
    /// <param name="rgba">destination, must be at least width*height*4 bytes</param>
    public static void Decode(
        ReadOnlySpan<byte> data,
        int width,
        int height,
        int blockWidth,
        int blockHeight,
        Span<byte> rgba)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (!SupportsBlockSize(blockWidth, blockHeight))
            throw new ArgumentOutOfRangeException(nameof(blockWidth), "ASTC block size must be 4..12 in each dimension");

        long needed = (long)width * height * 4;
        if ((long)rgba.Length < needed)
            throw new ArgumentException("destination span is too small", nameof(rgba));

        Span<uint> dst = MemoryMarshal.Cast<byte, uint>(rgba[..(int)needed]);

        int blocksX = (width + blockWidth - 1) / blockWidth;
        int blocksY = (height + blockHeight - 1) / blockHeight;

        var d = new BlockData
        {
            Bw = blockWidth,
            Bh = blockHeight,
        };

        Span<byte> block = stackalloc byte[16];

        for (int by = 0; by < blocksY; by++)
        {
            int y0 = by * blockHeight;
            int rows = Math.Min(blockHeight, height - y0);

            for (int bx = 0; bx < blocksX; bx++)
            {
                int off = (by * blocksX + bx) * 16;
                if (off >= 0 && off + 16 <= data.Length)
                    data.Slice(off, 16).CopyTo(block);
                else
                    block.Clear();

                DecodeBlock(block, d);

                int x0 = bx * blockWidth;
                int cols = Math.Min(blockWidth, width - x0);
                for (int r = 0; r < rows; r++)
                {
                    d.Pixels.AsSpan(r * blockWidth, cols)
                        .CopyTo(dst.Slice((y0 + r) * width + x0, cols));
                }
            }
        }
    }

    // ------------------------------------------------------------------ block

    private static void DecodeBlock(ReadOnlySpan<byte> b, BlockData d)
    {
        if (b[0] == 0xfc && (b[1] & 1) != 0)
        {
            // Void-extent block: a single constant color, 8-bit or half-float.
            uint c;
            if ((b[1] & 2) != 0)
            {
                c = Pack(F16ToU8(b, 8), F16ToU8(b, 10), F16ToU8(b, 12), F16ToU8(b, 14));
            }
            else
            {
                c = Pack(b[9], b[11], b[13], b[15]);
            }

            uint[] px = d.Pixels;
            for (int i = 0, n = d.Bw * d.Bh; i < n; i++) px[i] = c;
            return;
        }

        if (((b[0] & 0xc3) == 0xc0 && (b[1] & 1) != 0) || (b[0] & 0x0f) == 0)
        {
            // Reserved / error block: the reference decoder paints it magenta.
            const uint magenta = 0xFFFF00FFu; // bytes R=FF G=00 B=FF A=FF
            uint[] px = d.Pixels;
            for (int i = 0, n = d.Bw * d.Bh; i < n; i++) px[i] = magenta;
            return;
        }

        ulong lo = BinaryPrimitives.ReadUInt64LittleEndian(b);
        ulong hi = BinaryPrimitives.ReadUInt64LittleEndian(b[8..]);

        DecodeBlockParams(b, d, lo, hi);
        DecodeEndpoints(d, lo, hi);
        DecodeWeights(d, lo, hi);
        if (d.PartNum > 1) SelectPartition(b, d);
        ApplyColor(d);
    }

    private static void DecodeBlockParams(ReadOnlySpan<byte> b, BlockData d, ulong lo, ulong hi)
    {
        d.DualPlane = (b[1] & 4) != 0 ? 1 : 0;
        d.WeightRange = ((b[0] >> 4) & 1) | ((b[1] << 2) & 8);

        int u16 = b[0] | (b[1] << 8);

        if ((b[0] & 3) != 0)
        {
            d.WeightRange |= (b[0] << 1) & 6;
            switch (b[0] & 0xc)
            {
                case 0:
                    d.Width = ((u16 >> 7) & 3) + 4;
                    d.Height = ((b[0] >> 5) & 3) + 2;
                    break;
                case 4:
                    d.Width = ((u16 >> 7) & 3) + 8;
                    d.Height = ((b[0] >> 5) & 3) + 2;
                    break;
                case 8:
                    d.Width = ((b[0] >> 5) & 3) + 2;
                    d.Height = ((u16 >> 7) & 3) + 8;
                    break;
                default:
                    if ((b[1] & 1) != 0)
                    {
                        d.Width = ((b[0] >> 7) & 1) + 2;
                        d.Height = ((b[0] >> 5) & 3) + 2;
                    }
                    else
                    {
                        d.Width = ((b[0] >> 5) & 3) + 2;
                        d.Height = ((b[0] >> 7) & 1) + 6;
                    }
                    break;
            }
        }
        else
        {
            d.WeightRange |= (b[0] >> 1) & 6;
            switch (u16 & 0x180)
            {
                case 0:
                    d.Width = 12;
                    d.Height = ((b[0] >> 5) & 3) + 2;
                    break;
                case 0x80:
                    d.Width = ((b[0] >> 5) & 3) + 2;
                    d.Height = 12;
                    break;
                case 0x100:
                    d.Width = ((b[0] >> 5) & 3) + 6;
                    d.Height = ((b[1] >> 1) & 3) + 6;
                    d.DualPlane = 0;
                    d.WeightRange &= 7;
                    break;
                default:
                    d.Width = (b[0] & 0x20) != 0 ? 10 : 6;
                    d.Height = (b[0] & 0x20) != 0 ? 6 : 10;
                    break;
            }
        }

        d.PartNum = ((b[1] >> 3) & 3) + 1;

        d.WeightNum = d.Width * d.Height;
        if (d.DualPlane != 0) d.WeightNum *= 2;

        int weightBits;
        int weightPrecA = AstcTables.WeightPrecA[d.WeightRange];
        int weightPrecB = AstcTables.WeightPrecB[d.WeightRange];
        switch (weightPrecA)
        {
            case 3:
                weightBits = d.WeightNum * weightPrecB + (d.WeightNum * 8 + 4) / 5;
                break;
            case 5:
                weightBits = d.WeightNum * weightPrecB + (d.WeightNum * 7 + 2) / 3;
                break;
            default:
                weightBits = d.WeightNum * weightPrecB;
                break;
        }
        d.WeightBits = weightBits;

        int configBits;
        int cemBase = 0;
        if (d.PartNum == 1)
        {
            d.Cem[0] = ((b[1] | (b[2] << 8)) >> 5) & 0xf;
            configBits = 17;
        }
        else
        {
            cemBase = (int)(((uint)(b[2] | (b[3] << 8)) >> 7) & 3);
            if (cemBase == 0)
            {
                int cem = (b[3] >> 1) & 0xf;
                for (int i = 0; i < d.PartNum; i++) d.Cem[i] = cem;
                configBits = 29;
            }
            else
            {
                for (int i = 0; i < d.PartNum; i++)
                    d.Cem[i] = (((b[3] >> (i + 1)) & 1) + cemBase - 1) << 2;

                switch (d.PartNum)
                {
                    case 2:
                        d.Cem[0] |= (b[3] >> 3) & 3;
                        d.Cem[1] |= GetBits(lo, hi, 126 - weightBits, 2);
                        break;
                    case 3:
                        d.Cem[0] |= (b[3] >> 4) & 1;
                        d.Cem[0] |= GetBits(lo, hi, 122 - weightBits, 2) & 2;
                        d.Cem[1] |= GetBits(lo, hi, 124 - weightBits, 2);
                        d.Cem[2] |= GetBits(lo, hi, 126 - weightBits, 2);
                        break;
                    default:
                        for (int i = 0; i < 4; i++)
                            d.Cem[i] |= GetBits(lo, hi, 120 + i * 2 - weightBits, 2);
                        break;
                }
                configBits = 25 + d.PartNum * 3;
            }
        }

        if (d.DualPlane != 0)
        {
            configBits += 2;
            d.PlaneSelector = GetBits(
                lo,
                hi,
                cemBase != 0 ? 130 - weightBits - d.PartNum * 3 : 126 - weightBits,
                2);
        }

        int remainBits = 128 - configBits - weightBits;

        d.EndpointValueNum = 0;
        for (int i = 0; i < d.PartNum; i++)
            d.EndpointValueNum += ((d.Cem[i] >> 1) & 6) + 2;

        d.CemRange = 0;
        for (int i = 0; i < 19; i++)
        {
            int endpointBits;
            int a = AstcTables.CemA[i];
            int bb = AstcTables.CemB[i];
            switch (a)
            {
                case 3:
                    endpointBits = d.EndpointValueNum * bb + (d.EndpointValueNum * 8 + 4) / 5;
                    break;
                case 5:
                    endpointBits = d.EndpointValueNum * bb + (d.EndpointValueNum * 7 + 2) / 3;
                    break;
                default:
                    endpointBits = d.EndpointValueNum * bb;
                    break;
            }

            if (endpointBits <= remainBits)
            {
                d.CemRange = i;
                break;
            }
        }
    }

    // -------------------------------------------------------------- endpoints

    private static void DecodeEndpoints(BlockData d, ulong lo, ulong hi)
    {
        int cemA = AstcTables.CemA[d.CemRange];
        int cemB = AstcTables.CemB[d.CemRange];
        DecodeIntSeq(lo, hi, d.PartNum == 1 ? 17 : 29, cemA, cemB, d.EndpointValueNum, false, d.ESeq);

        int[] ev = d.Ev;
        IntSeqData[] seq = d.ESeq;
        int count = d.EndpointValueNum;

        if (cemA == 3)
        {
            int c = TritMultiplier(cemB);
            for (int i = 0; i < count; i++)
            {
                int a = (seq[i].Bits & 1) * 0x1ff;
                int x = seq[i].Bits >> 1;
                int b;
                switch (cemB)
                {
                    case 1: b = 0; break;
                    case 2: b = 0b100010110 * x; break;
                    case 3: b = (x << 7) | (x << 2) | x; break;
                    case 4: b = (x << 6) | x; break;
                    case 5: b = (x << 5) | (x >> 2); break;
                    case 6: b = (x << 4) | (x >> 4); break;
                    default: b = 0; break;
                }
                ev[i] = (a & 0x80) | (((seq[i].Nonbits * c + b) ^ a) >> 2);
            }
        }
        else if (cemA == 5)
        {
            int c = QuintMultiplier(cemB);
            for (int i = 0; i < count; i++)
            {
                int a = (seq[i].Bits & 1) * 0x1ff;
                int x = seq[i].Bits >> 1;
                int b;
                switch (cemB)
                {
                    case 1: b = 0; break;
                    case 2: b = 0b100001100 * x; break;
                    case 3: b = (x << 7) | (x << 1) | (x >> 1); break;
                    case 4: b = (x << 6) | (x >> 1); break;
                    case 5: b = (x << 5) | (x >> 3); break;
                    default: b = 0; break;
                }
                ev[i] = (a & 0x80) | (((seq[i].Nonbits * c + b) ^ a) >> 2);
            }
        }
        else
        {
            switch (cemB)
            {
                case 1:
                    for (int i = 0; i < count; i++) ev[i] = seq[i].Bits * 0xff;
                    break;
                case 2:
                    for (int i = 0; i < count; i++) ev[i] = seq[i].Bits * 0x55;
                    break;
                case 3:
                    for (int i = 0; i < count; i++) ev[i] = (seq[i].Bits << 5) | (seq[i].Bits << 2) | (seq[i].Bits >> 1);
                    break;
                case 4:
                    for (int i = 0; i < count; i++) ev[i] = (seq[i].Bits << 4) | seq[i].Bits;
                    break;
                case 5:
                    for (int i = 0; i < count; i++) ev[i] = (seq[i].Bits << 3) | (seq[i].Bits >> 2);
                    break;
                case 6:
                    for (int i = 0; i < count; i++) ev[i] = (seq[i].Bits << 2) | (seq[i].Bits >> 4);
                    break;
                case 7:
                    for (int i = 0; i < count; i++) ev[i] = (seq[i].Bits << 1) | (seq[i].Bits >> 6);
                    break;
                default:
                    for (int i = 0; i < count; i++) ev[i] = seq[i].Bits;
                    break;
            }
        }

        int[] e = d.Endpoints;
        int vi = 0;
        for (int cemIdx = 0; cemIdx < d.PartNum; cemIdx++)
        {
            int cem = d.Cem[cemIdx];
            int o = cemIdx << 3;
            switch (cem)
            {
                case 0:
                    SetEndpoint(e, o, ev[vi], ev[vi], ev[vi], 255, ev[vi + 1], ev[vi + 1], ev[vi + 1], 255);
                    break;
                case 1:
                {
                    int l0 = (ev[vi] >> 2) | (ev[vi + 1] & 0xc0);
                    int l1 = Clamp(l0 + (ev[vi + 1] & 0x3f));
                    SetEndpoint(e, o, l0, l0, l0, 255, l1, l1, l1, 255);
                    break;
                }
                case 2:
                {
                    int y0, y1;
                    if (ev[vi] <= ev[vi + 1])
                    {
                        y0 = ev[vi] << 4;
                        y1 = ev[vi + 1] << 4;
                    }
                    else
                    {
                        y0 = (ev[vi + 1] << 4) + 8;
                        y1 = (ev[vi] << 4) - 8;
                    }
                    SetEndpointHdr(e, o, y0, y0, y0, 0x780, y1, y1, y1, 0x780);
                    break;
                }
                case 3:
                {
                    int y0;
                    int delta;
                    if ((ev[vi] & 0x80) != 0)
                    {
                        y0 = ((ev[vi + 1] & 0xe0) << 4) | ((ev[vi] & 0x7f) << 2);
                        delta = (ev[vi + 1] & 0x1f) << 2;
                    }
                    else
                    {
                        y0 = ((ev[vi + 1] & 0xf0) << 4) | ((ev[vi] & 0x7f) << 1);
                        delta = (ev[vi + 1] & 0x0f) << 1;
                    }
                    int y1 = ClampHdr(y0 + delta);
                    SetEndpointHdr(e, o, y0, y0, y0, 0x780, y1, y1, y1, 0x780);
                    break;
                }
                case 4:
                    SetEndpoint(e, o, ev[vi], ev[vi], ev[vi], ev[vi + 2], ev[vi + 1], ev[vi + 1], ev[vi + 1], ev[vi + 3]);
                    break;
                case 5:
                {
                    int v0 = ev[vi], v1 = ev[vi + 1], v2 = ev[vi + 2], v3 = ev[vi + 3];
                    BitTransferSigned(ref v1, ref v0);
                    BitTransferSigned(ref v3, ref v2);
                    v1 += v0;
                    SetEndpointClamp(e, o, v0, v0, v0, v2, v1, v1, v1, v2 + v3);
                    break;
                }
                case 6:
                    SetEndpoint(
                        e,
                        o,
                        (ev[vi] * ev[vi + 3]) >> 8,
                        (ev[vi + 1] * ev[vi + 3]) >> 8,
                        (ev[vi + 2] * ev[vi + 3]) >> 8,
                        255,
                        ev[vi],
                        ev[vi + 1],
                        ev[vi + 2],
                        255);
                    break;
                case 7:
                    DecodeEndpointsHdr7(d, o, vi);
                    break;
                case 8:
                    if (ev[vi] + ev[vi + 2] + ev[vi + 4] <= ev[vi + 1] + ev[vi + 3] + ev[vi + 5])
                        SetEndpoint(e, o, ev[vi], ev[vi + 2], ev[vi + 4], 255, ev[vi + 1], ev[vi + 3], ev[vi + 5], 255);
                    else
                        SetEndpointBlue(e, o, ev[vi + 1], ev[vi + 3], ev[vi + 5], 255, ev[vi], ev[vi + 2], ev[vi + 4], 255);
                    break;
                case 9:
                {
                    int v0 = ev[vi], v1 = ev[vi + 1], v2 = ev[vi + 2], v3 = ev[vi + 3], v4 = ev[vi + 4], v5 = ev[vi + 5];
                    BitTransferSigned(ref v1, ref v0);
                    BitTransferSigned(ref v3, ref v2);
                    BitTransferSigned(ref v5, ref v4);
                    if (v1 + v3 + v5 >= 0)
                        SetEndpointClamp(e, o, v0, v2, v4, 255, v0 + v1, v2 + v3, v4 + v5, 255);
                    else
                        SetEndpointBlueClamp(e, o, v0 + v1, v2 + v3, v4 + v5, 255, v0, v2, v4, 255);
                    break;
                }
                case 10:
                    SetEndpoint(
                        e,
                        o,
                        (ev[vi] * ev[vi + 3]) >> 8,
                        (ev[vi + 1] * ev[vi + 3]) >> 8,
                        (ev[vi + 2] * ev[vi + 3]) >> 8,
                        ev[vi + 4],
                        ev[vi],
                        ev[vi + 1],
                        ev[vi + 2],
                        ev[vi + 5]);
                    break;
                case 11:
                    DecodeEndpointsHdr11(d, o, vi, 0x780, 0x780);
                    break;
                case 12:
                    if (ev[vi] + ev[vi + 2] + ev[vi + 4] <= ev[vi + 1] + ev[vi + 3] + ev[vi + 5])
                        SetEndpoint(e, o, ev[vi], ev[vi + 2], ev[vi + 4], ev[vi + 6], ev[vi + 1], ev[vi + 3], ev[vi + 5], ev[vi + 7]);
                    else
                        SetEndpointBlue(e, o, ev[vi + 1], ev[vi + 3], ev[vi + 5], ev[vi + 7], ev[vi], ev[vi + 2], ev[vi + 4], ev[vi + 6]);
                    break;
                case 13:
                {
                    int v0 = ev[vi], v1 = ev[vi + 1], v2 = ev[vi + 2], v3 = ev[vi + 3];
                    int v4 = ev[vi + 4], v5 = ev[vi + 5], v6 = ev[vi + 6], v7 = ev[vi + 7];
                    BitTransferSigned(ref v1, ref v0);
                    BitTransferSigned(ref v3, ref v2);
                    BitTransferSigned(ref v5, ref v4);
                    BitTransferSigned(ref v7, ref v6);
                    if (v1 + v3 + v5 >= 0)
                        SetEndpointClamp(e, o, v0, v2, v4, v6, v0 + v1, v2 + v3, v4 + v5, v6 + v7);
                    else
                        SetEndpointBlueClamp(e, o, v0 + v1, v2 + v3, v4 + v5, v6 + v7, v0, v2, v4, v6);
                    break;
                }
                case 14:
                    DecodeEndpointsHdr11(d, o, vi, ev[vi + 6], ev[vi + 7]);
                    break;
                default:
                {
                    int v6 = ev[vi + 6];
                    int v7 = ev[vi + 7];
                    int mode = ((v6 >> 7) & 1) | ((v7 >> 6) & 2);
                    v6 &= 0x7f;
                    v7 &= 0x7f;
                    if (mode == 3)
                    {
                        DecodeEndpointsHdr11(d, o, vi, v6 << 5, v7 << 5);
                    }
                    else
                    {
                        v6 |= (v7 << (mode + 1)) & 0x780;
                        v7 = ((v7 & (0x3f >> mode)) ^ (0x20 >> mode)) - (0x20 >> mode);
                        v6 <<= 4 - mode;
                        v7 <<= 4 - mode;
                        DecodeEndpointsHdr11(d, o, vi, v6, ClampHdr(v6 + v7));
                    }
                    break;
                }
            }

            vi += (cem / 4 + 1) * 2;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int TritMultiplier(int cemB) => cemB switch
    {
        1 => 204,
        2 => 93,
        3 => 44,
        4 => 22,
        5 => 11,
        6 => 5,
        _ => 0,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int QuintMultiplier(int cemB) => cemB switch
    {
        1 => 113,
        2 => 54,
        3 => 26,
        4 => 13,
        5 => 6,
        _ => 0,
    };

    private static void DecodeEndpointsHdr7(BlockData d, int o, int vi)
    {
        int[] v = d.Ev;
        int[] c = d.C;
        int modeval = ((v[vi + 2] >> 4) & 0x8) | ((v[vi + 1] >> 5) & 0x4) | (v[vi] >> 6);
        int majorComponent;
        int mode;
        if ((modeval & 0xc) != 0xc)
        {
            majorComponent = modeval >> 2;
            mode = modeval & 3;
        }
        else if (modeval != 0xf)
        {
            majorComponent = modeval & 3;
            mode = 4;
        }
        else
        {
            majorComponent = 0;
            mode = 5;
        }

        c[0] = v[vi] & 0x3f;
        c[1] = v[vi + 1] & 0x1f;
        c[2] = v[vi + 2] & 0x1f;
        c[3] = v[vi + 3] & 0x1f;

        switch (mode)
        {
            case 0:
                c[3] |= v[vi + 3] & 0x60;
                c[0] |= (v[vi + 3] >> 1) & 0x40;
                c[0] |= (v[vi + 2] << 1) & 0x80;
                c[0] |= (v[vi + 1] << 3) & 0x300;
                c[0] |= (v[vi + 2] << 5) & 0x400;
                c[0] <<= 1; c[1] <<= 1; c[2] <<= 1; c[3] <<= 1;
                break;
            case 1:
                c[1] |= v[vi + 1] & 0x20;
                c[2] |= v[vi + 2] & 0x20;
                c[0] |= (v[vi + 3] >> 1) & 0x40;
                c[0] |= (v[vi + 2] << 1) & 0x80;
                c[0] |= (v[vi + 1] << 2) & 0x100;
                c[0] |= (v[vi + 3] << 4) & 0x600;
                c[0] <<= 1; c[1] <<= 1; c[2] <<= 1; c[3] <<= 1;
                break;
            case 2:
                c[3] |= v[vi + 3] & 0xe0;
                c[0] |= (v[vi + 2] << 1) & 0xc0;
                c[0] |= (v[vi + 1] << 3) & 0x300;
                c[0] <<= 2; c[1] <<= 2; c[2] <<= 2; c[3] <<= 2;
                break;
            case 3:
                c[1] |= v[vi + 1] & 0x20;
                c[2] |= v[vi + 2] & 0x20;
                c[3] |= v[vi + 3] & 0x60;
                c[0] |= (v[vi + 3] >> 1) & 0x40;
                c[0] |= (v[vi + 2] << 1) & 0x80;
                c[0] |= (v[vi + 1] << 2) & 0x100;
                c[0] <<= 3; c[1] <<= 3; c[2] <<= 3; c[3] <<= 3;
                break;
            case 4:
                c[1] |= v[vi + 1] & 0x60;
                c[2] |= v[vi + 2] & 0x60;
                c[3] |= v[vi + 3] & 0x20;
                c[0] |= (v[vi + 3] >> 1) & 0x40;
                c[0] |= (v[vi + 3] << 1) & 0x80;
                c[0] <<= 4; c[1] <<= 4; c[2] <<= 4; c[3] <<= 4;
                break;
            default:
                c[1] |= v[vi + 1] & 0x60;
                c[2] |= v[vi + 2] & 0x60;
                c[3] |= v[vi + 3] & 0x60;
                c[0] |= (v[vi + 3] >> 1) & 0x40;
                c[0] <<= 5; c[1] <<= 5; c[2] <<= 5; c[3] <<= 5;
                break;
        }

        if (mode != 5)
        {
            c[1] = c[0] - c[1];
            c[2] = c[0] - c[2];
        }

        int[] e = d.Endpoints;
        if (majorComponent == 1)
            SetEndpointHdrClamp(e, o, c[1] - c[3], c[0] - c[3], c[2] - c[3], 0x780, c[1], c[0], c[2], 0x780);
        else if (majorComponent == 2)
            SetEndpointHdrClamp(e, o, c[2] - c[3], c[1] - c[3], c[0] - c[3], 0x780, c[2], c[1], c[0], 0x780);
        else
            SetEndpointHdrClamp(e, o, c[0] - c[3], c[1] - c[3], c[2] - c[3], 0x780, c[0], c[1], c[2], 0x780);
    }

    private static void DecodeEndpointsHdr11(BlockData d, int o, int vi, int alpha1, int alpha2)
    {
        int[] v = d.Ev;
        int[] e = d.Endpoints;

        int majorComponent = (v[vi + 4] >> 7) | ((v[vi + 5] >> 6) & 2);
        if (majorComponent == 3)
        {
            SetEndpointHdr(
                e,
                o,
                v[vi] << 4,
                v[vi + 2] << 4,
                (v[vi + 4] << 5) & 0xfe0,
                alpha1,
                v[vi + 1] << 4,
                v[vi + 3] << 4,
                (v[vi + 5] << 5) & 0xfe0,
                alpha2);
            return;
        }

        int mode = (v[vi + 1] >> 7) | ((v[vi + 2] >> 6) & 2) | ((v[vi + 3] >> 5) & 4);
        int va = v[vi] | ((v[vi + 1] << 2) & 0x100);
        int vb0 = v[vi + 2] & 0x3f;
        int vb1 = v[vi + 3] & 0x3f;
        int vc = v[vi + 1] & 0x3f;
        int vd0;
        int vd1;

        switch (mode)
        {
            case 0:
            case 2:
                vd0 = v[vi + 4] & 0x7f;
                if ((vd0 & 0x40) != 0) vd0 |= unchecked((int)0xffffff80);
                vd1 = v[vi + 5] & 0x7f;
                if ((vd1 & 0x40) != 0) vd1 |= unchecked((int)0xffffff80);
                break;
            case 1:
            case 3:
            case 5:
            case 7:
                vd0 = v[vi + 4] & 0x3f;
                if ((vd0 & 0x20) != 0) vd0 |= unchecked((int)0xffffffc0);
                vd1 = v[vi + 5] & 0x3f;
                if ((vd1 & 0x20) != 0) vd1 |= unchecked((int)0xffffffc0);
                break;
            default:
                vd0 = v[vi + 4] & 0x1f;
                if ((vd0 & 0x10) != 0) vd0 |= unchecked((int)0xffffffe0);
                vd1 = v[vi + 5] & 0x1f;
                if ((vd1 & 0x10) != 0) vd1 |= unchecked((int)0xffffffe0);
                break;
        }

        switch (mode)
        {
            case 0:
                vb0 |= v[vi + 2] & 0x40;
                vb1 |= v[vi + 3] & 0x40;
                break;
            case 1:
                vb0 |= v[vi + 2] & 0x40;
                vb1 |= v[vi + 3] & 0x40;
                vb0 |= (v[vi + 4] << 1) & 0x80;
                vb1 |= (v[vi + 5] << 1) & 0x80;
                break;
            case 2:
                va |= (v[vi + 2] << 3) & 0x200;
                vc |= v[vi + 3] & 0x40;
                break;
            case 3:
                va |= (v[vi + 4] << 3) & 0x200;
                vc |= v[vi + 5] & 0x40;
                vb0 |= v[vi + 2] & 0x40;
                vb1 |= v[vi + 3] & 0x40;
                break;
            case 4:
                va |= (v[vi + 4] << 4) & 0x200;
                va |= (v[vi + 5] << 5) & 0x400;
                vb0 |= v[vi + 2] & 0x40;
                vb1 |= v[vi + 3] & 0x40;
                vb0 |= (v[vi + 4] << 1) & 0x80;
                vb1 |= (v[vi + 5] << 1) & 0x80;
                break;
            case 5:
                va |= (v[vi + 2] << 3) & 0x200;
                va |= (v[vi + 3] << 4) & 0x400;
                vc |= v[vi + 5] & 0x40;
                vc |= (v[vi + 4] << 1) & 0x80;
                break;
            case 6:
                va |= (v[vi + 4] << 4) & 0x200;
                va |= (v[vi + 5] << 5) & 0x400;
                va |= (v[vi + 4] << 5) & 0x800;
                vc |= v[vi + 5] & 0x40;
                vb0 |= v[vi + 2] & 0x40;
                vb1 |= v[vi + 3] & 0x40;
                break;
            default:
                va |= (v[vi + 2] << 3) & 0x200;
                va |= (v[vi + 3] << 4) & 0x400;
                va |= (v[vi + 4] << 5) & 0x800;
                vc |= v[vi + 5] & 0x40;
                break;
        }

        int shamt = (mode >> 1) ^ 3;
        va <<= shamt;
        vb0 <<= shamt;
        vb1 <<= shamt;
        vc <<= shamt;
        int mult = 1 << shamt;
        vd0 *= mult;
        vd1 *= mult;

        if (majorComponent == 1)
            SetEndpointHdrClamp(
                e, o,
                va - vb0 - vc - vd0, va - vc, va - vb1 - vc - vd1, alpha1,
                va - vb0, va, va - vb1, alpha2);
        else if (majorComponent == 2)
            SetEndpointHdrClamp(
                e, o,
                va - vb1 - vc - vd1, va - vb0 - vc - vd0, va - vc, alpha1,
                va - vb1, va - vb0, va, alpha2);
        else
            SetEndpointHdrClamp(
                e, o,
                va - vc, va - vb0 - vc - vd0, va - vb1 - vc - vd1, alpha1,
                va, va - vb0, va - vb1, alpha2);
    }

    // ---------------------------------------------------------------- weights

    private static void DecodeWeights(BlockData d, ulong lo, ulong hi)
    {
        int precA = AstcTables.WeightPrecA[d.WeightRange];
        int precB = AstcTables.WeightPrecB[d.WeightRange];

        DecodeIntSeq(lo, hi, 128, precA, precB, d.WeightNum, true, d.WSeq);

        int[] wv = d.Wv;
        IntSeqData[] seq = d.WSeq;
        int count = d.WeightNum;
        Array.Clear(wv, 0, WvCapacity);

        if (precA == 0)
        {
            switch (precB)
            {
                case 1:
                    for (int i = 0; i < count; i++) wv[i] = seq[i].Bits != 0 ? 63 : 0;
                    break;
                case 2:
                    for (int i = 0; i < count; i++) wv[i] = (seq[i].Bits << 4) | (seq[i].Bits << 2) | seq[i].Bits;
                    break;
                case 3:
                    for (int i = 0; i < count; i++) wv[i] = (seq[i].Bits << 3) | seq[i].Bits;
                    break;
                case 4:
                    for (int i = 0; i < count; i++) wv[i] = (seq[i].Bits << 2) | (seq[i].Bits >> 2);
                    break;
                case 5:
                    for (int i = 0; i < count; i++) wv[i] = (seq[i].Bits << 1) | (seq[i].Bits >> 4);
                    break;
            }

            for (int i = 0; i < count; i++)
            {
                if (wv[i] > 32) ++wv[i];
            }
        }
        else if (precB == 0)
        {
            int s = precA == 3 ? 32 : 16;
            for (int i = 0; i < count; i++) wv[i] = seq[i].Nonbits * s;
        }
        else
        {
            if (precA == 3)
            {
                switch (precB)
                {
                    case 1:
                        for (int i = 0; i < count; i++) wv[i] = seq[i].Nonbits * 50;
                        break;
                    case 2:
                        for (int i = 0; i < count; i++)
                        {
                            wv[i] = seq[i].Nonbits * 23;
                            if ((seq[i].Bits & 2) != 0) wv[i] += 0b1000101;
                        }
                        break;
                    case 3:
                        for (int i = 0; i < count; i++)
                            wv[i] = seq[i].Nonbits * 11 + (((seq[i].Bits << 4) | (seq[i].Bits >> 1)) & 0b1100011);
                        break;
                }
            }
            else if (precA == 5)
            {
                switch (precB)
                {
                    case 1:
                        for (int i = 0; i < count; i++) wv[i] = seq[i].Nonbits * 28;
                        break;
                    case 2:
                        for (int i = 0; i < count; i++)
                        {
                            wv[i] = seq[i].Nonbits * 13;
                            if ((seq[i].Bits & 2) != 0) wv[i] += 0b1000010;
                        }
                        break;
                }
            }

            for (int i = 0; i < count; i++)
            {
                int a = (seq[i].Bits & 1) * 0x7f;
                wv[i] = (a & 0x20) | ((wv[i] ^ a) >> 2);
                if (wv[i] > 32) ++wv[i];
            }
        }

        int ds = (1024 + d.Bw / 2) / (d.Bw - 1);
        int dt = (1024 + d.Bh / 2) / (d.Bh - 1);
        int pn = d.DualPlane != 0 ? 2 : 1;
        int[] weights = d.Weights;

        for (int t = 0, i = 0; t < d.Bh; t++)
        {
            for (int s = 0; s < d.Bw; s++, i++)
            {
                int gs = (ds * s * (d.Width - 1) + 32) >> 6;
                int gt = (dt * t * (d.Height - 1) + 32) >> 6;
                int fs = gs & 0xf;
                int ft = gt & 0xf;
                int v = (gs >> 4) + (gt >> 4) * d.Width;
                int w11 = (fs * ft + 8) >> 4;
                int w10 = ft - w11;
                int w01 = fs - w11;
                int w00 = 16 - fs - ft + w11;

                for (int p = 0; p < pn; p++)
                {
                    int p00 = wv[v * pn + p];
                    int p01 = wv[(v + 1) * pn + p];
                    int p10 = wv[(v + d.Width) * pn + p];
                    int p11 = wv[(v + d.Width + 1) * pn + p];
                    weights[i * 2 + p] = (p00 * w00 + p01 * w01 + p10 * w10 + p11 * w11 + 8) >> 4;
                }
            }
        }
    }

    private static void SelectPartition(ReadOnlySpan<byte> b, BlockData d)
    {
        bool smallBlock = d.Bw * d.Bh < 31;
        uint blockBits = BinaryPrimitives.ReadUInt32LittleEndian(b);
        int seed = (int)((blockBits >> 13) & 0x3ff) | ((d.PartNum - 1) << 10);

        uint rnum = (uint)seed;
        rnum ^= rnum >> 15;
        rnum -= rnum << 17;
        rnum += rnum << 7;
        rnum += rnum << 4;
        rnum ^= rnum >> 5;
        rnum += rnum << 16;
        rnum ^= rnum >> 7;
        rnum ^= rnum >> 3;
        rnum ^= rnum << 6;
        rnum ^= rnum >> 17;

        int[] seeds = d.Seeds;
        for (int i = 0; i < 8; i++)
        {
            int s = (int)((rnum >> (i * 4)) & 0xF);
            seeds[i] = s * s;
        }

        int sh0 = (seed & 2) != 0 ? 4 : 5;
        int sh1 = d.PartNum == 3 ? 6 : 5;

        if ((seed & 1) != 0)
        {
            for (int i = 0; i < 8; i++) seeds[i] >>= (i & 1) == 0 ? sh0 : sh1;
        }
        else
        {
            for (int i = 0; i < 8; i++) seeds[i] >>= (i & 1) == 0 ? sh1 : sh0;
        }

        int[] partition = d.Partition;
        if (smallBlock)
        {
            for (int t = 0, i = 0; t < d.Bh; t++)
            {
                for (int s = 0; s < d.Bw; s++, i++)
                {
                    int x = s << 1;
                    int y = t << 1;
                    int a = (seeds[0] * x + seeds[1] * y + (int)(rnum >> 14)) & 0x3f;
                    int b2 = (seeds[2] * x + seeds[3] * y + (int)(rnum >> 10)) & 0x3f;
                    int c = d.PartNum < 3 ? 0 : (seeds[4] * x + seeds[5] * y + (int)(rnum >> 6)) & 0x3f;
                    int dd = d.PartNum < 4 ? 0 : (seeds[6] * x + seeds[7] * y + (int)(rnum >> 2)) & 0x3f;
                    partition[i] = a >= b2 && a >= c && a >= dd ? 0 : b2 >= c && b2 >= dd ? 1 : c >= dd ? 2 : 3;
                }
            }
        }
        else
        {
            for (int y = 0, i = 0; y < d.Bh; y++)
            {
                for (int x = 0; x < d.Bw; x++, i++)
                {
                    int a = (seeds[0] * x + seeds[1] * y + (int)(rnum >> 14)) & 0x3f;
                    int b2 = (seeds[2] * x + seeds[3] * y + (int)(rnum >> 10)) & 0x3f;
                    int c = d.PartNum < 3 ? 0 : (seeds[4] * x + seeds[5] * y + (int)(rnum >> 6)) & 0x3f;
                    int dd = d.PartNum < 4 ? 0 : (seeds[6] * x + seeds[7] * y + (int)(rnum >> 2)) & 0x3f;
                    partition[i] = a >= b2 && a >= c && a >= dd ? 0 : b2 >= c && b2 >= dd ? 1 : c >= dd ? 2 : 3;
                }
            }
        }
    }

    // ------------------------------------------------------------ interpolation

    private static void ApplyColor(BlockData d)
    {
        uint[] outbuf = d.Pixels;
        int[] e = d.Endpoints;
        int[] weights = d.Weights;
        int n = d.Bw * d.Bh;

        if (d.DualPlane != 0)
        {
            int psR = d.PlaneSelector == 0 ? 1 : 0;
            int psG = d.PlaneSelector == 1 ? 1 : 0;
            int psB = d.PlaneSelector == 2 ? 1 : 0;
            int psA = d.PlaneSelector == 3 ? 1 : 0;

            if (d.PartNum > 1)
            {
                int[] partition = d.Partition;
                for (int i = 0; i < n; i++)
                {
                    int p = partition[i];
                    int o = p << 3;
                    int cem = d.Cem[p];
                    int w0 = weights[i * 2];
                    int w1 = weights[i * 2 + 1];
                    bool hdrRgb = AstcTables.CemUsesHdrRgb[cem];
                    int r = hdrRgb ? SelectColorHdr(e[o], e[o + 4], psR != 0 ? w1 : w0) : SelectColor(e[o], e[o + 4], psR != 0 ? w1 : w0);
                    int g = hdrRgb ? SelectColorHdr(e[o + 1], e[o + 5], psG != 0 ? w1 : w0) : SelectColor(e[o + 1], e[o + 5], psG != 0 ? w1 : w0);
                    int b = hdrRgb ? SelectColorHdr(e[o + 2], e[o + 6], psB != 0 ? w1 : w0) : SelectColor(e[o + 2], e[o + 6], psB != 0 ? w1 : w0);
                    int a = AstcTables.CemUsesHdrAlpha[cem]
                        ? SelectColorHdr(e[o + 3], e[o + 7], psA != 0 ? w1 : w0)
                        : SelectColor(e[o + 3], e[o + 7], psA != 0 ? w1 : w0);
                    outbuf[i] = Pack(r, g, b, a);
                }
            }
            else
            {
                int cem = d.Cem[0];
                bool hdrRgb = AstcTables.CemUsesHdrRgb[cem];
                bool hdrA = AstcTables.CemUsesHdrAlpha[cem];
                for (int i = 0; i < n; i++)
                {
                    int w0 = weights[i * 2];
                    int w1 = weights[i * 2 + 1];
                    int r = hdrRgb ? SelectColorHdr(e[0], e[4], psR != 0 ? w1 : w0) : SelectColor(e[0], e[4], psR != 0 ? w1 : w0);
                    int g = hdrRgb ? SelectColorHdr(e[1], e[5], psG != 0 ? w1 : w0) : SelectColor(e[1], e[5], psG != 0 ? w1 : w0);
                    int b = hdrRgb ? SelectColorHdr(e[2], e[6], psB != 0 ? w1 : w0) : SelectColor(e[2], e[6], psB != 0 ? w1 : w0);
                    int a = hdrA ? SelectColorHdr(e[3], e[7], psA != 0 ? w1 : w0) : SelectColor(e[3], e[7], psA != 0 ? w1 : w0);
                    outbuf[i] = Pack(r, g, b, a);
                }
            }
        }
        else if (d.PartNum > 1)
        {
            int[] partition = d.Partition;
            for (int i = 0; i < n; i++)
            {
                int p = partition[i];
                int o = p << 3;
                int cem = d.Cem[p];
                int w = weights[i * 2];
                bool hdrRgb = AstcTables.CemUsesHdrRgb[cem];
                int r = hdrRgb ? SelectColorHdr(e[o], e[o + 4], w) : SelectColor(e[o], e[o + 4], w);
                int g = hdrRgb ? SelectColorHdr(e[o + 1], e[o + 5], w) : SelectColor(e[o + 1], e[o + 5], w);
                int b = hdrRgb ? SelectColorHdr(e[o + 2], e[o + 6], w) : SelectColor(e[o + 2], e[o + 6], w);
                int a = AstcTables.CemUsesHdrAlpha[cem]
                    ? SelectColorHdr(e[o + 3], e[o + 7], w)
                    : SelectColor(e[o + 3], e[o + 7], w);
                outbuf[i] = Pack(r, g, b, a);
            }
        }
        else
        {
            int cem = d.Cem[0];
            bool hdrRgb = AstcTables.CemUsesHdrRgb[cem];
            bool hdrA = AstcTables.CemUsesHdrAlpha[cem];
            for (int i = 0; i < n; i++)
            {
                int w = weights[i * 2];
                int r = hdrRgb ? SelectColorHdr(e[0], e[4], w) : SelectColor(e[0], e[4], w);
                int g = hdrRgb ? SelectColorHdr(e[1], e[5], w) : SelectColor(e[1], e[5], w);
                int b = hdrRgb ? SelectColorHdr(e[2], e[6], w) : SelectColor(e[2], e[6], w);
                int a = hdrA ? SelectColorHdr(e[3], e[7], w) : SelectColor(e[3], e[7], w);
                outbuf[i] = Pack(r, g, b, a);
            }
        }
    }

    // ---------------------------------------------------------------- int seq

    private static void DecodeIntSeq(
        ulong lo,
        ulong hi,
        int offset,
        int a,
        int b,
        int count,
        bool reverse,
        IntSeqData[] output)
    {
        if (count <= 0) return;

        int n = 0;

        if (a == 3)
        {
            int mask = (1 << b) - 1;
            int blockCount = (count + 4) / 5;
            int lastBlockCount = (count + 4) % 5 + 1;
            int blockSize = 8 + 5 * b;
            int lastBlockSize = (blockSize * lastBlockCount + 4) / 5;

            if (reverse)
            {
                for (int i = 0, p = offset; i < blockCount; i++, p -= blockSize)
                {
                    int nowSize = i < blockCount - 1 ? blockSize : lastBlockSize;
                    ulong d = BitReverseU64(GetBits64(lo, hi, p - nowSize, nowSize), nowSize);
                    int x = (int)(((d >> b) & 3) | ((d >> (b * 2)) & 0xc) | ((d >> (b * 3)) & 0x10) |
                                  ((d >> (b * 4)) & 0x60) | ((d >> (b * 5)) & 0x80));
                    for (int j = 0; j < 5 && n < count; j++, n++)
                    {
                        output[n].Bits = (int)((d >> (Mt(j) + b * j)) & (ulong)mask);
                        output[n].Nonbits = AstcTables.Trit(j, x);
                    }
                }
            }
            else
            {
                for (int i = 0, p = offset; i < blockCount; i++, p += blockSize)
                {
                    int nowSize = i < blockCount - 1 ? blockSize : lastBlockSize;
                    ulong d = GetBits64(lo, hi, p, nowSize);
                    int x = (int)(((d >> b) & 3) | ((d >> (b * 2)) & 0xc) | ((d >> (b * 3)) & 0x10) |
                                  ((d >> (b * 4)) & 0x60) | ((d >> (b * 5)) & 0x80));
                    for (int j = 0; j < 5 && n < count; j++, n++)
                    {
                        output[n].Bits = (int)((d >> (Mt(j) + b * j)) & (ulong)mask);
                        output[n].Nonbits = AstcTables.Trit(j, x);
                    }
                }
            }
        }
        else if (a == 5)
        {
            int mask = (1 << b) - 1;
            int blockCount = (count + 2) / 3;
            int lastBlockCount = (count + 2) % 3 + 1;
            int blockSize = 7 + 3 * b;
            int lastBlockSize = (blockSize * lastBlockCount + 2) / 3;

            if (reverse)
            {
                for (int i = 0, p = offset; i < blockCount; i++, p -= blockSize)
                {
                    int nowSize = i < blockCount - 1 ? blockSize : lastBlockSize;
                    ulong d = BitReverseU64(GetBits64(lo, hi, p - nowSize, nowSize), nowSize);
                    int x = (int)(((d >> b) & 7) | ((d >> (b * 2)) & 0x18) | ((d >> (b * 3)) & 0x60));
                    for (int j = 0; j < 3 && n < count; j++, n++)
                    {
                        output[n].Bits = (int)((d >> (Mq(j) + b * j)) & (ulong)mask);
                        output[n].Nonbits = AstcTables.Quint(j, x);
                    }
                }
            }
            else
            {
                for (int i = 0, p = offset; i < blockCount; i++, p += blockSize)
                {
                    int nowSize = i < blockCount - 1 ? blockSize : lastBlockSize;
                    ulong d = GetBits64(lo, hi, p, nowSize);
                    int x = (int)(((d >> b) & 7) | ((d >> (b * 2)) & 0x18) | ((d >> (b * 3)) & 0x60));
                    for (int j = 0; j < 3 && n < count; j++, n++)
                    {
                        output[n].Bits = (int)((d >> (Mq(j) + b * j)) & (ulong)mask);
                        output[n].Nonbits = AstcTables.Quint(j, x);
                    }
                }
            }
        }
        else
        {
            if (reverse)
            {
                for (int p = offset - b; n < count; n++, p -= b)
                {
                    output[n].Bits = AstcTables.ReverseByte(GetBits(lo, hi, p, b), b);
                    output[n].Nonbits = 0;
                }
            }
            else
            {
                for (int p = offset; n < count; n++, p += b)
                {
                    output[n].Bits = GetBits(lo, hi, p, b);
                    output[n].Nonbits = 0;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mt(int j) => j switch { 0 => 0, 1 => 2, 2 => 4, 3 => 5, _ => 7 };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mq(int j) => j switch { 0 => 0, 1 => 3, _ => 5 };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetBits(ulong lo, ulong hi, int bit, int len)
    {
        if (len <= 0) return 0;
        ulong mask = (1UL << len) - 1;
        ulong v;
        if (bit >= 64)
            v = hi >> (bit - 64);
        else if (bit <= 0)
            v = lo << -bit;
        else if (bit + len <= 64)
            v = lo >> bit;
        else
            v = (lo >> bit) | (hi << (64 - bit));
        return (int)(v & mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong GetBits64(ulong lo, ulong hi, int bit, int len)
    {
        if (len < 1) return 0;
        ulong mask = len >= 64 ? ulong.MaxValue : (1UL << len) - 1;
        if (bit >= 64)
            return (hi >> (bit - 64)) & mask;
        if (bit <= 0)
            return (lo << -bit) & mask;
        if (bit + len <= 64)
            return (lo >> bit) & mask;
        return ((lo >> bit) | (hi << (64 - bit))) & mask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong BitReverseU64(ulong d, int bits)
    {
        d = ((d >> 1) & 0x5555555555555555UL) | ((d & 0x5555555555555555UL) << 1);
        d = ((d >> 2) & 0x3333333333333333UL) | ((d & 0x3333333333333333UL) << 2);
        d = ((d >> 4) & 0x0F0F0F0F0F0F0F0FUL) | ((d & 0x0F0F0F0F0F0F0F0FUL) << 4);
        d = ((d >> 8) & 0x00FF00FF00FF00FFUL) | ((d & 0x00FF00FF00FF00FFUL) << 8);
        d = ((d >> 16) & 0x0000FFFF0000FFFFUL) | ((d & 0x0000FFFF0000FFFFUL) << 16);
        d = (d >> 32) | (d << 32);
        return bits >= 64 ? d : d >> (64 - bits);
    }

    // ---------------------------------------------------------------- helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Pack(int r, int g, int b, int a) =>
        (uint)(r | (g << 8) | (b << 16) | (a << 24));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clamp(int n) => n < 0 ? 0 : n > 255 ? 255 : n;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClampHdr(int n) => n < 0 ? 0 : n > 0xfff ? 0xfff : n;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SelectColor(int v0, int v1, int weight) =>
        (((((v0 << 8 | v0) * (64 - weight)) + ((v1 << 8 | v1) * weight) + 32) >> 6) * 255 + 32768) / 65536;

    private static int SelectColorHdr(int v0, int v1, int weight)
    {
        int c = (((v0 << 4) * (64 - weight)) + ((v1 << 4) * weight) + 32) >> 6;
        c &= 0xffff;
        int m = c & 0x7ff;
        if (m < 512) m *= 3;
        else if (m < 1536) m = 4 * m - 512;
        else m = 5 * m - 2048;

        float f = HalfToFloat(((c >> 1) & 0x7c00) | (m >> 3));
        if (!float.IsFinite(f)) return 255;
        return Clamp((int)Roundf(f * 255f));
    }

    private static float Roundf(float x)
    {
        if (x >= 0f)
        {
            float f = MathF.Floor(x);
            return x - f >= 0.5f ? f + 1f : f;
        }
        else
        {
            float c = MathF.Ceiling(x);
            return c - x >= 0.5f ? c - 1f : c;
        }
    }

    private static float HalfToFloat(int h)
    {
        uint sign = (uint)(h & 0x8000) << 16;
        int exp = (h >> 10) & 0x1f;
        int mant = h & 0x3ff;
        uint bits;
        if (exp == 0)
        {
            if (mant == 0)
            {
                bits = sign;
            }
            else
            {
                exp = 113;
                while ((mant & 0x400) == 0)
                {
                    mant <<= 1;
                    exp--;
                }
                mant &= 0x3ff;
                bits = sign | ((uint)exp << 23) | ((uint)mant << 13);
            }
        }
        else if (exp == 31)
        {
            bits = sign | 0x7f800000u | ((uint)mant << 13);
        }
        else
        {
            bits = sign | ((uint)(exp + 112) << 23) | ((uint)mant << 13);
        }

        return BitConverter.UInt32BitsToSingle(bits);
    }

    private static byte F16ToU8(ReadOnlySpan<byte> b, int offset) =>
        F32ToU8(HalfToFloat(BinaryPrimitives.ReadUInt16LittleEndian(b[offset..])));

    private static byte F32ToU8(float f)
    {
        float c = Roundf(f * 255f);
        if (c < 0f) return 0;
        if (c > 255f) return 255;
        return (byte)c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetEndpoint(
        int[] e, int o,
        int r1, int g1, int b1, int a1,
        int r2, int g2, int b2, int a2)
    {
        e[o] = r1;
        e[o + 1] = g1;
        e[o + 2] = b1;
        e[o + 3] = a1;
        e[o + 4] = r2;
        e[o + 5] = g2;
        e[o + 6] = b2;
        e[o + 7] = a2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetEndpointClamp(
        int[] e, int o,
        int r1, int g1, int b1, int a1,
        int r2, int g2, int b2, int a2)
    {
        e[o] = Clamp(r1);
        e[o + 1] = Clamp(g1);
        e[o + 2] = Clamp(b1);
        e[o + 3] = Clamp(a1);
        e[o + 4] = Clamp(r2);
        e[o + 5] = Clamp(g2);
        e[o + 6] = Clamp(b2);
        e[o + 7] = Clamp(a2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetEndpointHdr(
        int[] e, int o,
        int r1, int g1, int b1, int a1,
        int r2, int g2, int b2, int a2)
    {
        e[o] = r1;
        e[o + 1] = g1;
        e[o + 2] = b1;
        e[o + 3] = a1;
        e[o + 4] = r2;
        e[o + 5] = g2;
        e[o + 6] = b2;
        e[o + 7] = a2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetEndpointHdrClamp(
        int[] e, int o,
        int r1, int g1, int b1, int a1,
        int r2, int g2, int b2, int a2)
    {
        e[o] = ClampHdr(r1);
        e[o + 1] = ClampHdr(g1);
        e[o + 2] = ClampHdr(b1);
        e[o + 3] = ClampHdr(a1);
        e[o + 4] = ClampHdr(r2);
        e[o + 5] = ClampHdr(g2);
        e[o + 6] = ClampHdr(b2);
        e[o + 7] = ClampHdr(a2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetEndpointBlue(
        int[] e, int o,
        int r1, int g1, int b1, int a1,
        int r2, int g2, int b2, int a2)
    {
        e[o] = (r1 + b1) >> 1;
        e[o + 1] = (g1 + b1) >> 1;
        e[o + 2] = b1;
        e[o + 3] = a1;
        e[o + 4] = (r2 + b2) >> 1;
        e[o + 5] = (g2 + b2) >> 1;
        e[o + 6] = b2;
        e[o + 7] = a2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetEndpointBlueClamp(
        int[] e, int o,
        int r1, int g1, int b1, int a1,
        int r2, int g2, int b2, int a2)
    {
        e[o] = Clamp((r1 + b1) >> 1);
        e[o + 1] = Clamp((g1 + b1) >> 1);
        e[o + 2] = Clamp(b1);
        e[o + 3] = Clamp(a1);
        e[o + 4] = Clamp((r2 + b2) >> 1);
        e[o + 5] = Clamp((g2 + b2) >> 1);
        e[o + 6] = Clamp(b2);
        e[o + 7] = Clamp(a2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BitTransferSigned(ref int a, ref int b)
    {
        b = (b >> 1) | (a & 0x80);
        a = (a >> 1) & 0x3f;
        if ((a & 0x20) != 0) a -= 0x40;
    }

    private const int WvCapacity = 320;

    private struct IntSeqData
    {
        public int Bits;
        public int Nonbits;
    }

    private sealed class BlockData
    {
        public int Bw;
        public int Bh;
        public int Width;
        public int Height;
        public int PartNum;
        public int DualPlane;
        public int PlaneSelector;
        public int WeightRange;
        public int WeightNum;
        public int WeightBits;
        public int CemRange;
        public int EndpointValueNum;

        public readonly int[] Cem = new int[4];
        public readonly int[] Endpoints = new int[32];
        public readonly int[] Weights = new int[288];
        public readonly int[] Partition = new int[144];
        public readonly int[] Seeds = new int[8];
        public readonly int[] C = new int[4];
        public readonly int[] Wv = new int[WvCapacity];
        public readonly int[] Ev = new int[32];
        public readonly IntSeqData[] WSeq = new IntSeqData[WvCapacity];
        public readonly IntSeqData[] ESeq = new IntSeqData[32];
        public readonly uint[] Pixels = new uint[144];
    }
}
