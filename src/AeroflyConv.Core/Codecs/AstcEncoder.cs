using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AeroflyConv.Core.Codecs;

/// <summary>
/// Small, fully managed ASTC LDR encoder that emits blocks conforming to the
/// Khronos ASTC specification (the same layout astcenc writes and hardware
/// decodes):
///
///  * exactly constant blocks use the void-extent form.
///  * everything else is a single-partition, single-plane block.  The 11-bit
///    block mode (bits 0..10) is taken from the normative block-mode encoding
///    (astcenc <c>decode_block_mode_2d</c>), partition count - 1 = 0 lives in
///    bits 11..12, the colour endpoint mode in bits 13..16, the endpoint
///    integers are written with encode_ise (LSB first, pure-bit levels only)
///    at bit 17, and the weight integers occupy the top of the block, each one
///    bit-reversed in its field (ised stream stored back to front).
///  * only CEM 8 (RGB) and CEM 12 (RGBA) are used, with power-of-two endpoint
///    level counts (QUANT_8/16/32/64/128/256), which are stored as plain
///    little-endian bit fields because their scramble maps are the identity.
///  * weights use the power-of-two quant levels only (QUANT_2/4/8/16/32, i.e.
///    pure bit sequences), whose scramble maps are also the identity.
///
/// The configuration space (weight grid x weight precision) is enumerated from
/// the block-mode table at first use; for each block a cheap projection pass
/// ranks the candidates and the most promising ones get the full endpoint /
/// weight fit before the best one is emitted.
/// </summary>
public static class AstcEncoder
{
    private const int MaxPixels = 144;      // 12 x 12
    private const int MaxGridPoints = 144;
    private const int MaxConfigs = 16;

    // How many configurations survive the cheap pre-pass and get the full
    // endpoint/weight optimization.
    private const int RefineConfigCount = 8;

    // Weight search window (in quantized level indices) around the projected
    // optimum.
    private const int DescentRadius = 2;

    /// <summary>Per-call search budget.</summary>
    private readonly struct Settings
    {
        public readonly int RefineCount;
        public readonly int Iterations;
        public readonly int Radius;

        public Settings(int refineCount, int iterations, int radius)
        {
            RefineCount = refineCount;
            Iterations = iterations;
            Radius = radius;
        }

        public static Settings For(AstcQuality quality) => quality switch
        {
            AstcQuality.Fast => new Settings(3, 1, 1),
            AstcQuality.High => new Settings(MaxConfigs, 2, 3),
            _ => new Settings(RefineConfigCount, 2, DescentRadius),
        };
    }

    // Unquantised weight values for the pure-bit quant levels.  These are the
    // values the bit patterns stand for after weight infill, exactly as the
    // decoder derives them (and as astcenc's quant_and_xfer tables list them).
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

    private static readonly int[]?[] WeightLevels =
    {
        null, Levels1, Levels2, Levels3, Levels4, Levels5,
    };

    // Weight quantization method index (0..11 = QUANT_2..QUANT_32) to the
    // number of bits per weight; -1 for the trit/quint methods.
    private static readonly int[] QuantBits = { 1, -1, 2, -1, -1, 3, -1, -1, 4, -1, -1, 5 };

    // Endpoint levels: EpUnquant[range * 256 + p] is the 8-bit endpoint value a
    // stored integer p decodes to, EpQuant is its inverse (nearest value).
    // Only the pure-bit ranges (AstcTables.CemA == 0, index <= 15) are filled.
    private static readonly byte[] EpUnquant = new byte[19 * 256];
    private static readonly byte[] EpQuant = new byte[19 * 256];

    private static readonly ConcurrentDictionary<int, Config[]> ConfigCache = new();

    private struct Config
    {
        public ushort Mode;     // 11-bit block mode
        public byte Gw;         // weight grid width
        public byte Gh;         // weight grid height
        public byte WBits;      // bits per weight
        public byte EpRange;    // endpoint level, index into AstcTables.CemA/B
        public byte EpBits;     // bits per endpoint value
        public byte EpCount;    // 6 (CEM 8) or 8 (CEM 12) endpoint values
    }

    static AstcEncoder()
    {
        for (int range = 0; range < 19; range++)
        {
            if (AstcTables.CemA[range] != 0)
                continue;

            int bits = AstcTables.CemB[range];
            if (bits < 3 || bits > 8)
                continue;

            int levels = 1 << bits;
            for (int p = 0; p < levels; p++)
                EpUnquant[range * 256 + p] = (byte)Expand(p, bits);

            for (int v = 0; v < 256; v++)
            {
                int best = 0;
                int bestDelta = int.MaxValue;
                for (int p = 0; p < levels; p++)
                {
                    int delta = Math.Abs(EpUnquant[range * 256 + p] - v);
                    if (delta < bestDelta)
                    {
                        bestDelta = delta;
                        best = p;
                    }
                }

                EpQuant[range * 256 + v] = (byte)best;
            }
        }
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
    /// <param name="quality">Speed/quality trade-off; defaults to <see cref="AstcQuality.Standard"/>.</param>
    public static void Encode(
        ReadOnlySpan<byte> rgba,
        int width,
        int height,
        int blockWidth,
        int blockHeight,
        Span<byte> output,
        AstcQuality quality = AstcQuality.Standard)
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
        Config[] configs = GetConfigs(blockWidth, blockHeight, channels);
        Settings settings = Settings.For(quality);

        // Blocks are independent, so large images are encoded one row of blocks
        // at a time in parallel.  The result is bit-identical to the serial
        // path (no cross-block state).
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
                                configs, settings, bx, by,
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
                        configs, settings, bx, by,
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
        in Settings settings,
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
            int configCount = configs.Length;
            Span<long> scores = stackalloc long[MaxConfigs];
            for (int c = 0; c < configCount; c++)
                scores[c] = EvaluateConfig(pixels, blockWidth, blockHeight, channels, configs[c]);

            int refineCount = Math.Min(settings.RefineCount, configCount);
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
                    settings,
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
                // Cannot happen (every supported block size has at least one
                // configuration), but never emit something invalid.
                EmitVoidExtent(block, firstR, firstG, firstB, firstA);
            }
            else
            {
                bestBlock.CopyTo(block);
            }
        }

        block.CopyTo(destination);
    }

    // ------------------------------------------------------- configuration set

    private static Config[] GetConfigs(int blockWidth, int blockHeight, int channels)
    {
        int key = (blockWidth << 16) | (blockHeight << 8) | channels;
        if (ConfigCache.TryGetValue(key, out Config[]? cached))
            return cached;

        Config[] built = BuildConfigs(blockWidth, blockHeight, channels);
        return ConfigCache.GetOrAdd(key, built);
    }

    /// <summary>
    /// Enumerates the legal single-partition, single-plane block modes from the
    /// normative block-mode encoding and keeps those whose weight grid and
    /// endpoint precision fit the 128-bit block.
    /// </summary>
    private static Config[] BuildConfigs(int blockWidth, int blockHeight, int channels)
    {
        int epCount = channels == 4 ? 8 : 6;
        var list = new List<Config>(64);
        var seen = new HashSet<long>();
        int minArea = Math.Max(4, blockWidth * blockHeight / 6);

        for (int mode = 0; mode < 2048; mode++)
        {
            if (!DecodeBlockMode(mode, out int gw, out int gh, out bool dualPlane, out int quant))
                continue;
            if (dualPlane)
                continue;
            if (gw > blockWidth || gh > blockHeight)
                continue;

            int area = gw * gh;
            if (area < minArea)
                continue;

            // Several block modes decode to the same grid/quantisation (bit 8
            // is free except in one grid form); keep the first, smallest one.
            long key = ((long)gw << 24) | ((long)gh << 16) | ((long)quant << 8);
            if (!seen.Add(key))
                continue;

            int wb = area * QuantBits[quant];
            int colorBits = 111 - wb;
            if (colorBits < 16)
                continue;

            if (!TryEndpointLevel(epCount, colorBits, out int range, out int epBits))
                continue;

            list.Add(new Config
            {
                Mode = (ushort)mode,
                Gw = (byte)gw,
                Gh = (byte)gh,
                WBits = (byte)QuantBits[quant],
                EpRange = (byte)range,
                EpBits = (byte)epBits,
                EpCount = (byte)epCount,
            });
        }

        // Rank by a static quality proxy and keep a shortlist that mixes dense
        // grids, high weight precision and high endpoint precision.
        list.Sort((x, y) => Score(y, epCount).CompareTo(Score(x, epCount)));

        int keep = Math.Min(list.Count, MaxConfigs);
        var result = new Config[keep];
        for (int i = 0; i < keep; i++)
            result[i] = list[i];

        return result;
    }

    private static int Score(in Config c, int epCount) =>
        (3 * c.Gw * c.Gh) + (2 * c.WBits * c.Gw * c.Gh) + (c.EpBits * epCount / 2);

    /// <summary>
    /// Port of astcenc's <c>decode_block_mode_2d</c>: returns the weight grid,
    /// the dual-plane flag and the weight quantization method for an 11-bit
    /// block mode, and false when the mode is not a legal single-partition
    /// (2D) mode with a supported weight bit count.
    /// </summary>
    private static bool DecodeBlockMode(
        int mode, out int gw, out int gh, out bool dualPlane, out int quant)
    {
        gw = 0;
        gh = 0;
        dualPlane = false;
        quant = -1;

        int baseQuant = (mode >> 4) & 1;
        int h = (mode >> 9) & 1;
        int d = (mode >> 10) & 1;
        int a = (mode >> 5) & 3;

        if ((mode & 3) != 0)
        {
            baseQuant |= (mode & 3) << 1;
            int b = (mode >> 7) & 3;
            switch ((mode >> 2) & 3)
            {
                case 0:
                    gw = b + 4;
                    gh = a + 2;
                    break;
                case 1:
                    gw = b + 8;
                    gh = a + 2;
                    break;
                case 2:
                    gw = a + 2;
                    gh = b + 8;
                    break;
                default:
                    b &= 1;
                    if ((mode & 0x100) != 0)
                    {
                        gw = b + 2;
                        gh = a + 2;
                    }
                    else
                    {
                        gw = a + 2;
                        gh = b + 6;
                    }

                    break;
            }
        }
        else
        {
            int form = (mode >> 2) & 3;
            if (form == 0)
                return false;

            baseQuant |= form << 1;
            int b = (mode >> 9) & 3;
            switch ((mode >> 7) & 3)
            {
                case 0:
                    gw = 12;
                    gh = a + 2;
                    break;
                case 1:
                    gw = a + 2;
                    gh = 12;
                    break;
                case 2:
                    gw = a + 6;
                    gh = b + 6;
                    d = 0;
                    h = 0;
                    break;
                default:
                    switch ((mode >> 5) & 3)
                    {
                        case 0:
                            gw = 6;
                            gh = 10;
                            break;
                        case 1:
                            gw = 10;
                            gh = 6;
                            break;
                        default:
                            return false;
                    }

                    break;
            }
        }

        int count = gw * gh * (d + 1);
        quant = baseQuant - 2 + 6 * h;
        dualPlane = d != 0;

        if (quant is < 0 or > 11)
            return false;

        int bits = QuantBits[quant];
        if (bits < 0)
            return false;

        int weightBits = count * bits;
        return count <= 64 && weightBits >= 24 && weightBits <= 96;
    }

    /// <summary>
    /// Picks the endpoint quantization level the decoder will derive for
    /// <paramref name="colorBits"/> available bits: the highest-precision level
    /// whose ISE payload fits.  Returns false unless that level is a pure-bit
    /// level at least as precise as QUANT_8.
    /// </summary>
    private static bool TryEndpointLevel(int epCount, int colorBits, out int range, out int bits)
    {
        // Windows of `color_bits` for which astcenc's normative
        // `quant_mode_table[ep_count >> 1][color_bits]` selects a *pure bit*
        // quantization level (CemA == 0) *and* the managed decoder's
        // "highest precision that fits" scan picks the same level.  Both
        // conditions are required: the table is what hardware uses, the scan is
        // what AstcCodec uses, and they disagree on a few bit counts (e.g. 8
        // integers at 61 bits: the table says QUANT_128, the scan says
        // QUANT_192), so those counts must not be emitted.
        ReadOnlySpan<byte> windows = epCount == 8 ? EndpointWindows8 : EndpointWindows6;
        for (int i = 0; i < windows.Length; i += 3)
        {
            if (colorBits >= windows[i] && colorBits <= windows[i + 1])
            {
                range = windows[i + 2];
                bits = (byte)AstcTables.CemB[range];
                return true;
            }
        }

        range = -1;
        bits = 0;
        return false;
    }

    // (min color_bits, max color_bits, CemA/CemB index) triples, ordered by
    // descending endpoint precision.  Derived from quant_mode_table rows 3 and 4
    // plus the CemA/CemB scan rule; levels below QUANT_8 are omitted.
    private static readonly byte[] EndpointWindows6 =
    {
        48, 111, 0, // QUANT_256, 8 bits
        42, 43, 3,  // QUANT_128, 7 bits
        36, 37, 6,  // QUANT_64,  6 bits
        30, 31, 9,  // QUANT_32,  5 bits
        24, 25, 12, // QUANT_16,  4 bits
        18, 19, 15, // QUANT_8,   3 bits
    };

    private static readonly byte[] EndpointWindows8 =
    {
        64, 111, 0, // QUANT_256, 8 bits
        56, 58, 3,  // QUANT_128, 7 bits
        48, 50, 6,  // QUANT_64,  6 bits
        40, 42, 9,  // QUANT_32,  5 bits
        32, 34, 12, // QUANT_16,  4 bits
        24, 26, 15, // QUANT_8,   3 bits
    };

    // ------------------------------------------------------------ block fitting

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
        in Settings settings,
        Span<byte> outBlock)
    {
        int pixelCount = blockWidth * blockHeight;
        int gw = config.Gw;
        int gh = config.Gh;
        int gridCount = gw * gh;
        ReadOnlySpan<int> levels = WeightLevels[config.WBits];

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
        Span<byte> q0 = stackalloc byte[4];
        Span<byte> q1 = stackalloc byte[4];

        InitEndpoints(pixels, pixelCount, channels, e0, e1);
        ProjectGrid(
            pixels, pixelCount, channels, e0, e1, idx0, idx1, idx2, idx3,
            coef0, coef1, coef2, coef3, levels, grid);

        OptimizeWeights(
            pixels, pixelCount, channels, e0, e1, grid, weights, levels,
            idx0, idx1, idx2, idx3, coef0, coef1, coef2, coef3,
            affStart, affPixels, gridCount, passes: 2, radius: settings.Radius);

        for (int iteration = 0; iteration < settings.Iterations; iteration++)
        {
            FitEndpoints(pixels, pixelCount, channels, weights, e0, e1);
            QuantizeEndpoints(config.EpRange, channels, e0, e1, q0, q1);
            ProjectGrid(
                pixels, pixelCount, channels, e0, e1, idx0, idx1, idx2, idx3,
                coef0, coef1, coef2, coef3, levels, grid);
            OptimizeWeights(
                pixels, pixelCount, channels, e0, e1, grid, weights, levels,
                idx0, idx1, idx2, idx3, coef0, coef1, coef2, coef3,
                affStart, affPixels, gridCount, passes: 2, radius: settings.Radius);
        }

        // Final pass: least-squares endpoints for the current weights, snap them
        // to the endpoint level the block will carry, then refit the weights so
        // the emitted values are the ones that were actually scored.
        FitEndpoints(pixels, pixelCount, channels, weights, e0, e1);
        QuantizeEndpoints(config.EpRange, channels, e0, e1, q0, q1);
        ProjectGrid(
            pixels, pixelCount, channels, e0, e1, idx0, idx1, idx2, idx3,
            coef0, coef1, coef2, coef3, levels, grid);
        OptimizeWeights(
            pixels, pixelCount, channels, e0, e1, grid, weights, levels,
            idx0, idx1, idx2, idx3, coef0, coef1, coef2, coef3,
            affStart, affPixels, gridCount, passes: 2, radius: settings.Radius);

        EmitBlock(outBlock, config, grid, q0, q1);
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
    /// weight grid, no coordinate descent.  Used to rank configurations before
    /// the full optimization pass.
    /// </summary>
    private static long EvaluateConfig(
        ReadOnlySpan<byte> pixels, int blockWidth, int blockHeight, int channels, in Config config)
    {
        int pixelCount = blockWidth * blockHeight;
        int gw = config.Gw;
        int gh = config.Gh;
        ReadOnlySpan<int> levels = WeightLevels[config.WBits];

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
        Span<byte> q0 = stackalloc byte[4];
        Span<byte> q1 = stackalloc byte[4];

        InitEndpoints(pixels, pixelCount, channels, e0, e1);
        ProjectGrid(
            pixels, pixelCount, channels, e0, e1, idx0, idx1, idx2, idx3,
            coef0, coef1, coef2, coef3, levels, grid);
        QuantizeEndpoints(config.EpRange, channels, e0, e1, q0, q1);

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
        int gridCount, int passes, int radius)
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
                // a window of 2*radius+1 levels loses little while cutting
                // the search by up to 6x.
                int lo = Math.Max(0, original - radius);
                int hi = Math.Min(levels.Length - 1, original + radius);
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
    /// by a small integer refinement.  Keeps endpoint 0 at or below endpoint 1
    /// in RGB sum (the "blue contour" packing is lossy).
    /// </summary>
    private static void FitEndpoints(
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

            // Centre first so the neighbours can prune early.
            ReadOnlySpan<int> offsets = [0, 1, -1];
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

        if (e0[0] + e0[1] + e0[2] > e1[0] + e1[1] + e1[2])
        {
            for (int ch = 0; ch < 4; ch++)
                (e0[ch], e1[ch]) = (e1[ch], e0[ch]);
        }
    }

    /// <summary>
    /// Snaps the fitted endpoints to the stored integer range of the block's
    /// endpoint level, and makes <paramref name="e0"/>/<paramref name="e1"/>
    /// hold the values the decoder will reconstruct from them.
    /// </summary>
    private static void QuantizeEndpoints(
        int epRange, int channels, Span<int> e0, Span<int> e1,
        Span<byte> q0, Span<byte> q1)
    {
        int table = epRange * 256;
        for (int ch = 0; ch < 4; ch++)
        {
            q0[ch] = EpQuant[table + e0[ch]];
            q1[ch] = EpQuant[table + e1[ch]];
            e0[ch] = EpUnquant[table + q0[ch]];
            e1[ch] = EpUnquant[table + q1[ch]];
        }

        if (e0[0] + e0[1] + e0[2] > e1[0] + e1[1] + e1[2])
        {
            for (int ch = 0; ch < 4; ch++)
            {
                (e0[ch], e1[ch]) = (e1[ch], e0[ch]);
                (q0[ch], q1[ch]) = (q1[ch], q0[ch]);
            }
        }

        _ = channels;
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

    // -------------------------------------------------------------- bit writing

    private static void EmitBlock(
        Span<byte> block, in Config config,
        ReadOnlySpan<int> grid, ReadOnlySpan<byte> q0, ReadOnlySpan<byte> q1)
    {
        block.Clear();

        // Block mode (bits 0..10), partition count - 1 = 0 (bits 11..12) and
        // the colour endpoint mode (bits 13..16).
        int cem = config.EpCount == 8 ? 12 : 8;
        SetBits(block, 0, config.Mode, 11);
        SetBits(block, 13, cem, 4);

        // Endpoint integers, channel-interleaved: R0,R1,G0,G1,B0,B1[,A0,A1].
        int epBits = config.EpBits;
        int values = config.EpCount / 2;
        for (int ch = 0; ch < values; ch++)
        {
            SetBits(block, 17 + (ch * 2) * epBits, q0[ch], epBits);
            SetBits(block, 17 + (ch * 2 + 1) * epBits, q1[ch], epBits);
        }

        // Weight integers: written back to front into the top of the block,
        // each value bit-reversed inside its field.
        int bits = config.WBits;
        int count = config.Gw * config.Gh;
        for (int i = 0; i < count; i++)
        {
            int stored = ReverseBits(grid[i], bits);
            SetBits(block, 128 - (i + 1) * bits, stored, bits);
        }
    }

    /// <summary>
    /// Writes a void-extent block holding one constant colour: the reserved
    /// header bytes, then four little-endian UNORM16 values (which for 8-bit
    /// source colours are just the byte twice).
    /// </summary>
    private static void EmitVoidExtent(Span<byte> block, byte r, byte g, byte b, byte a)
    {
        block.Clear();
        block[0] = 0xFC;
        block[1] = 0xFD;
        for (int i = 2; i < 8; i++)
            block[i] = 0xFF;

        block[8] = r;
        block[9] = r;
        block[10] = g;
        block[11] = g;
        block[12] = b;
        block[13] = b;
        block[14] = a;
        block[15] = a;
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

    /// <summary>
    /// Expands a stored endpoint integer to the 8-bit value the decoder
    /// reconstructs (bit replication for pure-bit levels).
    /// </summary>
    private static int Expand(int p, int bits) => bits switch
    {
        8 => p,
        7 => (p << 1) | (p >> 6),
        6 => (p << 2) | (p >> 4),
        5 => (p << 3) | (p >> 2),
        4 => (p << 4) | p,
        3 => (p << 5) | (p << 2) | (p >> 1),
        2 => (p << 6) | (p << 4) | (p << 2) | p,
        _ => p * 255,
    };

    private static int SelectColor(int v0, int v1, int weight) =>
        ((((((v0 << 8) | v0) * (64 - weight)) + (((v1 << 8) | v1) * weight) + 32) >> 6) * 255 + 32768) / 65536;

    private static int Clamp255(int value) => value < 0 ? 0 : value > 255 ? 255 : value;
}
