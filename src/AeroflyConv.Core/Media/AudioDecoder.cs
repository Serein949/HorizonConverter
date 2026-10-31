using FlacBox;
using NVorbis;
using NLayer;

namespace AeroflyConv.Core.Media;

/// <summary>Decoded audio, always 16-bit signed little endian PCM.</summary>
public sealed class DecodedAudio
{
    public required byte[] Pcm16 { get; init; }
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required string Source { get; init; }
    public int Frames => Channels > 0 ? Pcm16.Length / (Channels * 2) : 0;
}

/// <summary>MP3 / OGG Vorbis / FLAC / WAV decoders (all pure managed).</summary>
public static class AudioDecoder
{
    public static readonly string[] SupportedExtensions = [".mp3", ".ogg", ".flac", ".wav"];

    public static DecodedAudio Decode(string path, Action<string>? status = null)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp3" => DecodeMp3(path, status),
            ".ogg" => DecodeOgg(path, status),
            ".flac" => DecodeFlac(path, status),
            ".wav" => DecodeWav(path),
            _ => throw new AeroflyFormatException($"unsupported audio format: {ext}"),
        };
    }

    public static byte[] ToWav(DecodedAudio audio) =>
        WavFile.Build(audio.Pcm16, audio.SampleRate, audio.Channels, 16);

    private static DecodedAudio DecodeMp3(string path, Action<string>? status)
    {
        status?.Invoke("Decoding MP3 ...");
        using var file = new MpegFile(path);
        int channels = file.Channels > 0 ? file.Channels : 2;
        int rate = file.SampleRate;
        var floats = new float[channels * 8192];
        using var pcm = new MemoryStream(1 << 20);
        var bytes = new byte[floats.Length * 2];
        int read;
        long frames = 0;
        while ((read = file.ReadSamples(floats, 0, floats.Length)) > 0)
        {
            int n = FloatToPcm16(floats.AsSpan(0, read), bytes);
            pcm.Write(bytes, 0, n);
            frames += read / channels;
        }
        return new DecodedAudio
        {
            Pcm16 = pcm.ToArray(),
            SampleRate = rate,
            Channels = channels,
            Source = "mp3",
        };
    }

    private static DecodedAudio DecodeOgg(string path, Action<string>? status)
    {
        status?.Invoke("Decoding OGG ...");
        using var reader = new VorbisReader(path);
        int channels = reader.Channels;
        int rate = reader.SampleRate;
        var floats = new float[channels * 8192];
        using var pcm = new MemoryStream(1 << 20);
        var bytes = new byte[floats.Length * 2];
        int read;
        while ((read = reader.ReadSamples(floats, 0, floats.Length)) > 0)
        {
            int n = FloatToPcm16(floats.AsSpan(0, read), bytes);
            pcm.Write(bytes, 0, n);
        }
        return new DecodedAudio
        {
            Pcm16 = pcm.ToArray(),
            SampleRate = rate,
            Channels = channels,
            Source = "ogg",
        };
    }

    private static DecodedAudio DecodeFlac(string path, Action<string>? status)
    {
        status?.Invoke("Decoding FLAC ...");
        using var stream = File.OpenRead(path);
        var reader = new FlacReader(stream, true);
        int guard = 0;
        while (reader.Streaminfo is null && guard++ < 32 && reader.Read()) { }
        FlacStreaminfo info = reader.Streaminfo
            ?? throw new AeroflyFormatException("FLAC stream has no STREAMINFO block");

        int channels = info.ChannelsCount;
        int rate = info.SampleRate;
        int bits = info.BitsPerSample;
        int shift = Math.Max(0, bits - 16);

        using var pcm = new MemoryStream((int)Math.Min(int.MaxValue, info.TotalSampleCount * channels * 2));
        var buffer = new byte[channels * 4096 * 2];
        var values = new List<int>(channels * 4096);

        while (true)
        {
            bool more;
            try
            {
                more = reader.Read();
            }
            catch (Exception) when (pcm.Length > 0)
            {
                // FlacBox throws "Frame sync is expected" once the last frame has been consumed.
                break;
            }
            if (!more) break;
            if (reader.RecordType != FlacRecordType.Frame) continue;
            values.Clear();
            foreach (int v in reader.GetValues()) values.Add(v);
            int count = values.Count;
            if (count == 0) continue;
            int bytes = count * 2;
            if (buffer.Length < bytes) buffer = new byte[bytes];
            for (int i = 0; i < count; i++)
            {
                int s = shift > 0 ? (values[i] + (1 << (shift - 1))) >> shift : values[i];
                if (s > 32767) s = 32767;
                else if (s < -32768) s = -32768;
                buffer[i * 2] = (byte)(s & 0xFF);
                buffer[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            pcm.Write(buffer, 0, bytes);
        }
        reader.Close();

        return new DecodedAudio
        {
            Pcm16 = pcm.ToArray(),
            SampleRate = rate,
            Channels = channels,
            Source = "flac",
        };
    }

    private static DecodedAudio DecodeWav(string path)
    {
        WavFile.WavInfo info = WavFile.Parse(File.ReadAllBytes(path));
        byte[] pcm;
        if (info.Bits == 16)
        {
            pcm = info.Pcm;
        }
        else if (info.Bits == 8)
        {
            pcm = new byte[info.Pcm.Length * 2];
            for (int i = 0; i < info.Pcm.Length; i++)
            {
                short s = (short)((info.Pcm[i] - 128) << 8);
                pcm[i * 2] = (byte)(s & 0xFF);
                pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
        }
        else if (info.Bits is 24 or 32)
        {
            int bytesPerSample = info.Bits / 8;
            int count = info.Pcm.Length / bytesPerSample;
            pcm = new byte[count * 2];
            for (int i = 0; i < count; i++)
            {
                int o = i * bytesPerSample;
                int s = info.Bits == 24
                    ? (info.Pcm[o + 1] << 8 | info.Pcm[o + 2])
                    : (info.Pcm[o + 2] << 8 | info.Pcm[o + 3]);
                pcm[i * 2] = (byte)(s & 0xFF);
                pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
        }
        else
        {
            throw new AeroflyFormatException($"unsupported WAV bit depth: {info.Bits}");
        }

        return new DecodedAudio
        {
            Pcm16 = pcm,
            SampleRate = info.SampleRate,
            Channels = info.Channels,
            Source = "wav",
        };
    }

    /// <summary>Converts float samples (-1..1) to signed 16-bit little endian. Returns bytes written.</summary>
    public static int FloatToPcm16(ReadOnlySpan<float> samples, Span<byte> dest)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            float v = samples[i];
            int s = (int)MathF.Round(v * 32767f);
            if (s > 32767) s = 32767;
            else if (s < -32768) s = -32768;
            dest[i * 2] = (byte)(s & 0xFF);
            dest[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return samples.Length * 2;
    }
}
