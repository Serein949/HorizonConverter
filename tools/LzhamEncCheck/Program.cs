using System.Diagnostics;
using System.Globalization;
using AeroflyConv.Core.Lzham;

namespace LzhamEncCheck;

/// <summary>
/// Verifies the managed LZHAM encoder against the native reference decoder and reports size/speed
/// against the native reference encoder.
///
/// Usage: dotnet run --project tools/LzhamEncCheck -- &lt;corpusDir&gt; [nativeToolPath]
///
/// For every file in the corpus this harness:
///   1. compresses it with <see cref="LzhamCodec.CompressRaw"/> (dict_log2=21, table_rate=20),
///   2. decompresses with the managed decoder and byte-compares,
///   3. decompresses with the native lzham_tool and byte-compares,
///   4. compresses with the native encoder for a reference size/speed.
/// Exits non-zero when any round trip differs.
/// </summary>
internal static class Program
{
    private const int DictLog2 = 21;
    private const int TableRate = 20;

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: LzhamEncCheck <corpusDir> [nativeToolPath]");
            return 2;
        }

        string corpusDir = Path.GetFullPath(args[0]);
        if (!Directory.Exists(corpusDir))
        {
            Console.Error.WriteLine($"corpus directory not found: {corpusDir}");
            return 2;
        }

        string? nativeTool = args.Length > 1 ? Path.GetFullPath(args[1]) : FindNativeTool();
        if (nativeTool is null || !File.Exists(nativeTool))
        {
            Console.Error.WriteLine("lzham_tool.exe not found; pass its path as the second argument.");
            return 2;
        }

        string[] files = Directory.GetFiles(corpusDir, "*.bin");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        if (files.Length == 0)
        {
            Console.Error.WriteLine($"no *.bin files in {corpusDir}");
            return 2;
        }

        // Scratch space next to the harness binary (the machine's %TEMP% may be sandboxed).
        string tempDir = Path.Combine(AppContext.BaseDirectory, "lzhamenc-tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        Console.WriteLine($"native tool : {nativeTool}");
        Console.WriteLine($"corpus      : {corpusDir} ({files.Length} file(s))");
        Console.WriteLine($"params      : dict_log2={DictLog2} table_rate={TableRate}");
        Console.WriteLine();
        Console.WriteLine($"{"item",-16} {"raw",10} {"C# size",10} {"C# x",7} {"nat size",10} {"nat x",7} {"C# MB/s",9} {"nat MB/s",9}  status");
        Console.WriteLine(new string('-', 108));

        int failures = 0;
        long totalRaw = 0, totalManaged = 0, totalNative = 0;
        double totalManagedSeconds = 0, totalNativeSeconds = 0;

        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            byte[] source = File.ReadAllBytes(file);

            byte[] managedStream;
            double managedSeconds;

            var sw = Stopwatch.StartNew();
            try
            {
                managedStream = LzhamCodec.CompressRaw(source, DictLog2, TableRate);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{name,-16} FAILED to compress: {ex.Message}");
                failures++;
                continue;
            }
            sw.Stop();
            managedSeconds = sw.Elapsed.TotalSeconds;

            string status = "ok";

            // (a) our own decoder must reproduce the input byte for byte
            try
            {
                byte[] roundTrip = LzhamCodec.Decompress(managedStream, source.Length);
                if (!roundTrip.AsSpan().SequenceEqual(source))
                    status = "MANAGED MISMATCH";
            }
            catch (Exception ex)
            {
                status = "MANAGED EXCEPTION: " + ex.Message;
            }

            // (b) the official decoder must reproduce the input byte for byte
            string streamPath = Path.Combine(tempDir, name + ".lz");
            string decodedPath = Path.Combine(tempDir, name + ".out");
            File.WriteAllBytes(streamPath, managedStream);

            if (RunNative(nativeTool, new[] { "d", streamPath, decodedPath, DictLog2.ToString(CultureInfo.InvariantCulture), TableRate.ToString(CultureInfo.InvariantCulture) }, out string nativeError))
            {
                byte[] nativeRoundTrip = File.ReadAllBytes(decodedPath);
                if (!nativeRoundTrip.AsSpan().SequenceEqual(source))
                {
                    int firstDiff = FirstDifference(nativeRoundTrip, source);
                    status = $"NATIVE MISMATCH @ {firstDiff} (got {nativeRoundTrip.Length} bytes, expected {source.Length})";
                }
            }
            else
            {
                status = "NATIVE DECODE FAILED: " + nativeError.Trim();
            }

            // Native encoder reference numbers (its output is headerless, so only compare sizes/timing).
            string nativeStreamPath = Path.Combine(tempDir, name + ".nat");
            var nsw = Stopwatch.StartNew();
            bool nativeOk = RunNative(nativeTool, new[] { "c", file, nativeStreamPath, DictLog2.ToString(CultureInfo.InvariantCulture), TableRate.ToString(CultureInfo.InvariantCulture), "3" }, out _);
            nsw.Stop();

            long nativeSize = nativeOk && File.Exists(nativeStreamPath) ? new FileInfo(nativeStreamPath).Length : -1;
            double nativeSeconds = nsw.Elapsed.TotalSeconds;

            double managedMBps = managedSeconds > 0 ? source.Length / 1048576.0 / managedSeconds : 0;
            double nativeMBps = nativeSeconds > 0 ? source.Length / 1048576.0 / nativeSeconds : 0;

            Console.WriteLine(
                $"{name,-16} {source.Length,10} {managedStream.Length,10} {Ratio(source.Length, managedStream.Length),7} " +
                $"{nativeSize,10} {(nativeSize > 0 ? Ratio(source.Length, nativeSize) : "-"),7} " +
                $"{managedMBps,9:F1} {nativeMBps,9:F1}  {status}");

            totalRaw += source.Length;
            totalManaged += managedStream.Length;
            totalNative += Math.Max(0, nativeSize);
            totalManagedSeconds += managedSeconds;
            totalNativeSeconds += nativeSeconds;

            if (status != "ok")
                failures++;
        }

        Console.WriteLine(new string('-', 108));
        Console.WriteLine(
            $"{"TOTAL",-16} {totalRaw,10} {totalManaged,10} {Ratio(totalRaw, totalManaged),7} " +
            $"{totalNative,10} {(totalNative > 0 ? Ratio(totalRaw, totalNative) : "-"),7} " +
            $"{(totalManagedSeconds > 0 ? totalRaw / 1048576.0 / totalManagedSeconds : 0),9:F1} " +
            $"{(totalNativeSeconds > 0 ? totalRaw / 1048576.0 / totalNativeSeconds : 0),9:F1}");
        Console.WriteLine();
        Console.WriteLine($"managed bytes / native bytes = {(totalNative > 0 ? (double)totalManaged / totalNative : 0):F3}");

        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch
        {
            // Temp cleanup is best effort.
        }

        if (failures != 0)
        {
            Console.WriteLine($"{failures} item(s) FAILED");
            return 1;
        }

        Console.WriteLine("all items verified against the managed and native decoders");
        return 0;
    }

    private static string Ratio(long raw, long compressed) =>
        compressed > 0 ? (raw / (double)compressed).ToString("F2", CultureInfo.InvariantCulture) : "-";

    private static int FirstDifference(byte[] actual, byte[] expected)
    {
        int n = Math.Min(actual.Length, expected.Length);
        for (int i = 0; i < n; i++)
        {
            if (actual[i] != expected[i])
                return i;
        }

        return n;
    }

    private static bool RunNative(string tool, string[] arguments, out string stderr)
    {
        var psi = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderrText = process.StandardError.ReadToEnd();
        process.WaitForExit();
        stderr = (stdout + " " + stderrText).Trim();

        if (process.ExitCode != 0)
            return false;

        // The tool reports failures on stdout and exits non-zero, but be defensive about both.
        return !stdout.Contains("failed", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindNativeTool()
    {
        // Walk up from the harness binary and the current directory looking for the bundled tool.
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "tools", "lzham-native", "bin", "lzham_tool.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
