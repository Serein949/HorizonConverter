using AeroflyConv.Core.Conversion;

namespace AeroflyConv.Cli;

/// <summary>X-Plane aircraft to Aerofly conversion commands.</summary>
internal static class XPlane
{
    public static int Run(ReadOnlySpan<string> args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine("""
                X-Plane aircraft conversion

                  AeroflyConv xplane <aircraft dir | .acf> [options]

                Options:
                  -o, --outdir <dir>   Output directory (default: %LOCALAPPDATA%\AeroflyConv\xp\<name>)
                  --no-flip-v          Do not flip texture V coordinates
                  --scale <factor>     Scale model positions and pivots
                  --skip-interior      Leave cockpit objects out
                  --dump               Also write <name>.tmb.txt for inspection
                  --report             Write a conversion report next to the output
                  --tmd <template.tmd> Rebind graphics onto this Aerofly .tmd (animation binding)
                  --no-tmd             Do not emit a .tmd
                  --no-textures        Do not convert textures to .ttx
                  --install            Copy the result into the Aerofly user aircraft folder and
                                       complete the package (panel, textures, config) from the template
                  --name <folder>      Install folder name (default: the .tmb base name)
                """);
            return args.Length == 0 ? 2 : 0;
        }

        var job = new XpJobOptions();
        string? input = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-o":
                case "--outdir":
                    job.OutputDirectory = Require(args, ref i, arg);
                    break;
                case "--no-flip-v":
                    job.Model.FlipV = false;
                    break;
                case "--scale":
                    job.Model.Scale = double.Parse(Require(args, ref i, arg),
                        System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--skip-interior":
                    job.Model.SkipInterior = true;
                    break;
                case "--dump":
                    job.Dump = true;
                    break;
                case "--report":
                    job.Report = true;
                    break;
                case "--debug":
                    job.Model.Debug = true;
                    break;
                case "--tmd":
                    job.TmdTemplate = Require(args, ref i, arg);
                    break;
                case "--no-tmd":
                    job.NoTmd = true;
                    break;
                case "--no-textures":
                    job.NoTextures = true;
                    break;
                case "--install":
                    job.Install = true;
                    break;
                case "--name":
                    job.InstallName = Require(args, ref i, arg);
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"unknown option: {arg}");
                        return 2;
                    }
                    input ??= arg;
                    break;
            }
        }

        if (input is null)
        {
            Console.Error.WriteLine("no aircraft directory given");
            return 2;
        }

        try
        {
            job.Input = input;
            XpJobResult result = XpPipeline.Run(job, Console.WriteLine);
            Console.WriteLine($"done      : {result.Seconds:0.00} s");
            return 0;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine($"install   : failed ({ex.Message})");
            Console.Error.WriteLine("            copy the output folder into " +
                                    "%USERPROFILE%\\Documents\\Aerofly FS 4\\aircraft\\<name> by hand, " +
                                    "or run the converter outside the sandbox");
            return 1;
        }
    }

    private static string Require(ReadOnlySpan<string> args, ref int i, string option)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{option} requires a value");
        return args[++i];
    }
}
