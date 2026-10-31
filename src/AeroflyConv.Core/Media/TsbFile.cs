using System.Buffers.Binary;
using System.Text;
using AeroflyConv.Core.Tm;

namespace AeroflyConv.Core.Media;

/// <summary>Uncompressed RIFF/WAVE PCM helper.</summary>
public static class WavFile
{
    public sealed class WavInfo
    {
        public required int Channels { get; init; }
        public required int SampleRate { get; init; }
        public required int Bits { get; init; }
        public required byte[] Pcm { get; init; }
        public int Frames => Channels > 0 ? Pcm.Length / (Channels * Math.Max(1, Bits / 8)) : 0;
    }

    public static WavInfo Parse(byte[] data)
    {
        if (data.Length < 12 ||
            !data.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !data.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new AeroflyFormatException("not a RIFF/WAVE file");

        int channels = 0, rate = 0, bits = 0;
        int pos = 12;
        while (pos + 8 <= data.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4, 4));
            string id = Encoding.ASCII.GetString(data, pos, 4);
            int body = pos + 8;
            if (id == "fmt " && length >= 16 && body + 16 <= data.Length)
            {
                int tag = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body, 2));
                if (tag != 1 && tag != 0xFFFE)
                    throw new AeroflyFormatException("only uncompressed (PCM) WAV files are supported");
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 2, 2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 14, 2));
            }
            else if (id == "data")
            {
                int available = Math.Min((int)length, data.Length - body);
                if (available < 0) available = 0;
                if (channels == 0) throw new AeroflyFormatException("WAV has no fmt chunk");
                return new WavInfo
                {
                    Channels = channels,
                    SampleRate = rate,
                    Bits = bits,
                    Pcm = data.AsSpan(body, available).ToArray(),
                };
            }
            pos = body + (int)length + ((int)length & 1);
        }
        throw new AeroflyFormatException("WAV has no data chunk");
    }

    public static byte[] Build(ReadOnlySpan<byte> pcm, int sampleRate, int channels, int bits)
    {
        int blockAlign = channels * Math.Max(1, bits / 8);
        var header = new byte[44];
        Encoding.ASCII.GetBytes("RIFF", header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), (uint)(36 + pcm.Length));
        Encoding.ASCII.GetBytes("WAVE", header.AsSpan(8));
        Encoding.ASCII.GetBytes("fmt ", header.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20, 2), 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22, 2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24, 4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28, 4), (uint)(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32, 2), (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34, 2), (ushort)bits);
        Encoding.ASCII.GetBytes("data", header.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), (uint)pcm.Length);

        var result = new byte[header.Length + pcm.Length];
        header.CopyTo(result, 0);
        pcm.CopyTo(result.AsSpan(header.Length));
        return result;
    }
}

/// <summary>Parsed TSB sound container.</summary>
public sealed class TsbSound
{
    public required string Format { get; init; }
    public required float SampleRate { get; init; }
    public required int Channels { get; init; }
    public required int Bits { get; init; }
    public required byte[] Pcm { get; init; }
    public int Frames => Channels > 0 && Bits > 0 ? Pcm.Length / (Channels * (Bits / 8)) : 0;
}

/// <summary>Aerofly <c>.tsb</c> sound containers (plain mono/stereo N-bit PCM).</summary>
public static class TsbFile
{
    public static TsbSound Parse(ReadOnlySpan<byte> raw)
    {
        byte[] normalized = TmFormats.Deobfuscate(raw);
        byte[] inner = TmFormats.IsCompressedFile(normalized)
            ? TmCompress.InflateFile(normalized)
            : normalized;

        int pos = TmFormats.SkipContainers(inner);
        string? format = null;
        float sampleRate = -1;
        byte[]? pcm = null;

        while (pos + TmFormats.ChunkHeaderSize <= inner.Length)
        {
            long size = (long)TmFormats.U64(inner, pos + 16);
            long hs = (long)TmFormats.U64(inner, pos + 24);
            if (size < TmFormats.ChunkHeaderSize) break;

            if (size > 200)
            {
                int length = (int)Math.Min(size - TmFormats.ChunkHeaderSize, inner.Length - pos - TmFormats.ChunkHeaderSize);
                pcm = inner.AsSpan(pos + TmFormats.ChunkHeaderSize, length).ToArray();
                break;
            }

            int step = TmFormats.ChunkStep(size, hs);
            if (step < TmFormats.ChunkHeaderSize) throw new AeroflyFormatException($"bad sound chunk step at 0x{pos:x}");

            int payloadLength = Math.Min((int)size - TmFormats.ChunkHeaderSize, step - TmFormats.ChunkHeaderSize);
            ReadOnlySpan<byte> payload = inner.AsSpan(pos + TmFormats.ChunkHeaderSize, Math.Max(0, payloadLength));

            string? s = payload.Length >= 8 ? TmFormats.TryParseString(payload) : null;
            if (s is not null)
            {
                format = s;
            }
            else if (sampleRate < 0 && payload.Length is >= 4 and <= 8)
            {
                sampleRate = BinaryPrimitives.ReadSingleLittleEndian(payload);
            }
            pos += step;
        }

        if (format is null || sampleRate < 0 || pcm is null)
            throw new AeroflyFormatException(
                $"incomplete sound parse: format={format ?? "?"} rate={sampleRate} pcm={pcm?.Length}");

        ParseFormat(format, out int channels, out int bits);
        return new TsbSound
        {
            Format = format,
            SampleRate = sampleRate,
            Channels = channels,
            Bits = bits,
            Pcm = pcm,
        };
    }

    private static void ParseFormat(string format, out int channels, out int bits)
    {
        channels = 1;
        bits = 16;
        string lower = format.ToLowerInvariant();
        if (lower.StartsWith("stereo", StringComparison.Ordinal)) channels = 2;
        else if (lower.StartsWith("mono", StringComparison.Ordinal)) channels = 1;
        int idx = lower.StartsWith("stereo", StringComparison.Ordinal) ? 6 :
            lower.StartsWith("mono", StringComparison.Ordinal) ? 4 : -1;
        if (idx >= 0 && int.TryParse(lower.AsSpan(idx), out int parsed) && parsed > 0)
            bits = parsed;
    }

    public static byte[] ToWav(TsbSound sound) =>
        WavFile.Build(sound.Pcm, (int)Math.Round(sound.SampleRate), sound.Channels, sound.Bits);

    public static TsbSound FromWav(byte[] wav)
    {
        WavFile.WavInfo info = WavFile.Parse(wav);
        if (info.Channels is not (1 or 2))
            throw new AeroflyFormatException($"TSB supports mono or stereo only (got {info.Channels} channels)");
        if (info.Bits is not (8 or 16 or 24 or 32))
            throw new AeroflyFormatException($"TSB supports 8/16/24/32 bit samples (got {info.Bits})");
        return new TsbSound
        {
            Format = (info.Channels == 1 ? "mono" : "stereo") + info.Bits,
            SampleRate = info.SampleRate,
            Channels = info.Channels,
            Bits = info.Bits,
            Pcm = info.Pcm,
        };
    }

    public static byte[] Build(TsbSound sound)
    {
        byte[] name = Encoding.ASCII.GetBytes(sound.Format);
        int pad = (8 - (name.Length + 8) % 8) % 8;
        var namePayload = new byte[8 + name.Length + pad];
        BinaryPrimitives.WriteUInt64LittleEndian(namePayload.AsSpan(0, 8), (ulong)name.Length);
        name.CopyTo(namePayload, 8);

        int nameSize = TmFormats.ChunkHeaderSize + namePayload.Length;
        byte[] nameChunk = Chunk(namePayload, nameSize, nameSize);

        var rateChunk = new byte[40];
        BinaryPrimitives.WriteUInt64LittleEndian(rateChunk.AsSpan(16, 8), 36);
        BinaryPrimitives.WriteUInt64LittleEndian(rateChunk.AsSpan(24, 8), 40);
        BinaryPrimitives.WriteSingleLittleEndian(rateChunk.AsSpan(32, 4), sound.SampleRate);

        byte[] pcmChunk = Chunk(sound.Pcm, TmFormats.ChunkHeaderSize + sound.Pcm.Length, TmFormats.ChunkHeaderSize);

        int total = 32 + nameChunk.Length + rateChunk.Length + pcmChunk.Length;
        var blob = new byte[total];
        TmFormats.MagicSound.CopyTo(blob);
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(8, 8), TmNames.NameNested);
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(16, 8), (ulong)total);
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(24, 8), 32);
        int offset = 32;
        nameChunk.CopyTo(blob, offset); offset += nameChunk.Length;
        rateChunk.CopyTo(blob, offset); offset += rateChunk.Length;
        pcmChunk.CopyTo(blob, offset);
        return blob;
    }

    private static byte[] Chunk(ReadOnlySpan<byte> payload, int size, int headerSize)
    {
        // The chunk always occupies 32 + payload bytes; "size" and "headerSize" are just fields.
        var chunk = new byte[TmFormats.ChunkHeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(chunk.AsSpan(16, 8), (ulong)size);
        BinaryPrimitives.WriteUInt64LittleEndian(chunk.AsSpan(24, 8), (ulong)headerSize);
        payload.CopyTo(chunk.AsSpan(TmFormats.ChunkHeaderSize));
        return chunk;
    }
}

