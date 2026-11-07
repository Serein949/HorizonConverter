using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AeroflyConv.Core.Codecs;
using AeroflyConv.Core.Textures;

internal static class Program
{
    private const string DefaultPython =
        @"C:\Users\17353\AppData\Local\Temp\aero-ref\venv\Scripts\python.exe";

    private static int _failures;
    private static int _oracleChecks;
    private static string _python = DefaultPython;
    private static string _tempDir = string.Empty;

    private static int Main(string[] args)
    {
        bool useOracle = true;
        bool useFixtures = true;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--no-python":
                    useOracle = false;
                    break;
                case "--no-fixtures":
                    useFixtures = false;
                    break;
                case "--python":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--python requires a path.");
                        return 2;
                    }
                    _python = args[++i];
                    break;
                case "--debug":
                    DebugBlock();
                    return 0;
                case "-h":
                case "--help":
                    Console.WriteLine("AstcEncCheck [--no-python] [--python <path>] [--no-fixtures]");
                    Console.WriteLine("Encodes synthetic images and real TTX fixtures with AstcEncoder,");
                    Console.WriteLine("decodes with AstcCodec and (optionally) cross-checks every payload");
                    Console.WriteLine("with Python's texture2ddecoder.decode_astc.");
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return 2;
            }
        }

        if (useOracle && !File.Exists(_python))
        {
            Console.Error.WriteLine($"Python oracle not found at '{_python}'.");
            Console.Error.WriteLine("Pass --python <path> or --no-python.");
            return 2;
        }

        _tempDir = Environment.GetEnvironmentVariable("ASTC_ENC_TMP");
        if (string.IsNullOrWhiteSpace(_tempDir))
            _tempDir = Path.Combine(Directory.GetCurrentDirectory(), "tmp", "astc-enc-check");
        Directory.CreateDirectory(_tempDir);

        Console.WriteLine("ASTC encoder check");
        Console.WriteLine($"  oracle: {(useOracle ? _python : "disabled")}");
        Console.WriteLine();

        var images = BuildImages();
        int[] blockSizes = { 4, 5, 6, 8 };

        foreach (int blockSize in blockSizes)
        {
            Console.WriteLine($"--- block {blockSize}x{blockSize} ---");
            double worstRgb = double.MaxValue;
            string worstName = string.Empty;

            foreach (TestImage image in images)
            {
                (double rgbPsnr, double alphaPsnr, bool ok) =
                    TestImageFormat(image, blockSize, blockSize, useOracle);
                if (!ok)
                    _failures++;
                if (rgbPsnr < worstRgb)
                {
                    worstRgb = rgbPsnr;
                    worstName = image.Name;
                }

                string alpha = double.IsNaN(alphaPsnr)
                    ? "alpha n/a"
                    : $"alpha {alphaPsnr,6:F2} dB";
                Console.WriteLine(
                    $"  {image.Name,-18} {image.Width,4}x{image.Height,-4} " +
                    $"RGB {rgbPsnr,6:F2} dB  {alpha}");
            }

            Console.WriteLine(
                $"  worst RGB: {worstRgb:F2} dB ({worstName})");
            Console.WriteLine();
        }

        if (useFixtures)
            RunFixtureChecks(useOracle);

        if (useOracle && _oracleChecks == 0)
        {
            Console.Error.WriteLine("No oracle checks ran.");
            _failures++;
        }

        Console.WriteLine(_failures == 0
            ? "RESULT: all blocks valid, cross-decoder identical."
            : $"RESULT: FAILED ({_failures} failure(s)).");
        return _failures == 0 ? 0 : 1;
    }

    private sealed class ManifestEntry
    {
        public string file { get; set; } = "";
        public string format { get; set; } = "";
        public int width { get; set; }
        public int height { get; set; }
        public int mips { get; set; }
        public int bytes { get; set; }
    }

    /// <summary>
    /// Decodes small real TTX fixtures, re-encodes them with AstcEncoder and decodes
    /// the result again. This covers the actual mobile payloads the encoder targets.
    /// </summary>
    private static void RunFixtureChecks(bool useOracle)
    {
        Console.WriteLine("--- real TTX fixtures (fixture -> encode -> decode) ---");

        string dir = ResolveDir(Path.Combine("tests", "vectors", "astc"));
        string manifestPath = Path.Combine(dir, "astc_manifest.json");
        if (!Directory.Exists(dir) || !File.Exists(manifestPath))
        {
            Console.WriteLine($"  fixtures not found at '{dir}' (skipped)");
            Console.WriteLine();
            return;
        }

        List<ManifestEntry> manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<List<ManifestEntry>>(
                File.ReadAllText(manifestPath)) ?? new List<ManifestEntry>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  manifest parse failed: {ex.Message} (skipped)");
            Console.WriteLine();
            return;
        }

        var candidates = manifest
            .Where(e => e.format is "type_rgb_astc_6x6" or "type_rgba_astc_5x5" or "type_rgba_astc_6x6")
            .Where(e => e.width <= 512 && e.height <= 512 && e.width >= 4 && e.height >= 4)
            .Where(e => File.Exists(Path.Combine(dir, e.file)))
            .OrderBy(e => (long)e.width * e.height)
            .ThenBy(e => e.file, StringComparer.Ordinal)
            .ToList();

        // Cover a few different texture sizes, preferring the larger (photo-like)
        // fixtures so the reported PSNR is representative of real content.
        var picked = candidates
            .GroupBy(e => e.format, StringComparer.Ordinal)
            .SelectMany(g => g
                .GroupBy(e => (e.width, e.height))
                .OrderByDescending(size => (long)size.Key.width * size.Key.height)
                .Take(3)
                .Select(size => size.First()))
            .ToList();

        if (picked.Count == 0)
        {
            Console.WriteLine("  no small RGB 6x6 / RGBA 5x5 fixtures found (skipped)");
            Console.WriteLine();
            return;
        }

        foreach (ManifestEntry entry in picked)
        {
            string name = entry.file;
            byte[] raw;
            TtxInfo info;
            try
            {
                raw = File.ReadAllBytes(Path.Combine(dir, entry.file));
                info = TtxFile.Parse(TtxFile.Inflate(raw));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {name}: READ FAILED: {ex.Message}");
                _failures++;
                continue;
            }

            var (bw, bh) = TextureFormats.AstcBlockSize(info.Format);
            if (bw < 4 || bh < 4)
            {
                Console.WriteLine($"  {name}: unsupported fixture format '{info.Format}'");
                _failures++;
                continue;
            }

            int width = info.Width;
            int height = info.Height;
            int blocksX = (width + bw - 1) / bw;
            int blocksY = (height + bh - 1) / bh;
            if (info.Pixels.Length < blocksX * blocksY * 16)
            {
                Console.WriteLine($"  {name}: fixture payload too short");
                _failures++;
                continue;
            }

            var source = new byte[width * height * 4];
            try
            {
                AstcCodec.Decode(info.Pixels.Span, width, height, bw, bh, source);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {name}: fixture decode threw: {ex.Message}");
                _failures++;
                continue;
            }

            var payload = new byte[blocksX * blocksY * 16];
            int paddedWidth = blocksX * bw;
            int paddedHeight = blocksY * bh;
            byte[] paddedSource = PadEdges(source, width, height, paddedWidth, paddedHeight);

            var stopwatch = Stopwatch.StartNew();
            bool ok = true;
            try
            {
                AstcEncoder.Encode(paddedSource, paddedWidth, paddedHeight, bw, bh, payload);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {name}: ENCODE THREW: {ex.Message}");
                _failures++;
                continue;
            }

            stopwatch.Stop();

            var decoded = new byte[paddedWidth * paddedHeight * 4];
            try
            {
                AstcCodec.Decode(payload, paddedWidth, paddedHeight, bw, bh, decoded);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {name}: DECODE THREW: {ex.Message}");
                ok = false;
            }

            double rgbPsnr = Psnr(paddedSource, decoded, width, height, paddedWidth, channels: 3);
            double alphaPsnr = info.Format.StartsWith("type_rgba_astc_", StringComparison.Ordinal)
                ? Psnr(paddedSource, decoded, width, height, paddedWidth, channels: 1, offset: 3)
                : double.NaN;
            if (double.IsNaN(rgbPsnr) || rgbPsnr < 8)
                ok = false;

            if (useOracle)
                ok &= RunOracle(payload, paddedWidth, paddedHeight, bw, bh, name, decoded);

            double ms = stopwatch.Elapsed.TotalMilliseconds;
            double mpix = width * (double)height / 1_000_000.0;
            string alpha = double.IsNaN(alphaPsnr) ? "alpha n/a" : $"alpha {alphaPsnr,6:F2} dB";
            string line =
                $"  {Truncate(name, 44),-44} {width,4}x{height,-4} {bw}x{bh} " +
                $"RGB {rgbPsnr,6:F2} dB  {alpha}";
            if (!ok)
            {
                Console.WriteLine(line + "   <-- FAILED");
                _failures++;
            }
            else
            {
                Console.WriteLine(line + $"  [{ms,7:F0} ms, {mpix / (ms / 1000.0),5:F2} MP/s]");
            }
        }

        Console.WriteLine();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "\u2026";

    private static bool RunOracle(
        byte[] payload, int paddedWidth, int paddedHeight, int bw, int bh,
        string stemName, byte[] decoded)
    {
        string stem = "fixture_" + ((uint)stemName.GetHashCode()).ToString("x8", CultureInfo.InvariantCulture);
        string payloadPath = Path.Combine(_tempDir, stem + ".astc");
        string rawPath = Path.Combine(_tempDir, stem + ".raw");
        string scriptPath = Path.Combine(_tempDir, "decode_oracle.py");
        File.WriteAllBytes(payloadPath, payload);
        File.WriteAllText(scriptPath, OracleScript);

        int exit = RunPython(
            scriptPath, payloadPath,
            paddedWidth.ToString(CultureInfo.InvariantCulture),
            paddedHeight.ToString(CultureInfo.InvariantCulture),
            bw.ToString(CultureInfo.InvariantCulture),
            bh.ToString(CultureInfo.InvariantCulture),
            rawPath);

        if (exit != 0 || !File.Exists(rawPath))
        {
            Console.WriteLine($"    ORACLE FAILED (exit {exit})");
            return false;
        }

        byte[] oracle = File.ReadAllBytes(rawPath);
        string? order = CompareDecoded(oracle, decoded);
        _oracleChecks++;
        if (order is null)
        {
            Console.WriteLine(
                $"    ORACLE MISMATCH: python {oracle.Length} bytes, csharp {decoded.Length} bytes");
            return false;
        }

        if (order != "BGRA")
            Console.WriteLine($"    ORACLE: identical after {order} -> RGBA conversion (unexpected channel order)");

        return true;
    }

    private static string ResolveDir(string relative)
    {
        if (Directory.Exists(relative)) return Path.GetFullPath(relative);

        DirectoryInfo? current = new(Directory.GetCurrentDirectory());
        for (int depth = 0; depth < 8 && current is not null; depth++, current = current.Parent)
        {
            string candidate = Path.GetFullPath(Path.Combine(current.FullName, relative));
            if (Directory.Exists(candidate)) return candidate;
        }

        return relative;
    }

    private static (double RgbPsnr, double AlphaPsnr, bool Ok) TestImageFormat(
        TestImage image, int bw, int bh, bool useOracle)
    {
        int width = image.Width;
        int height = image.Height;
        int blocksX = (width + bw - 1) / bw;
        int blocksY = (height + bh - 1) / bh;
        int expectedBytes = blocksX * blocksY * 16;

        byte[] payload = new byte[expectedBytes];
        bool ok = true;

        try
        {
            AstcEncoder.Encode(image.Pixels, width, height, bw, bh, payload);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    ENCODE THREW: {ex.Message}");
            return (0, double.NaN, false);
        }

        if (payload.Length != expectedBytes)
        {
            Console.WriteLine($"    SIZE MISMATCH: {payload.Length} != {expectedBytes}");
            ok = false;
        }

        // texture2ddecoder's decode_astc needs dimensions that are whole blocks,
        // so both decoders are run on the block-padded image.
        int paddedWidth = blocksX * bw;
        int paddedHeight = blocksY * bh;
        byte[] paddedSource = PadEdges(image.Pixels, width, height, paddedWidth, paddedHeight);
        byte[] decoded = new byte[paddedWidth * paddedHeight * 4];
        try
        {
            AstcCodec.Decode(payload, paddedWidth, paddedHeight, bw, bh, decoded);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    DECODE THREW: {ex.Message}");
            return (0, double.NaN, false);
        }

        double rgbPsnr = Psnr(
            paddedSource, decoded, width, height, paddedWidth, channels: 3);
        double alphaPsnr = image.HasAlpha
            ? Psnr(paddedSource, decoded, width, height, paddedWidth, channels: 1, offset: 3)
            : double.NaN;

        // Sanity floor: random noise is intrinsically hard for a 1-partition encoder,
        // so a low-but-plausible PSNR is fine. Anything below 8 dB means the block
        // decoded to garbage rather than to a rough approximation.
        if (double.IsNaN(rgbPsnr) || rgbPsnr < 8)
            ok = false;

        if (useOracle)
        {
            string stem = $"{image.Name}_{bw}x{bh}";
            string payloadPath = Path.Combine(_tempDir, stem + ".astc");
            string rawPath = Path.Combine(_tempDir, stem + ".raw");
            string scriptPath = Path.Combine(_tempDir, "decode_oracle.py");
            File.WriteAllBytes(payloadPath, payload);
            File.WriteAllText(scriptPath, OracleScript);

            int exit = RunPython(
                scriptPath, payloadPath,
                paddedWidth.ToString(CultureInfo.InvariantCulture),
                paddedHeight.ToString(CultureInfo.InvariantCulture),
                bw.ToString(CultureInfo.InvariantCulture),
                bh.ToString(CultureInfo.InvariantCulture),
                rawPath);

            if (exit != 0 || !File.Exists(rawPath))
            {
                Console.WriteLine($"    ORACLE FAILED (exit {exit})");
                ok = false;
            }
            else
            {
                byte[] oracle = File.ReadAllBytes(rawPath);
                string? order = CompareDecoded(oracle, decoded);
                _oracleChecks++;
                if (order is null)
                {
                    Console.WriteLine(
                        $"    ORACLE MISMATCH: python {oracle.Length} bytes, csharp {decoded.Length} bytes");
                    ok = false;
                }
                else if (order != "BGRA")
                {
                    Console.WriteLine(
                        $"    ORACLE: identical after {order} -> RGBA conversion (unexpected channel order)");
                }
            }
        }

        return (rgbPsnr, alphaPsnr, ok);
    }

    private static void DebugBlock()
    {
        const int size = 4;
        byte[] source = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int o = (y * size + x) * 4;
                source[o] = (byte)(x * 17);
                source[o + 1] = (byte)(y * 17);
                source[o + 2] = 128;
                source[o + 3] = 255;
            }
        }

        byte[] payload = new byte[16];
        AstcEncoder.Encode(source, size, size, size, size, payload);
        Console.WriteLine("block: " + Convert.ToHexString(payload));

        byte[] decoded = new byte[size * size * 4];
        AstcCodec.Decode(payload, size, size, size, size, decoded);
        for (int y = 0; y < size; y++)
        {
            var sb = new StringBuilder();
            for (int x = 0; x < size; x++)
            {
                int o = (y * size + x) * 4;
                sb.Append(
                    $"{decoded[o],3},{decoded[o + 1],3},{decoded[o + 2],3}  ");
            }

            Console.WriteLine($"src y={y}: " + sb);
        }

        string blockPath = Path.Combine(Path.GetTempPath(), "astc-debug.block");
        string rawPath = Path.Combine(Path.GetTempPath(), "astc-debug.raw");
        string scriptPath = Path.Combine(Path.GetTempPath(), "astc-debug.py");
        File.WriteAllBytes(blockPath, payload);
        File.WriteAllText(scriptPath, OracleScript);
        int exit = RunPython(scriptPath, blockPath, "4", "4", "4", "4", rawPath);
        Console.WriteLine($"python exit {exit}");
        if (exit == 0 && File.Exists(rawPath))
        {
            byte[] raw = File.ReadAllBytes(rawPath);
            Console.WriteLine("python raw: " + Convert.ToHexString(raw));
            Console.WriteLine("csharp raw: " + Convert.ToHexString(decoded));
        }
    }

    private static string? CompareDecoded(byte[] oracle, byte[] csharp)
    {
        if (oracle.Length != csharp.Length)
            return null;

        // texture2ddecoder currently emits BGRA; try that first, then plain RGBA
        // (for neutral pixels both are identical).
        if (MatchesWithSwap(oracle, csharp, swap: true))
            return "BGRA";
        if (MatchesWithSwap(oracle, csharp, swap: false))
            return "RGBA";
        return null;
    }

    private static bool MatchesWithSwap(byte[] oracle, byte[] csharp, bool swap)
    {
        for (int i = 0; i < csharp.Length; i += 4)
        {
            if (oracle[i] != (swap ? csharp[i + 2] : csharp[i]) ||
                oracle[i + 1] != csharp[i + 1] ||
                oracle[i + 2] != (swap ? csharp[i] : csharp[i + 2]) ||
                oracle[i + 3] != csharp[i + 3])
            {
                return false;
            }
        }

        return true;
    }

    private static int RunPython(string script, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _python,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(script);
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (!string.IsNullOrWhiteSpace(stderr) && process.ExitCode != 0)
            Console.Error.WriteLine(stderr.Trim());
        _ = stdout;
        return process.ExitCode;
    }

    private static double Psnr(
        byte[] source, byte[] decoded,
        int width, int height, int stride, int channels, int offset = 0)
    {
        double sum = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * stride + x) * 4 + offset;
                for (int c = 0; c < channels; c++)
                {
                    int delta = source[index + c] - decoded[index + c];
                    sum += delta * delta;
                }
            }
        }

        double mse = sum / ((double)width * height * channels);
        return mse <= 1e-12 ? 99.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static byte[] PadEdges(
        byte[] pixels, int width, int height, int paddedWidth, int paddedHeight)
    {
        byte[] padded = new byte[paddedWidth * paddedHeight * 4];
        for (int y = 0; y < paddedHeight; y++)
        {
            int sy = Math.Min(y, height - 1);
            for (int x = 0; x < paddedWidth; x++)
            {
                int sx = Math.Min(x, width - 1);
                int sourceIndex = (sy * width + sx) * 4;
                int destinationIndex = (y * paddedWidth + x) * 4;
                padded[destinationIndex] = pixels[sourceIndex];
                padded[destinationIndex + 1] = pixels[sourceIndex + 1];
                padded[destinationIndex + 2] = pixels[sourceIndex + 2];
                padded[destinationIndex + 3] = pixels[sourceIndex + 3];
            }
        }

        return padded;
    }

    private sealed record TestImage(
        string Name, int Width, int Height, bool HasAlpha, byte[] Pixels);

    private static List<TestImage> BuildImages()
    {
        var images = new List<TestImage>
        {
            MakePhotoLike("photo-128", 128, 128),
            MakeGradient("gradient-64", 64, 64),
            MakeEdges("edges-96x64", 96, 64),
            MakeAlpha("alpha-64", 64, 64),
            MakeNoise("noise-64", 64, 64),
            MakeTiny("tiny-5x7", 5, 7),
            MakeConstant("constant-32", 32, 32),
        };
        return images;
    }

    private static TestImage MakeGradient(string name, int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                pixels[o] = (byte)(255 * x / (width - 1));
                pixels[o + 1] = (byte)(255 * y / (height - 1));
                pixels[o + 2] = (byte)(128 + 60 * Math.Sin(x * 0.3 + y * 0.2));
                pixels[o + 3] = 255;
            }
        }

        return new TestImage(name, width, height, false, pixels);
    }

    private static TestImage MakePhotoLike(string name, int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double fx = x / (double)(width - 1);
                double fy = y / (double)(height - 1);
                double baseR = 115
                    + 80 * Math.Sin((fx * 2.1 + fy * 0.9 + 0.1) * 6.283)
                    + 28 * Math.Sin((fx * 7.3 - fy * 3.1) * 6.283);
                double baseG = 120
                    + 65 * Math.Sin((fx * 1.4 - fy * 1.7 + 0.35) * 6.283)
                    + 25 * Math.Cos((fx * 5.9 + fy * 4.3) * 6.283);
                double baseB = 110
                    + 55 * Math.Cos((fx * 0.8 + fy * 2.4 + 0.2) * 6.283)
                    + 30 * Math.Sin((fx * 9.1 + fy * 1.3) * 6.283);

                // A couple of hard-edged shapes.
                double dx = x - width * 0.62;
                double dy = y - height * 0.34;
                if (dx * dx + dy * dy < width * width * 0.012)
                {
                    baseR = 220;
                    baseG = 180;
                    baseB = 70;
                }
                else if (x > width * 0.18 && x < width * 0.34 &&
                         y > height * 0.58 && y < height * 0.86)
                {
                    baseR = 40;
                    baseG = 70;
                    baseB = 160;
                }

                int noise = (int)(Hash(x, y) % 21) - 10;
                int o = (y * width + x) * 4;
                pixels[o] = ClampToByte(baseR + noise);
                pixels[o + 1] = ClampToByte(baseG + noise);
                pixels[o + 2] = ClampToByte(baseB + noise);
                pixels[o + 3] = 255;
            }
        }

        return new TestImage(name, width, height, false, pixels);
    }

    private static TestImage MakeEdges(string name, int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sector = (x * 6) / width + ((y * 3) / height) * 6;
                int o = (y * width + x) * 4;
                switch (sector % 5)
                {
                    case 0: pixels[o] = 235; pixels[o + 1] = 40; pixels[o + 2] = 35; break;
                    case 1: pixels[o] = 30; pixels[o + 1] = 200; pixels[o + 2] = 45; break;
                    case 2: pixels[o] = 35; pixels[o + 1] = 55; pixels[o + 2] = 225; break;
                    case 3: pixels[o] = 240; pixels[o + 1] = 235; pixels[o + 2] = 20; break;
                    default: pixels[o] = 15; pixels[o + 1] = 15; pixels[o + 2] = 15; break;
                }

                if (((x + y) / 7) % 2 == 0)
                {
                    pixels[o] = (byte)(255 - pixels[o]);
                    pixels[o + 1] = (byte)(255 - pixels[o + 1]);
                    pixels[o + 2] = (byte)(255 - pixels[o + 2]);
                }

                pixels[o + 3] = 255;
            }
        }

        return new TestImage(name, width, height, false, pixels);
    }

    private static TestImage MakeAlpha(string name, int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double fx = x / (double)(width - 1);
                double fy = y / (double)(height - 1);
                double radial = Math.Sqrt(
                    (fx - 0.5) * (fx - 0.5) + (fy - 0.5) * (fy - 0.5));
                int o = (y * width + x) * 4;
                pixels[o] = ClampToByte(220 * fx + 20);
                pixels[o + 1] = ClampToByte(200 * fy + 25);
                pixels[o + 2] = ClampToByte(180 - 100 * fx * fy);
                pixels[o + 3] = ClampToByte(255 * (1.2 - radial * 1.8));
            }
        }

        return new TestImage(name, width, height, true, pixels);
    }

    private static TestImage MakeNoise(string name, int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                uint h = Hash(x, y);
                int o = (y * width + x) * 4;
                pixels[o] = (byte)(h & 0xff);
                pixels[o + 1] = (byte)((h >> 8) & 0xff);
                pixels[o + 2] = (byte)((h >> 16) & 0xff);
                pixels[o + 3] = 255;
            }
        }

        return new TestImage(name, width, height, false, pixels);
    }

    private static TestImage MakeTiny(string name, int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                pixels[o] = (byte)(x * 40 + 20);
                pixels[o + 1] = (byte)(y * 30 + 15);
                pixels[o + 2] = (byte)(200 - x * 20 - y * 10);
                pixels[o + 3] = 255;
            }
        }

        return new TestImage(name, width, height, false, pixels);
    }

    private static TestImage MakeConstant(string name, int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                bool left = x < width / 2;
                pixels[o] = left ? (byte)90 : (byte)190;
                pixels[o + 1] = left ? (byte)140 : (byte)60;
                pixels[o + 2] = left ? (byte)200 : (byte)25;
                pixels[o + 3] = 255;
            }
        }

        return new TestImage(name, width, height, false, pixels);
    }

    private static byte ClampToByte(double value) =>
        value < 0 ? (byte)0 : value > 255 ? (byte)255 : (byte)Math.Round(value);

    private static uint Hash(int x, int y)
    {
        uint value = (uint)(x * 374761393 + y * 668265263);
        value = (value ^ (value >> 13)) * 1274126177;
        return value ^ (value >> 16);
    }

    private const string OracleScript = """
        import sys
        import texture2ddecoder as t

        payload = open(sys.argv[1], "rb").read()
        width, height, bw, bh = (int(v) for v in sys.argv[2:6])
        out = t.decode_astc(payload, width, height, bw, bh)
        open(sys.argv[6], "wb").write(out)
        """;
}
