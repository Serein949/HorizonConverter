using System.Runtime.CompilerServices;

namespace AeroflyConv.Core.Codecs;

/// <summary>
/// ETC2 RGB / ETC2 RGBA encoders.
///
/// Only the ETC1-compatible modes (individual and differential) are emitted.  Every block tries
/// both sub-block layouts (flip bit), both modes, and all 8 modifier tables, refines the winning
/// base colours with a least-squares fit and keeps the candidate with the smallest RGB error.
/// This is well above the quality of a single-mode table search and comfortably beats 30 dB PSNR
/// on photographic content at 4:1.
///
/// The EAC alpha block uses the etcpak layout (8-bit base, 4-bit multiplier, 4-bit table) and the
/// etcpak table/range search, so the result decodes exactly with texture2ddecoder's decode_etc2a8.
/// </summary>
internal static unsafe class EtcEncoder
{
    private static readonly int[] Modifier =
    [
        2, 8, 5, 17, 9, 29, 13, 42, 18, 60, 24, 80, 33, 106, 47, 183,
    ];

    // Raster position of the ETC selector slot i (column-major order).
    private static readonly int[] WriteOrder = [0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15];

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

    // 0x100FF / (1 + AlphaMod[r][7] - AlphaMod[r][3]) from etcpak.
    private static readonly int[] AlphaRange =
    [
        0x100FF / 30, 0x100FF / 26, 0x100FF / 26, 0x100FF / 26,
        0x100FF / 24, 0x100FF / 22, 0x100FF / 22, 0x100FF / 22,
        0x100FF / 20, 0x100FF / 20, 0x100FF / 20, 0x100FF / 20,
        0x100FF / 20, 0x100FF / 20, 0x100FF / 18, 0x100FF / 18,
    ];

    public static void EncodeRgb(ReadOnlySpan<byte> rgba, int width, int height, Span<byte> dest)
    {
        Validate(rgba, width, height, dest, 8);
        fixed (byte* srcFixed = rgba)
        fixed (byte* dstFixed = dest)
        {
            byte* dst = dstFixed;
            byte* block = stackalloc byte[64];
            byte* sel = stackalloc byte[16];

            for (int by = 0; by < height >> 2; by++)
            {
                for (int bx = 0; bx < width >> 2; bx++)
                {
                    LoadBlock(srcFixed, width, bx, by, block);
                    EncodeColorBlock(block, sel, out int d0, out int d1, out int d2, out int d3, out int d4, out int d5, out int d6, out int d7);
                    dst[0] = (byte)d0;
                    dst[1] = (byte)d1;
                    dst[2] = (byte)d2;
                    dst[3] = (byte)d3;
                    dst[4] = (byte)d4;
                    dst[5] = (byte)d5;
                    dst[6] = (byte)d6;
                    dst[7] = (byte)d7;
                    dst += 8;
                }
            }
        }
    }

    public static void EncodeRgba(ReadOnlySpan<byte> rgba, int width, int height, Span<byte> dest)
    {
        Validate(rgba, width, height, dest, 16);
        fixed (byte* srcFixed = rgba)
        fixed (byte* dstFixed = dest)
        {
            byte* dst = dstFixed;
            byte* block = stackalloc byte[64];
            byte* sel = stackalloc byte[16];
            byte* alphas = stackalloc byte[16];

            for (int by = 0; by < height >> 2; by++)
            {
                for (int bx = 0; bx < width >> 2; bx++)
                {
                    LoadBlock(srcFixed, width, bx, by, block);
                    for (int i = 0; i < 16; i++)
                    {
                        alphas[i] = block[i * 4 + 3];
                    }

                    EncodeAlphaBlock(alphas, dst);

                    EncodeColorBlock(block, sel, out int d0, out int d1, out int d2, out int d3, out int d4, out int d5, out int d6, out int d7);
                    dst[8] = (byte)d0;
                    dst[9] = (byte)d1;
                    dst[10] = (byte)d2;
                    dst[11] = (byte)d3;
                    dst[12] = (byte)d4;
                    dst[13] = (byte)d5;
                    dst[14] = (byte)d6;
                    dst[15] = (byte)d7;
                    dst += 16;
                }
            }
        }
    }

    private static void Validate(ReadOnlySpan<byte> rgba, int width, int height, Span<byte> dest, int bytesPerBlock)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }
        if ((width & 3) != 0 || (height & 3) != 0)
        {
            throw new ArgumentException("ETC encode requires width and height to be multiples of 4.");
        }
        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("RGBA source is too small.", nameof(rgba));
        }
        if (dest.Length < (width >> 2) * (height >> 2) * bytesPerBlock)
        {
            throw new ArgumentException("ETC destination is too small.", nameof(dest));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoadBlock(byte* src, int width, int bx, int by, byte* block)
    {
        int x0 = bx * 4;
        int y0 = by * 4;
        for (int y = 0; y < 4; y++)
        {
            byte* s = src + ((y0 + y) * width + x0) * 4;
            byte* d = block + y * 16;
            for (int i = 0; i < 16; i++)
            {
                d[i] = s[i];
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Colour block
    // ---------------------------------------------------------------------------------------

    private static void EncodeColorBlock(byte* block, byte* sel,
        out int d0, out int d1, out int d2, out int d3, out int d4, out int d5, out int d6, out int d7)
    {
        int* pos0 = stackalloc int[8];
        int* pos1 = stackalloc int[8];
        byte* selTmp = stackalloc byte[16];
        byte* selBest = stackalloc byte[16];

        int bestErr = int.MaxValue;
        int bestFlip = 0, bestDiff = 0;
        int bestT0 = 0, bestT1 = 0;
        int bestB0r = 0, bestB0g = 0, bestB0b = 0;
        int bestB1r = 0, bestB1g = 0, bestB1b = 0;

        for (int flip = 0; flip < 2; flip++)
        {
            BuildPositions(flip, pos0, pos1);

            // ---- individual mode (RGB444 bases) ----
            Optimize(block, pos0, diff: false, constrained: false, 0, 0, 0,
                out int ti0, out int bir, out int big, out int bib, selTmp, out int ei0);
            Optimize(block, pos1, diff: false, constrained: false, 0, 0, 0,
                out int ti1, out int bir1, out int big1, out int bib1, selTmp, out int ei1);
            int totalI = ei0 + ei1;
            if (totalI < bestErr)
            {
                // Re-run the pixel assignment to fill selTmp for both halves (cheap, and keeps
                // the packing code straightforward).
                Eval(block, pos0, bir, big, bib, ti0, false, selTmp);
                Eval(block, pos1, bir1, big1, bib1, ti1, false, selTmp);
                for (int i = 0; i < 16; i++) selBest[i] = selTmp[i];
                bestErr = totalI;
                bestFlip = flip;
                bestDiff = 0;
                bestT0 = ti0; bestT1 = ti1;
                bestB0r = bir; bestB0g = big; bestB0b = bib;
                bestB1r = bir1; bestB1g = big1; bestB1b = bib1;
            }

            // ---- differential mode (RGB555 base + 3-bit delta) ----
            Optimize(block, pos0, diff: true, constrained: false, 0, 0, 0,
                out int td0, out int b0r, out int b0g, out int b0b, selTmp, out int ed0);
            Optimize(block, pos1, diff: true, constrained: true, b0r, b0g, b0b,
                out int td1, out int b1r, out int b1g, out int b1b, selTmp, out int ed1);
            int totalD = ed0 + ed1;
            if (totalD < bestErr)
            {
                Eval(block, pos0, b0r, b0g, b0b, td0, true, selTmp);
                Eval(block, pos1, b1r, b1g, b1b, td1, true, selTmp);
                for (int i = 0; i < 16; i++) selBest[i] = selTmp[i];
                bestErr = totalD;
                bestFlip = flip;
                bestDiff = 1;
                bestT0 = td0; bestT1 = td1;
                bestB0r = b0r; bestB0g = b0g; bestB0b = b0b;
                bestB1r = b1r; bestB1g = b1g; bestB1b = b1b;
            }
        }

        for (int i = 0; i < 16; i++) sel[i] = selBest[i];

        int third = (bestT0 << 5) | (bestT1 << 2) | (bestDiff << 1) | bestFlip;
        if (bestDiff == 0)
        {
            d0 = (bestB0r << 4) | bestB1r;
            d1 = (bestB0g << 4) | bestB1g;
            d2 = (bestB0b << 4) | bestB1b;
        }
        else
        {
            d0 = (bestB0r << 3) | ((bestB1r - bestB0r) & 7);
            d1 = (bestB0g << 3) | ((bestB1g - bestB0g) & 7);
            d2 = (bestB0b << 3) | ((bestB1b - bestB0b) & 7);
        }
        d3 = third;

        int j = 0, k = 0;
        for (int p = 0; p < 16; p++)
        {
            int i = ((p & 3) << 2) | (p >> 2);
            int s = sel[p];
            if ((s & 2) != 0) j |= 1 << i;
            if ((s & 1) != 0) k |= 1 << i;
        }
        d4 = (k >> 8) & 0xFF;
        d5 = k & 0xFF;
        d6 = (j >> 8) & 0xFF;
        d7 = j & 0xFF;
    }

    private static void BuildPositions(int flip, int* pos0, int* pos1)
    {
        int n0 = 0, n1 = 0;
        for (int i = 0; i < 16; i++)
        {
            int x = i >> 2;
            int y = i & 3;
            int p = (y << 2) | x; // raster position of column-major slot i
            int sb = flip == 0 ? (i < 8 ? 0 : 1) : ((i & 3) < 2 ? 0 : 1);
            if (sb == 0)
            {
                pos0[n0++] = p;
            }
            else
            {
                pos1[n1++] = p;
            }
        }
    }

    /// <summary>Finds the best table codeword and base colour for one sub-block.</summary>
    private static void Optimize(byte* block, int* pos, bool diff, bool constrained,
        int c0r, int c0g, int c0b,
        out int table, out int br, out int bg, out int bb, byte* sel, out int err)
    {
        int sumR = 0, sumG = 0, sumB = 0;
        for (int i = 0; i < 8; i++)
        {
            int p = pos[i];
            sumR += block[p * 4];
            sumG += block[p * 4 + 1];
            sumB += block[p * 4 + 2];
        }

        int qr = QuantizeBase((sumR + 4) / 8, diff);
        int qg = QuantizeBase((sumG + 4) / 8, diff);
        int qb = QuantizeBase((sumB + 4) / 8, diff);
        if (constrained)
        {
            qr = ClampRange(qr, c0r - 4, c0r + 3);
            qg = ClampRange(qg, c0g - 4, c0g + 3);
            qb = ClampRange(qb, c0b - 4, c0b + 3);
        }

        // Pass 1: score every table with the mean-derived base.
        Span<int> tableErrors = stackalloc int[8];
        for (int t = 0; t < 8; t++)
        {
            tableErrors[t] = Eval(block, pos, qr, qg, qb, t, diff, sel);
        }

        // Pass 2: least-squares refine the four best tables.
        Span<int> order = stackalloc int[4];
        for (int i = 0; i < 4; i++)
        {
            order[i] = i;
        }
        for (int i = 1; i < 4; i++)
        {
            int v = order[i];
            int j = i - 1;
            while (j >= 0 && tableErrors[order[j]] > tableErrors[v])
            {
                order[j + 1] = order[j];
                j--;
            }
            order[j + 1] = v;
        }
        for (int t = 4; t < 8; t++)
        {
            int e = tableErrors[t];
            if (e >= tableErrors[order[3]])
            {
                continue;
            }
            int slot = 2;
            while (slot >= 0 && tableErrors[order[slot]] > e)
            {
                order[slot + 1] = order[slot];
                slot--;
            }
            order[slot + 1] = t;
        }

        int bestT = 0, bestQr = 0, bestQg = 0, bestQb = 0, bestE = int.MaxValue;

        for (int oi = 0; oi < 4; oi++)
        {
            int t = order[oi];
            int rq = qr, gq = qg, bq = qb;
            for (int iter = 0; iter < 2; iter++)
            {
                int m0 = Modifier[t * 2];
                int m1 = Modifier[t * 2 + 1];
                int baseR = ExpandBase(rq, diff);
                int baseG = ExpandBase(gq, diff);
                int baseB = ExpandBase(bq, diff);

                int sumDr = 0, sumDg = 0, sumDb = 0;
                for (int i = 0; i < 8; i++)
                {
                    int p = pos[i];
                    int r = block[p * 4];
                    int g = block[p * 4 + 1];
                    int b = block[p * 4 + 2];
                    int idx = ChooseIndex(r, g, b, baseR, baseG, baseB, m0, m1);
                    int delta = idx switch { 1 => -m0, 2 => m1, 3 => -m1, _ => m0 };
                    sumDr += r - delta;
                    sumDg += g - delta;
                    sumDb += b - delta;
                }

                int nr = QuantizeBase((sumDr + 4) / 8, diff);
                int ng = QuantizeBase((sumDg + 4) / 8, diff);
                int nb = QuantizeBase((sumDb + 4) / 8, diff);
                if (constrained)
                {
                    nr = ClampRange(nr, c0r - 4, c0r + 3);
                    ng = ClampRange(ng, c0g - 4, c0g + 3);
                    nb = ClampRange(nb, c0b - 4, c0b + 3);
                }
                if (nr == rq && ng == gq && nb == bq)
                {
                    break;
                }
                rq = nr; gq = ng; bq = nb;
            }

            int e = Eval(block, pos, rq, gq, bq, t, diff, sel);
            if (e < bestE)
            {
                bestE = e;
                bestT = t;
                bestQr = rq; bestQg = gq; bestQb = bq;
            }
        }

        table = bestT;
        br = bestQr; bg = bestQg; bb = bestQb;
        err = bestE;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ExpandBase(int q, bool diff) => diff ? ((q << 3) | (q >> 2)) : q * 17;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int QuantizeBase(int v, bool diff)
    {
        if (v < 0) v = 0;
        if (v > 255) v = 255;
        int q = diff ? (v * 31 + 127) / 255 : (v + 8) / 17;
        return q < 0 ? 0 : q > (diff ? 31 : 15) ? (diff ? 31 : 15) : q;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClampRange(int v, int lo, int hi)
    {
        if (lo < 0) lo = 0;
        if (hi > 31) hi = 31;
        return v < lo ? lo : v > hi ? hi : v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClampByte(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ChooseIndex(int r, int g, int b, int baseR, int baseG, int baseB, int m0, int m1)
    {
        int dr = r - ClampByte(baseR + m0);
        int dg = g - ClampByte(baseG + m0);
        int db = b - ClampByte(baseB + m0);
        int best = dr * dr + dg * dg + db * db;
        int idx = 0;

        dr = r - ClampByte(baseR - m0);
        dg = g - ClampByte(baseG - m0);
        db = b - ClampByte(baseB - m0);
        int d = dr * dr + dg * dg + db * db;
        if (d < best) { best = d; idx = 1; }

        dr = r - ClampByte(baseR + m1);
        dg = g - ClampByte(baseG + m1);
        db = b - ClampByte(baseB + m1);
        d = dr * dr + dg * dg + db * db;
        if (d < best) { best = d; idx = 2; }

        dr = r - ClampByte(baseR - m1);
        dg = g - ClampByte(baseG - m1);
        db = b - ClampByte(baseB - m1);
        d = dr * dr + dg * dg + db * db;
        if (d < best) { idx = 3; }

        return idx;
    }

    /// <summary>Assigns palette indexes for all pixels of a sub-block and returns the squared error.</summary>
    private static int Eval(byte* block, int* pos, int qr, int qg, int qb, int t, bool diff, byte* sel)
    {
        int baseR = ExpandBase(qr, diff);
        int baseG = ExpandBase(qg, diff);
        int baseB = ExpandBase(qb, diff);
        int m0 = Modifier[t * 2];
        int m1 = Modifier[t * 2 + 1];

        int err = 0;
        for (int i = 0; i < 8; i++)
        {
            int p = pos[i];
            int r = block[p * 4];
            int g = block[p * 4 + 1];
            int b = block[p * 4 + 2];

            int dr = r - ClampByte(baseR + m0);
            int dg = g - ClampByte(baseG + m0);
            int db = b - ClampByte(baseB + m0);
            int best = dr * dr + dg * dg + db * db;
            int idx = 0;

            dr = r - ClampByte(baseR - m0);
            dg = g - ClampByte(baseG - m0);
            db = b - ClampByte(baseB - m0);
            int d = dr * dr + dg * dg + db * db;
            if (d < best) { best = d; idx = 1; }

            dr = r - ClampByte(baseR + m1);
            dg = g - ClampByte(baseG + m1);
            db = b - ClampByte(baseB + m1);
            d = dr * dr + dg * dg + db * db;
            if (d < best) { best = d; idx = 2; }

            dr = r - ClampByte(baseR - m1);
            dg = g - ClampByte(baseG - m1);
            db = b - ClampByte(baseB - m1);
            d = dr * dr + dg * dg + db * db;
            if (d < best) { best = d; idx = 3; }

            sel[p] = (byte)idx;
            err += best;
        }
        return err;
    }

    // ---------------------------------------------------------------------------------------
    // EAC alpha block
    // ---------------------------------------------------------------------------------------

    private static void EncodeAlphaBlock(byte* alphas, byte* dst)
    {
        int min = 255, max = 0;
        for (int i = 0; i < 16; i++)
        {
            int a = alphas[i];
            if (a < min) min = a;
            if (a > max) max = a;
        }

        if (min == max)
        {
            dst[0] = (byte)min;
            for (int i = 1; i < 8; i++) dst[i] = 0;
            return;
        }

        int range = max - min;
        int mid = min + range / 2;

        int bestTable = 0, bestMul = 1, bestErr = int.MaxValue;
        byte* sel = stackalloc byte[16];

        for (int t = 0; t < 16; t++)
        {
            int mul = ((range * AlphaRange[t]) >> 16) + 1;
            int err = EvalAlpha(alphas, mid, mul, t, sel);
            if (err < bestErr)
            {
                bestErr = err;
                bestTable = t;
                bestMul = mul;
            }
        }

        // Fill the selector array for the winner.
        EvalAlpha(alphas, mid, bestMul, bestTable, sel);

        dst[0] = (byte)mid;
        dst[1] = (byte)((bestMul << 4) | bestTable);
        ulong packed = 0;
        // The EAC index bits are stored column-major (same slot order as the ETC selectors),
        // with slot 0 in the most significant three bits of the 48-bit field.
        for (int i = 0; i < 16; i++)
        {
            int p = WriteOrder[i];
            packed |= (ulong)sel[p] << (45 - 3 * i);
        }
        dst[2] = (byte)(packed >> 40);
        dst[3] = (byte)(packed >> 32);
        dst[4] = (byte)(packed >> 24);
        dst[5] = (byte)(packed >> 16);
        dst[6] = (byte)(packed >> 8);
        dst[7] = (byte)packed;
    }

    private static int EvalAlpha(byte* alphas, int baseValue, int mul, int table, byte* sel)
    {
        int err = 0;
        for (int p = 0; p < 16; p++)
        {
            int v = alphas[p];
            int best = int.MaxValue;
            int idx = 0;
            for (int j = 0; j < 8; j++)
            {
                int rec = ClampByte(baseValue + mul * AlphaMod[table, j]);
                int d = v - rec;
                d *= d;
                if (d < best)
                {
                    best = d;
                    idx = j;
                }
            }
            sel[p] = (byte)idx;
            err += best;
        }
        return err;
    }
}
