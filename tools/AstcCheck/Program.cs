using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AeroflyConv.Core.Codecs;
using AeroflyConv.Core.Textures;

namespace AstcCheck;

internal static class Program
{
    private sealed class ManifestEntry
    {
        public string file { get; set; } = "";
        public string format { get; set; } = "";
        public int width { get; set; }
        public int height { get; set; }
        public int mips { get; set; }
        public int bytes { get; set; }
        public string rgba_sha256 { get; set; } = "";
    }

    private static int Main(string[] args)
    {
        string dir = args.Length > 0 ? ResolveDir(args[0]) : FindDefaultDir();
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"fixtures directory not found: {dir}");
            return 2;
        }

        Console.WriteLine($"fixtures: {Path.GetFullPath(dir)}");

        string manifestPath = Path.Combine(dir, "astc_manifest.json");
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"manifest not found: {manifestPath}");
            return 2;
        }

        var manifest = JsonSerializer.Deserialize<List<ManifestEntry>>(File.ReadAllText(manifestPath))
                       ?? new List<ManifestEntry>();

        var fixtures = new List<(string Path, long Size)>();
        foreach (string path in Directory.GetFiles(dir, "*.ttx"))
            fixtures.Add((path, new FileInfo(path).Length));
        fixtures.Sort((a, b) => a.Size.CompareTo(b.Size));

        int passed = 0;
        int total = 0;
        var big = new List<(string Name, int W, int H, int Bw, int Bh, byte[] Data, byte[] Rgba)>();

        foreach ((string path, long size) in fixtures)
        {
            string name = Path.GetFileName(path);
            ManifestEntry? entry = manifest.FirstOrDefault(m =>
                string.Equals(m.file, name, StringComparison.OrdinalIgnoreCase) && m.bytes == size);
            entry ??= manifest.FirstOrDefault(m =>
                string.Equals(m.file, name, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                Console.WriteLine($"SKIP {name}: no manifest entry");
                continue;
            }

            total++;
            byte[] raw = File.ReadAllBytes(path);
            TtxInfo info = TtxFile.Parse(TtxFile.Inflate(raw));

            if (!TryParseBlockSize(info.Format, out int bw, out int bh))
            {
                Console.WriteLine($"FAIL {name}: cannot parse block size from '{info.Format}'");
                continue;
            }

            int w = info.Width, h = info.Height;
            var rgba = new byte[w * h * 4];

            int blocksX = (w + bw - 1) / bw;
            int blocksY = (h + bh - 1) / bh;
            long needed = (long)blocksX * blocksY * 16;
            if (info.Pixels.Length < needed)
            {
                Console.WriteLine($"FAIL {name}: pixel payload {info.Pixels.Length} < {needed} bytes needed");
                continue;
            }

            try
            {
                AstcCodec.Decode(info.Pixels.Span, w, h, bw, bh, rgba);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            string actual = Convert.ToHexStringLower(SHA256.HashData(rgba));
            bool ok = string.Equals(actual, entry.rgba_sha256, StringComparison.OrdinalIgnoreCase);
            if (ok)
            {
                passed++;
                Console.WriteLine($"PASS {name} ({w}x{h} {bw}x{bh})");
            }
            else
            {
                Console.WriteLine($"FAIL {name} ({w}x{h} {bw}x{bh})");
                Console.WriteLine($"     expected {entry.rgba_sha256}");
                Console.WriteLine($"     actual   {actual}");
            }

            if (ok && (long)w * h >= 1024L * 1024L)
                big.Add((name, w, h, bw, bh, info.Pixels.ToArray(), rgba));
        }

        Console.WriteLine($"ASTC DECODE: {passed}/{total} passed");

        if (big.Count > 0)
        {
            Console.WriteLine("THROUGHPUT:");
            double totalBytes = 0;
            double totalSeconds = 0;

            foreach ((string name, int w, int h, int bw, int bh, byte[] data, byte[] firstRgba) in big)
            {
                var dst = new byte[w * h * 4];
                // Warm up once (JIT + caches) then take the best of three timed runs.
                AstcCodec.Decode(data, w, h, bw, bh, dst);
                double best = double.MaxValue;
                for (int i = 0; i < 3; i++)
                {
                    var sw = Stopwatch.StartNew();
                    AstcCodec.Decode(data, w, h, bw, bh, dst);
                    sw.Stop();
                    best = Math.Min(best, sw.Elapsed.TotalSeconds);
                }

                // Sanity: repeated timing must reproduce the first decode.
                if (!dst.AsSpan().SequenceEqual(firstRgba))
                {
                    Console.WriteLine($"     WARNING {name}: non-deterministic output");
                }

                double mb = w * (double)h * 4 / (1024.0 * 1024.0);
                double mbs = mb / best;
                totalBytes += w * (double)h * 4;
                totalSeconds += best;
                Console.WriteLine($"  {name,-32} {w}x{h} {bw}x{bh}  {mbs,8:F1} MB/s  ({best * 1000.0:F1} ms)");
            }

            if (big.Count > 1)
                Console.WriteLine($"  TOTAL {totalBytes / (1024.0 * 1024.0),8:F1} MB in {totalSeconds * 1000.0:F1} ms = " +
                                  $"{totalBytes / (1024.0 * 1024.0) / totalSeconds:F1} MB/s");
        }

        return passed == total && total > 0 ? 0 : 1;
    }

    private static string FindDefaultDir() => ResolveDir(Path.Combine("tests", "vectors", "astc"));

    /// <summary>
    /// Resolves a fixtures path relative to the current directory or any of its ancestors,
    /// so the tool works whether it is launched from the workspace root, the solution
    /// directory or the tool directory. As a last resort it searches ancestors for
    /// <c>tests/vectors/astc</c>.
    /// </summary>
    private static string ResolveDir(string arg)
    {
        if (Directory.Exists(arg)) return Path.GetFullPath(arg);

        DirectoryInfo? dir = new(Directory.GetCurrentDirectory());
        for (int depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
        {
            string candidate = Path.GetFullPath(Path.Combine(dir.FullName, arg));
            if (Directory.Exists(candidate)) return candidate;

            string fallback = Path.Combine(dir.FullName, "tests", "vectors", "astc");
            if (Directory.Exists(fallback)) return fallback;
        }

        return arg;
    }

    private static bool TryParseBlockSize(string format, out int bw, out int bh)
    {
        bw = 0;
        bh = 0;
        int idx = format.LastIndexOf("astc_", StringComparison.Ordinal);
        if (idx < 0) return false;
        string tail = format[(idx + 5)..];
        int x = tail.IndexOf('x');
        if (x <= 0) return false;
        return int.TryParse(tail[..x], out bw) && int.TryParse(tail[(x + 1)..], out bh) &&
               bw > 0 && bh > 0;
    }
}
