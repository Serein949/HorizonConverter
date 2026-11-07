using System;
using System.Threading.Tasks;

namespace AeroflyConv.Core.Codecs;

/// <summary>
/// Minimal, fully managed ASTC LDR encoder.
///
/// The encoder deliberately emits a narrow but completely legal subset of the
/// ASTC block format so that <see cref="AstcCodec"/> (and any conforming ASTC
/// decoder) can decode the result:
///
///  * one partition, dual-plane disabled;
///  * void-extent blocks for exactly constant blocks;
///  * otherwise CEM 8 (RGB, six 8-bit endpoints) or CEM 12 (RGBA, eight 8-bit
///    endpoints), always with endpoint range 0 (no trits/quints) and a pure-bit
///    weight sequence (2..5 bits per weight);
///  * the weight grid is chosen from a small fixed candidate list and the best
///    candidate for each block is selected by exact squared error.
///
/// The CEM 8/12 decoder in <see cref="AstcCodec"/> swaps endpoints when the
/// first endpoint has a larger RGB sum ("blue-contour" packing). The encoder
/// therefore keeps endpoint 0 at or below endpoint 1 in RGB sum, which avoids
/// that lossy special case.
/// </summary>
public static class AstcEncoder
{
    private const int MaxPixels = 144;      // 12 x 12
    private const int MaxGridPoints = 144;
    private const int MaxConfigs = 16;

    // How many configurations survive the cheap pre-pass and get the full
    // endpoint/weight optimization. Encoding every configuration fully costs
    // several times more for a PSNR difference well below 0.1 dB.
    private const int RefineConfigCount = 8;

    // Weight search window (in quantized level indices) around the projected
    // optimum. Full enumeration over 32 levels costs ~6x more for ~0.05 dB.
    private const int DescentRadius = 2;

    private static readonly int[] Levels1 = { 0, 64 };

    private static readonly int[] Levels2 = { 0, 21, 43, 64 };

    private static readonly int[] Levels3 = { 0, 9, 18, 27, 37, 46, 55, 64 };

    private static readonly int[] Levels4 =
    {
        0, 4, 8, 12, 17, 21, 25, 29, 35, 39, 43, 47, 52, 56, 60, 64,
    };

    private static readonly int[] Levels5 =
    {
        0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30,
        34, 36, 38, 40, 42, 44, 46, 48, 50, 52, 54, 56, 58, 60, 62, 64,
    };

    // (width, height) weight grids worth trying, best candidates first. A grid
    // is only used when the bit budget allows it.
    private static readonly (byte W, byte H)[] CandidateGrids =
    {
        (5, 5), (4, 4), (4, 3), (3, 4), (3, 3),
        (6, 5), (5, 6), (5, 4), (4, 5),
        (7, 4), (4, 7), (6, 4), (4, 6),
        (9, 3), (3, 9), (10, 3), (3, 10),
        (8, 5), (5, 8), (7, 5), (5, 7), (8, 4), (4, 8),
        (2, 2), (2, 3), (3, 2), (2, 5), (5, 2), (2, 6), (6, 2),
    };

    private struct Config
    {
        public byte Gw;
        public byte Gh;
        public byte Bits;
    }

    /// <summary>True when <see cref="Encode"/> can handle the given block size.</summary>
    public static bool SupportsBlockSize(int blockWidth, int blockHeight) =>
        blockWidth is >= 4 and <= 12 && blockHeight is >= 4 and <= 12;

    /// <summary>
    /// Encodes a top-down, row-major RGBA image into ASTC LDR blocks.
    /// </summary>
    /// <param name="rgba">Source pixels, <paramref name="width"/>*<paramref name="height"/>*4 bytes.</param>
    /// <param name="width">Source width in pixels.</param>
    /// <param name="height">Source height in pixels.</param>
    /// <param name="blockWidth">ASTC block width (4..12).</param>
    /// <param name="blockHeight">ASTC block height (4..12).</param>
    /// <param name="output">
    /// Destination, at least ceil(width/blockWidth)*ceil(height/blockHeight)*16 bytes,
    /// row-major blocks.
    /// </param>
    public static void Encode(
        ReadOnlySpan<byte> rgba,
        int width,
        int height,
        int blockWidth,
        int blockHeight,
        Span<byte> output)
    {
        if (!SupportsBlockSize(blockWidth, blockHeight))
            throw new ArgumentOutOfRangeException(
                nameof(blockWidth),
                "ASTC block size must be between 4x4 and 12x12.");
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

        int pixelCount = width * height;
        if (rgba.Length < pixelCount * 4)
            throw new ArgumentException("Source RGBA buffer is too small.", nameof(rgba));

        int blocksX = (width + blockWidth - 1) / blockWidth;
        int blocksY = (height + blockHeight - 1) / blockHeight;
        int required = blocksX * blocksY * 16;
        if (output.Length < required)
            throw new ArgumentException("Destination buffer is too small.", nameof(output));

        bool hasAlpha = false;
        for (int i = 3; i < pixelCount * 4; i += 4)
        {
            if (rgba[i] != 255)
            {
                hasAlpha = true;
                break;
            }
        }

        int channels = hasAlpha ? 4 : 3;
        var configs = new Config[MaxConfigs];
        int configCount = BuildConfigs(blockWidth, blockHeight, channels, configs);

        // Blocks are independent, so large images are encoded one row of blocks
        // at a time in parallel. The result is bit-identical to the serial path
        // (no cross-block state); the fan-out only saves wall-clock time.
        if (blocksX * blocksY >= 64)
        {
            unsafe
            {
                fixed (byte* source = rgba)
                fixed (byte* destination = output)
                {
                    byte* src = source;
                    byte* dst = destination;
                    int sourceLength = pixelCount * 4;
                    Parallel.For(0, blocksY, by =>
                    {
                        var blockRows = new ReadOnlySpan<byte>(src, sourceLength);
                        Span<byte> pixels = stackalloc byte[MaxPixels * 4];
                        for (int bx = 0; bx < blocksX; bx++)
                        {
                            EncodeBlock(
                                blockRows, width, height, blockWidth, blockHeight, channels,
                                configs, configCount, bx, by,
                                new Span<byte>(dst + (by * blocksX + bx) * 16, 16),
                                pixels);
                        }
                    });
                }
            }

            return;
        }

        {
            Span<byte> pixels = stackalloc byte[MaxPixels * 4];
            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    EncodeBlock(
                        rgba, width, height, blockWidth, blockHeight, channels,
                        configs, configCount, bx, by,
                        output.Slice((by * blocksX + bx) * 16, 16),
                        pixels);
                }
            }
        }
    }

    private static void EncodeBlock(
        ReadOnlySpan<byte> rgba,
        int width,
        int height,
        int blockWidth,
        int blockHeight,
        int channels,
        Config[] configs,
        int configCount,
        int bx,
        int by,
        Span<byte> destination,
        Span<byte> pixels)
    {
        int sourceX = bx * blockWidth;
        int sourceY = by * blockHeight;

        // Gather the block, clamping at the right/bottom edge.
        bool constant = true;
        byte firstR = 0, firstG = 0, firstB = 0, firstA = 0;
        for (int y = 0; y < blockHeight; y++)
        {
            int sy = Math.Min(sourceY + y, height - 1) * width;
            for (int x = 0; x < blockWidth; x++)
            {
                int sx = Math.Min(sourceX + x, width - 1);
                int si = (sy + sx) * 4;
                int di = (y * blockWidth + x) * 4;
                byte r = rgba[si];
                byte g = rgba[si + 1];
                byte b = rgba[si + 2];
                byte a = rgba[si + 3];
                pixels[di] = r;
                pixels[di + 1] = g;
                pixels[di + 2] = b;
                pixels[di + 3] = a;
                if (x == 0 && y == 0)
                {
                    firstR = r;
                    firstG = g;
                    firstB = b;
                    firstA = a;
                }
                else if (constant && (r != firstR || g != firstG || b != firstB || a != firstA))
                {
                    constant = false;
                }
            }
        }

        Span<byte> block = stackalloc byte[16];
        Span<byte> bestBlock = stackalloc byte[16];

        if (constant)
        {
            EmitVoidExtent(block, firstR, firstG, firstB, firstA);
        }
        else
        {
            // Two-phase search: score every configuration with a cheap
            // projection pass, then run the expensive endpoint/weight
            // optimization only on the most promising ones.
            Span<long> scores = stackalloc long[MaxConfigs];
            for (int c = 0; c < configCount; c++)
                scores[c] = EvaluateConfig(pixels, blockWidth, blockHeight, channels, configs[c]);

            int refineCount = Math.Min(RefineConfigCount, configCount);
            long bestError = long.MaxValue;
            for (int pick = 0; pick < refineCount; pick++)
            {
                int bestIndex = -1;
                for (int c = 0; c < configCount; c++)
                {
                    if (scores[c] < 0)
                        continue;
                    if (bestIndex < 0 || scores[c] < scores[bestIndex])
                        bestIndex = c;
                }

                if (bestIndex < 0)
                    break;

                long error = EncodeConfig(
                    pixels,
                    blockWidth,
                    blockHeight,
                    channels,
                    configs[bestIndex],
                    block);
                if (error < bestError)
                {
                    bestError = error;
                    block.CopyTo(bestBlock);
                }

                scores[bestIndex] = -1;
            }

            if (bestError == long.MaxValue)
            {
                // Cannot happen (a 2x2/5-bit configuration always fits),
                // but never emit something invalid.
                EmitVoidExtent(block, firstR, firstG, firstB, firstA);
            }
            else
            {
                bestBlock.CopyTo(block);
            }
        }

        block.CopyTo(destination);
    }

    private static int BuildConfigs(int blockWidth, int blockHeight, int channels, Span<Config> destination)
    {
        int endpointBits = channels == 4 ? 64 : 48;
        int count = 0;

        foreach ((byte gw0, byte gh0) in CandidateGrids)
        {
            if (count >= MaxConfigs)
                break;

            int gw = gw0;
            int gh = gh0;

            // Very large grids on small blocks are allowed by ASTC but waste
            // weight bits; keep them one step away from the block at most.
            if (gw > blockWidth + 1 || gh > blockHeight + 1)
                continue;

            for (int bits = 5; bits >= 1; bits--)
            {
                int weightBits = gw * gh * bits;
                if (17 + weightBits + endpointBits > 128)
                    continue;
                if (!TryGrid(gw, gh, RangeForBits(bits), out _, out _))
                    continue;

                destination[count++] = new Config
                {
                    Gw = (byte)gw,
                    Gh = (byte)gh,
                    Bits = (byte)bits,
                };
                break;
            }
        }

        return count;
    }

    private static int RangeForBits(int bits) => bits switch
    {
        1 => 2,
        2 => 4,
        3 => 7,
        4 => 12,
        _ => 15,
    };

    private static ReadOnlySpan<int> LevelsForBits(int bits) => bits switch
    {
        1 => Levels1,
        2 => Levels2,
        3 => Levels3,
        4 => Levels4,
        _ => Levels5,
    };

    /// <summary>
    /// Encodes one block into <paramref name="outBlock"/> and returns the exact
    /// squared error the decoder will observe (RGB, plus alpha in RGBA mode).
    /// </summary>
    private static long EncodeConfig(
        ReadOnlySpan<byte> pixels,
        int blockWidth,
        int blockHeight,
        int channels,
        in Config config,
        Span<byte> outBlock)
    {
        int pixelCount = blockWidth * blockHeight;
        int gw = config.Gw;
        int gh = config.Gh;
        int bits = config.Bits;
        int gridCount = gw * gh;
        ReadOnlySpan<int> levels = LevelsForBits(bits);

        int ds = (1024 + blockWidth / 2) / (blockWidth - 1);
        int dt = (1024 + blockHeight / 2) / (blockHeight - 1);

        // Pixel -> weight-grid interpolation.
        Span<byte> idx0 = stackalloc byte[MaxPixels];
        Span<byte> idx1 = stackalloc byte[MaxPixels];
        Span<byte> idx2 = stackalloc byte[MaxPixels];
        Span<byte> idx3 = stackalloc byte[MaxPixels];
        Span<byte> coef0 = stackalloc byte[MaxPixels];
        Span<byte> coef1 = stackalloc byte[MaxPixels];
        Span<byte> coef2 = stackalloc byte[MaxPixels];
        Span<byte> coef3 = stackalloc byte[MaxPixels];

        BuildTables(
            blockWidth, blockHeight, gw, gh, ds, dt,
            idx0, idx1, idx2, idx3, coef0, coef1, coef2, coef3);

        // Affected-pixel lists, so coordinate descent only touches pixels that
        // actually use the grid point being changed.
        Span<int> affStart = stackalloc int[MaxGridPoints + 1];
        Span<int> affPixels = stackalloc int[MaxPixels * 4];
        Span<int> cursor = stackalloc int[MaxGridPoints];
        for (int i = 0; i <= gridCount; i++)
            affStart[i] = 0;
        for (int p = 0; p < pixelCount; p++)
        {
            if (coef0[p] != 0) affStart[idx0[p] + 1]++;
            if (coef1[p] != 0) affStart[idx1[p] + 1]++;
            if (coef2[p] != 0) affStart[idx2[p] + 1]++;
            if (coef3[p] != 0) affStart[idx3[p] + 1]++;
        }
        for (int g = 0; g < gridCount; g++)
            affStart[g + 1] += affStart[g];
        for (int g = 0; g < gridCount; g++)
            cursor[g] = affStart[g];
        for (int p = 0; p < pixelCount; p++)
        {
            if (coef0[p] != 0) affPixels[cursor[idx0[p]]++] = p;
            if (coef1[p] != 0) affPixels[cursor[idx1[p]]++] = p;
            if (coef2[p] != 0) affPixels[cursor[idx2[p]]++] = p;
            if (coef3[p] != 0) affPixels[cursor[idx3[p]]++] = p;
        }

        Span<int> grid = stackalloc int[MaxGridPoints + 16];
        Span<int> weights = stackalloc int[MaxPixels];
        Span<int> e0 = stackalloc int[4];
        Span<int> e1 = stackalloc int[4];

        InitEndpoints(pixels, pixelCount, channels, e0, e1);
        ProjectGrid(
            pixels, pixelCount, channels, e0, e1, idx0, idx1, idx2, idx3,
            coef0, coef1, coef2, coef3, levels, grid);

        OptimizeWeights(
            pixels, pixelCount, channels, e0, e1, grid, weights, levels,
            idx0, idx1, idx2, idx3, coef0, coef1, coef2, coef3,
            affStart, affPixels, gridCount, passes: 2);

        for (int iteration = 0; iteration < 2; iteration++)
        {
            FitEndpoints(pixels, pixelCount, channels, weights, e0, e1);
            ProjectGrid(
                pixels, pixelCount, channels, e0, e1, idx0, idx1, idx2, idx3,
                coef0, coef1, coef2, coef3, levels, grid);
            OptimizeWeights(
                pixels, pixelCount, channels, e0, e1, grid, weights, levels,
                idx0, idx1, idx2, idx3, coef0, coef1, coef2, coef3,
                affStart, affPixels, gridCount, passes: 2);
        }

        // A final endpoint pass makes sure the emitted pair is the best one for
        // the current weights and still respects the endpoint ordering rule.
        bool endpointsChanged = FitEndpoints(pixels, pixelCount, channels, weights, e0, e1);
        if (endpointsChanged)
        {
            ProjectGrid(
                pixels, pixelCount, channels, e0, e1, idx0, idx1, idx2, idx3,
                coef0, coef1, coef2, coef3, levels, grid);
            OptimizeWeights(
                pixels, pixelCount, channels, e0, e1, grid, weights, levels,
                idx0, idx1, idx2, idx3, coef0, coef1, coef2, coef3,
                affStart, affPixels, gridCount, passes: 2);
        }

        EmitBlock(outBlock, channels, config, grid, levels, e0, e1);
        return ComputeError(
            pixels, pixelCount, channels, e0, e1, grid, levels,
            idx0, idx1, idx2, idx3, coef0, coef1, coef2, coef3, weights);
    }

    private static void BuildTables(
        int blockWidth, int blockHeight, int gw, int gh, int ds, int dt,
        Span<byte> idx0, Span<byte> idx1, Span<byte> idx2, Span<byte> idx3,
        Span<byte> coef0, Span<byte> coef1, Span<byte> coef2, Span<byte> coef3)
    {
        for (int t = 0, p = 0; t < blockHeight; t++)
        {
            for (int s = 0; s < blockWidth; s++, p++)
            {
                int gs = (ds * s * (gw - 1) + 32) >> 6;
                int gt = (dt * t * (gh - 1) + 32) >> 6;
                int fs = gs & 0xf;
                int ft = gt & 0xf;
                int v = (gs >> 4) + (gt >> 4) * gw;
                int w11 = (fs * ft + 8) >> 4;
                int w10 = ft - w11;
                int w01 = fs - w11;
                int w00 = 16 - fs - ft + w11;

                idx0[p] = (byte)v;
                idx1[p] = (byte)(v + 1);
                idx2[p] = (byte)(v + gw);
                idx3[p] = (byte)(v + gw + 1);
                coef0[p] = (byte)w00;
                coef1[p] = (byte)w01;
                coef2[p] = (byte)w10;
                coef3[p] = (byte)w11;
            }
        }
    }

    /// <summary>
    /// Cheap score for a configuration: min/max endpoints, one projection of the
    /// weight grid, no coordinate descent. Used to rank configurations before
    /// the full optimization pass.
    /// </summary>
    private static long EvaluateConfig(
        ReadOnlySpan<byte> pixels, int blockWidth, int blockHeight, int channels, in Config config)
    {
        int pixelCount = blockWidth * blockHeight;
        int gw = config.Gw;
        int gh = config.Gh;
        ReadOnlySpan<int> levels = LevelsForBits(config.Bits);

        int ds = (1024 + blockWidth / 2) / (blockWidth - 1);
        int dt = (1024 + blockHeight / 2) / (blockHeight - 1);

        Span<byte> idx0 = stackalloc byte[MaxPixels];
        Span<byte> idx1 = stackalloc byte[MaxPixels];
        Span<byte> idx2 = stackalloc byte[MaxPixels];
        Span<byte> idx3 = stackalloc byte[MaxPixels];
        Span<byte> coef0 = stackalloc byte[MaxPixels];
        Span<byte> coef1 = stackalloc byte[MaxPixels];
        Span<byte> coef2 = stackalloc byte[MaxPixels];
        Span<byte> coef3 = stackalloc byte[MaxPixels];

        BuildTables(
            blockWidth, blockHeight, gw, gh, ds, dt,
            idx0, idx1, idx2, idx3, coef0, coef1, coef2, coef3);

        Span<int> grid = stackalloc int[MaxGridPoints + 16];
        Span<int> weights = stackalloc int[MaxPixels];
        Span<int> e0 = stackalloc int[4];
        Span<int> e1 = stackalloc int[4];

        InitEndpoints(pixels, pixelCount, channels, e0, e1);
        ProjectGrid(
            pixels, pixelCount, channels, e0, e1, idx0, idx1, idx2, idx3,
            coef0, coef1, coef2, coef3, levels, grid);

        return ComputeError(
            pixels, pixelCount, channels, e0, e1, grid, levels,
            idx0, idx1, idx2, idx3, coef0, coef1, coef2, coef3, weights);
    }

    private static void InitEndpoints(
        ReadOnlySpan<byte> pixels, int pixelCount, int channels,
        Span<int> e0, Span<int> e1)
    {
        for (int ch = 0; ch < channels; ch++)
        {
            int min = 255;
            int max = 0;
            for (int p = 0; p < pixelCount; p++)
            {
                int v = pixels[p * 4 + ch];
                if (v < min) min = v;
                if (v > max) max = v;
            }

            e0[ch] = min;
            e1[ch] = max;
        }

        if (channels == 3)
        {
            e0[3] = 255;
            e1[3] = 255;
        }

        if (e0[0] + e0[1] + e0[2] > e1[0] + e1[1] + e1[2])
        {
            for (int ch = 0; ch < 4; ch++)
                (e0[ch], e1[ch]) = (e1[ch], e0[ch]);
        }
    }

    private static int IdealWeight(
        ReadOnlySpan<byte> pixels, int pixel, int channels, ReadOnlySpan<int> e0, ReadOnlySpan<int> e1)
    {
        double numerator = 0;
        double denominator = 0;
        for (int ch = 0; ch < channels; ch++)
        {
            double d = e1[ch] - e0[ch];
            numerator += (pixels[pixel * 4 + ch] - e0[ch]) * d;
            denominator += d * d;
        }

        if (denominator < 1e-9)
            return 0;

        int weight = (int)Math.Round(64.0 * numerator / denominator);
        return weight < 0 ? 0 : weight > 64 ? 64 : weight;
    }

    private static int NearestLevel(ReadOnlySpan<int> levels, int value)
    {
        int best = 0;
        int bestDelta = int.MaxValue;
        for (int i = 0; i < levels.Length; i++)
        {
            int delta = Math.Abs(levels[i] - value);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = i;
            }
        }

        return best;
    }

    private static void ProjectGrid(
        ReadOnlySpan<byte> pixels, int pixelCount, int channels,
        ReadOnlySpan<int> e0, ReadOnlySpan<int> e1,
        ReadOnlySpan<byte> idx0, ReadOnlySpan<byte> idx1,
        ReadOnlySpan<byte> idx2, ReadOnlySpan<byte> idx3,
        ReadOnlySpan<byte> coef0, ReadOnlySpan<byte> coef1,
        ReadOnlySpan<byte> coef2, ReadOnlySpan<byte> coef3,
        ReadOnlySpan<int> levels, Span<int> grid)
    {
        int gridCount = levels.Length == 0 ? 0 : 0;
        _ = gridCount;

        int maxIndex = 0;
        for (int p = 0; p < pixelCount; p++)
        {
            int m = idx3[p];
            if (m > maxIndex) maxIndex = m;
        }

        Span<long> accumulator = stackalloc long[MaxGridPoints + 16];
        Span<int> weightSum = stackalloc int[MaxGridPoints + 16];
        for (int i = 0; i <= maxIndex; i++)
        {
            accumulator[i] = 0;
            weightSum[i] = 0;
        }

        for (int p = 0; p < pixelCount; p++)
        {
            int target = IdealWeight(pixels, p, channels, e0, e1);
            if (coef0[p] != 0)
            {
                accumulator[idx0[p]] += (long)coef0[p] * target;
                weightSum[idx0[p]] += coef0[p];
            }

            if (coef1[p] != 0)
            {
                accumulator[idx1[p]] += (long)coef1[p] * target;
                weightSum[idx1[p]] += coef1[p];
            }

            if (coef2[p] != 0)
            {
                accumulator[idx2[p]] += (long)coef2[p] * target;
                weightSum[idx2[p]] += coef2[p];
            }

            if (coef3[p] != 0)
            {
                accumulator[idx3[p]] += (long)coef3[p] * target;
                weightSum[idx3[p]] += coef3[p];
            }
        }

        for (int i = 0; i <= maxIndex; i++)
        {
            int value = weightSum[i] == 0
                ? 0
                : (int)((accumulator[i] + weightSum[i] / 2) / weightSum[i]);
            grid[i] = NearestLevel(levels, value);
        }
    }

    private static int PixelWeight(
        int pixel,
        ReadOnlySpan<int> grid,
        ReadOnlySpan<int> levels,
        ReadOnlySpan<byte> idx0, ReadOnlySpan<byte> idx1,
        ReadOnlySpan<byte> idx2, ReadOnlySpan<byte> idx3,
        ReadOnlySpan<byte> coef0, ReadOnlySpan<byte> coef1,
        ReadOnlySpan<byte> coef2, ReadOnlySpan<byte> coef3)
    {
        int sum = levels[grid[idx0[pixel]]] * coef0[pixel]
                + levels[grid[idx1[pixel]]] * coef1[pixel]
                + levels[grid[idx2[pixel]]] * coef2[pixel]
                + levels[grid[idx3[pixel]]] * coef3[pixel];
        return (sum + 8) >> 4;
    }

    private static long ErrorAt(
        int pixel, int channels,
        ReadOnlySpan<int> e0, ReadOnlySpan<int> e1, int weight,
        ReadOnlySpan<byte> pixels)
    {
        long error = 0;
        for (int ch = 0; ch < channels; ch++)
        {
            int value = SelectColor(e0[ch], e1[ch], weight);
            int delta = value - pixels[pixel * 4 + ch];
            error += delta * delta;
        }

        return error;
    }

    private static void OptimizeWeights(
        ReadOnlySpan<byte> pixels, int pixelCount, int channels,
        ReadOnlySpan<int> e0, ReadOnlySpan<int> e1,
        Span<int> grid, Span<int> weights, ReadOnlySpan<int> levels,
        ReadOnlySpan<byte> idx0, ReadOnlySpan<byte> idx1,
        ReadOnlySpan<byte> idx2, ReadOnlySpan<byte> idx3,
        ReadOnlySpan<byte> coef0, ReadOnlySpan<byte> coef1,
        ReadOnlySpan<byte> coef2, ReadOnlySpan<byte> coef3,
        ReadOnlySpan<int> affStart, ReadOnlySpan<int> affPixels,
        int gridCount, int passes)
    {
        for (int pass = 0; pass < passes; pass++)
        {
            for (int g = 0; g < gridCount; g++)
            {
                int original = grid[g];
                long bestError = AffectedError(
                    g, pixels, channels, e0, e1, grid, weights, levels,
                    idx0, idx1, idx2, idx3, coef0, coef1, coef2, coef3,
                    affStart, affPixels);
                int best = original;

                // Only levels near the projected optimum are considered: the
                // error curve for a single grid point is unimodal enough that
                // a window of 2*DescentRadius+1 levels loses ~0.05 dB while
                // cutting the search by up to 6x.
                int lo = Math.Max(0, original - DescentRadius);
                int hi = Math.Min(levels.Length - 1, original + DescentRadius);
                for (int level = lo; level <= hi; level++)
                {
                    if (level == original)
                        continue;
                    grid[g] = level;
                    long error = AffectedError(
                        g, pixels, channels, e0, e1, grid, weights, levels,
                        idx0, idx1, idx2, idx3, coef0, coef1, coef2, coef3,
                        affStart, affPixels, bestError);
                    if (error < bestError)
                    {
                        bestError = error;
                        best = level;
                    }
                }

                grid[g] = best;
            }
        }

        for (int p = 0; p < pixelCount; p++)
        {
            weights[p] = PixelWeight(p, grid, levels, idx0, idx1, idx2, idx3, coef0, coef1, coef2, coef3);
        }
    }

    private static long AffectedError(
        int gridPoint, ReadOnlySpan<byte> pixels, int channels,
        ReadOnlySpan<int> e0, ReadOnlySpan<int> e1,
        ReadOnlySpan<int> grid, ReadOnlySpan<int> weights, ReadOnlySpan<int> levels,
        ReadOnlySpan<byte> idx0, ReadOnlySpan<byte> idx1,
        ReadOnlySpan<byte> idx2, ReadOnlySpan<byte> idx3,
        ReadOnlySpan<byte> coef0, ReadOnlySpan<byte> coef1,
        ReadOnlySpan<byte> coef2, ReadOnlySpan<byte> coef3,
        ReadOnlySpan<int> affStart, ReadOnlySpan<int> affPixels,
        long pruneAbove = long.MaxValue)
    {
        _ = weights;
        long error = 0;
        int end = affStart[gridPoint + 1];
        for (int k = affStart[gridPoint]; k < end; k++)
        {
            int p = affPixels[k];
            int weight = PixelWeight(p, grid, levels, idx0, idx1, idx2, idx3, coef0, coef1, coef2, coef3);
            error += ErrorAt(p, channels, e0, e1, weight, pixels);
            if (error >= pruneAbove)
                return pruneAbove;
        }

        return error;
    }

    /// <summary>
    /// Least-squares fit of the two endpoints for the current weights, followed
    /// by a small integer refinement. Returns true when the endpoint order was
    /// swapped (which invalidates the weight grid projection).
    /// </summary>
    private static bool FitEndpoints(
        ReadOnlySpan<byte> pixels, int pixelCount, int channels,
        ReadOnlySpan<int> weights, Span<int> e0, Span<int> e1)
    {
        for (int ch = 0; ch < channels; ch++)
        {
            double sumT = 0;
            double sumT2 = 0;
            double sumP = 0;
            double sumTP = 0;

            for (int p = 0; p < pixelCount; p++)
            {
                double t = weights[p] / 64.0;
                double value = pixels[p * 4 + ch];
                sumT += t;
                sumT2 += t * t;
                sumP += value;
                sumTP += t * value;
            }

            // Model p = v0*(1-t) + v1*t.
            double aa = pixelCount - 2 * sumT + sumT2;   // sum (1-t)^2
            double bb = sumT - sumT2;                    // sum (1-t)*t
            double cc = sumT2;                           // sum t^2
            double dd = sumP - sumTP;                    // sum (1-t)*p
            double ee = sumTP;                           // sum t*p
            double det = aa * cc - bb * bb;
            double v0;
            double v1;
            if (Math.Abs(det) < 1e-9)
            {
                double mean = sumP / pixelCount;
                v0 = mean;
                v1 = mean;
            }
            else
            {
                v1 = (aa * ee - bb * dd) / det;
                v0 = (dd - bb * v1) / aa;
            }

            int base0 = Clamp255((int)Math.Round(v0));
            int base1 = Clamp255((int)Math.Round(v1));
            long bestError = long.MaxValue;
            int best0 = base0;
            int best1 = base1;

            // Centre first so the neighbours can prune early: most refinements
            // lose against the least-squares fit within a handful of pixels.
            ReadOnlySpan<int> offsets = stackalloc int[] { 0, 1, -1 };
            for (int i0 = 0; i0 < 3; i0++)
            {
                int r0 = Clamp255(base0 + offsets[i0]);
                for (int i1 = 0; i1 < 3; i1++)
                {
                    int r1 = Clamp255(base1 + offsets[i1]);
                    long error = 0;
                    bool pruned = false;
                    for (int p = 0; p < pixelCount; p++)
                    {
                        int value = SelectColor(r0, r1, weights[p]);
                        int delta = value - pixels[p * 4 + ch];
                        error += delta * delta;
                        if (error >= bestError)
                        {
                            pruned = true;
                            break;
                        }
                    }

                    if (!pruned && error < bestError)
                    {
                        bestError = error;
                        best0 = r0;
                        best1 = r1;
                    }
                }
            }

            e0[ch] = best0;
            e1[ch] = best1;
        }

        if (channels == 3)
        {
            e0[3] = 255;
            e1[3] = 255;
        }

        // The decoder uses a lossy "blue contour" endpoint representation when
        // the first endpoint has the larger RGB sum. Keep endpoint 0 darker.
        if (e0[0] + e0[1] + e0[2] > e1[0] + e1[1] + e1[2])
        {
            for (int ch = 0; ch < 4; ch++)
                (e0[ch], e1[ch]) = (e1[ch], e0[ch]);
            return true;
        }

        return false;
    }

    private static long ComputeError(
        ReadOnlySpan<byte> pixels, int pixelCount, int channels,
        ReadOnlySpan<int> e0, ReadOnlySpan<int> e1,
        ReadOnlySpan<int> grid, ReadOnlySpan<int> levels,
        ReadOnlySpan<byte> idx0, ReadOnlySpan<byte> idx1,
        ReadOnlySpan<byte> idx2, ReadOnlySpan<byte> idx3,
        ReadOnlySpan<byte> coef0, ReadOnlySpan<byte> coef1,
        ReadOnlySpan<byte> coef2, ReadOnlySpan<byte> coef3,
        Span<int> weights)
    {
        long error = 0;
        for (int p = 0; p < pixelCount; p++)
        {
            int weight = PixelWeight(p, grid, levels, idx0, idx1, idx2, idx3, coef0, coef1, coef2, coef3);
            weights[p] = weight;
            error += ErrorAt(p, channels, e0, e1, weight, pixels);
        }

        return error;
    }

    private static void EmitBlock(
        Span<byte> block, int channels, in Config config,
        ReadOnlySpan<int> grid, ReadOnlySpan<int> levels,
        ReadOnlySpan<int> e0, ReadOnlySpan<int> e1)
    {
        block.Clear();

        int cem = channels == 4 ? 12 : 8;
        int range = RangeForBits(config.Bits);
        if (!TryGrid(config.Gw, config.Gh, range, out int b0, out int b1))
            throw new InvalidOperationException("Internal ASTC configuration error.");

        b1 |= (cem & 7) << 5;
        int b2 = (cem >> 3) & 1;

        SetBits(block, 0, b0, 8);
        SetBits(block, 8, b1, 8);
        SetBits(block, 16, b2, 1);

        if (channels == 3)
        {
            SetBits(block, 17, e0[0], 8);
            SetBits(block, 25, e1[0], 8);
            SetBits(block, 33, e0[1], 8);
            SetBits(block, 41, e1[1], 8);
            SetBits(block, 49, e0[2], 8);
            SetBits(block, 57, e1[2], 8);
        }
        else
        {
            // CEM 12 interleaves the two endpoints: R0,R1,G0,G1,B0,B1,A0,A1.
            for (int ch = 0; ch < 4; ch++)
            {
                SetBits(block, 17 + (ch * 2) * 8, e0[ch], 8);
                SetBits(block, 17 + (ch * 2 + 1) * 8, e1[ch], 8);
            }
        }

        int bits = config.Bits;
        int count = config.Gw * config.Gh;
        for (int i = 0; i < count; i++)
        {
            int index = grid[i];
            int stored = ReverseBits(index, bits);
            SetBits(block, 128 - (i + 1) * bits, stored, bits);
        }

        _ = levels;
    }

    private static void EmitVoidExtent(Span<byte> block, byte r, byte g, byte b, byte a)
    {
        block.Clear();
        block[0] = 0xfc;
        block[1] = 1;
        block[9] = r;
        block[11] = g;
        block[13] = b;
        block[15] = a;
    }

    /// <summary>
    /// Builds block-mode / grid bits for a single-partition block using the
    /// given grid and pure-bit weight range.
    /// </summary>
    private static bool TryGrid(int gw, int gh, int range, out int b0, out int b1)
    {
        b0 = 0;
        b1 = 0;
        int mode = (range >> 1) & 3;
        int rangeBit0 = range & 1;
        int rangeBit3 = (range >> 3) & 1;

        if (mode == 0)
        {
            // Block mode 0 (b0 & 3 == 0). In this mode the low weight-range
            // bits come from b0 bits 2..3, and b1 bit 1 may be reused by the
            // grid. Only weight range 4 (2-bit weights) can be expressed.
            if (range != 4)
                return false;

            if (gw == 12 && gh is >= 2 and <= 5)
            {
                b0 = (gh - 2) << 5;             // b0 bit7 = 0, b1 bit0 = 0
            }
            else if (gh == 12 && gw is >= 2 and <= 5)
            {
                b0 = 0x80 | ((gw - 2) << 5);    // b0 bit7 = 1, b1 bit0 = 0
            }
            else if (gw is >= 6 and <= 9 && gh is >= 6 and <= 9)
            {
                // u16 & 0x180 == 0x100: b0 bit7 = 0, b1 bit0 = 1.
                b0 = (gw - 6) << 5;
                b1 = 1 | ((gh - 6) << 1);       // b1 bit0 = 1, b1 bits1..2 = H-6
            }
            else if (gw == 10 && gh == 6)
            {
                b0 = 0x80 | (1 << 5);           // W = 10, H = 6
                b1 = 1;
            }
            else if (gw == 6 && gh == 10)
            {
                b0 = 0x80;                      // W = 6, H = 10
                b1 = 1;
            }
            else
            {
                return false;
            }

            b0 |= (range & 6) << 1;             // range bits 1..2 -> b0 bits 2..3
            b0 |= rangeBit0 << 4;
            return true;
        }

        int caseBits;
        if (gw is >= 4 and <= 7 && gh is >= 2 and <= 5)
        {
            caseBits = 0;
            int w = gw - 4;
            b0 = mode | caseBits | ((gh - 2) << 5) | ((w & 1) << 7) | (rangeBit0 << 4);
            b1 = ((w >> 1) & 1) | (rangeBit3 << 1);
        }
        else if (gw is >= 8 and <= 11 && gh is >= 2 and <= 5)
        {
            caseBits = 4;
            int w = gw - 8;
            b0 = mode | caseBits | ((gh - 2) << 5) | ((w & 1) << 7) | (rangeBit0 << 4);
            b1 = ((w >> 1) & 1) | (rangeBit3 << 1);
        }
        else if (gw is >= 2 and <= 5 && gh is >= 8 and <= 11)
        {
            caseBits = 8;
            int h = gh - 8;
            b0 = mode | caseBits | ((gw - 2) << 5) | ((h & 1) << 7) | (rangeBit0 << 4);
            b1 = ((h >> 1) & 1) | (rangeBit3 << 1);
        }
        else if (gw is >= 2 and <= 3 && gh is >= 2 and <= 5)
        {
            caseBits = 12;
            b0 = mode | caseBits | ((gh - 2) << 5) | ((gw - 2) << 7) | (rangeBit0 << 4);
            b1 = 1 | (rangeBit3 << 1);
        }
        else if (gw is >= 2 and <= 5 && gh is >= 6 and <= 7)
        {
            caseBits = 12;
            b0 = mode | caseBits | ((gw - 2) << 5) | ((gh - 6) << 7) | (rangeBit0 << 4);
            b1 = rangeBit3 << 1;
        }
        else
        {
            return false;
        }

        return true;
    }

    private static void SetBits(Span<byte> destination, int bitPosition, int value, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int bit = bitPosition + i;
            int mask = 1 << (bit & 7);
            if (((value >> i) & 1) != 0)
                destination[bit >> 3] |= (byte)mask;
            else
                destination[bit >> 3] &= (byte)~mask;
        }
    }

    private static int ReverseBits(int value, int bits)
    {
        int result = 0;
        for (int i = 0; i < bits; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }

        return result;
    }

    private static int SelectColor(int v0, int v1, int weight) =>
        ((((((v0 << 8) | v0) * (64 - weight)) + (((v1 << 8) | v1) * weight) + 32) >> 6) * 255 + 32768) / 65536;

    private static int Clamp255(int value) => value < 0 ? 0 : value > 255 ? 255 : value;
}
