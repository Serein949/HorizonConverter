using System.Runtime.CompilerServices;

namespace AeroflyConv.Core.Codecs;

/// <summary>
/// BC1/BC3 encoders.  Both work on 4x4 blocks and never allocate per block.
///
/// BC1 endpoint selection uses several candidates - the RGB bounding-box diagonal and a
/// principal-axis (least-squares line) fit - followed by one least-squares endpoint refinement
/// pass.  Every candidate is scored by actually building the 4-colour palette and measuring the
/// resulting RGB error, so the winner is the one that decodes best.
/// </summary>
internal static unsafe class BcEncoder
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Expand5(int v) => (v << 3) | (v >> 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Expand6(int v) => (v << 2) | (v >> 4);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Quant5(int v) => (v * 31 + 127) / 255;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Quant6(int v) => (v * 63 + 127) / 255;

    public static void EncodeBc1(ReadOnlySpan<byte> rgba, int width, int height, Span<byte> dest)
    {
        if ((width & 3) != 0 || (height & 3) != 0)
        {
            throw new ArgumentException("BC1 encode requires width and height to be multiples of 4.");
        }
        if (width <= 0 || height <= 0)
        {
            return;
        }

        int blocksW = width >> 2;
        int blocksH = height >> 2;
        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("RGBA source is too small.", nameof(rgba));
        }
        if (dest.Length < blocksW * blocksH * 8)
        {
            throw new ArgumentException("BC1 destination is too small.", nameof(dest));
        }

        fixed (byte* srcFixed = rgba)
        fixed (byte* dstFixed = dest)
        {
            byte* dst = dstFixed;
            byte* block = stackalloc byte[64];

            for (int by = 0; by < blocksH; by++)
            {
                for (int bx = 0; bx < blocksW; bx++)
                {
                    LoadBlock(srcFixed, width, bx, by, block);
                    EncodeColorBlock(block, out int q0, out int q1, out uint indices);

                    dst[0] = (byte)q0;
                    dst[1] = (byte)(q0 >> 8);
                    dst[2] = (byte)q1;
                    dst[3] = (byte)(q1 >> 8);
                    dst[4] = (byte)indices;
                    dst[5] = (byte)(indices >> 8);
                    dst[6] = (byte)(indices >> 16);
                    dst[7] = (byte)(indices >> 24);
                    dst += 8;
                }
            }
        }
    }

    public static void EncodeBc3(ReadOnlySpan<byte> rgba, int width, int height, Span<byte> dest)
    {
        if ((width & 3) != 0 || (height & 3) != 0)
        {
            throw new ArgumentException("BC3 encode requires width and height to be multiples of 4.");
        }
        if (width <= 0 || height <= 0)
        {
            return;
        }

        int blocksW = width >> 2;
        int blocksH = height >> 2;
        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("RGBA source is too small.", nameof(rgba));
        }
        if (dest.Length < blocksW * blocksH * 16)
        {
            throw new ArgumentException("BC3 destination is too small.", nameof(dest));
        }

        fixed (byte* srcFixed = rgba)
        fixed (byte* dstFixed = dest)
        {
            byte* dst = dstFixed;
            byte* block = stackalloc byte[64];
            byte* alphas = stackalloc byte[16];

            for (int by = 0; by < blocksH; by++)
            {
                for (int bx = 0; bx < blocksW; bx++)
                {
                    LoadBlock(srcFixed, width, bx, by, block);
                    for (int i = 0; i < 16; i++)
                    {
                        alphas[i] = block[i * 4 + 3];
                    }

                    EncodeAlphaBlock(alphas, dst);
                    EncodeColorBlock(block, out int q0, out int q1, out uint indices);
                    dst[8] = (byte)q0;
                    dst[9] = (byte)(q0 >> 8);
                    dst[10] = (byte)q1;
                    dst[11] = (byte)(q1 >> 8);
                    dst[12] = (byte)indices;
                    dst[13] = (byte)(indices >> 8);
                    dst[14] = (byte)(indices >> 16);
                    dst[15] = (byte)(indices >> 24);
                    dst += 16;
                }
            }
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
            d[0] = s[0];
            d[1] = s[1];
            d[2] = s[2];
            d[3] = s[3];
            d[4] = s[4];
            d[5] = s[5];
            d[6] = s[6];
            d[7] = s[7];
            d[8] = s[8];
            d[9] = s[9];
            d[10] = s[10];
            d[11] = s[11];
            d[12] = s[12];
            d[13] = s[13];
            d[14] = s[14];
            d[15] = s[15];
        }
    }

    // ---------------------------------------------------------------------------------------
    // Colour block
    // ---------------------------------------------------------------------------------------

    private static void EncodeColorBlock(byte* block, out int outQ0, out int outQ1, out uint outIndices)
    {
        int minR = 255, minG = 255, minB = 255;
        int maxR = 0, maxG = 0, maxB = 0;
        int sumR = 0, sumG = 0, sumB = 0;

        for (int i = 0; i < 16; i++)
        {
            int r = block[i * 4];
            int g = block[i * 4 + 1];
            int b = block[i * 4 + 2];
            if (r < minR) minR = r;
            if (g < minG) minG = g;
            if (b < minB) minB = b;
            if (r > maxR) maxR = r;
            if (g > maxG) maxG = g;
            if (b > maxB) maxB = b;
            sumR += r;
            sumG += g;
            sumB += b;
        }

        // Candidate 1: bounding-box diagonal.
        int qa0 = Quant565(minR, minG, minB);
        int qa1 = Quant565(maxR, maxG, maxB);

        // Candidate 2: endpoints along the principal axis of the RGB covariance.
        bool haveAxis = TryPrincipalAxis(block, sumR, sumG, sumB,
            out int axisR0, out int axisG0, out int axisB0,
            out int axisR1, out int axisG1, out int axisB1);

        Order(ref qa0, ref qa1);
        uint ia = Evaluate(block, qa0, qa1, out int errA);

        int qb0 = 0, qb1 = 0;
        uint ib = 0;
        int errB = int.MaxValue;
        if (haveAxis)
        {
            qb0 = Quant565(axisR0, axisG0, axisB0);
            qb1 = Quant565(axisR1, axisG1, axisB1);
            Order(ref qb0, ref qb1);
            ib = Evaluate(block, qb0, qb1, out errB);
        }

        int q0, q1;
        uint indices;
        if (errB < errA)
        {
            q0 = qb0; q1 = qb1; indices = ib;
        }
        else
        {
            q0 = qa0; q1 = qa1; indices = ia;
        }

        // Least-squares endpoint refit using the currently assigned indices.
        RefitEndpoints(block, indices, q0, q1, out int rr0, out int rg0, out int rb0,
            out int rr1, out int rg1, out int rb1);
        int qr0 = Quant565(rr0, rg0, rb0);
        int qr1 = Quant565(rr1, rg1, rb1);
        Order(ref qr0, ref qr1);
        uint ir = Evaluate(block, qr0, qr1, out int errR);
        if (errR < (errB < errA ? errB : errA))
        {
            q0 = qr0; q1 = qr1; indices = ir;
        }

        // Guarantee a valid 4-colour block.  Pure black blocks fall back to the 3-colour mode,
        // which still reproduces black exactly through palette entry 0.
        if (q0 == q1)
        {
            if (q0 > 0)
            {
                q1--;
            }
        }

        outQ0 = q0;
        outQ1 = q1;
        outIndices = indices;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Quant565(int r, int g, int b)
        => (Quant5(r) << 11) | (Quant6(g) << 5) | Quant5(b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Order(ref int q0, ref int q1)
    {
        if (q0 < q1)
        {
            int t = q0; q0 = q1; q1 = t;
        }
    }

    private static uint Evaluate(byte* block, int q0, int q1, out int error)
    {
        int r0 = Expand5((q0 >> 11) & 31);
        int g0 = Expand6((q0 >> 5) & 63);
        int b0 = Expand5(q0 & 31);
        int r1 = Expand5((q1 >> 11) & 31);
        int g1 = Expand6((q1 >> 5) & 63);
        int b1 = Expand5(q1 & 31);

        int r2, g2, b2, r3, g3, b3;
        if (q0 > q1)
        {
            r2 = (2 * r0 + r1) / 3;
            g2 = (2 * g0 + g1) / 3;
            b2 = (2 * b0 + b1) / 3;
            r3 = (r0 + 2 * r1) / 3;
            g3 = (g0 + 2 * g1) / 3;
            b3 = (b0 + 2 * b1) / 3;
        }
        else
        {
            r2 = (r0 + r1) / 2;
            g2 = (g0 + g1) / 2;
            b2 = (b0 + b1) / 2;
            r3 = 0; g3 = 0; b3 = 0;
        }

        uint bits = 0;
        int err = 0;
        for (int i = 0; i < 16; i++)
        {
            int r = block[i * 4];
            int g = block[i * 4 + 1];
            int b = block[i * 4 + 2];

            int dr = r - r0, dg = g - g0, db = b - b0;
            int best = dr * dr + dg * dg + db * db;
            int idx = 0;

            dr = r - r1; dg = g - g1; db = b - b1;
            int d = dr * dr + dg * dg + db * db;
            if (d < best) { best = d; idx = 1; }

            dr = r - r2; dg = g - g2; db = b - b2;
            d = dr * dr + dg * dg + db * db;
            if (d < best) { best = d; idx = 2; }

            dr = r - r3; dg = g - g3; db = b - b3;
            d = dr * dr + dg * dg + db * db;
            if (d < best) { best = d; idx = 3; }

            err += best;
            bits |= (uint)idx << (2 * i);
        }

        error = err;
        return bits;
    }

    private static bool TryPrincipalAxis(byte* block, int sumR, int sumG, int sumB,
        out int r0, out int g0, out int b0, out int r1, out int g1, out int b1)
    {
        double mr = sumR / 16.0;
        double mg = sumG / 16.0;
        double mb = sumB / 16.0;

        double crr = 0, crg = 0, crb = 0, cgg = 0, cgb = 0, cbb = 0;
        for (int i = 0; i < 16; i++)
        {
            double dr = block[i * 4] - mr;
            double dg = block[i * 4 + 1] - mg;
            double db = block[i * 4 + 2] - mb;
            crr += dr * dr;
            crg += dr * dg;
            crb += dr * db;
            cgg += dg * dg;
            cgb += dg * db;
            cbb += db * db;
        }

        double vx = 1, vy = 1, vz = 1;
        for (int it = 0; it < 4; it++)
        {
            double nx = crr * vx + crg * vy + crb * vz;
            double ny = crg * vx + cgg * vy + cgb * vz;
            double nz = crb * vx + cgb * vy + cbb * vz;
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-9)
            {
                r0 = g0 = b0 = r1 = g1 = b1 = 0;
                return false;
            }
            vx = nx / len; vy = ny / len; vz = nz / len;
        }

        double tmin = double.MaxValue, tmax = double.MinValue;
        for (int i = 0; i < 16; i++)
        {
            double t = (block[i * 4] - mr) * vx + (block[i * 4 + 1] - mg) * vy + (block[i * 4 + 2] - mb) * vz;
            if (t < tmin) tmin = t;
            if (t > tmax) tmax = t;
        }
        if (tmax - tmin < 1.0)
        {
            r0 = g0 = b0 = r1 = g1 = b1 = 0;
            return false;
        }

        r0 = ClampByte((int)Math.Round(mr + tmin * vx));
        g0 = ClampByte((int)Math.Round(mg + tmin * vy));
        b0 = ClampByte((int)Math.Round(mb + tmin * vz));
        r1 = ClampByte((int)Math.Round(mr + tmax * vx));
        g1 = ClampByte((int)Math.Round(mg + tmax * vy));
        b1 = ClampByte((int)Math.Round(mb + tmax * vz));
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClampByte(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    private static void RefitEndpoints(byte* block, uint indices, int q0, int q1,
        out int r0, out int g0, out int b0, out int r1, out int g1, out int b1)
    {
        // Endpoints currently in use (expanded), with the palette weights of each index.
        r0 = Expand5((q0 >> 11) & 31);
        g0 = Expand6((q0 >> 5) & 63);
        b0 = Expand5(q0 & 31);
        r1 = Expand5((q1 >> 11) & 31);
        g1 = Expand6((q1 >> 5) & 63);
        b1 = Expand5(q1 & 31);

        if (q0 <= q1)
        {
            return;
        }

        // Palette weights: colour = (wa*p0 + wb*p1) / 3.
        double saa = 0, sab = 0, sbb = 0;
        double savR = 0, savG = 0, savB = 0;
        double sbvR = 0, sbvG = 0, sbvB = 0;

        for (int i = 0; i < 16; i++)
        {
            int k = (int)((indices >> (2 * i)) & 3);
            double a = PaletteWa[k];
            double b = PaletteWb[k];
            double r = block[i * 4];
            double g = block[i * 4 + 1];
            double bl = block[i * 4 + 2];
            saa += a * a;
            sab += a * b;
            sbb += b * b;
            savR += a * r; sbvR += b * r;
            savG += a * g; sbvG += b * g;
            savB += a * bl; sbvB += b * bl;
        }

        double det = saa * sbb - sab * sab;
        if (Math.Abs(det) < 1e-9)
        {
            return;
        }

        double inv = 1.0 / det;
        double p0r = (savR * sbb - sbvR * sab) * inv * 3.0;
        double p0g = (savG * sbb - sbvG * sab) * inv * 3.0;
        double p0b = (savB * sbb - sbvB * sab) * inv * 3.0;
        double p1r = (sbvR * saa - savR * sab) * inv * 3.0;
        double p1g = (sbvG * saa - savG * sab) * inv * 3.0;
        double p1b = (sbvB * saa - savB * sab) * inv * 3.0;

        r0 = ClampByte((int)Math.Round(p0r));
        g0 = ClampByte((int)Math.Round(p0g));
        b0 = ClampByte((int)Math.Round(p0b));
        r1 = ClampByte((int)Math.Round(p1r));
        g1 = ClampByte((int)Math.Round(p1g));
        b1 = ClampByte((int)Math.Round(p1b));
    }

    // ---------------------------------------------------------------------------------------
    // BC3 alpha block
    // ---------------------------------------------------------------------------------------

    private static readonly int[] PaletteWa = [3, 0, 2, 1];
    private static readonly int[] PaletteWb = [0, 3, 1, 2];
    private static readonly int[] AlphaWa = [7, 0, 6, 5, 4, 3, 2, 1];
    private static readonly int[] AlphaWb = [0, 7, 1, 2, 3, 4, 5, 6];

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
            // Constant alpha: a0 == a1 selects the 6-value palette and index 0 is a0 exactly.
            dst[0] = (byte)min;
            dst[1] = (byte)min;
            for (int i = 2; i < 8; i++)
            {
                dst[i] = 0;
            }
            return;
        }

        int bestA0 = max, bestA1 = min;
        ulong bestBits = 0;
        int bestErr = int.MaxValue;

        // A small search around the (max,min) endpoint pair.  Keeping a0 > a1 selects the
        // 8-value palette, which is what the reference encoder uses for varying alpha.
        int range = max - min;
        int wide = Math.Max(1, range / 8);
        for (int k0 = 0; k0 < 4; k0++)
        {
            int off0 = k0 switch { 0 => 0, 1 => -1, 2 => -2, _ => -wide };
            int a0 = max + off0;
            if (a0 < 1 || a0 > 255)
            {
                continue;
            }
            for (int k1 = 0; k1 < 4; k1++)
            {
                int off1 = k1 switch { 0 => 0, 1 => 1, 2 => 2, _ => wide };
                int a1 = min + off1;
                if (a1 < 0 || a1 >= a0)
                {
                    continue;
                }
                ulong bits = EvaluateAlpha(alphas, (byte)a0, (byte)a1, out int err);
                if (err < bestErr)
                {
                    bestErr = err;
                    bestA0 = a0;
                    bestA1 = a1;
                    bestBits = bits;
                }
            }
        }

        // Least-squares refit of the endpoints for the winning palette assignment.
        RefitAlpha(alphas, bestBits, bestA0, bestA1, out int ra0, out int ra1);
        if (ra0 > ra1 && ra0 >= 0 && ra0 <= 255 && ra1 >= 0 && ra1 <= 255)
        {
            ulong bits = EvaluateAlpha(alphas, (byte)ra0, (byte)ra1, out int err);
            if (err < bestErr)
            {
                bestErr = err;
                bestA0 = ra0;
                bestA1 = ra1;
                bestBits = bits;
            }
        }

        dst[0] = (byte)bestA0;
        dst[1] = (byte)bestA1;
        dst[2] = (byte)bestBits;
        dst[3] = (byte)(bestBits >> 8);
        dst[4] = (byte)(bestBits >> 16);
        dst[5] = (byte)(bestBits >> 24);
        dst[6] = (byte)(bestBits >> 32);
        dst[7] = (byte)(bestBits >> 40);
    }

    private static ulong EvaluateAlpha(byte* alphas, byte a0, byte a1, out int error)
    {
        Span<byte> pal = stackalloc byte[8];
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

        ulong bits = 0;
        int err = 0;
        for (int i = 0; i < 16; i++)
        {
            int v = alphas[i];
            int best = v - pal[0];
            best *= best;
            int idx = 0;
            for (int k = 1; k < 8; k++)
            {
                int d = v - pal[k];
                d *= d;
                if (d < best)
                {
                    best = d;
                    idx = k;
                }
            }
            err += best;
            bits |= (ulong)idx << (3 * i);
        }
        error = err;
        return bits;
    }

    private static void RefitAlpha(byte* alphas, ulong bits, int a0, int a1, out int ra0, out int ra1)
    {
        ra0 = a0;
        ra1 = a1;
        if (a0 <= a1)
        {
            return;
        }

        // Palette value = (wa*a0 + wb*a1) / 7; solve the 2x2 normal equations for a0 and a1.
        double saa = 0, sab = 0, sbb = 0, sav = 0, sbv = 0;
        for (int i = 0; i < 16; i++)
        {
            int k = (int)((bits >> (3 * i)) & 7);
            double a = AlphaWa[k];
            double b = AlphaWb[k];
            double v = alphas[i];
            saa += a * a;
            sab += a * b;
            sbb += b * b;
            sav += a * v;
            sbv += b * v;
        }

        double det = saa * sbb - sab * sab;
        if (Math.Abs(det) < 1e-9)
        {
            return;
        }

        double inv = 1.0 / det;
        ra0 = ClampByte((int)Math.Round((sav * sbb - sbv * sab) * inv * 7.0));
        ra1 = ClampByte((int)Math.Round((sbv * saa - sav * sab) * inv * 7.0));
    }
}

