using System.Diagnostics;
using AeroflyConv.Core.Media;
using AeroflyConv.Core.Textures;
using AeroflyConv.Core.Tm;

namespace AeroflyConv.Core.Conversion;

/// <summary>Conversion modes exposed by the CLI and the GUI.</summary>
public enum ConversionMode
{
    TtxToPng,
    PngToTtx,
    TsbToWav,
    WavToTsb,
    AudioToWav,
    TmToTxt,
    TtcToPng,
    PngToTtc,
    ZipToTme,
    TmeToZip,
    Auto,
}

public sealed class ConvertOptions
{
    /// <summary>Flip textures vertically (Aerofly liveries are stored bottom-up).</summary>
    public bool Flip { get; set; }

    /// <summary>Target TTX format: <c>dxt_auto</c> (default), <c>type_rgba</c>, <c>dxt1</c>, ...</summary>
    public string TtxFormat { get; set; } = TextureFormats.AutoDxt;

    /// <summary>Target TTC format: <c>dxt1</c>, <c>dxt5</c>, <c>etc2</c> or <c>r8</c>.</summary>
    public string TtcFormat { get; set; } = "dxt1";

    public bool TtcMipmaps { get; set; } = true;

    /// <summary>Header "extra"/zoom for TTC output; null = derive from the file name.</summary>
    public int? TtcZoom { get; set; }

    /// <summary>Write TTX as <c>compress_file=true</c> (LZHAM).</summary>
    public bool CompressTtx { get; set; } = true;

    public int MaxParallelism { get; set; } = Environment.ProcessorCount;

    public bool Overwrite { get; set; } = true;

    /// <summary>PNG output speed/size trade-off.</summary>
    public PngQuality PngQuality { get; set; } = PngQuality.Fast;

    public ConvertOptions Clone() => (ConvertOptions)MemberwiseClone();
}

public sealed class ConversionOutcome
{
    public required string Input { get; init; }
    public string? Output { get; init; }
    public required ConversionMode Mode { get; init; }
    public bool Ok { get; init; }
    public string Summary { get; init; } = "";
    public string? Error { get; init; }
    public double Milliseconds { get; init; }
}

/// <summary>File level conversion pipeline (shared by CLI and GUI).</summary>
public static class Converter
{
    public static string[] InputExtensions(ConversionMode mode) => mode switch
    {
        ConversionMode.TtxToPng => [".ttx"],
        ConversionMode.PngToTtx => ImageIo.SupportedExtensions,
        ConversionMode.TsbToWav => [".tsb"],
        ConversionMode.WavToTsb => [".wav"],
        ConversionMode.AudioToWav => [".mp3", ".flac", ".ogg"],
        ConversionMode.TmToTxt => [".toc", ".tsc", ".wad", ".tmb", ".tsl"],
        ConversionMode.TtcToPng => [".ttc"],
        ConversionMode.PngToTtc => ImageIo.SupportedExtensions,
        ConversionMode.ZipToTme => [".zip"],
        ConversionMode.TmeToZip => [".tme"],
        _ => [],
    };

    public static ConversionMode DetectMode(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".ttx" => ConversionMode.TtxToPng,
            ".ttc" => ConversionMode.TtcToPng,
            ".tsb" => ConversionMode.TsbToWav,
            ".wav" => ConversionMode.WavToTsb,
            ".mp3" or ".flac" or ".ogg" => ConversionMode.AudioToWav,
            ".toc" or ".tsc" or ".wad" or ".tmb" or ".tsl" => ConversionMode.TmToTxt,
            ".zip" => ConversionMode.ZipToTme,
            ".tme" => ConversionMode.TmeToZip,
            _ when ImageIo.SupportedExtensions.Contains(ext) => ConversionMode.PngToTtx,
            _ => ConversionMode.Auto,
        };
    }

    public static string DefaultOutputPath(string input, ConversionMode mode)
    {
        string dir = Path.GetDirectoryName(input) ?? ".";
        string name = Path.GetFileNameWithoutExtension(input);
        string ext = mode switch
        {
            ConversionMode.TtxToPng => ".png",
            ConversionMode.PngToTtx => ".ttx",
            ConversionMode.TsbToWav => ".wav",
            ConversionMode.WavToTsb => ".tsb",
            ConversionMode.AudioToWav => ".wav",
            ConversionMode.TmToTxt => Path.GetExtension(input) + ".txt",
            ConversionMode.TtcToPng => ".png",
            ConversionMode.PngToTtc => ".ttc",
            ConversionMode.ZipToTme => ".tme",
            ConversionMode.TmeToZip => ".zip",
            _ => ".out",
        };
        return Path.Combine(dir, name + ext);
    }

    /// <summary>Expands folders recursively into files matching the mode's input extensions.</summary>
    public static List<string> Expand(IEnumerable<string> inputs, ConversionMode mode, bool recursive = true)
    {
        var files = new List<string>();
        foreach (string input in inputs)
        {
            if (Directory.Exists(input))
            {
                string[] exts = InputExtensions(mode);
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = recursive,
                    IgnoreInaccessible = true,
                };
                foreach (string file in Directory.EnumerateFiles(input, "*", options))
                {
                    if (exts.Length == 0 || exts.Contains(Path.GetExtension(file).ToLowerInvariant()))
                        files.Add(file);
                }
            }
            else if (File.Exists(input))
            {
                files.Add(input);
            }
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    /// <summary>Runs one file through the pipeline and writes the output.</summary>
    public static ConversionOutcome ConvertFile(string input, string? output, ConversionMode mode,
        ConvertOptions options, Action<string>? log = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (mode == ConversionMode.Auto) mode = DetectMode(input);
            output ??= DefaultOutputPath(input, mode);

            if (!options.Overwrite && File.Exists(output) &&
                !string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            {
                return new ConversionOutcome
                {
                    Input = input, Output = output, Mode = mode, Ok = true,
                    Summary = "skipped (exists)", Milliseconds = 0,
                };
            }

            string summary = mode switch
            {
                ConversionMode.TtxToPng => TtxToPng(input, output, options, log),
                ConversionMode.PngToTtx => PngToTtx(input, output, options, log),
                ConversionMode.TsbToWav => TsbToWav(input, output, log),
                ConversionMode.WavToTsb => WavToTsb(input, output, log),
                ConversionMode.AudioToWav => AudioToWav(input, output, log),
                ConversionMode.TmToTxt => TmToTxt(input, output, log),
                ConversionMode.TtcToPng => TtcToPng(input, output, options, log),
                ConversionMode.PngToTtc => PngToTtc(input, output, options, log),
                ConversionMode.ZipToTme => ZipToTme(input, output),
                ConversionMode.TmeToZip => TmeToZip(input, output),
                _ => throw new AeroflyFormatException($"cannot detect a conversion for {Path.GetFileName(input)}"),
            };

            sw.Stop();
            return new ConversionOutcome
            {
                Input = input, Output = output, Mode = mode, Ok = true,
                Summary = summary, Milliseconds = sw.Elapsed.TotalMilliseconds,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConversionOutcome
            {
                Input = input, Output = null, Mode = mode, Ok = false,
                Error = ex.Message, Milliseconds = sw.Elapsed.TotalMilliseconds,
            };
        }
    }

    /// <summary>Converts many files, optionally in parallel, reporting each outcome as it finishes.</summary>
    public static List<ConversionOutcome> ConvertAll(IReadOnlyList<string> files, ConversionMode mode,
        ConvertOptions options, Action<ConversionOutcome>? progress = null, Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ConversionOutcome>(files.Count);
        object gate = new();
        int parallelism = Math.Clamp(options.MaxParallelism, 1, 64);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = cancellationToken,
        };

        Parallel.ForEach(files, parallelOptions, file =>
        {
            if (cancellationToken.IsCancellationRequested) return;
            ConversionOutcome outcome = ConvertFile(file, null, mode, options, log);
            lock (gate)
            {
                results.Add(outcome);
                progress?.Invoke(outcome);
            }
        });

        results.Sort((a, b) => string.CompareOrdinal(a.Input, b.Input));
        return results;
    }

    // ------------------------------------------------------------------ modes

    private static string TtxToPng(string input, string output, ConvertOptions options, Action<string>? log)
    {
        log?.Invoke($"Reading {Path.GetFileName(input)} ...");
        DecodedTexture texture = TtxFile.Decode(File.ReadAllBytes(input), options.Flip);
        if (!texture.PowerOfTwo)
            log?.Invoke(TmFormats.PowerOfTwoWarning(texture.Width, texture.Height));
        log?.Invoke($"Writing PNG ({texture.Format} {texture.Width}x{texture.Height} mips={texture.Mips}) ...");
        ImageIo.SavePng(output, texture.Rgba, texture.Width, texture.Height, texture.PngHasAlpha, options.PngQuality);
        return $"{texture.Format} {texture.Width}x{texture.Height} mips={texture.Mips}" +
               (texture.Compressed ? " (LZHAM)" : "");
    }

    private static string PngToTtx(string input, string output, ConvertOptions options, Action<string>? log)
    {
        log?.Invoke($"Reading {Path.GetFileName(input)} ...");
        (byte[] rgba, int w, int h) = ImageIo.LoadRgba(input);
        if (!TmFormats.IsPowerOfTwo(w) || !TmFormats.IsPowerOfTwo(h))
            log?.Invoke(TmFormats.PowerOfTwoWarning(w, h));
        log?.Invoke($"Encoding {options.TtxFormat} ...");
        TtxFile.EncodeResult result = TtxFile.Encode(rgba, w, h, options.TtxFormat, options.Flip,
            options.CompressTtx, log);
        File.WriteAllBytes(output, result.Data);
        return $"{result.Format} {result.Width}x{result.Height} mips={result.Mips} " +
               $"{result.Data.Length:N0} bytes";
    }

    private static string TsbToWav(string input, string output, Action<string>? log)
    {
        log?.Invoke($"Decoding {Path.GetFileName(input)} ...");
        TsbSound sound = TsbFile.Parse(File.ReadAllBytes(input));
        File.WriteAllBytes(output, TsbFile.ToWav(sound));
        return $"{sound.Format} {sound.SampleRate:0} Hz {sound.Channels}ch {sound.Bits}-bit";
    }

    private static string WavToTsb(string input, string output, Action<string>? log)
    {
        log?.Invoke($"Encoding {Path.GetFileName(input)} ...");
        TsbSound sound = TsbFile.FromWav(File.ReadAllBytes(input));
        File.WriteAllBytes(output, TsbFile.Build(sound));
        return $"{sound.Format} {sound.SampleRate:0} Hz {sound.Frames} frames";
    }

    private static string AudioToWav(string input, string output, Action<string>? log)
    {
        DecodedAudio audio = AudioDecoder.Decode(input, log);
        File.WriteAllBytes(output, AudioDecoder.ToWav(audio));
        return $"{audio.Source} {audio.SampleRate} Hz {audio.Channels}ch {audio.Frames} frames";
    }

    private static string TmToTxt(string input, string output, Action<string>? log)
    {
        log?.Invoke($"Decoding {Path.GetFileName(input)} ...");
        string text = TmTextDump.DumpText(File.ReadAllBytes(input), out int inflated, wrapFile: true);
        File.WriteAllText(output, text, new System.Text.UTF8Encoding(false));
        int placements = TmTextDump.CountXrefs(text);
        return $"{inflated:N0} bytes inflated, placements={placements}";
    }

    private static string TtcToPng(string input, string output, ConvertOptions options, Action<string>? log)
    {
        log?.Invoke($"Reading {Path.GetFileName(input)} ...");
        DecodedTexture texture = TtcFile.Decode(File.ReadAllBytes(input), options.Flip);
        log?.Invoke($"Writing PNG ({texture.Format} {texture.Width}x{texture.Height}) ...");
        ImageIo.SavePng(output, texture.Rgba, texture.Width, texture.Height, withAlpha: true, options.PngQuality);
        return $"{texture.Format} {texture.Width}x{texture.Height} mips={texture.Mips}";
    }

    private static string PngToTtc(string input, string output, ConvertOptions options, Action<string>? log)
    {
        log?.Invoke($"Reading {Path.GetFileName(input)} ...");
        (byte[] rgba, int w, int h) = ImageIo.LoadRgba(input);
        TtcFile.EncodeResult result = TtcFile.Encode(rgba, w, h, options.TtcFormat, options.Flip,
            options.TtcMipmaps, options.TtcZoom, input);
        File.WriteAllBytes(output, result.Data);
        return $"{result.Format} {result.Width}x{result.Height} mips={result.Mips} extra={result.Extra} " +
               $"{result.Data.Length:N0} bytes";
    }

    private static string ZipToTme(string input, string output)
    {
        ZipTme.ZipToTme(input, output);
        return "zip -> tme";
    }

    private static string TmeToZip(string input, string output)
    {
        ZipTme.TmeToZip(input, output);
        return "tme -> zip";
    }

    /// <summary>Metadata-only description of a file (used by --info).</summary>
    public static string Describe(string input)
    {
        string ext = Path.GetExtension(input).ToLowerInvariant();
        byte[] raw = File.ReadAllBytes(input);
        switch (ext)
        {
            case ".ttx":
            {
                TtxInfo info = TtxFile.Parse(TtxFile.Inflate(raw));
                return $"{info.Format} {info.Width}x{info.Height} mips={info.Mips} " +
                       $"compressed={TmFormats.IsCompressedFile(raw)} pixels={info.Pixels.Length:N0}";
            }
            case ".ttc":
            {
                if (TtcFile.IsNativeTtc(raw))
                {
                    TtcFile.TtcInfo info = TtcFile.Parse(raw);
                    return $"native TTC {info.Format} {info.Width}x{info.Height} mips={info.Mips} " +
                           $"extra={info.Extra} {info.PayloadMethod} uncomp={info.Uncompressed:N0}";
                }
                DecodedTexture t = TtcFile.Decode(raw, flip: false);
                return $"TTX-twin .ttc {t.Format} {t.Width}x{t.Height} mips={t.Mips}";
            }
            case ".tsb":
            {
                TsbSound s = TsbFile.Parse(raw);
                return $"{s.Format} {s.SampleRate:0} Hz {s.Channels}ch {s.Bits}-bit frames={s.Frames}";
            }
            case ".toc" or ".tsc" or ".wad" or ".tmb" or ".tsl":
            {
                string text = TmTextDump.DumpText(raw, out int inflated, wrapFile: false);
                return $"TM container {inflated:N0} bytes inflated, " +
                       $"placements={TmTextDump.CountXrefs(text)}";
            }
            default:
                return $"{ext} file, {raw.Length:N0} bytes";
        }
    }
}
