using System.Diagnostics;
using System.Text.Json;
using AeroflyConv.Core.Codecs;

namespace CodecCheck;

internal static class Program
{
    private const string DefaultPython =
        @"C:\Users\17353\AppData\Local\Temp\aero-ref\venv\Scripts\python.exe";

    private static string _repoRoot = "";
    private static string _checkDir = "";
    private static string _fixturesDir = "";
    private static string _python = DefaultPython;

    private static int Main(string[] args)
    {
        try
        {
            _repoRoot = FindRepoRoot();
            _checkDir = Path.Combine(_repoRoot, "tools", "CodecCheck");
            _fixturesDir = Path.Combine(_checkDir, "fixtures");
            _python = Environment.GetEnvironmentVariable("CODECCHECK_PYTHON") ?? DefaultPython;

            Console.WriteLine($"repo: {_repoRoot}");
            Console.WriteLine($"python: {_python}");

            bool skipGen = args.Contains("--skip-gen");
            if (!skipGen)
            {
                Console.WriteLine("generating fixtures with the python reference ...");
                var gen = Run(_python, $"\"{Path.Combine(_checkDir, "gen_fixtures.py")}\" \"{_fixturesDir}\"", _checkDir);
                Console.Write(gen.StdOut);
                if (gen.ExitCode != 0)
                {
                    Console.Error.WriteLine(gen.StdErr);
                    return Fail("fixture generation failed");
                }
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_fixturesDir, "manifest.json")));
            var root = doc.RootElement;

            int decodeTotal = 0, decodeFailed = 0;
            foreach (var c in root.GetProperty("decode").EnumerateArray())
            {
                decodeTotal++;
                if (!RunDecodeCase(c))
                {
                    decodeFailed++;
                }
            }

            Console.WriteLine();
            int encodeTotal = 0, encodeFailed = 0;
            foreach (var c in root.GetProperty("encode").EnumerateArray())
            {
                encodeTotal++;
                if (!RunEncodeCase(c))
                {
                    encodeFailed++;
                }
            }

            // Let the python reference decode our encodings and score PSNR.
            var verify = Run(_python, $"\"{Path.Combine(_checkDir, "verify_encodes.py")}\" \"{_fixturesDir}\"", _checkDir);
            Console.WriteLine(verify.StdOut);
            if (verify.ExitCode != 0)
            {
                Console.Error.WriteLine(verify.StdErr);
            }

            bool encodeQualityOk = true;
            var resultsPath = Path.Combine(_fixturesDir, "results.json");
            if (File.Exists(resultsPath))
            {
                using var rdoc = JsonDocument.Parse(File.ReadAllText(resultsPath));
                foreach (var c in rdoc.RootElement.GetProperty("cases").EnumerateArray())
                {
                    bool ok = c.GetProperty("ok").GetBoolean();
                    encodeQualityOk &= ok;
                    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {c.GetProperty("name").GetString(),-34} " +
                        $"RGB {c.GetProperty("psnr_rgb").GetDouble(),6:F2} dB  " +
                        $"A {c.GetProperty("psnr_alpha").GetDouble(),6:F2} dB");
                }
                var naive = rdoc.RootElement.GetProperty("naive_bc1");
                if (naive.ValueKind == JsonValueKind.Object)
                {
                    double n = naive.GetProperty("naive_psnr_rgb").GetDouble();
                    double o = naive.GetProperty("ours_psnr_rgb").GetDouble();
                    bool beat = naive.GetProperty("beats_naive").GetBoolean();
                    Console.WriteLine();
                    Console.WriteLine($"  BC1 vs naive reference on {naive.GetProperty("w").GetInt32()}x" +
                        $"{naive.GetProperty("h").GetInt32()}: naive {n:F2} dB, ours {o:F2} dB -> " +
                        $"{(beat ? "PASS (beats naive)" : "FAIL (not better than naive)")}");
                    encodeQualityOk &= beat;
                }
            }
            else
            {
                encodeQualityOk = false;
            }

            Console.WriteLine();
            Console.WriteLine("throughput (1024x1024):");
            RunBenchmarks();

            Console.WriteLine();
            bool pass = decodeFailed == 0 && encodeFailed == 0 && encodeQualityOk && verify.ExitCode == 0;
            Console.WriteLine($"decode:  {decodeTotal - decodeFailed}/{decodeTotal} byte-exact PASS");
            Console.WriteLine($"encode:  {encodeTotal - encodeFailed}/{encodeTotal} produced output, PSNR checks {(encodeQualityOk ? "PASS" : "FAIL")}");
            Console.WriteLine(pass ? "RESULT: PASS" : "RESULT: FAIL");
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return Fail(ex.Message);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("RESULT: FAIL - " + message);
        return 1;
    }

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "tools", "CodecCheck", "gen_fixtures.py")))
                {
                    return dir.FullName;
                }
            }
        }
        throw new InvalidOperationException("Could not locate the AeroflyConv repository root.");
    }

    private static (int ExitCode, string StdOut, string StdErr) Run(string exe, string arguments, string workdir)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start " + exe);
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static string FixturePath(string relative)
        => Path.Combine(_fixturesDir, relative.Replace('/', Path.DirectorySeparatorChar));

    private static bool RunDecodeCase(JsonElement c)
    {
        string name = c.GetProperty("name").GetString()!;
        string fmt = c.GetProperty("format").GetString()!;
        int w = c.GetProperty("width").GetInt32();
        int h = c.GetProperty("height").GetInt32();
        byte[] data = File.ReadAllBytes(FixturePath(c.GetProperty("data").GetString()!));
        byte[] expected = File.ReadAllBytes(FixturePath(c.GetProperty("expected").GetString()!));

        byte[] actual = fmt switch
        {
            "bc4" => new byte[w * h],
            "bc5" => new byte[w * h * 2],
            _ => new byte[w * h * 4],
        };

        switch (fmt)
        {
            case "bc1": BcCodec.DecodeBc1(data, w, h, actual); break;
            case "bc2": BcCodec.DecodeBc2(data, w, h, actual); break;
            case "bc3": BcCodec.DecodeBc3(data, w, h, actual); break;
            case "bc4": BcCodec.DecodeBc4(data, w, h, actual); break;
            case "bc5": BcCodec.DecodeBc5(data, w, h, actual); break;
            case "etc1": EtcCodec.DecodeEtc1(data, w, h, actual); break;
            case "etc2rgb": EtcCodec.DecodeEtc2Rgb(data, w, h, actual); break;
            case "etc2rgba": EtcCodec.DecodeEtc2Rgba(data, w, h, actual); break;
            default: Console.WriteLine($"  FAIL {name}: unknown format {fmt}"); return false;
        }

        if (actual.Length != expected.Length)
        {
            Console.WriteLine($"  FAIL {name}: decoded length {actual.Length}, expected {expected.Length}");
            return false;
        }
        int firstBad = -1, bad = 0;
        for (int i = 0; i < actual.Length; i++)
        {
            if (actual[i] != expected[i])
            {
                if (firstBad < 0) firstBad = i;
                bad++;
            }
        }

        if (bad == 0)
        {
            Console.WriteLine($"  PASS {name}");
            return true;
        }

        Console.WriteLine($"  FAIL {name}: {bad}/{actual.Length} bytes differ, first at {firstBad} " +
            $"(got {actual[firstBad]:X2}, expected {expected[firstBad]:X2})");
        return false;
    }

    private static bool RunEncodeCase(JsonElement c)
    {
        string name = c.GetProperty("name").GetString()!;
        string fmt = c.GetProperty("format").GetString()!;
        int w = c.GetProperty("width").GetInt32();
        int h = c.GetProperty("height").GetInt32();
        byte[] src = File.ReadAllBytes(FixturePath(c.GetProperty("source").GetString()!));
        int blocks = (w / 4) * (h / 4);

        byte[] dest = new byte[blocks * (fmt is "bc1" or "etc2rgb" ? 8 : 16)];
        switch (fmt)
        {
            case "bc1": BcCodec.EncodeBc1(src, w, h, dest); break;
            case "bc3": BcCodec.EncodeBc3(src, w, h, dest); break;
            case "etc2rgb": EtcCodec.EncodeEtc2Rgb(src, w, h, dest); break;
            case "etc2rgba": EtcCodec.EncodeEtc2Rgba(src, w, h, dest); break;
            default: Console.WriteLine($"  FAIL {name}: unknown encode format {fmt}"); return false;
        }

        // Sanity: no degenerate all-zero output.
        bool anyNonZero = false;
        foreach (byte b in dest)
        {
            if (b != 0) { anyNonZero = true; break; }
        }
        if (!anyNonZero)
        {
            Console.WriteLine($"  FAIL {name}: encoder produced an all-zero block stream");
            return false;
        }

        File.WriteAllBytes(FixturePath(c.GetProperty("encoded").GetString()!), dest);
        return true;
    }

    // ---------------------------------------------------------------------------------------
    // Throughput measurement
    // ---------------------------------------------------------------------------------------

    private static void RunBenchmarks()
    {
        const int W = 1024, H = 1024;
        byte[] src = MakeSource(W, H);

        int blocks = (W / 4) * (H / 4);
        byte[] bc1 = new byte[blocks * 8];
        byte[] bc3 = new byte[blocks * 16];
        byte[] etc2 = new byte[blocks * 8];
        byte[] etc2a = new byte[blocks * 16];
        byte[] rgba = new byte[W * H * 4];

        BcCodec.EncodeBc1(src, W, H, bc1);
        BcCodec.EncodeBc3(src, W, H, bc3);
        EtcCodec.EncodeEtc2Rgb(src, W, H, etc2);
        EtcCodec.EncodeEtc2Rgba(src, W, H, etc2a);

        Bench("BC1 decode", () => BcCodec.DecodeBc1(bc1, W, H, rgba), W * H * 4);
        Bench("BC3 decode", () => BcCodec.DecodeBc3(bc3, W, H, rgba), W * H * 4);
        Bench("ETC2 RGB decode", () => EtcCodec.DecodeEtc2Rgb(etc2, W, H, rgba), W * H * 4);
        Bench("ETC2 RGBA decode", () => EtcCodec.DecodeEtc2Rgba(etc2a, W, H, rgba), W * H * 4);
        BenchEncode("BC1 encode", () => BcCodec.EncodeBc1(src, W, H, bc1), W * H);
        BenchEncode("BC3 encode", () => BcCodec.EncodeBc3(src, W, H, bc3), W * H);
        BenchEncode("ETC2 RGB encode", () => EtcCodec.EncodeEtc2Rgb(src, W, H, etc2), W * H);
        BenchEncode("ETC2 RGBA encode", () => EtcCodec.EncodeEtc2Rgba(src, W, H, etc2a), W * H);
    }

    private static void Bench(string label, Action action, int outputBytes)
    {
        action();
        double best = double.MaxValue;
        const int reps = 5;
        for (int i = 0; i < reps; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        double mbps = outputBytes / (best / 1000.0) / (1024.0 * 1024.0);
        Console.WriteLine($"  {label,-18} {best,8:F2} ms   {mbps,8:F1} MB/s output");
    }

    private static void BenchEncode(string label, Action action, int pixels)
    {
        action();
        double best = double.MaxValue;
        const int reps = 5;
        for (int i = 0; i < reps; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        double mps = pixels / (best / 1000.0) / 1e6;
        Console.WriteLine($"  {label,-18} {best,8:F2} ms   {mps,8:F1} Mpixel/s");
    }

    private static byte[] MakeSource(int w, int h)
    {
        var px = new byte[w * h * 4];
        uint state = 0x9E3779B9;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                int noise = (int)(state & 0x1F) - 16;
                px[o] = (byte)Math.Clamp(32 + (x * 190 / w) + noise, 0, 255);
                px[o + 1] = (byte)Math.Clamp(32 + (y * 190 / h) + noise / 2, 0, 255);
                px[o + 2] = (byte)Math.Clamp(64 + ((x + y) * 150 / (w + h)) - noise, 0, 255);
                px[o + 3] = (byte)Math.Clamp(255 - (x * 128 / w), 0, 255);
            }
        }
        return px;
    }
}
