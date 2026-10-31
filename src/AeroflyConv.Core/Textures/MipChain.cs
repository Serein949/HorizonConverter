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
