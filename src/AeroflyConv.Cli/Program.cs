using System.Diagnostics;
using System.Runtime.InteropServices;
using AeroflyConv.Core;
using AeroflyConv.Core.Conversion;
using AeroflyConv.Core.Media;

namespace AeroflyConv.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length > 0 && args[0] is "-v" or "--version" or "version")
        {
            Console.WriteLine($"{ProductInfo.Name} / {ProductInfo.Banner}");
            Console.WriteLine("Aerofly FS TTX / TSB / TOC / TTC 转换器（纯 C# 实现）");
            return 0;
        }

        if (args.Length == 0 || args.Any(a => a is "-h" or "--help" or "help"))
        {
            PrintHelp();
            return args.Length == 0 ? 2 : 0;
        }

        ConversionMode mode = ConversionMode.Auto;
        string? output = null;
        var options = new ConvertOptions();
        var paths = new List<string>();
        bool info = false;
        bool flipSet = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-m":
                case "--mode":
                    mode = ParseMode(RequireArg(args, ref i, arg));
                    break;
                case "-o":
                case "--output":
                    output = RequireArg(args, ref i, arg);
                    break;
                case "-d":
                case "--outdir":
                    options.OutputDirectory = Path.GetFullPath(RequireArg(args, ref i, arg));
                    break;
                case "-f":
                case "--format":
                    options.TtxFormat = RequireArg(args, ref i, arg);
                    break;
                case "--ttc-format":
                    options.TtcFormat = RequireArg(args, ref i, arg);
                    break;
                case "-j":
                case "--jobs":
                    options.MaxParallelism = int.Parse(RequireArg(args, ref i, arg));
                    break;
                case "--flip":
                    options.Flip = true;
                    flipSet = true;
                    break;
                case "--no-flip":
                    options.Flip = false;
                    flipSet = true;
                    break;
                case "--no-mipmaps":
                    options.TtcMipmaps = false;
                    break;
                case "--no-compress":
                    options.CompressTtx = false;
                    break;
                case "--png-quality":
                    options.PngQuality = RequireArg(args, ref i, arg).ToLowerInvariant() switch
                    {
                        "fast" => PngQuality.Fast,
                        "balanced" => PngQuality.Balanced,
                        "best" => PngQuality.Best,
                        _ => throw new ArgumentException("--png-quality must be fast, balanced or best"),
                    };
                    break;
                case "--info":
                    info = true;
                    break;
                case "--no-overwrite":
                    options.Overwrite = false;
                    break;
                case "--size":
                    options.TargetTextureSize = int.Parse(RequireArg(args, ref i, arg));
                    break;
                case "--no-fit":
                    options.FitExistingTexture = false;
                    break;
                case "--astc-quality":
                {
                    string text = RequireArg(args, ref i, arg);
                    if (!AeroflyConv.Core.Codecs.AstcQualityNames.TryParse(text, out var quality))
                    {
                        Console.Error.WriteLine($"unknown ASTC quality: {text} (fast|standard|high)");
                        return 2;
                    }

                    options.AstcQuality = quality;
                    break;
                }
                case "--y-up":
                    options.ModelYUp = true;
                    break;
                case "--no-textures":
                    options.ModelTextures = false;
                    break;
                case "--livery":
                    options.ModelLivery = RequireArg(args, ref i, arg);
                    break;
                case "--no-uv-flip":
                    options.ModelFlipV = false;
                    break;
                case "--sp":
                case "--substance":
                    options.ModelSubstance = true;
                    break;
                case "--zoom":
                    options.TtcZoom = int.Parse(RequireArg(args, ref i, arg));
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"unknown option: {arg}");
                        return 2;
                    }
                    paths.Add(arg);
                    break;
            }
        }

        if (paths.Count == 0)
        {
            PrintHelp();
            return 2;
        }

        // Aerofly stores texture rows bottom-up: texture modes flip unless --no-flip was given.
        options.FlipExplicit = flipSet;

        if (info)
            return RunInfo(paths);

        List<string> files = Converter.Expand(paths, mode);
        if (files.Count == 0)
        {
            Console.Error.WriteLine("No matching files found.");
            return 1;
        }
        if (files.Count > 1 && output is not null)
        {
            Console.Error.WriteLine("-o/--output only works with a single input file; ignoring it.");
            output = null;
        }

        var total = Stopwatch.StartNew();
        int failures = 0;
        long bytes = 0;
        int done = 0;
        var gate = new object();

        void Report(ConversionOutcome outcome)
        {
            lock (gate)
            {
                done++;
                string label = Path.GetFileName(outcome.Input);
                if (outcome.Ok)
                {
                    bytes += SafeLength(outcome.Output);
                    Console.WriteLine(
                        $"[{done}/{files.Count}] {label} -> {Path.GetFileName(outcome.Output)}  " +
                        $"{outcome.Summary}  ({outcome.Milliseconds:0} ms)");
                }
                else
                {
                    failures++;
                    Console.Error.WriteLine($"[{done}/{files.Count}] {label}  ERROR: {outcome.Error}");
                }
            }
        }

        if (files.Count == 1 && output is not null)
        {
            Report(Converter.ConvertFile(files[0], output, mode, options));
        }
        else
        {
            Converter.ConvertAll(files, mode, options, progress: Report, log: null);
        }

        total.Stop();
        double seconds = Math.Max(total.Elapsed.TotalSeconds, 0.0001);
        Console.WriteLine();
        Console.WriteLine($"{files.Count - failures}/{files.Count} converted in {seconds:0.00} s " +
                          $"({files.Count / seconds:0.0} files/s, {bytes / 1048576.0 / seconds:0.0} MB/s output)");
        return failures == 0 ? 0 : 1;
    }

    private static long SafeLength(string? path)
    {
        try { return path is not null && File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
    }

    private static int RunInfo(List<string> paths)
    {
        List<string> files = Converter.Expand(paths, ConversionMode.Auto);
        if (files.Count == 0)
        {
            Console.Error.WriteLine("No matching files found.");
            return 1;
        }
        int failures = 0;
        foreach (string file in files)
        {
            try
            {
                Console.WriteLine($"{file}");
                Console.WriteLine($"    {Converter.Describe(file)}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"    ERROR: {ex.Message}");
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static string RequireArg(string[] args, ref int i, string option)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"{option} needs a value");
        return args[++i];
    }

    private static ConversionMode ParseMode(string value) => value.ToLowerInvariant() switch
    {
        "auto" => ConversionMode.Auto,
        "ttx2png" or "ttx" or "decode" => ConversionMode.TtxToPng,
        "png2ttx" or "ttxencode" or "encode" => ConversionMode.PngToTtx,
        "tsb2wav" or "tsb" => ConversionMode.TsbToWav,
        "wav2tsb" => ConversionMode.WavToTsb,
        "audio2wav" or "audio" or "mp3" => ConversionMode.AudioToWav,
        "tm2txt" or "toc" or "txt" => ConversionMode.TmToTxt,
        "ttc2png" or "ttc" => ConversionMode.TtcToPng,
        "png2ttc" or "ttcencode" => ConversionMode.PngToTtc,
        "zip2tme" => ConversionMode.ZipToTme,
        "tme2zip" => ConversionMode.TmeToZip,
        "audio2tsb" or "tsbencode" => ConversionMode.AudioToTsb,
        "ttx2ttx" or "recode" or "retranscode" => ConversionMode.TtxToTtx,
        "tmb2obj" or "tmb2model" or "model" or "obj" => ConversionMode.TmbToObj,
        _ => throw new ArgumentException($"unknown mode: {value}"),
    };

    private static void PrintHelp()
    {
        Console.WriteLine("""
            AeroflyConv - fast Aerofly FS TTX / TSB / TOC / TTC converter (C#)

            Usage:
              AeroflyConv <file-or-folder> [more paths...] [options]

            The mode is detected from the file extension unless -m is given:
              .ttx                      -> PNG
              .png .jpg .bmp .psd ...   -> TTX (texture)
              .ttc                      -> PNG (scenery tile)
              .tmb                      -> OBJ (3D model + MTL + textures)
              .tsb                      -> WAV
              .wav                      -> TSB
              .mp3 .flac .ogg           -> WAV
              .toc .tsc .wad .tsl       -> TXT (generic TM dump)
              .zip                      -> TME
              .tme                      -> ZIP

            Options:
              -m, --mode <name>       auto|ttx2png|png2ttx|tsb2wav|wav2tsb|audio2wav|
                                      tm2txt|ttc2png|png2ttc|zip2tme|tme2zip|
                                      audio2tsb|ttx2ttx|tmb2obj
              -o, --output <path>     output path (single input only)
              -d, --outdir <dir>      write outputs below this folder (mirrors input folders).
                                      Needed when the source folder is read-only, e.g. inside the
                                      SteamLibrary game directory.
              -f, --format <fmt>      TTX target format (default dxt_auto):
                                      dxt_auto, dxt1, dxt5, type_rgba, type_r,
                                      etc2, type_rgba_etc2,
                                      astc6x6, astc5x5, type_rgba_astc_6x6,
                                      type_rgb_astc_6x6, astc_auto
                  --ttc-format <f>    TTC target format: dxt1 (default), dxt5, etc2, r8
                  --flip/--no-flip    flip textures vertically (default: on for texture modes,
                                      Aerofly stores textures bottom-up)
                  --no-mipmaps        TTC: write the top mip only
                  --no-compress       TTX: write an uncompressed container
                  --png-quality <q>   fast (default) | balanced | best
                  --size <n>          force the output texture size (power of two, e.g. 4096)
                  --no-fit            do not match the size/format of an existing .ttx
                  --astc-quality <q>  ASTC encoding: fast | standard (default) | high
                  --y-up              tmb2obj: convert Z-up source to Y-up (Blender/glTF)
                  --no-textures       tmb2obj: geometry only, skip texture conversion
                  --no-uv-flip        tmb2obj: keep UVs as stored (for models that look mirrored)
                  --livery <folder>   tmb2obj: use this livery sub folder for textures
                  --sp, --substance   tmb2obj: flat textures next to the OBJ, named <material>_basecolor.png
                                      (ready for Substance Painter texture sets)
                  --zoom <n>          TTC header "extra"/zoom (default: from map_N_ name)
                  --no-overwrite      skip files that already exist
                  --info              only print metadata
              -j, --jobs <n>          parallel workers (default: CPU count)
              -h, --help              this help

            Examples:
              AeroflyConv "C:\Aerofly FS 4\aircraft" -m ttx2png -j 8
              AeroflyConv livery.png -m png2ttx -f dxt_auto --flip
              AeroflyConv scenery.tmb -m tm2txt
              AeroflyConv a350_1000.tmb -m tmb2obj
              (tmb2obj extras: --y-up, --no-textures, --no-uv-flip, --livery, --sp)
              AeroflyConv "E:\SteamLibrary\...\aircraft" -d D:\extracted -j 16
            """);
    }
}




