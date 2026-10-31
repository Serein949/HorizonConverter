using Android.App;
using Android.Content;
using Android.Database;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using AeroflyConv.Core;
using AeroflyConv.Core.Conversion;
using AeroflyConv.Core.Textures;
using Uri = Android.Net.Uri;
using Path = System.IO.Path;
using AndroidResource = Android.Resource;

namespace AeroflyConv.Android;

[Activity(Label = "Aerofly Converter", MainLauncher = true, Theme = "@android:style/Theme.Material.NoActionBar")]
public class MainActivity : Activity
{
    private const int RequestPickFiles = 1001;

    private static readonly Color Bg = Color.ParseColor("#0B0E14");
    private static readonly Color Card = Color.ParseColor("#151A22");
    private static readonly Color Fg = Color.ParseColor("#F8FAFD");
    private static readonly Color Muted = Color.ParseColor("#96A0AF");
    private static readonly Color Accent = Color.ParseColor("#00B8FF");

    private readonly List<string> _picked = [];

    private TextView _filesLabel = null!;
    private TextView _status = null!;
    private TextView _log = null!;
    private ProgressBar _progress = null!;
    private Spinner _modeSpinner = null!;
    private Button _convertButton = null!;
    private bool _busy;

    private static readonly string[] ModeLabels =
    [
        "Auto detect (by extension)",
        "TTX \u2192 PNG",
        "PNG \u2192 TTX (DXT1/5)",
        "TSB \u2192 WAV",
        "WAV \u2192 TSB",
        "TM \u2192 TXT (toc/tsc/wad/tmb/tsl)",
        "TTC \u2192 PNG",
        "PNG \u2192 TTC",
    ];

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetStatusBarColor(Color.ParseColor("#0B0E14"));
        Window?.SetNavigationBarColor(Color.ParseColor("#0B0E14"));

        SetContentView(BuildUi());

        // create the drop folder so files can also be copied in over USB / adb
        try
        {
            Directory.CreateDirectory(Path.Combine(BaseDir(), "in"));
            Directory.CreateDirectory(Path.Combine(BaseDir(), "out"));
            Log("drop folder: " + Path.Combine(BaseDir(), "in"));
        }
        catch (Exception ex)
        {
            Log("cannot create folders: " + ex.Message);
        }

        if (Intent?.GetBooleanExtra("autorun", false) == true)
        {
            _modeSpinner.SetSelection(ModeFromName(Intent.GetStringExtra("mode")));
            RunAutoAsync();
        }
    }

    // ------------------------------------------------------------------ UI

    private View BuildUi()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Bg);
        root.SetPadding(Dp(18), Dp(24), Dp(18), Dp(16));

        var title = new TextView(this) { Text = "Aerofly Converter", TextSize = 24 };
        title.SetTextColor(Fg);
        title.Typeface = Typeface.DefaultBold;
        root.AddView(title);

        var subtitle = new TextView(this)
        {
            Text = "TTX \u2194 PNG   TSB \u2194 WAV   TTC \u2194 PNG   TOC \u2192 TXT",
            TextSize = 12,
        };
        subtitle.SetTextColor(Muted);
        root.AddView(subtitle);

        _modeSpinner = new Spinner(this);
        var adapter = new ArrayAdapter<string>(this, AndroidResource.Layout.SimpleSpinnerDropDownItem, ModeLabels);
        adapter.SetDropDownViewResource(AndroidResource.Layout.SimpleSpinnerDropDownItem);
        _modeSpinner.Adapter = adapter;
        var spinnerParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(48));
        spinnerParams.TopMargin = Dp(16);
        root.AddView(_modeSpinner, spinnerParams);

        var buttonRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var buttonRowParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(52));
        buttonRowParams.TopMargin = Dp(14);
        root.AddView(buttonRow, buttonRowParams);

        var pick = MakeButton("Select files", accent: false);
        pick.Click += (_, _) => PickFiles();
        buttonRow.AddView(pick, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f));

        _convertButton = MakeButton("Convert", accent: true);
        _convertButton.Click += async (_, _) => await ConvertPickedAsync();
        var convertParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f);
        convertParams.LeftMargin = Dp(10);
        buttonRow.AddView(_convertButton, convertParams);

        _filesLabel = new TextView(this) { Text = "No files selected", TextSize = 12 };
        _filesLabel.SetTextColor(Muted);
        var filesParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        filesParams.TopMargin = Dp(14);
        root.AddView(_filesLabel, filesParams);

        _progress = new ProgressBar(this, null, AndroidResource.Attribute.ProgressBarStyleHorizontal) { Max = 1 };
        var progressParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(10));
        progressParams.TopMargin = Dp(12);
        root.AddView(_progress, progressParams);

        _status = new TextView(this) { Text = "Ready", TextSize = 12 };
        _status.SetTextColor(Muted);
        var statusParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        statusParams.TopMargin = Dp(6);
        root.AddView(_status, statusParams);

        var scroll = new ScrollView(this);
        var scrollParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f);
        scrollParams.TopMargin = Dp(12);
        root.AddView(scroll, scrollParams);

        _log = new TextView(this)
        {
            Text = "Results are saved to Download/AeroflyConv",
            TextSize = 11,
        };
        _log.SetTextColor(Color.ParseColor("#B0D6BE"));
        _log.Typeface = Typeface.Monospace;
        _log.SetTextIsSelectable(true);

        var logCard = new LinearLayout(this) { Orientation = Orientation.Vertical };
        logCard.SetBackgroundColor(Card);
        logCard.SetPadding(Dp(12), Dp(12), Dp(12), Dp(12));
        logCard.AddView(_log);
        scroll.AddView(logCard);
        return root;
    }

    private Button MakeButton(string text, bool accent)
    {
        var button = new Button(this) { Text = text, TextSize = 14 };
        button.SetTextColor(accent ? Color.ParseColor("#04121A") : Fg);
        button.SetBackgroundColor(accent ? Accent : Card);
        button.SetAllCaps(false);
        return button;
    }

    private int Dp(int value) => (int)(value * (Resources?.DisplayMetrics?.Density ?? 1f));

    private void Log(string message) => RunOnUiThread(() => _log.Text += "\n" + message);

    private void SetStatus(string text) => RunOnUiThread(() => _status.Text = text);

    // ------------------------------------------------------------------ picking

    private void PickFiles()
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraAllowMultiple, true);
        StartActivityForResult(intent, RequestPickFiles);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != RequestPickFiles || resultCode != Result.Ok || data is null) return;

        var uris = new List<Uri>();
        if (data.ClipData is not null)
        {
            for (int i = 0; i < data.ClipData.ItemCount; i++)
            {
                Uri? uri = data.ClipData.GetItemAt(i)?.Uri;
                if (uri is not null) uris.Add(uri);
            }
        }
        else if (data.Data is not null)
        {
            uris.Add(data.Data);
        }

        foreach (Uri uri in uris)
        {
            string name = DisplayName(uri) ?? $"file_{_picked.Count}";
            string local = CopyToCache(uri, name);
            if (local.Length > 0) _picked.Add(local);
        }

        _filesLabel.Text = _picked.Count == 0
            ? "No files selected"
            : $"{_picked.Count} file(s): " + string.Join(", ", _picked.Select(Path.GetFileName));
    }

    private string? DisplayName(Uri uri)
    {
        try
        {
            using ICursor? cursor = ContentResolver?.Query(uri, [IOpenableColumns.DisplayName], null, null, null);
            if (cursor is not null && cursor.MoveToFirst())
            {
                int index = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
                if (index >= 0) return cursor.GetString(index);
            }
        }
        catch (Exception)
        {
            // fall back to the path segment
        }
        return uri.LastPathSegment?.Split('/').Last();
    }

    private string CopyToCache(Uri uri, string displayName)
    {
        try
        {
            string dir = Path.Combine(CacheDir!.AbsolutePath, "in");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, Sanitize(displayName));
            using Stream? input = ContentResolver?.OpenInputStream(uri);
            if (input is null) return "";
            using var output = File.Create(target);
            input.CopyTo(output);
            return target;
        }
        catch (Exception ex)
        {
            Log("read failed: " + ex.Message);
            return "";
        }
    }

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "file.bin" : name;
    }

    // ------------------------------------------------------------------ converting

    private static ConversionMode SelectedMode(int index) => index switch
    {
        1 => ConversionMode.TtxToPng,
        2 => ConversionMode.PngToTtx,
        3 => ConversionMode.TsbToWav,
        4 => ConversionMode.WavToTsb,
        5 => ConversionMode.TmToTxt,
        6 => ConversionMode.TtcToPng,
        7 => ConversionMode.PngToTtc,
        _ => ConversionMode.Auto,
    };

    private static int ModeFromName(string? name) => name?.ToLowerInvariant() switch
    {
        "ttx2png" => 1,
        "png2ttx" => 2,
        "tsb2wav" => 3,
        "wav2tsb" => 4,
        "tm2txt" => 5,
        "ttc2png" => 6,
        "png2ttc" => 7,
        _ => 0,
    };

    private async Task ConvertPickedAsync()
    {
        if (_busy) return;
        if (_picked.Count == 0)
        {
            Log("select files first");
            return;
        }

        _busy = true;
        _convertButton.Enabled = false;
        _progress.Max = _picked.Count;
        _progress.Progress = 0;

        ConversionMode mode = SelectedMode(_modeSpinner.SelectedItemPosition);
        await Task.Run(() => ConvertFiles(_picked, mode));

        _busy = false;
        _convertButton.Enabled = true;
        SetStatus("Done");
    }

    private void ConvertFiles(IReadOnlyList<string> files, ConversionMode mode)
    {
        var options = new ConvertOptions
        {
            FlipExplicit = false,               // texture modes flip (bottom-up storage)
            TtxFormat = TextureFormats.AutoDxt,
            CompressTtx = true,
            MaxParallelism = Math.Clamp(System.Environment.ProcessorCount, 1, 4),
        };

        int ok = 0, failed = 0, done = 0;

        foreach (string file in files)
        {
            ConversionOutcome outcome = Converter.ConvertFile(file, null, mode, options);
            done++;
            RunOnUiThread(() => _progress.Progress = done);

            if (!outcome.Ok || outcome.Output is null)
            {
                failed++;
                Log($"FAIL {Path.GetFileName(file)}: {outcome.Error}");
                continue;
            }

            try
            {
                string published = PublishToDownloads(outcome.Output, Path.GetFileName(outcome.Output));
                ok++;
                Log($"OK   {Path.GetFileName(file)} \u2192 {published}");
            }
            catch (Exception ex)
            {
                failed++;
                Log($"FAIL publishing {Path.GetFileName(outcome.Output)}: {ex.Message}");
            }

            SetStatus($"{done}/{files.Count}  \u00b7  {ok} ok, {failed} failed");
        }

        SetStatus($"{ok}/{files.Count} converted" + (failed > 0 ? $", {failed} failed" : ""));
    }

    private string BaseDir() => GetExternalFilesDir(null)?.AbsolutePath ?? CacheDir!.AbsolutePath;

    private string OutputDirectory()
    {
        string dir = Path.Combine(BaseDir(), "out");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void LogResult(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(OutputDirectory(), "log.txt"),
                DateTime.Now.ToString("HH:mm:ss") + " " + message + System.Environment.NewLine);
        }
        catch (Exception)
        {
            // best effort only
        }
    }

    /// <summary>Copies a result into the public Download collection so the file manager can see it.</summary>
    private string PublishToDownloads(string localPath, string displayName)
    {
        string mime = MimeFor(displayName);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
        {
            var values = new ContentValues();
            values.Put(MediaStore.IMediaColumns.DisplayName, displayName);
            values.Put(MediaStore.IMediaColumns.MimeType, mime);
            values.Put(MediaStore.IMediaColumns.RelativePath, "Download/AeroflyConv");

            Uri? collection = MediaStore.Downloads.GetContentUri(MediaStore.VolumeExternalPrimary);
            Uri? target = ContentResolver?.Insert(collection!, values);
            if (target is not null)
            {
                using Stream? output = ContentResolver?.OpenOutputStream(target);
                if (output is not null)
                {
                    using FileStream input = File.OpenRead(localPath);
                    input.CopyTo(output);
                    return "Download/AeroflyConv/" + displayName;
                }
            }
        }

        string dest = Path.Combine(OutputDirectory(), displayName);
        File.Copy(localPath, dest, overwrite: true);
        return dest;
    }

    private static string MimeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".wav" => "audio/wav",
        ".txt" => "text/plain",
        ".zip" or ".tme" => "application/zip",
        _ => "application/octet-stream",
    };

    // ------------------------------------------------------------------ headless test hook

    /// <summary>Test hook: converts everything in files/in and writes files/out/result.txt.</summary>
    private async void RunAutoAsync()
    {
        string baseDir = GetExternalFilesDir(null)?.AbsolutePath ?? CacheDir!.AbsolutePath;
        string inputDir = Path.Combine(baseDir, "in");
        Directory.CreateDirectory(inputDir);
        List<string> files;
        try
        {
            files = Directory.GetFiles(inputDir)
                .Where(f => !Path.GetFileName(f).StartsWith("result", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToList();
        }
        catch (Exception ex)
        {
            Log("cannot list input files: " + ex.Message);
            WriteResult("cannot list input files: " + ex.Message);
            return;
        }

        Log($"autorun: {files.Count} file(s) in {inputDir}");
        if (files.Count == 0)
        {
            WriteResult("no input files");
            return;
        }

        var lines = new List<string>();
        ConversionMode mode = SelectedMode(_modeSpinner.SelectedItemPosition);
        await Task.Run(() =>
        {
            var options = new ConvertOptions { FlipExplicit = false, TtxFormat = TextureFormats.AutoDxt };
            string outDir = OutputDirectory();
            foreach (string file in files)
            {
                ConversionOutcome outcome = Converter.ConvertFile(file, null, mode, options);
                if (outcome.Ok && outcome.Output is not null)
                {
                    string dest = Path.Combine(outDir, Path.GetFileName(outcome.Output));
                    File.Copy(outcome.Output, dest, overwrite: true);
                    lines.Add($"OK {Path.GetFileName(file)} -> {dest} | {outcome.Summary}");
                    Log($"OK {Path.GetFileName(file)} -> {Path.GetFileName(dest)}");
                }
                else
                {
                    lines.Add($"FAIL {Path.GetFileName(file)}: {outcome.Error}");
                    Log($"FAIL {Path.GetFileName(file)}: {outcome.Error}");
                }
            }
        });

        WriteResult(string.Join("\n", lines));
    }

    private void WriteResult(string text)
    {
        try
        {
            string path = Path.Combine(OutputDirectory(), "result.txt");
            File.WriteAllText(path, text);
            Log("result written: " + path);
        }
        catch (Exception ex)
        {
            Log("result write failed: " + ex.Message);
        }
    }
}



