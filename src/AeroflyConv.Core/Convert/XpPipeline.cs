using System.Diagnostics;
using AeroflyConv.Core.Aerofly;
using AeroflyConv.Core.Tm;
using AeroflyConv.Core.XPlane;

namespace AeroflyConv.Core.Conversion;

/// <summary>Everything the CLI and the GUI need to turn one X-Plane aircraft into an Aerofly package.</summary>
public sealed class XpJobOptions
{
    /// <summary>Aircraft folder or a single .acf file.</summary>
    public string Input = "";

    /// <summary>Working folder for the generated files. Defaults to %LOCALAPPDATA%\AeroflyConv\xp\&lt;name&gt;.</summary>
    public string? OutputDirectory;

    /// <summary>Aerofly .tmd used as the flight-model / animation skeleton.</summary>
    public string? TmdTemplate;

    /// <summary>Folder name inside the Aerofly user aircraft directory.</summary>
    public string? InstallName;

    public bool Install;
    public bool Dump;
    public bool Report;
    public bool NoTextures;
    public bool NoTmd;

    public XpConvertOptions Model = new();
}

public sealed class XpJobResult
{
    public string AcfPath = "";
    public string AircraftName = "";
    public string OutputDirectory = "";
    public string TmbPath = "";
    public string TmdPath = "";
    public string TmcPath = "";
    public string ReportPath = "";
    public string TemplatePath = "";
    public string InstallPath = "";
    public bool Installed;

    public int ObjectsRead;
    public int ObjectsSkipped;
    public int Triangles;
    public int Vertices;
    public int GeometryCount;
    public int MaterialCount;
    public int TextureCount;
    public int TextureTotal;
    public int PartCount;
    public int TmdRebinds;
    public int TmdBoundGeometry;
    public double Seconds;

    public readonly List<string> Log = [];
    public readonly List<string> Warnings = [];
    public readonly List<AnimatedPart> Parts = [];
    public readonly List<string> TmdLog = [];
}

/// <summary>
/// The X-Plane → Aerofly conversion as one callable unit: parse the .acf, build the .tmb, encode the
/// .ttx textures, rebind the .tmd/.tmc and optionally drop the result into the Aerofly folder.
/// </summary>
public static class XpPipeline
{
    public static XpJobResult Run(XpJobOptions options, Action<string>? log = null,
                                  CancellationToken cancellation = default)
    {
        var result = new XpJobResult();

        void Say(string message)
        {
            result.Log.Add(message);
            log?.Invoke(message);
        }

        string acfPath = ResolveAcf(options.Input);
        if (acfPath.Length == 0)
            throw new FileNotFoundException($"no .acf file found in '{options.Input}'");

        result.AcfPath = acfPath;
        var timer = Stopwatch.StartNew();

        AcfFile acf = AcfFile.ParseFile(acfPath);
        Say($"{Path.GetFileName(acfPath)}: {acf.Objects.Count} attached objects, {acf.WingCount} wings, " +
            $"{acf.EngineCount} engines");

        cancellation.ThrowIfCancellationRequested();
        ModelConversionResult model = XpModelConverter.Convert(acf, options.Model);

        string name = XpModelConverter.Sanitize(Path.GetFileNameWithoutExtension(acfPath)).ToLowerInvariant();
        string outDir = options.OutputDirectory is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                           "AeroflyConv", "xp", name);
        Directory.CreateDirectory(outDir);

        result.AircraftName = name;
        result.OutputDirectory = outDir;
        result.ObjectsRead = model.ObjectsRead;
        result.ObjectsSkipped = model.ObjectsSkipped;
        result.Triangles = model.Triangles;
        result.Vertices = model.Scene.VertexCount;
        result.GeometryCount = model.Scene.Geometries.Count;
        result.MaterialCount = model.Scene.Materials.Count;
        result.PartCount = model.Parts.Count;
        result.Parts.AddRange(model.Parts);
        result.Warnings.AddRange(model.Warnings);

        Say($"objects   : {model.ObjectsRead} read, {model.ObjectsSkipped} skipped");
        Say($"geometry  : {result.GeometryCount} objects, {result.MaterialCount} materials, " +
            $"{result.Triangles} triangles, {result.Vertices} vertices");

        cancellation.ThrowIfCancellationRequested();
        byte[] tmb = model.Scene.ToTmb(compress: true);
        string tmbPath = Path.Combine(outDir, name + ".tmb");
        File.WriteAllBytes(tmbPath, tmb);
        result.TmbPath = tmbPath;
        Say($"tmb       : {tmbPath} ({tmb.Length / 1024.0 / 1024.0:0.00} MB)");

        Say($"animated  : {result.PartCount} part(s)");
        foreach (AnimatedPart part in model.Parts)
            Say($"  {part.Name,-20} role={part.Role,-12} max={part.MaxDegrees:0.#}deg " +
                $"pivot=({part.Pivot[0]:0.###}, {part.Pivot[1]:0.###}, {part.Pivot[2]:0.###})");

        if (model.Warnings.Count > 0)
        {
            Say($"warnings  : {model.Warnings.Count}");
            foreach (string warning in model.Warnings) Say($"  {warning}");
        }

        if (options.Dump)
        {
            string txtPath = tmbPath + ".txt";
            File.WriteAllText(txtPath, TmTextDump.DumpText(File.ReadAllBytes(tmbPath), out _));
            Say($"dump      : {txtPath}");
        }

        if (!options.NoTextures)
        {
            cancellation.ThrowIfCancellationRequested();
            var textureLog = new List<string>();
            List<string> written = TtxExporter.Export(model.Textures, outDir, options.Model.FlipV, textureLog.Add);
            result.TextureCount = written.Count;
            result.TextureTotal = model.Textures.Count;
            Say($"textures  : {written.Count} of {model.Textures.Count} .ttx written");
            foreach (string line in textureLog) Say($"  {line}");
        }

        if (!options.NoTmd)
        {
            string template = options.TmdTemplate is { Length: > 0 } explicitTemplate
                ? explicitTemplate
                : FindTemplate(name);
            result.TemplatePath = template;

            if (template.Length == 0)
            {
                Say("tmd       : no Aerofly template found, skipped");
            }
            else if (!File.Exists(template))
            {
                Say($"tmd       : template '{template}' not found, skipped");
            }
            else
            {
                cancellation.ThrowIfCancellationRequested();
                List<TmTextNode> roots = TmTextTree.ParseFile(template);
                TmdBuildResult tmd = TmdBuilder.Build(roots, model);
                string tmdPath = Path.Combine(outDir, name + ".tmd");
                File.WriteAllText(tmdPath, tmd.Text);
                result.TmdPath = tmdPath;
                result.TmdRebinds = tmd.ReboundParts;
                result.TmdBoundGeometry = tmd.BoundGeometry;
                Say($"tmd       : {tmdPath} (template {Path.GetFileName(template)}, " +
                    $"{tmd.ReboundParts} part(s) rebound, {tmd.BoundGeometry} binding(s))");
                foreach (string entry in tmd.Log)
                {
                    result.TmdLog.Add(entry);
                    Say($"            {entry}");
                }

                string templateTmc = Path.ChangeExtension(template, ".tmc");
                if (File.Exists(templateTmc))
                {
                    string displayName = Path.GetFileNameWithoutExtension(acfPath);
                    string tmc = TmcBuilder.Build(TmTextTree.ParseFile(templateTmc), displayName,
                        acf.GetString("acf/_ICAO", "C172"));
                    string tmcPath = Path.Combine(outDir, name + ".tmc");
                    File.WriteAllText(tmcPath, tmc);
                    result.TmcPath = tmcPath;
                    Say($"tmc       : {tmcPath}");
                }
            }
        }

        if (options.Report)
        {
            string reportPath = Path.Combine(outDir, "conversion.txt");
            using (var writer = new StreamWriter(reportPath))
                WriteReport(writer, acfPath, model);
            result.ReportPath = reportPath;
            Say($"report    : {reportPath}");
        }

        timer.Stop();
        result.Seconds = timer.Elapsed.TotalSeconds;

        if (options.Install)
        {
            string folder = options.InstallName is { Length: > 0 } explicitName ? explicitName : name;
            string target = Install(outDir, folder, result.TemplatePath, Say);
            result.InstallPath = target;
            result.Installed = true;
        }

        return result;
    }

    /// <summary>Target folder inside the Aerofly user aircraft directory for a given install name.</summary>
    public static string InstallTarget(string folder) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Aerofly FS 4", "aircraft", folder);

    /// <summary>
    /// Copies a finished conversion into the Aerofly user aircraft folder. The model itself is only half
    /// the package: the engine also wants the panel, the repaint/config files and the auxiliary textures
    /// the template dynamics file references, so those are copied from the template aircraft when missing.
    /// </summary>
    public static string Install(string outDir, string folder, string template, Action<string>? log = null)
    {
        string target = InstallTarget(folder);
        Directory.CreateDirectory(target);

        int copied = 0;
        foreach (string file in Directory.GetFiles(outDir))
        {
            if (file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            copied++;
        }

        int extra = 0;
        string templateDir = template.Length > 0 ? Path.GetDirectoryName(template) ?? "" : "";
        if (templateDir.Length > 0 && Directory.Exists(templateDir))
        {
            // The template panel/checklist/config files describe the cockpit, not the airframe.
            foreach (string pattern in new[] { "*.ttx", "*.tmc", "*.tsb" })
                foreach (string file in Directory.GetFiles(templateDir, pattern))
                    extra += CopyIfMissing(file, Path.Combine(target, Path.GetFileName(file)));

            foreach (string file in new[] { "controls.tmd", "preview.ttx", "preview_small.ttx" })
            {
                string source = Path.Combine(templateDir, file);
                if (File.Exists(source)) extra += CopyIfMissing(source, Path.Combine(target, file));
            }

            string panel = Path.Combine(templateDir, "classic_panel");
            if (Directory.Exists(panel))
            {
                string panelTarget = Path.Combine(target, "classic_panel");
                Directory.CreateDirectory(panelTarget);
                foreach (string file in Directory.GetFiles(panel))
                    extra += CopyIfMissing(file, Path.Combine(panelTarget, Path.GetFileName(file)));
            }
        }

        int files = Directory.GetFiles(target).Length;
        double mb = Directory.GetFiles(target, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length) / 1048576.0;
        log?.Invoke($"install   : {target}");
        log?.Invoke($"            {copied} converted file(s), {extra} template file(s) added, " +
                    $"{files} file(s) / {mb:0.0} MB total");
        log?.Invoke("            start Aerofly, pick the aircraft under 'Aircraft', and report anything odd.");
        return target;
    }

    private static int CopyIfMissing(string source, string destination)
    {
        if (File.Exists(destination)) return 0;
        File.Copy(source, destination);
        return 1;
    }

    private static void WriteReport(TextWriter writer, string acfPath, ModelConversionResult result)
    {
        writer.WriteLine($"source      : {acfPath}");
        writer.WriteLine($"objects     : {result.ObjectsRead} read, {result.ObjectsSkipped} skipped");
        writer.WriteLine($"triangles   : {result.Triangles}");
        writer.WriteLine();
        writer.WriteLine("materials:");
        foreach (TmbMaterial material in result.Scene.Materials)
            writer.WriteLine($"  {material.Name,-28} {material.ShaderHint,-16} " +
                             string.Join(", ", material.Textures.Select(t => $"{t.Channel}={t.Name}")));
        writer.WriteLine();
        writer.WriteLine("geometry:");
        foreach (TmbGeometry geometry in result.Scene.Geometries)
            writer.WriteLine($"  {geometry.Id} {geometry.Name,-24} {geometry.Patches.Count} patch(es), " +
                             $"{geometry.Patches.Sum(p => p.NumFaces)} tris");
        writer.WriteLine();
        writer.WriteLine("animated parts:");
        foreach (AnimatedPart part in result.Parts)
            writer.WriteLine($"  {part.Name,-20} role={part.Role,-12} max={part.MaxDegrees:0.#} " +
                             $"pivot=({part.Pivot[0]:0.####}, {part.Pivot[1]:0.####}, {part.Pivot[2]:0.####}) " +
                             $"axis=({part.Axis[0]:0.####}, {part.Axis[1]:0.####}, {part.Axis[2]:0.####}) " +
                             string.Join(";", part.Datarefs));
        writer.WriteLine();
        writer.WriteLine("textures:");
        foreach (TextureSource texture in result.Textures)
            writer.WriteLine($"  {texture.TargetName,-32} <- {texture.SourcePath}");
        writer.WriteLine();
        writer.WriteLine("warnings:");
        foreach (string warning in result.Warnings) writer.WriteLine($"  {warning}");
    }

    /// <summary>Finds an installed Aerofly aircraft to use as animation / flight-model template.</summary>
    public static string FindTemplate(string aircraftName)
    {
        foreach (string root in CandidateAeroflyRoots())
        {
            string aircraftDir = Path.Combine(root, "aircraft");
            if (!Directory.Exists(aircraftDir)) continue;
            string normalized = Normalize(aircraftName);
            string? exact = Directory.GetDirectories(aircraftDir)
                .FirstOrDefault(d => Normalize(Path.GetFileName(d)) == normalized);
            if (exact is not null)
            {
                string? match = Directory.GetFiles(exact, "*.tmd")
                    .FirstOrDefault(f => Normalize(Path.GetFileNameWithoutExtension(f)) == normalized);
                if (match is not null) return match;
            }

            // any single-engine trainer works as a skeleton as long as it has cameras and a body
            foreach (string fallback in new[] { "c172", "dr400", "c42" })
            {
                string dir = Path.Combine(aircraftDir, fallback);
                if (!Directory.Exists(dir)) continue;
                string? file = Directory.GetFiles(dir, fallback + ".tmd").FirstOrDefault();
                if (file is not null) return file;
            }
        }
        return "";
    }

    /// <summary>Every Aerofly FS 4 installation folder we can find on this machine.</summary>
    public static IEnumerable<string> CandidateAeroflyRoots()
    {
        var roots = new List<string>();
        string? fromEnv = Environment.GetEnvironmentVariable("AEROFLY_ROOT");
        if (!string.IsNullOrEmpty(fromEnv)) roots.Add(fromEnv);

        foreach (string drive in DriveLetters())
        {
            roots.Add(Path.Combine(drive, "SteamLibrary", "steamapps", "common", "Aerofly FS 4 Flight Simulator"));
            roots.Add(Path.Combine(drive, "Steam", "steamapps", "common", "Aerofly FS 4 Flight Simulator"));
            roots.Add(Path.Combine(drive, "Program Files (x86)", "Steam", "steamapps", "common",
                "Aerofly FS 4 Flight Simulator"));
        }
        return roots.Where(Directory.Exists);
    }

    /// <summary>Every X-Plane 12 installation folder we can find on this machine.</summary>
    public static IEnumerable<string> CandidateXPlaneRoots()
    {
        var roots = new List<string>();
        string? fromEnv = Environment.GetEnvironmentVariable("XPLANE");
        if (!string.IsNullOrEmpty(fromEnv)) roots.Add(fromEnv);

        foreach (string drive in DriveLetters())
        {
            roots.Add(Path.Combine(drive, "X-Plane 12"));
            roots.Add(Path.Combine(drive, "SteamLibrary", "steamapps", "common", "X-Plane 12"));
            roots.Add(Path.Combine(drive, "Steam", "steamapps", "common", "X-Plane 12"));
        }
        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> DriveLetters()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }
        foreach (DriveInfo drive in drives)
            if (drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady)
                yield return drive.RootDirectory.FullName;
    }

    /// <summary>All aircraft folders (max two levels deep) that contain an .acf file.</summary>
    public static List<string> FindAircraftFolders()
    {
        var found = new List<string>();
        foreach (string root in CandidateXPlaneRoots())
        {
            string aircraft = Path.Combine(root, "Aircraft");
            if (!Directory.Exists(aircraft)) continue;
            AddIfAircraft(aircraft, found);
            foreach (string sub in SafeDirectories(aircraft))
                AddIfAircraft(sub, found);
        }
        return found;
    }

    private static void AddIfAircraft(string folder, List<string> found)
    {
        try
        {
            if (Directory.GetFiles(folder, "*.acf").Length > 0 &&
                !found.Contains(folder, StringComparer.OrdinalIgnoreCase))
                found.Add(folder);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    public static string ResolveAcf(string input)
    {
        if (File.Exists(input) && input.EndsWith(".acf", StringComparison.OrdinalIgnoreCase)) return input;
        if (!Directory.Exists(input)) return "";
        string[] candidates = Directory.GetFiles(input, "*.acf");
        if (candidates.Length == 0) return "";
        candidates = candidates
            .Where(c => !c.EndsWith("_vr.acf", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Prefer the .acf whose name matches the folder (Cessna 172 SP -> Cessna_172SP.acf).
        string folder = Normalize(Path.GetFileName(Path.TrimEndingDirectorySeparator(input)));
        string? best = candidates.FirstOrDefault(c => Normalize(Path.GetFileNameWithoutExtension(c)) == folder);
        if (best is not null) return best;

        // Otherwise the largest plausible file, ignoring obvious variant suffixes.
        string[] variantWords = ["seaplane", "_vr", "_float", "_amph", "_taildragger"];
        string[] main = candidates
            .Where(c => !variantWords.Any(w => c.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (main.Length == 0) main = candidates;
        return main.OrderByDescending(c => new FileInfo(c).Length).First();
    }

    public static string Normalize(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
