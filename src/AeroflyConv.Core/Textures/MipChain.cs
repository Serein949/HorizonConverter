using AeroflyConv.Core.Tm;

namespace AeroflyConv.Core.Textures;

/// <summary>Downsamples RGBA mip levels the way Pillow's <c>Image.Resampling.BOX</c> does.</summary>
public static class MipChain
{
    /// <summary>Number of mip levels down to 1x1.</summary>
    public static int Count(int width, int height)
    {
        int n = 1, m = Math.Max(width, height);
        while (m > 1) { m /= 2; n++; }
        return n;
    }

    /// <summary>Area-average ("box") downscale by a factor of two.</summary>
    public static byte[] Halve(ReadOnlySpan<byte> rgba, int width, int height, out int newWidth, out int newHeight)
    {
        newWidth = Math.Max(1, width / 2);
        newHeight = Math.Max(1, height / 2);
        var dest = new byte[newWidth * newHeight * 4];

        // Integer box filter: accumulate the source pixels that map onto each dest pixel.
        Span<int> sums = stackalloc int[4];
        for (int dy = 0; dy < newHeight; dy++)
        {
            int sy0 = dy * height / newHeight;
            int sy1 = Math.Max(sy0 + 1, (dy + 1) * height / newHeight);
            for (int dx = 0; dx < newWidth; dx++)
            {
                int sx0 = dx * width / newWidth;
                int sx1 = Math.Max(sx0 + 1, (dx + 1) * width / newWidth);
                sums.Clear();
                int n = 0;
                for (int sy = sy0; sy < sy1; sy++)
                {
                    int rowBase = (sy * width + sx0) * 4;
                    for (int sx = sx0; sx < sx1; sx++)
                    {
                        int o = rowBase + (sx - sx0) * 4;
                        sums[0] += rgba[o];
                        sums[1] += rgba[o + 1];
                        sums[2] += rgba[o + 2];
                        sums[3] += rgba[o + 3];
                        n++;
                    }
                }
                int d = (dy * newWidth + dx) * 4;
                dest[d] = (byte)((sums[0] + n / 2) / n);
                dest[d + 1] = (byte)((sums[1] + n / 2) / n);
                dest[d + 2] = (byte)((sums[2] + n / 2) / n);
                dest[d + 3] = (byte)((sums[3] + n / 2) / n);
            }
        }
        return dest;
    }

    /// <summary>Enumerates the mip chain, stopping when max(w,h) is &lt;= <paramref name="lastMax"/>.</summary>
    public static IEnumerable<(byte[] Rgba, int Width, int Height)> Levels(
        byte[] rgba, int width, int height, int lastMax = 1)
    {
        byte[] cur = rgba;
        int w = width, h = height;
        while (true)
        {
            yield return (cur, w, h);
            if (Math.Max(w, h) <= lastMax) yield break;
            cur = Halve(cur, w, h, out int nw, out int nh);
            w = nw;
            h = nh;
        }
    }

    /// <summary>Nearest power of two to <paramref name="value"/> (at least 4, at most 16384).</summary>
    public static int NearestPowerOfTwo(int value)
    {
        int floor = FloorPowerOfTwo(Math.Max(1, value));
        int ceil = floor <= 1 ? 1 : floor * 2;
        int result = value - floor <= ceil - value ? floor : ceil;
        return Math.Clamp(result, 4, 16384);
    }

    /// <summary>Largest power of two that is &lt;= value (at least 1).</summary>
    public static int FloorPowerOfTwo(int value)
    {
        int p = 1;
        while (p * 2 <= value) p *= 2;
        return p;
    }

    private readonly struct ResampleWeights
    {
        public required int[] Starts { get; init; }
        public required int[] Counts { get; init; }
        public required int[] Offsets { get; init; }
        public required float[] Values { get; init; }
    }

    /// <summary>Per-destination-pixel taps: box coverage when shrinking, bilinear when enlarging.</summary>
    private static ResampleWeights BuildWeights(int source, int dest, bool shrink)
    {
        var starts = new int[dest];
        var counts = new int[dest];
        var offsets = new int[dest];
        var values = new List<float>(dest * (shrink ? 4 : 2));
        float scale = source / (float)dest;

        for (int i = 0; i < dest; i++)
        {
            offsets[i] = values.Count;
            if (shrink)
            {
                float begin = i * scale;
                float end = Math.Min((i + 1) * scale, source);
                int s = (int)MathF.Floor(begin);
                int e = Math.Max(s + 1, (int)MathF.Ceiling(end));
                if (e > source) e = source;
                starts[i] = s;
                counts[i] = e - s;
                float span = Math.Max(1e-6f, end - begin);
                for (int k = s; k < e; k++)
                {
                    float lo = Math.Max(begin, k);
                    float hi = Math.Min(end, k + 1);
                    values.Add(Math.Max(0f, hi - lo) / span);
                }
            }
            else
            {
                float center = (i + 0.5f) * scale - 0.5f;
                int s = (int)MathF.Floor(center);
                float t = center - s;
                starts[i] = s;
                counts[i] = 2;
                values.Add(1f - t);
                values.Add(t);
            }
        }

        return new ResampleWeights
        {
            Starts = starts, Counts = counts, Offsets = offsets, Values = values.ToArray(),
        };
    }

    /// <summary>General RGBA resample: exact area average when shrinking, bilinear when enlarging.</summary>
    public static byte[] Resample(ReadOnlySpan<byte> rgba, int width, int height, int newWidth, int newHeight)
    {
        if (width == newWidth && height == newHeight) return rgba.ToArray();

        bool shrink = newWidth < width || newHeight < height;
        ResampleWeights wx = BuildWeights(width, newWidth, shrink);
        ResampleWeights wy = BuildWeights(height, newHeight, shrink);

        // horizontal pass -> float buffer of newWidth x height
        var temp = new float[newWidth * height * 4];
        for (int y = 0; y < height; y++)
        {
            int srcRow = y * width * 4;
            for (int x = 0; x < newWidth; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                int count = wx.Counts[x], offset = wx.Offsets[x];
                for (int k = 0; k < count; k++)
                {
                    int srcX = wx.Starts[x] + k;
                    if (srcX < 0) srcX = 0;
                    else if (srcX >= width) srcX = width - 1;
                    float w = wx.Values[offset + k];
                    int o = srcRow + srcX * 4;
                    r += rgba[o] * w;
                    g += rgba[o + 1] * w;
                    b += rgba[o + 2] * w;
                    a += rgba[o + 3] * w;
                }
                int d = (y * newWidth + x) * 4;
                temp[d] = r;
                temp[d + 1] = g;
                temp[d + 2] = b;
                temp[d + 3] = a;
            }
        }

        // vertical pass -> byte output
        var dest = new byte[newWidth * newHeight * 4];
        for (int y = 0; y < newHeight; y++)
        {
            int count = wy.Counts[y], offset = wy.Offsets[y];
            for (int x = 0; x < newWidth; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (int k = 0; k < count; k++)
                {
                    int srcY = wy.Starts[y] + k;
                    if (srcY < 0) srcY = 0;
                    else if (srcY >= height) srcY = height - 1;
                    float w = wy.Values[offset + k];
                    int o = (srcY * newWidth + x) * 4;
                    r += temp[o] * w;
                    g += temp[o + 1] * w;
                    b += temp[o + 2] * w;
                    a += temp[o + 3] * w;
                }
                int d = (y * newWidth + x) * 4;
                dest[d] = ToByte(r);
                dest[d + 1] = ToByte(g);
                dest[d + 2] = ToByte(b);
                dest[d + 3] = ToByte(a);
            }
        }
        return dest;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
    /// <summary>Flips an RGBA image vertically (Aerofly liveries are stored bottom-up).</summary>
    public static byte[] FlipVertical(ReadOnlySpan<byte> rgba, int width, int height)
    {
        int row = width * 4;
        var dest = new byte[rgba.Length];
        for (int y = 0; y < height; y++)
            rgba.Slice(y * row, row).CopyTo(dest.AsSpan((height - 1 - y) * row, row));
        return dest;
    }

    /// <summary>Pads an RGBA image up to a multiple of <paramref name="align"/> by edge clamping.</summary>
    public static byte[] PadToMultiple(ReadOnlySpan<byte> rgba, int width, int height, int align,
        out int paddedWidth, out int paddedHeight)
    {
        paddedWidth = (width + align - 1) / align * align;
        paddedHeight = (height + align - 1) / align * align;
        if (paddedWidth == width && paddedHeight == height) return rgba.ToArray();

        var dest = new byte[paddedWidth * paddedHeight * 4];
        for (int y = 0; y < paddedHeight; y++)
        {
            int sy = Math.Min(y, height - 1);
            int srcRow = sy * width * 4;
            int dstRow = y * paddedWidth * 4;
            for (int x = 0; x < paddedWidth; x++)
            {
                int sx = Math.Min(x, width - 1);
                int so = srcRow + sx * 4;
                int do_ = dstRow + x * 4;
                dest[do_] = rgba[so];
                dest[do_ + 1] = rgba[so + 1];
                dest[do_ + 2] = rgba[so + 2];
                dest[do_ + 3] = rgba[so + 3];
            }
        }
        return dest;
    }
}



