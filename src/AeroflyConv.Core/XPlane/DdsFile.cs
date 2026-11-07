using System.Buffers.Binary;
using AeroflyConv.Core.Codecs;

namespace AeroflyConv.Core.XPlane;

/// <summary>Decoded image data.</summary>
public sealed class DdsImage
{
    public int Width { get; init; }
    public int Height { get; init; }
    public required byte[] Rgba { get; init; }
    public string Format { get; init; } = "";
}

/// <summary>
/// Minimal DDS reader for the formats X-Plane ships (DXT1/DXT3/DXT5, BC5 normals and plain 32-bit).
/// Mip chains are ignored: only the base level is decoded, since Aerofly regenerates mips.
/// </summary>
public static class DdsFile
{
    private const uint Magic = 0x20534444; // "DDS "

    public static bool IsDds(string path) =>
        path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);

    public static DdsImage ParseFile(string path) => Parse(File.ReadAllBytes(path));

    public static DdsImage Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
            throw new InvalidDataException("not a DDS file");

        int height = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        int width = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        int mipCount = Math.Max(1, (int)BinaryPrimitives.ReadUInt32LittleEndian(data[28..]));

        int pfFlags = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[80..]);
        string fourCc = new([(char)data[84], (char)data[85], (char)data[86], (char)data[87]]);
        int rgbBits = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[88..]);
        uint rMask = BinaryPrimitives.ReadUInt32LittleEndian(data[92..]);
        uint gMask = BinaryPrimitives.ReadUInt32LittleEndian(data[96..]);
        uint bMask = BinaryPrimitives.ReadUInt32LittleEndian(data[100..]);
        uint aMask = BinaryPrimitives.ReadUInt32LittleEndian(data[104..]);

        int offset = 128;
        int dxgiFormat = 0;
        if (fourCc == "DX10")
        {
            if (data.Length < 148 || BinaryPrimitives.ReadUInt32LittleEndian(data[128..]) != 20)
                throw new InvalidDataException("bad DDS DX10 header");
            dxgiFormat = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(132));
            offset = 148;
        }

        var rgba = new byte[width * height * 4];
        const int DDPF_FOURCC = 0x4;
        const int DDPF_RGB = 0x40;
        const int DDPF_LUMINANCE = 0x20000;

        if ((pfFlags & DDPF_FOURCC) != 0)
        {
            switch (fourCc)
            {
                case "DXT1":
                    BcCodec.DecodeBc1(data[offset..], width, height, rgba);
                    return new DdsImage { Width = width, Height = height, Rgba = rgba, Format = "DXT1" };
                case "DXT3":
                    BcCodec.DecodeBc2(data[offset..], width, height, rgba);
                    return new DdsImage { Width = width, Height = height, Rgba = rgba, Format = "DXT3" };
                case "DXT5":
                    BcCodec.DecodeBc3(data[offset..], width, height, rgba);
                    return new DdsImage { Width = width, Height = height, Rgba = rgba, Format = "DXT5" };
                case "ATI2":
                case "BC5U":
                {
                    var rg = new byte[width * height * 2];
                    BcCodec.DecodeBc5(data[offset..], width, height, rg);
                    for (int i = 0; i < width * height; i++)
                    {
                        rgba[i * 4] = rg[i * 2];
                        rgba[i * 4 + 1] = rg[i * 2 + 1];
                        // reconstruct Z of a unit normal
                        double x = rg[i * 2] / 127.5 - 1.0;
                        double y = rg[i * 2 + 1] / 127.5 - 1.0;
                        double z = Math.Sqrt(Math.Max(0.0, 1.0 - x * x - y * y));
                        rgba[i * 4 + 2] = (byte)Math.Clamp((z * 0.5 + 0.5) * 255.0, 0, 255);
                        rgba[i * 4 + 3] = 255;
                    }
                    return new DdsImage { Width = width, Height = height, Rgba = rgba, Format = "BC5" };
                }
                case "DX10":
                    return DecodeDxgi(dxgiFormat, data[offset..], width, height, rgba);
                default:
                    throw new NotSupportedException($"unsupported DDS fourCC '{fourCc}'");
            }
        }

        if ((pfFlags & (DDPF_RGB | DDPF_LUMINANCE)) != 0 && rgbBits is 32 or 24)
        {
            int bytesPerPixel = rgbBits / 8;
            int needed = width * height * bytesPerPixel;
            if (data.Length - offset < needed) throw new InvalidDataException("truncated DDS pixel data");
            for (int i = 0; i < width * height; i++)
            {
                uint pixel = bytesPerPixel == 4
                    ? BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + i * 4)..])
                    : (uint)(data[offset + i * 3] | (data[offset + i * 3 + 1] << 8) | (data[offset + i * 3 + 2] << 16));
                rgba[i * 4] = Extract(pixel, rMask, 255);
                rgba[i * 4 + 1] = Extract(pixel, gMask, 255);
                rgba[i * 4 + 2] = Extract(pixel, bMask, 255);
                rgba[i * 4 + 3] = aMask == 0 ? (byte)255 : Extract(pixel, aMask, 255);
            }
            return new DdsImage
            {
                Width = width, Height = height, Rgba = rgba,
                Format = $"RGB{rgbBits}",
            };
        }

        _ = mipCount;
        throw new NotSupportedException($"unsupported DDS layout (flags 0x{pfFlags:X}, bits {rgbBits})");
    }

    private static DdsImage DecodeDxgi(int dxgiFormat, ReadOnlySpan<byte> payload, int width, int height, byte[] rgba)
    {
        switch (dxgiFormat)
        {
            case 71: // BC1_UNORM
                BcCodec.DecodeBc1(payload, width, height, rgba);
                return new DdsImage { Width = width, Height = height, Rgba = rgba, Format = "BC1" };
            case 77: // BC3_UNORM
                BcCodec.DecodeBc3(payload, width, height, rgba);
                return new DdsImage { Width = width, Height = height, Rgba = rgba, Format = "BC3" };
            case 83: // BC5_UNORM
            {
                var rg = new byte[width * height * 2];
                BcCodec.DecodeBc5(payload, width, height, rg);
                for (int i = 0; i < width * height; i++)
                {
                    rgba[i * 4] = rg[i * 2];
                    rgba[i * 4 + 1] = rg[i * 2 + 1];
                    rgba[i * 4 + 2] = 255;
                    rgba[i * 4 + 3] = 255;
                }
                return new DdsImage { Width = width, Height = height, Rgba = rgba, Format = "BC5" };
            }
            case 28: // R8G8B8A8_UNORM
            case 29: // R8G8B8A8_UNORM_SRGB
                payload[..(width * height * 4)].CopyTo(rgba);
                return new DdsImage { Width = width, Height = height, Rgba = rgba, Format = "RGBA8" };
            case 87: // B8G8R8A8_UNORM
                for (int i = 0; i < width * height; i++)
                {
                    rgba[i * 4] = payload[i * 4 + 2];
                    rgba[i * 4 + 1] = payload[i * 4 + 1];
                    rgba[i * 4 + 2] = payload[i * 4];
                    rgba[i * 4 + 3] = payload[i * 4 + 3];
                }
                return new DdsImage { Width = width, Height = height, Rgba = rgba, Format = "BGRA8" };
            default:
                throw new NotSupportedException($"unsupported DXGI format {dxgiFormat}");
        }
    }

    private static byte Extract(uint pixel, uint mask, byte fallback)
    {
        if (mask == 0) return fallback;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint value = (pixel & mask) >> shift;
        uint max = mask >> shift;
        return (byte)(max == 0 ? 0 : value * 255 / max);
    }
}
