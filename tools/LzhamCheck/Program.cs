using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AeroflyConv.Core.Lzham;

namespace LzhamCheck;

internal sealed class ManifestEntry
{
    public string id { get; set; } = "";
    public string src { get; set; } = "";
    public string? ext { get; set; }
    public long file_bytes { get; set; }
    public long inflated_len { get; set; }
    public string inflated_sha256 { get; set; } = "";
}

internal static class Program
{
    private static int Main(string[] args)
    {
        string? vectorsDir = null;
        string? dumpDir = null;
        string? filter = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dump":
                    dumpDir = ++i < args.Length ? args[i] : null;
                    break;
                case "--filter":
                    filter = ++i < args.Length ? args[i] : null;
                    break;
                case "-h":
                case "--help":
                    Console.WriteLine("usage: LzhamCheck <vectorsDir> [--dump <dir>] [--filter <id>]");
                    return 0;
                default:
                    vectorsDir ??= args[i];
                    break;
            }
        }

        if (vectorsDir == null)
        {
            Console.Error.WriteLine("usage: LzhamCheck <vectorsDir> [--dump <dir>] [--filter <id>]");
            return 2;
        }

        string manifestPath = Path.Combine(vectorsDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"manifest.json not found in {vectorsDir}");
            return 2;
        }

        List<ManifestEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<ManifestEntry>>(
                File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"failed to read manifest.json: {ex.Message}");
            return 2;
        }

        if (entries == null || entries.Count == 0)
        {
            Console.Error.WriteLine("manifest.json contains no entries");
            return 2;
        }

        if (dumpDir != null)
            Directory.CreateDirectory(dumpDir);

        int decodeTotal = 0, decodePassed = 0;
        int rtTotal = 0, rtPassed = 0;
        long decodedBytes = 0, compressedBytes = 0;
        double decodeMs = 0, encodeMs = 0;

        foreach (ManifestEntry entry in entries)
        {
            if (filter != null && !entry.id.Equals(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            decodeTotal++;

            string? path = FindVectorFile(vectorsDir, entry);
            if (path == null)
            {
                Console.WriteLine($"FAIL {entry.id} (file not found)");
                continue;
            }

            byte[] fileBytes;
            try
            {
                fileBytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL {entry.id} (read error: {ex.Message})");
                continue;
            }

            bool compressedContainer = IsCompressedContainer(fileBytes);
            int payloadOffset = 0;
            int payloadLength = 0;

            if (compressedContainer && !TryFindPayload(fileBytes, out payloadOffset, out payloadLength))
            {
                Console.WriteLine($"FAIL {entry.id} (no LZHAM payload found in {fileBytes.Length} byte container)");
                continue;
            }

            byte[]? inflated = null;
            double elapsed = 0;
            try
            {
                if (compressedContainer)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    inflated = LzhamCodec.DecompressTmBlob(fileBytes.AsSpan(payloadOffset, payloadLength));
                    elapsed = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                }
                else
                {
                    // Not a compress_file=true container: the manifest's "inflated" data is the file itself.
                    inflated = (byte[])fileBytes.Clone();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL {entry.id} (decode threw {ex.GetType().Name}: {ex.Message})");
            }

            if (inflated != null)
            {
                string sha = Convert.ToHexString(SHA256.HashData(inflated)).ToLowerInvariant();
                bool ok = inflated.Length == entry.inflated_len &&
                          sha.Equals(entry.inflated_sha256, StringComparison.OrdinalIgnoreCase);

                decodeMs += elapsed;
                decodedBytes += inflated.Length;

                string kind = compressedContainer ? "" : " (plain, no LZHAM)";
                if (ok)
                {
                    decodePassed++;
                    Console.WriteLine($"PASS {entry.id} {fileBytes.Length}->{inflated.Length} sha256 ok{kind} ({elapsed:F1} ms)");
                }
                else
                {
                    Console.WriteLine(
                        $"FAIL {entry.id} expected {entry.inflated_len} bytes sha {entry.inflated_sha256[..Math.Min(16, entry.inflated_sha256.Length)]}..., " +
                        $"got {inflated.Length} bytes sha {sha[..16]}...{kind}");
                }

                // Round-trip through our own raw-block encoder.
                rtTotal++;
                try
                {
                    long t0 = Stopwatch.GetTimestamp();
                    byte[] compressed = LzhamCodec.CompressRaw(inflated);
                    long t1 = Stopwatch.GetTimestamp();
                    byte[] back = LzhamCodec.Decompress(compressed, inflated.Length);
                    long t2 = Stopwatch.GetTimestamp();

                    encodeMs += Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
                    compressedBytes += inflated.Length;

                    bool rtOk = back.Length == inflated.Length && back.AsSpan().SequenceEqual(inflated);
                    if (rtOk)
                    {
                        rtPassed++;
                        Console.WriteLine(
                            $"PASS {entry.id} roundtrip {inflated.Length}->{compressed.Length}->{back.Length} " +
                            $"({Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds:F1}+{Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds:F1} ms)");
                    }
                    else
                    {
                        Console.WriteLine($"FAIL {entry.id} roundtrip mismatch ({inflated.Length}->{compressed.Length}->{back.Length})");
                    }

                    if (dumpDir != null && ok && rtOk)
                    {
                        if (compressedContainer)
                        {
                            File.WriteAllBytes(Path.Combine(dumpDir, entry.id + ".lzham"),
                                fileBytes.AsSpan(payloadOffset + 64, payloadLength - 64).ToArray());
                        }
                        File.WriteAllBytes(Path.Combine(dumpDir, entry.id + ".raw.lzham"), compressed);
                        File.WriteAllBytes(Path.Combine(dumpDir, entry.id + ".inflated"), inflated);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FAIL {entry.id} roundtrip threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"LZHAM DECODE: {decodePassed}/{decodeTotal} passed");
        Console.WriteLine($"LZHAM ENCODE ROUNDTRIP: {rtPassed}/{rtTotal} passed");
        Console.WriteLine(
            $"LZHAM DECODE THROUGHPUT: {MbPerSec(decodedBytes, decodeMs):F1} MB/s " +
            $"({decodedBytes / 1e6:F1} MB in {decodeMs:F0} ms)");
        Console.WriteLine(
            $"LZHAM ENCODE THROUGHPUT: {MbPerSec(compressedBytes, encodeMs):F1} MB/s " +
            $"({compressedBytes / 1e6:F1} MB in {encodeMs:F0} ms)");

        bool allOk = decodeTotal > 0 && decodePassed == decodeTotal && rtPassed == rtTotal;
        return allOk ? 0 : 1;
    }

    private static double MbPerSec(long bytes, double milliseconds) =>
        milliseconds <= 0 ? 0 : bytes / 1e6 / (milliseconds / 1000.0);

    private static string? FindVectorFile(string dir, ManifestEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.ext))
        {
            string p = Path.Combine(dir, entry.id + entry.ext);
            if (File.Exists(p))
                return p;
        }

        string[] matches = Directory.GetFiles(dir, entry.id + ".*");
        foreach (string m in matches)
        {
            string name = Path.GetFileName(m);
            if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                continue;
            return m;
        }
        return null;
    }

    private const ulong CompressedFileMagic = 0xB7EB3CB7C724FEB5UL;

    /// <summary>True if the file starts with the Aerofly compress_file=true container magic.</summary>
    private static bool IsCompressedContainer(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && BinaryPrimitives.ReadUInt64LittleEndian(data) == CompressedFileMagic;

    /// <summary>Port of the Aerofly TM container chunk walk used by the reference Python tool.</summary>
    private static bool TryFindPayload(ReadOnlySpan<byte> data, out int payloadOffset, out int payloadLength)
    {
        payloadOffset = 0;
        payloadLength = 0;

        if (data.Length < 0x100)
            return false;

        int pos = 0x40;
        while (pos + 32 <= data.Length)
        {
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(pos + 16, 8));
            ulong hs = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(pos + 24, 8));

            if (size > 200)
            {
                if (size < 32 || (long)size > data.Length - pos)
                    return false;
                payloadOffset = pos + 32;
                payloadLength = (int)size - 32;
                return true;
            }

            long step = (size == 36 && hs == 40) ? 40 : (long)size;
            if (step < 32)
                return false;
            pos += (int)step;
        }

        return false;
    }
}
