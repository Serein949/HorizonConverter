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

    /// <summary>
    /// Where to write outputs. Null = next to each source file (original behaviour).
    /// When set, the input folder structure is mirrored below this directory.
    /// </summary>
    public string? OutputDirectory { get; set; }

    /// <summary>
    /// When overwriting an existing .ttx, resize the source image to match the original
    /// texture dimensions (Aerofly slots expect a fixed size, usually power-of-two).
    /// </summary>
    public bool FitExistingTexture { get; set; } = true;

    /// <summary>
    /// Force the output texture's longest side to this many pixels (must be a power of two).
    /// Null = automatic: match the existing slot when overwriting, otherwise the nearest
    /// power of two below the source size.
    /// </summary>
    public int? TargetTextureSize { get; set; }

    /// <summary>
    /// True when <see cref="Flip"/> was chosen explicitly (CLI --flip/--no-flip, GUI checkbox).
    /// When false the pipeline decides per file: texture modes flip, everything else does not.
    /// </summary>
    public bool FlipExplicit { get; set; }

    /// <summary>Set by <see cref="Converter.ConvertAll"/> so folder conversions mirror their layout.</summary>
    public string? MirrorRoot { get; set; }

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

    /// <summary>Every extension any mode accepts (used when the mode is "auto").</summary>
    public static string[] AllInputExtensions { get; } =
        Enum.GetValues<ConversionMode>()
            .Where(m => m != ConversionMode.Auto)
            .SelectMany(InputExtensions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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

    /// <summary>File extension produced by a mode (TM dumps keep the source extension).</summary>
    public static string OutputExtension(string input, ConversionMode mode) => mode switch
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

    public static string DefaultOutputPath(string input, ConversionMode mode) =>
        ResolveOutputPath(input, mode, new ConvertOptions());

    /// <summary>
    /// Resolves the output path: next to the source, or below <paramref name="root"/> while mirroring
    /// the input folder layout (so converting a whole tree keeps it organised).
    /// </summary>
    public static string ResolveOutputPath(string input, ConversionMode mode, ConvertOptions options, string? root = null)
    {
        string name = Path.GetFileNameWithoutExtension(input) + OutputExtension(input, mode);
        string? outputRoot = root ?? options.OutputDirectory;
        if (outputRoot is null)
            return Path.Combine(Path.GetDirectoryName(input) ?? ".", name);

        string relative = "";
        if (!string.IsNullOrEmpty(options.MirrorRoot))
        {
            string dir = Path.GetDirectoryName(input) ?? "";
            relative = Path.GetRelativePath(options.MirrorRoot!, dir);
            if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal))
                relative = "";
        }
        return Path.Combine(outputRoot, relative, name);
    }

    /// <summary>Creates the output directory and fails early with a clear message when it is read-only.</summary>
    private static void EnsureOutputDirectory(string outputPath)
    {
        string? dir = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
    }

    /// <summary>Expands folders recursively into files matching the mode's input extensions.</summary>
    public static List<string> Expand(IEnumerable<string> inputs, ConversionMode mode, bool recursive = true)
    {
        var files = new List<string>();
        foreach (string input in inputs)
        {
            if (Directory.Exists(input))
            {
                string[] exts = mode == ConversionMode.Auto ? AllInputExtensions : InputExtensions(mode);
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
        if (mode == ConversionMode.Auto) mode = DetectMode(input);

        bool explicitOutput = output is not null;
        output ??= ResolveOutputPath(input, mode, options);

        try
        {
            return Run(input, output, mode, options, log, sw);
        }
        catch (Exception ex) when (!explicitOutput && IsPermissionProblem(ex))
        {
            // Source folders under Program Files / SteamLibrary are often not writable - fall back
            // to the first writable output root so a batch never dies just because of where it reads.
            string? lastError = null;
            foreach (string root in FallbackRoots())
            {
                string fallback = ResolveOutputPath(input, mode, options, root);
                try
                {
                    ConversionOutcome outcome = Run(input, fallback, mode, options, log, sw);
                    return new ConversionOutcome
                    {
                        Input = outcome.Input,
                        Output = outcome.Output,
                        Mode = outcome.Mode,
                        Ok = true,
                        Summary = outcome.Summary + $"  [源目录不可写，已输出到 {root}]",
                        Milliseconds = outcome.Milliseconds,
                    };
                }
                catch (Exception inner)
                {
                    lastError = DescribeWriteError(inner, fallback);
                }
            }
            return Failure(input, mode,
                (lastError ?? ex.Message) + "  [could not find a writable output folder]", sw);
        }
        catch (Exception ex)
        {
            return Failure(input, mode, DescribeWriteError(ex, output), sw);
        }
    }

    private static ConversionOutcome Run(string input, string output, ConversionMode mode,
        ConvertOptions options, Action<string>? log, Stopwatch sw)
    {
        if (!options.Overwrite && File.Exists(output) &&
            !string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
        {
            return new ConversionOutcome
            {
                Input = input, Output = output, Mode = mode, Ok = true,
                Summary = "skipped (exists)", Milliseconds = 0,
            };
        }

        EnsureOutputDirectory(output);

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

    private static ConversionOutcome Failure(string input, ConversionMode mode, string error, Stopwatch sw)
    {
        sw.Stop();
        return new ConversionOutcome
        {
            Input = input, Output = null, Mode = mode, Ok = false,
            Error = error, Milliseconds = sw.Elapsed.TotalMilliseconds,
        };
    }

    /// <summary>Writable output locations tried when the source folder refuses writes.</summary>
    private static IEnumerable<string> FallbackRoots()
    {
        string exeDir = AppContext.BaseDirectory;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AeroflyConv-out");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AeroflyConv", "out");
        yield return Path.Combine(Path.GetTempPath(), "AeroflyConv-out");
        if (!string.IsNullOrEmpty(exeDir)) yield return Path.Combine(exeDir, "out");
    }

    private static bool IsTextureMode(ConversionMode mode) =>
        mode is ConversionMode.TtxToPng or ConversionMode.PngToTtx
            or ConversionMode.TtcToPng or ConversionMode.PngToTtc;

    private static bool IsPermissionProblem(Exception ex) =>
        ex is UnauthorizedAccessException ||
        (ex is IOException && ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase));

    private static string DescribeWriteError(Exception ex, string? output)
    {
        if (IsPermissionProblem(ex) && output is not null)
        {
            return $"cannot write '{output}': permission denied. " +
                   "Choose another output folder (CLI: -d/--outdir, GUI: \u201cOutput \u2192 Choose\u2026\u201d) " +
                   "or run the app as administrator.";
        }
        return ex.Message;
    }

    /// <summary>Converts many files, optionally in parallel, reporting each outcome as it finishes.</summary>
    public static List<ConversionOutcome> ConvertAll(IReadOnlyList<string> files, ConversionMode mode,
        ConvertOptions options, Action<ConversionOutcome>? progress = null, Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ConversionOutcome>(files.Count);
        object gate = new();
        int parallelism = Math.Clamp(options.MaxParallelism, 1, 64);

        // mirror the folder layout when writing into a dedicated output directory
        options.MirrorRoot = options.OutputDirectory is null ? null : CommonRoot(files);

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

    /// <summary>Deepest directory that contains every input file (used to mirror folder layouts).</summary>
    private static string? CommonRoot(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return null;
        string[] first = (Path.GetDirectoryName(Path.GetFullPath(files[0])) ?? "").Split(Path.DirectorySeparatorChar);
        int common = first.Length;
        for (int i = 1; i < files.Count; i++)
        {
            string[] parts = (Path.GetDirectoryName(Path.GetFullPath(files[i])) ?? "").Split(Path.DirectorySeparatorChar);
            common = Math.Min(common, parts.Length);
            for (int j = 0; j < common; j++)
            {
                if (!string.Equals(first[j], parts[j], StringComparison.OrdinalIgnoreCase))
                {
                    common = j;
                    break;
                }
            }
        }
        if (common == 0) return null;
        string root = string.Join(Path.DirectorySeparatorChar, first.Take(common));
        return root.Length == 0 ? Path.DirectorySeparatorChar.ToString() : root;
    }

    // ------------------------------------------------------------------ modes

    private static string TtxToPng(string input, string output, ConvertOptions options, Action<string>? log)
    {
        log?.Invoke($"Reading {Path.GetFileName(input)} ...");
        DecodedTexture texture = TtxFile.Decode(File.ReadAllBytes(input), options.Flip);
        if (!texture.PowerOfTwo)
            log?.Invoke(TmFormats.PowerOfTwoWarning(texture.Width, texture.Height));
        LogAlphaInfo(texture.Rgba, log);
        log?.Invoke($"Writing PNG ({texture.Format} {texture.Width}x{texture.Height} mips={texture.Mips}) ...");
        ImageIo.SavePng(output, texture.Rgba, texture.Width, texture.Height, texture.PngHasAlpha, options.PngQuality);
        return $"{texture.Format} {texture.Width}x{texture.Height} mips={texture.Mips}" +
               (texture.Compressed ? " (LZHAM)" : "");
    }

    /// <summary>
    /// Explains why a decoded texture may show a black area in ordinary image viewers: those pixels
    /// are fully transparent (alpha = 0) and many viewers paint transparency as black.
    /// </summary>
    /// <summary>Scales width/height so the longest side becomes <paramref name="longest"/> (power of two).</summary>
    private static (int W, int H) FitSize(int width, int height, int longest)
    {
        if (width <= 0 || height <= 0) return (longest, longest);
        double scale = longest / (double)Math.Max(width, height);
        int w = MipChain.NearestPowerOfTwo(Math.Max(1, (int)Math.Round(width * scale)));
        int h = MipChain.NearestPowerOfTwo(Math.Max(1, (int)Math.Round(height * scale)));
        return (Math.Min(w, 16384), Math.Min(h, 16384));
    }

    /// <summary>Explains why a decoded texture may show a black area in ordinary image viewers.</summary>
    private static void LogAlphaInfo(byte[] rgba, Action<string>? log)
    {
        if (log is null || rgba.Length == 0) return;
        int pixels = rgba.Length / 4;
        int transparent = 0;
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] == 0) transparent++;
        if (transparent == 0) return;
        log.Invoke($"note: {transparent * 100.0 / pixels:0.0}% of the pixels are transparent (alpha=0); " +
                   "viewers that ignore alpha show them as black");
    }

    private static string PngToTtx(string input, string output, ConvertOptions options, Action<string>? log)
    {
        log?.Invoke($"Reading {Path.GetFileName(input)} ...");
        (byte[] rgba, int w, int h) = ImageIo.LoadRgba(input);

        // A texture slot expects a fixed size and GPU format. When we overwrite an existing .ttx,
        // adopt both so the game keeps sampling the slot correctly; an explicit --size / GUI "Size"
        // choice overrides the slot size (e.g. 4096 for a sharper livery).
        int targetW = 0, targetH = 0;
        string? targetFormat = null;

        if (options.TargetTextureSize is int forced && forced >= 32)
        {
            (targetW, targetH) = FitSize(w, h, forced);
        }
        else if (options.FitExistingTexture && File.Exists(output) &&
                 output.EndsWith(".ttx", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                TtxInfo existing = TtxFile.Parse(TtxFile.Inflate(File.ReadAllBytes(output)));
                if (existing.Width >= 4 && existing.Height >= 4)
                {
                    targetW = existing.Width;
                    targetH = existing.Height;
                    if (TextureFormats.IsSupportedDecode(existing.Format))
                        targetFormat = existing.Format;
                }
            }
            catch (Exception)
            {
                // unreadable existing file: fall back to power-of-two sizing below
            }
        }

        if (targetW > 0 && (targetW != w || targetH != h))
        {
            log?.Invoke($"resizing {w}x{h} -> {targetW}x{targetH}");
            rgba = MipChain.Resample(rgba, w, h, targetW, targetH);
            w = targetW;
            h = targetH;
        }
        else if (!TmFormats.IsPowerOfTwo(w) || !TmFormats.IsPowerOfTwo(h))
        {
            int nw = MipChain.FloorPowerOfTwo(w);
            int nh = MipChain.FloorPowerOfTwo(h);
            log?.Invoke($"{TmFormats.PowerOfTwoWarning(w, h)} - resizing to {nw}x{nh}");
            rgba = MipChain.Resample(rgba, w, h, nw, nh);
            w = nw;
            h = nh;
        }

        string format = targetFormat ?? options.TtxFormat;
        if (targetFormat is not null &&
            TextureFormats.Canonical(options.TtxFormat) != targetFormat &&
            options.TtxFormat != TextureFormats.AutoDxt)
        {
            log?.Invoke($"using the existing slot format ({targetFormat})");
        }

        log?.Invoke($"Encoding {format} ...");
        TtxFile.EncodeResult result = TtxFile.Encode(rgba, w, h, format, options.Flip,
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
        LogAlphaInfo(texture.Rgba, log);
        log?.Invoke($"Writing PNG ({texture.Format} {texture.Width}x{texture.Height}) ...");
        ImageIo.SavePng(output, texture.Rgba, texture.Width, texture.Height, withAlpha: true, options.PngQuality);
        return $"{texture.Format} {texture.Width}x{texture.Height} mips={texture.Mips}";
    }

    private static string PngToTtc(string input, string output, ConvertOptions options, Action<string>? log)
    {
        log?.Invoke($"Reading {Path.GetFileName(input)} ...");
        (byte[] rgba, int w, int h) = ImageIo.LoadRgba(input);
        if (!TmFormats.IsPowerOfTwo(w) || !TmFormats.IsPowerOfTwo(h))
        {
            int nw = MipChain.FloorPowerOfTwo(w);
            int nh = MipChain.FloorPowerOfTwo(h);
            log?.Invoke($"{TmFormats.PowerOfTwoWarning(w, h)} - resizing to {nw}x{nh}");
            rgba = MipChain.Resample(rgba, w, h, nw, nh);
            w = nw;
            h = nh;
        }
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












