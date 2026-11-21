using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AeroflyConv.Gui;

internal static class Program
{
    private static readonly string TracePath = Path.Combine(AppContext.BaseDirectory, "shot_trace.txt");
    private static readonly string ErrorLogPath = Path.Combine(AppContext.BaseDirectory, "AeroflyConv-error.log");
    private static bool _trace;

    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            // Windows Forms writes a temporary activation-context manifest on startup. On machines
            // where %TEMP% is not writable that throws and the app dies without any window, so point
            // TMP/TEMP at a folder we know we can write before touching WinForms.
            PrepareTempDirectory();

            try
            {
                ApplicationConfiguration.Initialize();
            }
            catch (Exception ex)
            {
                LogCrash("Initialize", ex);
                try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); }
                catch (Exception) { }
            }

            try { Application.SetColorMode(SystemColorMode.Dark); }
            catch (Exception ex) { LogCrash("SetColorMode", ex); }

            Application.ThreadException += (_, e) =>
            {
                Trace("ThreadException " + e.Exception);
                LogCrash("ThreadException", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                var error = e.ExceptionObject as Exception;
                Trace("Unhandled " + error);
                LogCrash("Unhandled", error);
            };

            // `AeroflyConv.exe --screenshot out.png` captures the live window and exits (docs / UI tests).
            if (args.Length >= 2 && args[0] == "--screenshot")
            {
                string path = Path.GetFullPath(args[1]);
                _trace = Environment.GetEnvironmentVariable("AEROFLYCONV_UI_TRACE") == "1";
                using var form = new MainForm();
                for (int i = 2; i + 1 < args.Length; i++)
                {
                    if (args[i] == "--nav" && int.TryParse(args[i + 1], out int navIndex))
                        form.SelectNav(navIndex);
                }
                var timer = new System.Windows.Forms.Timer { Interval = 1000 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    try
                    {
                        Capture(form, path);
                    }
                    catch (Exception ex)
                    {
                        Trace("capture failed: " + ex);
                        LogCrash("screenshot", ex);
                    }
                    finally
                    {
                        form.Close();
                    }
                };
                form.Shown += (_, _) =>
                {
                    Trace("shown");
                    timer.Start();
                };
                Application.Run(form);
                return;
            }

            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            LogCrash("Fatal", ex);
            ShowFatalError(ex);
        }
    }

    /// <summary>Makes sure %TMP%/%TEMP% point somewhere writable before any WinForms call.</summary>
    private static void PrepareTempDirectory()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AeroflyConv", "temp"),
            Path.Combine(AppContext.BaseDirectory, "temp"),
        ];

        foreach (string candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                string probe = Path.Combine(candidate, "probe.tmp");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                Environment.SetEnvironmentVariable("TMP", candidate);
                Environment.SetEnvironmentVariable("TEMP", candidate);
                return;
            }
            catch (Exception)
            {
                // try the next candidate
            }
        }
    }

    private static void ShowFatalError(Exception ex)
    {
        string message =
            "Aerofly Converter could not start.\n\n" +
            ex.GetType().Name + ": " + ex.Message + "\n\n" +
            "Details were written to:\n" + ErrorLogPath;
        try
        {
            MessageBox.Show(message, "Aerofly Converter", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception)
        {
            try { Console.Error.WriteLine(message); }
            catch (Exception) { }
        }
    }

    private static void Capture(MainForm form, string path)
    {
        Trace($"capture start {form.Width}x{form.Height} visible={form.Visible} loc={form.Location}");
        using var bmp = new Bitmap(form.Width, form.Height);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            form.Invalidate(true);
            form.Update();
            Application.DoEvents();
            IntPtr hdc = g.GetHdc();
            bool ok = PrintWindow(form.Handle, hdc, 2);
            g.ReleaseHdc(hdc);
            Trace("printwindow=" + ok);
            if (!ok)
            {
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                Trace("drawtobitmap fallback");
            }
        }
        bmp.Save(path, ImageFormat.Png);
        Trace("saved " + path);
    }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    internal static void Trace(string message)
    {
        if (!_trace) return;
        try
        {
            File.AppendAllText(TracePath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
        }
        catch (Exception)
        {
            // tracing is best effort only
        }
    }

    internal static void LogCrash(string kind, Exception? ex)
    {
        try
        {
            File.AppendAllText(ErrorLogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {kind}: {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // never let logging mask the original failure
        }
    }
}
