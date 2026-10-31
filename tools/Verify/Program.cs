using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AeroflyConv.Core.Conversion;
using AeroflyConv.Core.Media;
using AeroflyConv.Core.Textures;
using AeroflyConv.Core.Tm;

namespace AeroflyConv.Verify;

/// <summary>
/// End-to-end verification against reference outputs produced by the Python converter.
/// Usage: dotnet run --project tools/Verify -- [vectorsDir]
/// </summary>
internal static class Program
{
    private static int _pass, _fail;
    private static readonly List<string> Failures = [];
    private static string _vectors = "";

    private static int Main(string[] args)
    {
        _vectors = args.Length > 0
            ? args[0]
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "vectors"));

        if (!Directory.Exists(_vectors))
        {
            Console.Error.WriteLine($"vectors dir not found: {_vectors}");
            return 2;
        }

        Console.WriteLine($"verifying against {_vectors}\n");
        var manifest = JsonSerializer.Deserialize<List<Entry>>(
            File.ReadAllText(Path.Combine(_vectors, "manifest.json")),
            JsonOpts) ?? [];

        Section("TTX decode (top mip RGBA)", () => { foreach (var e in manifest) CheckTtx(e); });
        Section("TSB -> WAV", () => { foreach (var e in manifest) CheckTsb(e); });
        Section("TM -> TXT (generic dump)", () => { foreach (var e in manifest) CheckTm(e); });
        Section("TTC decode", CheckTtc);
        Section("ASTC decode (Aerofly mobile)", CheckAstc);
        Section("Reference-encoded TTX (real LZHAM compressed blocks)", CheckEncoded);
        Section("Livery (XOR-obfuscated) containers", CheckObfuscated);
        Section("Round trips (encode -> decode)", RoundTrips);

        Console.WriteLine();
        Console.WriteLine($"TOTAL: {_pass} passed, {_fail} failed");
        foreach (string f in Failures) Console.WriteLine("  FAIL " + f);
        return _fail == 0 ? 0 : 1;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Ext { get; set; } = "";
        public string? Fmt { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        [JsonPropertyName("rgba_sha256")]
        public string? RgbaSha256 { get; set; }
        [JsonPropertyName("rgba_rg_sha256")]
        public string? RgbaRgSha256 { get; set; }
        [JsonPropertyName("text_sha256")]
        public string? TextSha256 { get; set; }
        [JsonPropertyName("wav_sha256")]
        public string? WavSha256 { get; set; }
        [JsonPropertyName("wav_len")]
        public long WavLen { get; set; }
        public string? DecodeError { get; set; }
    }

    private static void Section(string title, Action action)
    {
        Console.WriteLine($"== {title}");
        try { action(); }
        catch (Exception ex) { Fail($"{title}: {ex.Message}"); }
        Console.WriteLine();
    }

    private static void Pass(string what) => _pass++;

    private static void Fail(string what)
    {
        _fail++;
        Failures.Add(what);
        Console.WriteLine("   FAIL " + what);
    }

    private static string Sha(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static void CheckTtx(Entry e)
    {
        if (e.Ext != ".ttx" || e.RgbaSha256 is null) return;
        if (e.DecodeError is not null)
        {
            Console.WriteLine($"   skip {e.Id} ({e.Fmt}: {e.DecodeError})");
            return;
        }
        try
        {
            DecodedTexture tex = TtxFile.Decode(File.ReadAllBytes(Path.Combine(_vectors, e.Id + e.Ext)));
            byte[] rgba = tex.Rgba;
            // Intentional difference: BC5 normal maps get a *correct* reconstructed Z,
            // while the reference tool always derives it from a zero blue channel.
            // So only the X/Y (red/green) channels are compared for that format.
            if (e.Fmt == "type_rg_rgtc2")
            {
                rgba = (byte[])tex.Rgba.Clone();
                for (int i = 2; i < rgba.Length; i += 4) rgba[i] = 0;
            }
            string expected = e.Fmt == "type_rg_rgtc2" && e.RgbaRgSha256 is not null
                ? e.RgbaRgSha256
                : e.RgbaSha256;
            if (Sha(rgba) != expected)
                Fail($"{e.Id} {e.Fmt} {e.Width}x{e.Height}: rgba hash mismatch");
            else if (tex.Width != e.Width || tex.Height != e.Height)
                Fail($"{e.Id}: size {tex.Width}x{tex.Height} != {e.Width}x{e.Height}");
            else Pass(e.Id);
        }
        catch (Exception ex) { Fail($"{e.Id}: {ex.Message}"); }
    }

    private static void CheckTsb(Entry e)
    {
        if (e.Ext != ".tsb" || e.WavSha256 is null) return;
        try
        {
            byte[] wav = TsbFile.ToWav(TsbFile.Parse(File.ReadAllBytes(Path.Combine(_vectors, e.Id + e.Ext))));
            if (Sha(wav) != e.WavSha256) Fail($"{e.Id}: wav hash mismatch ({wav.Length} vs {e.WavLen} bytes)");
            else Pass(e.Id);
        }
        catch (Exception ex) { Fail($"{e.Id}: {ex.Message}"); }
    }

    private static void CheckTm(Entry e)
    {
        if (e.TextSha256 is null) return;
        try
        {
            string text = TmTextDump.DumpText(File.ReadAllBytes(Path.Combine(_vectors, e.Id + e.Ext)), out _);
            if (Sha(Encoding.UTF8.GetBytes(text)) != e.TextSha256)
            {
                Fail($"{e.Id}: tm text hash mismatch ({text.Length} chars)");
                DiffAgainstReference(e, text);
            }
            else Pass(e.Id);
        }
        catch (Exception ex) { Fail($"{e.Id}: {ex.Message}"); }
    }

    private static void DiffAgainstReference(Entry e, string text)
    {
        string refPath = Path.Combine(_vectors, "ref", e.Id + ".txt");
        if (!File.Exists(refPath)) return;
        string[] expected = File.ReadAllLines(refPath);
        string[] actual = text.Split('\n');
        for (int i = 0; i < Math.Min(expected.Length, actual.Length); i++)
        {
            if (expected[i] != actual[i])
            {
                Console.WriteLine($"      first diff at line {i + 1}:");
                Console.WriteLine($"        ref: {Truncate(expected[i])}");
                Console.WriteLine($"        got: {Truncate(actual[i])}");
                return;
            }
        }
        Console.WriteLine($"      line count differs: ref {expected.Length}, got {actual.Length}");
        if (expected.Length > actual.Length) Console.WriteLine($"        missing: {Truncate(expected[actual.Length])}");
        else Console.WriteLine($"        extra: {Truncate(actual[expected.Length])}");
    }

    private static string Truncate(string s) => s.Length <= 160 ? s : s[..160] + "...";

    private sealed class AstcRef
    {
        public string File { get; set; } = "";
        public string Format { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public long Bytes { get; set; }

        [JsonPropertyName("rgba_sha256")]
        public string RgbaSha256 { get; set; } = "";
    }

    /// <summary>ASTC (Aerofly mobile) fixtures: decode must be byte-exact against the reference decoder.</summary>
    private static void CheckAstc()
    {
        string dir = Path.Combine(Path.GetDirectoryName(_vectors)!, "vectors", "astc");
        if (!Directory.Exists(dir)) dir = Path.Combine(_vectors, "astc");
        string manifestPath = Path.Combine(dir, "astc_manifest.json");
        if (!File.Exists(manifestPath)) return;

        var refs = JsonSerializer.Deserialize<List<AstcRef>>(File.ReadAllText(manifestPath), JsonOpts) ?? [];
        foreach (AstcRef r in refs)
        {
            string path = Path.Combine(dir, r.File);
            if (!File.Exists(path) || new FileInfo(path).Length != r.Bytes) continue;
            try
            {
                DecodedTexture tex = TtxFile.Decode(File.ReadAllBytes(path), flip: false);
                if (tex.Width != r.Width || tex.Height != r.Height)
                    Fail($"{r.File}: size {tex.Width}x{tex.Height} != {r.Width}x{r.Height}");
                else if (Sha(tex.Rgba) != r.RgbaSha256)
                    Fail($"{r.File} ({r.Format}): rgba hash mismatch");
                else Pass($"{r.File} ({r.Format})");
            }
            catch (Exception ex) { Fail($"{r.File}: {ex.Message}"); }
        }
    }
    private sealed class TtcRef
    {
        public string File { get; set; } = "";
        public string Format { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        [JsonPropertyName("rgba_sha256")]
        public string RgbaSha256 { get; set; } = "";
    }

    private static void CheckTtc()
    {
        string refPath = Path.Combine(_vectors, "ref", "ttc_ref.json");
        if (!File.Exists(refPath)) return;
        var refs = JsonSerializer.Deserialize<List<TtcRef>>(File.ReadAllText(refPath), JsonOpts) ?? [];
        foreach (TtcRef r in refs)
        {
            string path = Path.Combine(_vectors, r.File);
            if (!File.Exists(path)) continue;
            try
            {
                DecodedTexture tex = TtcFile.Decode(File.ReadAllBytes(path), flip: true);
                if (Sha(tex.Rgba) != r.RgbaSha256) Fail($"{r.File} ({r.Format}): rgba hash mismatch");
                else if (tex.Width != r.Width || tex.Height != r.Height)
                    Fail($"{r.File}: size {tex.Width}x{tex.Height} != {r.Width}x{r.Height}");
                else Pass(r.File);
            }
            catch (Exception ex) { Fail($"{r.File}: {ex.Message}"); }
        }
    }

    /// <summary>
    /// FS4 livery textures in the user folder XOR the first 0x40 bytes with 0x5A and use their own
    /// outer magic. Rebuilding that layout from a normal vector must decode to the same pixels.
    /// </summary>
    private static void CheckObfuscated()
    {
        var manifest = JsonSerializer.Deserialize<List<Entry>>(
            File.ReadAllText(Path.Combine(_vectors, "manifest.json")), JsonOpts) ?? [];
        int checkedCount = 0;
        foreach (Entry e in manifest)
        {
            if (e.Ext != ".ttx" || e.RgbaSha256 is null || e.Fmt == "type_rg_rgtc2") continue;
            if (checkedCount++ >= 3) break;
            try
            {
                byte[] plain = File.ReadAllBytes(Path.Combine(_vectors, e.Id + e.Ext));
                byte[] obfuscated = AeroflyConv.Core.Tm.TmFormats.Deobfuscate(plain); // normalise first
                for (int i = 0; i < 0x40 && i < obfuscated.Length; i++) obfuscated[i] ^= 0x5A;
                byte[] signature = [0xFF, 0xFE, 0x7E, 0x9D, 0xED, 0x66, 0xB1, 0xED];
                signature.CopyTo(obfuscated, 0);

                DecodedTexture tex = TtxFile.Decode(obfuscated);
                if (Sha(tex.Rgba) != e.RgbaSha256)
                    Fail($"{e.Id}: obfuscated decode differs");
                else Pass($"{e.Id} obfuscated");
            }
            catch (Exception ex) { Fail($"{e.Id} obfuscated: {ex.Message}"); }
        }
    }

    // --------------------------------------------------------------- round trips

    private sealed class EncRef
    {
        public string File { get; set; } = "";
        public string Format { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public int Mips { get; set; }
        public long Bytes { get; set; }
        [JsonPropertyName("rgba_sha256")]
        public string RgbaSha256 { get; set; } = "";
    }

    private static void CheckEncoded()
    {
        string dir = Path.Combine(_vectors, "enc");
        string manifestPath = Path.Combine(dir, "enc_manifest.json");
        if (!File.Exists(manifestPath)) return;
        var refs = JsonSerializer.Deserialize<List<EncRef>>(File.ReadAllText(manifestPath), JsonOpts) ?? [];
        foreach (EncRef r in refs)
        {
            string path = Path.Combine(dir, r.File);
            if (!File.Exists(path)) continue;
            try
            {
                DecodedTexture tex = TtxFile.Decode(File.ReadAllBytes(path));
                if (tex.Width != r.Width || tex.Height != r.Height)
                    Fail($"{r.File}: size {tex.Width}x{tex.Height} != {r.Width}x{r.Height}");
                else if (tex.Mips != r.Mips)
                    Fail($"{r.File}: mips {tex.Mips} != {r.Mips}");
                else if (Sha(tex.Rgba) != r.RgbaSha256)
                    Fail($"{r.File}: rgba hash mismatch");
                else Pass(r.File);
            }
            catch (Exception ex) { Fail($"{r.File}: {ex.Message}"); }
        }
    }

    private static void RoundTrips()
    {
        string tmp = Path.Combine(_vectors, "_verify_tmp");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        Directory.CreateDirectory(tmp);

        (byte[] rgba, int w, int h) = MakeTestImage(256, 128);

        string[] formats =
        [
            "type_rgba", "type_r", "type_rgb_s3tc_dxt1", "type_rgba_s3tc_dxt5",
            "type_rgb_etc2", "type_rgba_etc2", "dxt_auto",
        ];
        foreach (string fmt in formats)
        {
            try
            {
                TtxFile.EncodeResult enc = TtxFile.Encode(rgba, w, h, fmt, flip: false, compress: true);
                DecodedTexture dec = TtxFile.Decode(enc.Data);
                if (dec.Width != w || dec.Height != h)
                {
                    Fail($"roundtrip {fmt}: size {dec.Width}x{dec.Height}");
                    continue;
                }
                double psnr = Psnr(fmt == "type_r" ? ToGray(rgba) : rgba, dec.Rgba, w, h);
                if (psnr < 25.0) Fail($"roundtrip {fmt}: PSNR {psnr:0.0} dB too low");
                else
                {
                    Pass($"roundtrip {fmt}");
                    Console.WriteLine($"   {fmt,-24} {enc.Data.Length,9:N0} bytes  mips={enc.Mips,-3} PSNR={psnr:0.0} dB");
                }

                TtxFile.EncodeResult plain = TtxFile.Encode(rgba, w, h, fmt, flip: false, compress: false);
                TtxInfo info = TtxFile.Parse(plain.Data);
                if (info.Width != w) Fail($"plain {fmt}: parse width {info.Width}");
                else Pass($"plain {fmt}");
            }
            catch (Exception ex) { Fail($"roundtrip {fmt}: {ex.Message}"); }
        }

        try
        {
            var pcm = new byte[22050 * 2 * 2];
            for (int i = 0; i < pcm.Length; i += 2)
            {
                short s = (short)(Math.Sin(i * 0.01) * 12000);
                pcm[i] = (byte)(s & 0xFF);
                pcm[i + 1] = (byte)((s >> 8) & 0xFF);
            }
            byte[] wav = WavFile.Build(pcm, 22050, 2, 16);
            byte[] wav2 = TsbFile.ToWav(TsbFile.Parse(TsbFile.Build(TsbFile.FromWav(wav))));
            if (Sha(wav) != Sha(wav2)) Fail("wav->tsb->wav mismatch");
            else Pass("wav->tsb->wav");
        }
        catch (Exception ex) { Fail("wav->tsb->wav: " + ex.Message); }

        foreach (string fmt in new[] { "dxt1", "dxt5", "etc2", "r8" })
        {
            try
            {
                TtcFile.EncodeResult enc = TtcFile.Encode(rgba, w, h, fmt, flip: false, mipmaps: true);
                DecodedTexture dec = TtcFile.Decode(enc.Data, flip: false);
                if (dec.Width != w || dec.Height != h)
                    Fail($"ttc {fmt}: size {dec.Width}x{dec.Height} != {w}x{h}");
                // R8 masks store alpha when the source has any transparency.
                else if (Psnr(fmt == "r8" ? ToAlpha(rgba) : rgba, dec.Rgba, w, h) < 20.0) Fail($"ttc {fmt}: PSNR too low");
                else Pass($"ttc {fmt}");
            }
            catch (Exception ex) { Fail($"ttc {fmt}: {ex.Message}"); }
        }

        string music = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "111");
        if (Directory.Exists(music))
        {
            foreach (string file in Directory.EnumerateFiles(music, "*.*")
                         .Where(f => f.EndsWith(".flac", StringComparison.OrdinalIgnoreCase) ||
                                     f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                                     f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                         .Take(2))
            {
                try
                {
                    DecodedAudio audio = AudioDecoder.Decode(file);
                    if (audio.Pcm16.Length == 0) Fail($"audio {Path.GetFileName(file)}: no samples");
                    else
                    {
                        Pass("audio " + Path.GetFileName(file));
                        Console.WriteLine($"   audio {Path.GetFileName(file),-45} {audio.SampleRate} Hz " +
                                          $"{audio.Channels}ch {audio.Frames / (double)audio.SampleRate:0.0} s");
                    }
                }
                catch (Exception ex) { Fail($"audio {Path.GetFileName(file)}: {ex.Message}"); }
            }
        }

        string outDir = Path.Combine(tmp, "out");
        Directory.CreateDirectory(outDir);
        try
        {
            var options = new ConvertOptions { MaxParallelism = 4 };
            int ok = 0, bad = 0;
            foreach (string file in Directory.EnumerateFiles(_vectors))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".json" or ".txt") continue;
                // v09..v11 are the user-mod TTX variant that the reference converter cannot read either.
                if (ext == ".ttx" && file.Contains("v09") || ext == ".ttx" && file.Contains("v10") || ext == ".ttx" && file.Contains("v11")) continue;
                ConversionOutcome outcome = Converter.ConvertFile(file,
                    Path.Combine(outDir, Path.GetFileName(file)), ConversionMode.Auto, options);
                if (outcome.Ok) ok++;
                else
                {
                    bad++;
                    Console.WriteLine($"   convert {Path.GetFileName(file)}: {outcome.Error}");
                }
            }
            if (bad > 0) Fail($"batch conversion: {bad} files failed");
            else Pass($"batch conversion ({ok} files)");
        }
        catch (Exception ex) { Fail("batch conversion: " + ex.Message); }
    }

    private static (byte[] Rgba, int W, int H) MakeTestImage(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                rgba[o] = (byte)(x * 255 / (w - 1));
                rgba[o + 1] = (byte)(y * 255 / (h - 1));
                rgba[o + 2] = (byte)(((x >> 3) + (y >> 3)) % 2 == 0 ? 40 : 200);
                rgba[o + 3] = (byte)((x / 32 + y / 32) % 2 == 0 ? 255 : 128);
            }
        }
        return (rgba, w, h);
    }

    private static byte[] ToGray(byte[] rgba)
    {
        var gray = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            byte v = (byte)((rgba[i] * 299 + rgba[i + 1] * 587 + rgba[i + 2] * 114 + 500) / 1000);
            gray[i] = gray[i + 1] = gray[i + 2] = v;
            gray[i + 3] = 255;
        }
        return gray;
    }

    private static byte[] ToAlpha(byte[] rgba)
    {
        var alpha = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            byte v = rgba[i + 3];
            alpha[i] = alpha[i + 1] = alpha[i + 2] = v;
            alpha[i + 3] = 255;
        }
        return alpha;
    }

    private static double Psnr(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int w, int h)
    {
        double sum = 0;
        int n = w * h;
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            for (int c = 0; c < 3; c++)
            {
                double d = a[o + c] - b[o + c];
                sum += d * d;
            }
        }
        double mse = sum / (n * 3.0);
        return mse <= 0 ? 99.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }
}







