using Android.App;
using Android.Content;
using Android.Database;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Text;
using Android.Text.Style;
using ClipboardManager = Android.Content.ClipboardManager;
using Android.Views;
using Android.Widget;
using AeroflyConv.Core;
using AeroflyConv.Core.Codecs;
using AeroflyConv.Core.Conversion;
using AeroflyConv.Core.Textures;
using Uri = Android.Net.Uri;
using Path = System.IO.Path;
using AndroidResource = Android.Resource;

namespace AeroflyConv.Android;

[Activity(Label = MainActivity.AppLabel, MainLauncher = true, Theme = "@android:style/Theme.Material.NoActionBar")]
public class MainActivity : Activity
{
    private const int RequestPickFiles = 1001;
#if PUBLIC_BUILD
    /// <summary>Window and launcher label; the public build drops the Alpha tag.</summary>
    public const string AppLabel = "Aerofly 转换器";

    private const bool IsPublicBuild = true;

#else
    public const string AppLabel = "Aerofly 转换器 Alpha";

    private const bool IsPublicBuild = false;

#endif

    /// <summary>Window title / log prefix, always in sync with <see cref="ProductInfo.Version"/>.</summary>
    private static readonly string ProductLabel = (IsPublicBuild ? ProductInfo.Name : ProductInfo.FullName) + " v" + ProductInfo.Version;

    private const int RequestPickFolder = 1002;
    private const int RequestPickOutput = 1003;
    private const string Prefs = "aeroflyconv";

    private static readonly Color Bg = Color.ParseColor("#0B0E14");
    private static readonly Color Fg = Color.ParseColor("#F8FAFD");
    private static readonly Color Muted = Color.ParseColor("#7C8797");
    private static readonly Color Soft = Color.ParseColor("#C6CEDA");
    private static readonly Color Accent = Color.ParseColor("#00B8FF");

    private static readonly string[] ModeLabels =
    [
        "自动识别",
        "TTX \u2192 PNG",
        "图片 \u2192 TTX",
        "TSB \u2192 WAV",
        "音频 \u2192 TSB",
        "TM \u2192 TXT",
        "TTC \u2192 PNG",
        "图片 \u2192 TTC",
        "TTX \u2192 TTX",
    ];

    private static readonly string[] ModeHints =
    [
        "根据文件扩展名自动判断转换方向。",
        "还原 DXT1/3/5、BC5、ETC1/2、ASTC（手机端）以及 RGBA/R8 为 PNG。",
        "把 PNG / JPG / BMP / PSD 等图片重新编码成 TTX 贴图（DXT1 / DXT5 / ETC2 / R8 / RGBA 或自动）。",
        "把 Aerofly 音效包解包成 WAV 文件。",
        "把 MP3 / FLAC / OGG / WAV 直接打包成 Aerofly TSB 音效包。",
        "把 TOC/TSC/WAD/TMB/TSL 容器导出为可读文本。",
        "把地景贴图容器（.ttc）的第 0 级贴图转成 PNG。",
        "把图片编码成 .ttc 地景贴图。",
        "在手机上解码 TTX 再重新编码成另一种格式的 TTX。",
    ];

    /// <summary>Indexes into <see cref="ModeLabels"/> that this build offers.</summary>
    private static readonly int[] VisibleModeIndexes =
#if PUBLIC_BUILD
        [2, 4, 8];
#else
        [0, 1, 2, 3, 4, 5, 6, 7, 8];
#endif

    /// <summary>Mode list entry for the current pill selection.</summary>
    private int RealMode => VisibleModeIndexes[Math.Clamp(_modes.SelectedIndex, 0, VisibleModeIndexes.Length - 1)];

    private readonly List<Picked> _picked = [];
    private readonly List<ResultItem> _results = [];

    private LinearLayout _llModes = null!;
    private LinearLayout _llFormat = null!;
    private LinearLayout _llSize = null!;
    private LinearLayout _llQuality = null!;
    private LinearLayout _llFiles = null!;
    private LinearLayout _rowFormat = null!, _rowSize = null!, _rowFlip = null!, _rowCompress = null!, _rowTtcMips = null!;
    private LinearLayout _rowQuality = null!;
    private LinearLayout _rowResultActions = null!;
    private LinearLayout _cardOptions = null!;
    private TextView _tvModeHint = null!;
    private TextView _tvFileCount = null!;
    private TextView _tvEmpty = null!;
    private TextView _tvStatus = null!;
    private TextView _tvLog = null!;
    private TextView _tvLogCount = null!;
    private LinearLayout _rowLogHeader = null!;
    private TextView _tvLogChevron = null!;
    private readonly System.Text.StringBuilder _logText = new();
    private int _logCount;
    private TextView _tvOutput = null!;
    private ProgressBar _progress = null!;
    private Switch _swFlip = null!;
    private Switch _swCompress = null!;
    private Switch _swTtcMips = null!;
    private Button _btnConvert = null!;
    private ScrollView _scrollLog = null!;

    private PillGroup _modes = null!;
    private PillGroup _formats = null!;
    private PillGroup _sizes = null!;
    private PillGroup _qualities = null!;

    private static readonly string[] FormatLabels = ["自动", "DXT1", "DXT5", "ETC2", "R8", "RGBA 无损", "ASTC 6x6", "ASTC 5x5"];
    private static readonly string[] TTXFormats = ["dxt_auto", "dxt1", "dxt5", "etc2", "r8", "type_rgba", "astc6x6", "astc5x5"];
    private static readonly string[] TtcFormats = ["dxt1", "dxt1", "dxt5", "etc2", "r8", "dxt1", "dxt1", "dxt1"];
    private static readonly string[] SizeLabels = ["匹配原尺寸", "4096", "2048", "1024", "源尺寸"];
    private static readonly int?[] SizeValues = [null, 4096, 2048, 1024, null];
    private static readonly string[] QualityLabels = ["快速", "标准", "高质量"];

    private Uri? _outputTree;
    private bool _busy;
    private CancellationTokenSource? _cts;

    private sealed record Picked(string LocalPath, string DisplayName)
    {
        public TextView? RowStatus { get; set; }
    }
    private sealed record ResultItem(string DisplayName, string Where, Uri? Uri);

    /// <summary>
    /// Writes an unhandled exception to <c>Android/data/com.aeroflyconv.*/files/crash.txt</c> so a
    /// crash can still be diagnosed when no adb session is available.
    /// </summary>
    private void InstallCrashLog()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject as Exception);
        global::Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
        {
            WriteCrash(e.Exception);
            e.Handled = true;
        };
    }

    private void WriteCrash(Exception? ex)
    {
        if (ex is null)
        {
            return;
        }

        try
        {
            string path = Path.Combine(GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath, "crash.txt");
            File.WriteAllText(path, DateTime.Now.ToString("s") + System.Environment.NewLine + ex);
        }
        catch (Exception)
        {
            // nothing useful left to do
        }
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        InstallCrashLog();
        Title = ProductLabel;
        // let the wallpaper gradient run under the status bar; the insets listener pads the content
        Window?.AddFlags(WindowManagerFlags.DrawsSystemBarBackgrounds);
        Window?.SetStatusBarColor(Bg);
        Window?.SetNavigationBarColor(Bg);

        SetContentView(Resource.Layout.activity_main);
        BindViews();
        BuildPills();
        RestoreState();
        WireEvents();

        try
        {
            Directory.CreateDirectory(Path.Combine(BaseDir(), "in"));
            Directory.CreateDirectory(Path.Combine(BaseDir(), "out"));
        }
        catch (Exception ex)
        {
            Log("无法创建目录：" + ex.Message);
        }

        Log("就绪 · " + ProductLabel + " · " + ModeLabels[RealMode] + " · 输出：" + OutputLabel());
        UpdateModeUi();

        if (Intent?.GetBooleanExtra("autorun", false) == true)
        {
            if (Intent.GetStringExtra("mode") is { } m)
            {
                // ModeFromName returns an index into ModeLabels, while the pills are positions in
                // VisibleModeIndexes - map one to the other so hidden modes cannot shift the list.
                int labelIndex = ModeFromName(m);
                int pillIndex = Array.IndexOf(VisibleModeIndexes, labelIndex);
                _modes.SetSelected(Math.Max(0, pillIndex));
                SaveState();
            }
            if (Intent.GetStringExtra("format") is { } f)
            {
                int fi = Array.IndexOf(TTXFormats, f.ToLowerInvariant());
                if (fi >= 0) _formats.SetSelected(fi);
            }
            if (Intent.GetStringExtra("quality") is { } q &&
                AstcQualityNames.TryParse(q, out AstcQuality parsedQuality))
            {
                _qualities.SetSelected((int)parsedQuality);
            }
            if (Intent.HasExtra("flip"))
            {
                _swFlip.Checked = Intent.GetBooleanExtra("flip", true);
            }
            if (Intent.GetBooleanExtra("nocompress", false))
            {
                _swCompress.Checked = false;
            }
            UpdateModeUi();
            RunAutoAsync();
            return;
        }

        AcceptIncoming(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        AcceptIncoming(intent);
    }

    // ------------------------------------------------------------------ views

    private void BindViews()
    {
        _llModes = FindViewById<LinearLayout>(Resource.Id.llModes)!;
        _llFormat = FindViewById<LinearLayout>(Resource.Id.llFormat)!;
        _llSize = FindViewById<LinearLayout>(Resource.Id.llSize)!;
        _llQuality = FindViewById<LinearLayout>(Resource.Id.llQuality)!;
        _llFiles = FindViewById<LinearLayout>(Resource.Id.llFiles)!;
        _rowFormat = FindViewById<LinearLayout>(Resource.Id.rowFormat)!;
        _rowSize = FindViewById<LinearLayout>(Resource.Id.rowSize)!;
        _rowQuality = FindViewById<LinearLayout>(Resource.Id.rowQuality)!;
        _rowFlip = FindViewById<LinearLayout>(Resource.Id.rowFlip)!;
        _rowCompress = FindViewById<LinearLayout>(Resource.Id.rowCompress)!;
        _rowTtcMips = FindViewById<LinearLayout>(Resource.Id.rowTtcMips)!;
        _rowResultActions = FindViewById<LinearLayout>(Resource.Id.rowResultActions)!;
        _cardOptions = FindViewById<LinearLayout>(Resource.Id.cardOptions)!;
        _tvFileCount = FindViewById<TextView>(Resource.Id.tvFileCount)!;
        _tvEmpty = FindViewById<TextView>(Resource.Id.tvEmpty)!;
        _tvStatus = FindViewById<TextView>(Resource.Id.tvStatus)!;
        _tvLog = FindViewById<TextView>(Resource.Id.tvLog)!;
        _tvLogCount = FindViewById<TextView>(Resource.Id.tvLogCount)!;
        _rowLogHeader = FindViewById<LinearLayout>(Resource.Id.rowLogHeader)!;
        _tvLogChevron = FindViewById<TextView>(Resource.Id.tvLogChevron)!;
        _rowLogHeader.Click += (_, _) => ToggleLog();
        FindViewById<TextView>(Resource.Id.btnLogCopy)!.Click += (_, _) => CopyLog();
        FindViewById<TextView>(Resource.Id.btnLogClear)!.Click += (_, _) => ClearLog();
        _tvOutput = FindViewById<TextView>(Resource.Id.tvOutput)!;
        _progress = FindViewById<ProgressBar>(Resource.Id.progress)!;
        _swFlip = FindViewById<Switch>(Resource.Id.swFlip)!;
        _swCompress = FindViewById<Switch>(Resource.Id.swCompress)!;
        _swTtcMips = FindViewById<Switch>(Resource.Id.swTtcMips)!;
        _btnConvert = FindViewById<Button>(Resource.Id.btnConvert)!;
        _scrollLog = FindViewById<ScrollView>(Resource.Id.scrollLog)!;

        // "Alpha" written in a flowing script: use the system Dancing Script face, else any cursive
        TextView edition = FindViewById<TextView>(Resource.Id.tvEdition)!;
        edition.Typeface = LoadScriptTypeface() ?? Typeface.Create("cursive", TypefaceStyle.Bold);
        edition.Text = IsPublicBuild ? "Public" : "Alpha";
        edition.TextSize = IsPublicBuild ? 34 : 42;

        if (IsPublicBuild)
        {
            // This build only ships the image / audio entry points, so keep the empty state honest.
            _tvEmpty.Text = "还没有文件\n添加 .png / .jpg / .bmp / .psd / .mp3 / .flac / .ttx";
        }

        // mode hint line under the pills
        _tvModeHint = new TextView(this) { TextSize = 11.5f };
        _tvModeHint.SetTextColor(Color.ParseColor("#7E8B9C"));
        var hintParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        hintParams.TopMargin = Dp(10);
        FindViewById<LinearLayout>(Resource.Id.cardMode)!.AddView(_tvModeHint, hintParams);

        foreach (Button b in new[] { FindViewById<Button>(Resource.Id.btnAddFiles)!, FindViewById<Button>(Resource.Id.btnAddFolder)!, FindViewById<Button>(Resource.Id.btnClear)!, FindViewById<Button>(Resource.Id.btnPickOutput)!, FindViewById<Button>(Resource.Id.btnOpen)!, FindViewById<Button>(Resource.Id.btnShare)! })
        {
            b.StateListAnimator = null;
        }

        ApplyWindowInsets();
    }

    /// <summary>
    /// The flowing script face for the "Alpha" wordmark: the AOSP Dancing Script font when the ROM
    /// ships it, a bundled font from the assets folder otherwise, and any cursive face as the fallback.
    /// </summary>
    private Typeface? LoadScriptTypeface()
    {
        foreach (string path in new[]
                 {
                     "/system/fonts/DancingScript-Regular.ttf",
                     "/system/fonts/DancingScript-Regular.otf",
                     "/system/fonts/DancingScript-Bold.ttf",
                 })
        {
            try
            {
                if (File.Exists(path)) return Typeface.CreateFromFile(path);
            }
            catch (Exception)
            {
                // try the next candidate
            }
        }

        try
        {
            return Typeface.CreateFromAsset(Assets, "fonts/GreatVibes-Regular.ttf");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void ApplyWindowInsets()
    {
        var root = FindViewById<ScrollView>(Resource.Id.rootScroll)!;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            root.SetOnApplyWindowInsetsListener(new InsetApplier());
        }
        else
        {
            root.SetPadding(0, Dp(24), 0, Dp(12));
        }
    }

    private sealed class InsetApplier : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets? OnApplyWindowInsets(View? v, WindowInsets? insets)
        {
            if (v is null || insets is null) return insets;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
                v.SetPadding(0, bars.Top, 0, bars.Bottom);
            }
            return insets;
        }
    }

    private void BuildPills()
    {
        string[] visibleLabels = VisibleModeIndexes.Select(i => ModeLabels[i]).ToArray();
        _modes = new PillGroup(this, _llModes, visibleLabels, 0, _ => UpdateModeUi());
        _formats = new PillGroup(this, _llFormat, FormatLabels, 0, _ => UpdateQualityRow());
        _sizes = new PillGroup(this, _llSize, SizeLabels, 0, _ => { });
        _qualities = new PillGroup(this, _llQuality, QualityLabels, 1, _ => { });
    }

    private void WireEvents()
    {
        FindViewById<Button>(Resource.Id.btnAddFiles)!.Click += (_, _) => PickFiles();
        FindViewById<Button>(Resource.Id.btnAddFolder)!.Click += (_, _) => PickFolder();
        FindViewById<Button>(Resource.Id.btnClear)!.Click += (_, _) => ClearFiles();
        FindViewById<Button>(Resource.Id.btnPickOutput)!.Click += (_, _) => PickOutputFolder();
        FindViewById<Button>(Resource.Id.btnOpen)!.Click += (_, _) => OpenLast();
        FindViewById<Button>(Resource.Id.btnShare)!.Click += (_, _) => ShareLast();
        _btnConvert.Click += async (_, _) => await OnConvertClicked();

        _swFlip.CheckedChange += (_, _) => SaveState();
        _swCompress.CheckedChange += (_, _) => SaveState();
        _swTtcMips.CheckedChange += (_, _) => SaveState();
    }

    private int Dp(int value) => (int)(value * (Resources?.DisplayMetrics?.Density ?? 1f));

    // ------------------------------------------------------------------ mode / options

    private static ConversionMode SelectedMode(int index) => index switch
    {
        1 => ConversionMode.TtxToPng,
        2 => ConversionMode.PngToTtx,
        3 => ConversionMode.TsbToWav,
        4 => ConversionMode.AudioToTsb,
        5 => ConversionMode.TmToTxt,
        6 => ConversionMode.TtcToPng,
        7 => ConversionMode.PngToTtc,
        8 => ConversionMode.TtxToTtx,
        _ => ConversionMode.Auto,
    };

    private static int ModeFromName(string? name) => name?.ToLowerInvariant() switch
    {
        "ttx2png" => 1,
        "png2ttx" => 2,
        "tsb2wav" => 3,
        "wav2tsb" or "audio2tsb" => 4,
        "tm2txt" => 5,
        "ttc2png" => 6,
        "png2ttc" => 7,
        "ttx2ttx" => 8,
        _ => 0,
    };

    private bool IsTextureMode(int index) => index is 1 or 2 or 6 or 7 or 8;

    private void UpdateModeUi()
    {
        int mode = RealMode;
        _tvModeHint.Text = ModeHints[mode];

        _rowFormat.Visibility = mode is 2 or 7 or 8 ? ViewStates.Visible : ViewStates.Gone;
        _rowSize.Visibility = mode is 2 or 7 or 8 ? ViewStates.Visible : ViewStates.Gone;
        _rowFlip.Visibility = IsTextureMode(mode) ? ViewStates.Visible : ViewStates.Gone;
        _rowCompress.Visibility = mode is 2 or 8 ? ViewStates.Visible : ViewStates.Gone;
        _rowTtcMips.Visibility = mode == 7 ? ViewStates.Visible : ViewStates.Gone;

        bool anyOption = _rowFormat.Visibility == ViewStates.Visible
            || _rowSize.Visibility == ViewStates.Visible
            || _rowFlip.Visibility == ViewStates.Visible
            || _rowCompress.Visibility == ViewStates.Visible
            || _rowTtcMips.Visibility == ViewStates.Visible
            || _rowQuality.Visibility == ViewStates.Visible;
        _cardOptions.Visibility = anyOption ? ViewStates.Visible : ViewStates.Gone;

        if (mode is 2 or 8)
        {
            _llFormat.Visibility = ViewStates.Visible;
            SetFormatVisibility([true, true, true, true, true, true, true, true]);
        }
        else if (mode == 7)
        {
            _llFormat.Visibility = ViewStates.Visible;
            SetFormatVisibility([false, true, true, true, true, false, false, false]);
        }

        _btnConvert.Text = "开始转换";
        UpdateQualityRow();
    }

    /// <summary>
    /// The quality tier only changes ASTC encoding, so the row is shown only for
    /// PNG -> TTX with one of the ASTC output formats.
    /// </summary>
    private void UpdateQualityRow()
    {
        if (_rowQuality is null || _formats is null || _cardOptions is null)
            return;

        bool astc = RealMode is 2 or 8
            && _formats.SelectedIndex >= 0
            && _formats.SelectedIndex < TTXFormats.Length
            && TTXFormats[_formats.SelectedIndex].StartsWith("astc", StringComparison.Ordinal);
        _rowQuality.Visibility = astc ? ViewStates.Visible : ViewStates.Gone;

        bool anyOption = _rowFormat.Visibility == ViewStates.Visible
            || _rowSize.Visibility == ViewStates.Visible
            || _rowFlip.Visibility == ViewStates.Visible
            || _rowCompress.Visibility == ViewStates.Visible
            || _rowTtcMips.Visibility == ViewStates.Visible
            || astc;
        _cardOptions.Visibility = anyOption ? ViewStates.Visible : ViewStates.Gone;
    }

    private void SetFormatVisibility(bool[] enabled)
    {
        for (int i = 0; i < _formats.Count && i < enabled.Length; i++)
            _formats.SetEnabled(i, enabled[i]);

        int selected = _formats.SelectedIndex;
        bool selectedEnabled = selected < 0 || selected >= enabled.Length || enabled[selected];
        if (selectedEnabled)
            return;

        for (int i = 0; i < enabled.Length; i++)
        {
            if (enabled[i])
            {
                _formats.SetSelected(i);
                break;
            }
        }
    }

    private ConvertOptions BuildOptions()
    {
        int mode = RealMode;
        var options = new ConvertOptions
        {
            Flip = _swFlip.Checked,
            FlipExplicit = IsTextureMode(mode),
            CompressTtx = _swCompress.Checked,
            TtcMipmaps = _swTtcMips.Checked,
            MaxParallelism = 1,
            Overwrite = true,
        };

        if (mode is 2 or 8)
        {
            options.TtxFormat = TTXFormats[_formats.SelectedIndex];
            options.AstcQuality = (AstcQuality)_qualities.SelectedIndex;
        }
        else if (mode == 7)
        {
            options.TtcFormat = TtcFormats[_formats.SelectedIndex];
        }

        int sizeIndex = _sizes.SelectedIndex;
        options.TargetTextureSize = SizeValues[sizeIndex];
        options.FitExistingTexture = sizeIndex == 0;
        return options;
    }

    // ------------------------------------------------------------------ files

    private void PickFiles()
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraMimeTypes, new[]
        {
            "image/*", "audio/*", "application/octet-stream", "*/*",
        });
        intent.PutExtra(Intent.ExtraAllowMultiple, true);
        StartActivityForResult(intent, RequestPickFiles);
    }

    private void PickFolder()
    {
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        StartActivityForResult(intent, RequestPickFolder);
    }

    private void PickOutputFolder()
    {
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission
            | ActivityFlags.GrantPersistableUriPermission);
        StartActivityForResult(intent, RequestPickOutput);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != Result.Ok || data?.Data is null) return;
        Uri uri = data.Data;

        switch (requestCode)
        {
            case RequestPickFiles:
            {
                var uris = new List<Uri>();
                if (data.ClipData is not null)
                {
                    for (int i = 0; i < data.ClipData.ItemCount; i++)
                    {
                        if (data.ClipData.GetItemAt(i)?.Uri is { } item) uris.Add(item);
                    }
                }
                else
                {
                    uris.Add(uri);
                }

                foreach (Uri item in uris) AddFromUri(item);
                RenderFiles();
                break;
            }

            case RequestPickFolder:
                AddFromTree(uri);
                RenderFiles();
                break;

            case RequestPickOutput:
                _outputTree = uri;
                try
                {
                    ContentResolver?.TakePersistableUriPermission(uri,
                        ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
                }
                catch (Exception ex)
                {
                    Log("无法保存文件夹权限：" + ex.Message);
                }
                SaveState();
                _tvOutput.Text = OutputLabel();
                Log("输出文件夹：" + OutputLabel());
                break;
        }
    }

    private void AddFromUri(Uri uri)
    {
        string name = DisplayName(uri) ?? $"file_{_picked.Count}";
        string local = CopyToCache(uri, name);
        if (local.Length > 0)
        {
            _picked.Add(new Picked(local, name));
            Log("已添加 " + name);
        }
    }

    private void AddFromTree(Uri tree)
    {
        try
        {
            string rootId = DocumentsContract.GetTreeDocumentId(tree)!;
            int added = WalkTree(tree, rootId, 0);
            Log($"文件夹扫描：找到 {added} 个可转换文件");
        }
        catch (Exception ex)
        {
            Log("文件夹扫描失败：" + ex.Message);
        }
    }

    private int WalkTree(Uri tree, string documentId, int depth)
    {
        if (depth > 8) return 0;
        int added = 0;
        Uri children = DocumentsContract.BuildChildDocumentsUriUsingTree(tree, documentId);
        string[] projection =
        [
            DocumentsContract.Document.ColumnDocumentId,
            DocumentsContract.Document.ColumnDisplayName,
            DocumentsContract.Document.ColumnMimeType,
        ];

        using ICursor? cursor = ContentResolver?.Query(children, projection, null, null, null);
        if (cursor is null) return 0;
        while (cursor.MoveToNext())
        {
            string? id = cursor.GetString(0);
            string? name = cursor.GetString(1);
            string? mime = cursor.GetString(2);
            if (id is null || name is null) continue;

            if (mime == DocumentsContract.Document.MimeTypeDir)
            {
                added += WalkTree(tree, id, depth + 1);
                continue;
            }

            if (!IsSupported(name)) continue;
            Uri fileUri = DocumentsContract.BuildDocumentUriUsingTree(tree, id);
            string local = CopyToCache(fileUri, name);
            if (local.Length == 0) continue;
            _picked.Add(new Picked(local, name));
            added++;
        }
        return added;
    }

    private static bool IsSupported(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".ttx" or ".tsb" or ".ttc" or ".tmc" or ".tsc" or ".wad" or ".tmb" or ".tsl"
            or ".png" or ".wav" or ".jpg" or ".jpeg" or ".mp3" or ".ogg" or ".flac" or ".tme" or ".zip"
            or ".bmp" or ".gif" or ".tga" or ".webp" or ".psd" or ".tif" or ".tiff" or ".hdr"
            or ".m4a" or ".aac" or ".pbm" or ".qoi" => true,
        _ => false,
    };

    private void ClearFiles()
    {
        _picked.Clear();
        _results.Clear();
        _rowResultActions.Visibility = ViewStates.Gone;
        _progress.Visibility = ViewStates.Invisible;
        RenderFiles();
        SetStatus("就绪");
    }

    private void RemoveFile(int index)
    {
        if (index < 0 || index >= _picked.Count) return;
        _picked.RemoveAt(index);
        RenderFiles();
    }

    private void RenderFiles()
    {
        _llFiles.RemoveAllViews();
        _tvFileCount.Text = _picked.Count.ToString();
        _tvEmpty.Visibility = _picked.Count == 0 ? ViewStates.Visible : ViewStates.Gone;

        for (int i = 0; i < _picked.Count; i++)
        {
            Picked item = _picked[i];
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetBackgroundResource(Resource.Drawable.bg_file_row);
            row.SetPadding(Dp(12), Dp(9), Dp(6), Dp(9));
            var rowParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            rowParams.TopMargin = Dp(6);

            var status = new TextView(this) { Text = "\u00b7", TextSize = 14 };
            status.SetTextColor(Color.ParseColor("#7E8B9C"));
            status.Gravity = GravityFlags.Center;
            status.SetPadding(0, 0, Dp(8), 0);
            row.AddView(status);
            item.RowStatus = status;

            var label = new TextView(this) { TextSize = 12 };
            label.SetTextColor(Soft);
            long size = new FileInfo(item.LocalPath).Length;
            label.Text = $"{item.DisplayName}\n{FormatSize(size)}";
            label.SetSingleLine(false);
            row.AddView(label, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            var remove = new TextView(this) { Text = "\u2715", TextSize = 16 };
            remove.SetTextColor(Color.ParseColor("#6B7686"));
            remove.Gravity = GravityFlags.Center;
            remove.SetPadding(Dp(10), Dp(2), Dp(10), Dp(2));
            int index = i;
            remove.Click += (_, _) => RemoveFile(index);
            row.AddView(remove);

            _llFiles.AddView(row, rowParams);
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B",
    };

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
            string target = UniquePath(dir, Sanitize(displayName));
            using Stream? input = ContentResolver?.OpenInputStream(uri);
            if (input is null) return "";
            using var output = File.Create(target);
            input.CopyTo(output);
            return target;
        }
        catch (Exception ex)
        {
            Log("读取失败：" + ex.Message);
            return "";
        }
    }

    private static string UniquePath(string dir, string name)
    {
        string target = Path.Combine(dir, name);
        if (!File.Exists(target)) return target;
        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            target = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(target)) return target;
        }
    }

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "file.bin" : name;
    }

    // ------------------------------------------------------------------ converting

    private async Task OnConvertClicked()
    {
        if (_busy)
        {
            _cts?.Cancel();
            Log("正在取消…");
            return;
        }

        if (_picked.Count == 0)
        {
            Log("请先添加文件");
            return;
        }

        _busy = true;
        _cts = new CancellationTokenSource();
        _btnConvert.Text = "取消";
        _progress.Visibility = ViewStates.Visible;
        _progress.Max = _picked.Count;
        _progress.Progress = 0;
        _results.Clear();
        _rowResultActions.Visibility = ViewStates.Gone;

        var files = _picked.ToList();
        ConversionMode mode = SelectedMode(RealMode);
        ConvertOptions options = BuildOptions();
        CancellationToken token = _cts.Token;

        Log($"\n\u2014 {files.Count} 个文件 · {ModeLabels[RealMode]} \u2014");

        await Task.Run(() =>
        {
            int ok = 0, failed = 0, done = 0;
            foreach (Picked file in files)
            {
                if (token.IsCancellationRequested)
                {
                    Log("已取消");
                    break;
                }

                MarkRow(file, "\u25b6", "#00B8FF");
                ConversionOutcome outcome = Converter.ConvertFile(file.LocalPath, null, mode, options);
                done++;
                int progressValue = done;
                RunOnUiThread(() => _progress.Progress = progressValue);
                SetStatus($"正在转换 {done}/{files.Count}");

                if (!outcome.Ok || string.IsNullOrEmpty(outcome.Output))
                {
                    failed++;
                    MarkRow(file, "\u2715", "#FF7A7A");
                    Log($"失败 {file.DisplayName}：{outcome.Error}");
                    continue;
                }

                // A file that the current mode cannot read comes back as "skipped" with no output.
                if (outcome.Summary.StartsWith("skipped", StringComparison.OrdinalIgnoreCase))
                {
                    MarkRow(file, "\u2013", "#8A96A8");
                    Log($"跳过 {file.DisplayName}：{outcome.Summary}");
                    continue;
                }

                string produced = outcome.Output;
                // Keep the original name: only the extension changes, so a texture exported
                // as "f14_livery.png" comes back as "f14_livery.ttx" (never ".converted" or " (1)").
                string name = Path.GetFileNameWithoutExtension(file.DisplayName) + Path.GetExtension(produced);
                try
                {
                    (string where, Uri? uri) = Publish(produced, name);
                    _results.Add(new ResultItem(name, where, uri));
                    ok++;
                    MarkRow(file, "\u2713", "#5BE49B");
                    Log($"完成 {file.DisplayName} \u2192 {where}");
                }
                catch (Exception ex)
                {
                    failed++;
                    MarkRow(file, "\u2715", "#FF7A7A");
                    Log($"写入失败 {name}：{ex.Message}");
                }

                SetStatus($"{done}/{files.Count} · 成功 {ok}，失败 {failed}");
            }

            SetStatus(failed == 0
                ? $"已转换 {ok} 个文件"
                : $"成功 {ok} 个，失败 {failed} 个");

            // a failure is worth seeing: open the log panel so the reason is on screen
            if (failed > 0 && _scrollLog.Visibility != ViewStates.Visible)
                SetLogOpen(true);
        });

        _busy = false;
        _cts = null;
        _btnConvert.Text = "开始转换";
        _rowResultActions.Visibility = _results.Count > 0 ? ViewStates.Visible : ViewStates.Gone;
    }

    private (string Where, Uri? Uri) Publish(string localPath, string displayName)
    {
        string mime = MimeFor(displayName);

        if (_outputTree is not null)
        {
            try
            {
                string parentId = DocumentsContract.GetTreeDocumentId(_outputTree)!;
                Uri parent = DocumentsContract.BuildDocumentUriUsingTree(_outputTree, parentId);
                DeleteExistingInTree(parentId, displayName);
                Uri? created = DocumentsContract.CreateDocument(ContentResolver!, parent, mime, displayName);
                if (created is not null)
                {
                    using Stream? output = ContentResolver!.OpenOutputStream(created);
                    if (output is not null)
                    {
                        using FileStream input = File.OpenRead(localPath);
                        input.CopyTo(output);
                        return (displayName, created);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("写入所选文件夹失败：" + ex.Message);
            }
        }

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
        {
            var values = new ContentValues();
            values.Put(MediaStore.IMediaColumns.DisplayName, displayName);
            values.Put(MediaStore.IMediaColumns.MimeType, mime);
            values.Put(MediaStore.IMediaColumns.RelativePath, "Download/AeroflyConv");

            Uri? collection = MediaStore.Downloads.GetContentUri(MediaStore.VolumeExternalPrimary);
            DeleteExistingInMediaStore(collection!, displayName);
            Uri? target = ContentResolver?.Insert(collection!, values);
            if (target is not null)
            {
                using Stream? output = ContentResolver?.OpenOutputStream(target);
                if (output is not null)
                {
                    using FileStream input = File.OpenRead(localPath);
                    input.CopyTo(output);
                    return ("Download/AeroflyConv/" + displayName, target);
                }
            }
        }

        string dest = Path.Combine(OutputDirectory(), displayName);
        File.Copy(localPath, dest, overwrite: true);
        return (dest, null);
    }

    /// <summary>Removes a previously published file with the same name inside the chosen folder.</summary>
    private void DeleteExistingInTree(string parentId, string displayName)
    {
        try
        {
            Uri children = DocumentsContract.BuildChildDocumentsUriUsingTree(_outputTree!, parentId);
            string[] projection =
            [
                DocumentsContract.Document.ColumnDocumentId,
                DocumentsContract.Document.ColumnDisplayName,
            ];
            using ICursor? cursor = ContentResolver?.Query(children, projection, null, null, null);
            while (cursor is not null && cursor.MoveToNext())
            {
                if (!string.Equals(cursor.GetString(1), displayName, StringComparison.OrdinalIgnoreCase)) continue;
                string? childId = cursor.GetString(0);
                if (childId is null) continue;
                Uri child = DocumentsContract.BuildDocumentUriUsingTree(_outputTree!, childId);
                DocumentsContract.DeleteDocument(ContentResolver!, child);
                return;
            }
        }
        catch (Exception ex)
        {
            Log("覆盖同名文件失败：" + ex.Message);
        }
    }

    /// <summary>Removes a previously published file with the same name from Download/AeroflyConv.</summary>
    private void DeleteExistingInMediaStore(Uri collection, string displayName)
    {
        try
        {
            string[] projection = ["_id"];            string selection = MediaStore.IMediaColumns.DisplayName + "=? AND "
                               + MediaStore.IMediaColumns.RelativePath + "=?";
            string[] arguments = [displayName, "Download/AeroflyConv/"];
            var victims = new List<Uri>();
            using (ICursor? cursor = ContentResolver?.Query(collection, projection, selection, arguments, null))
            {
                while (cursor is not null && cursor.MoveToNext())
                    victims.Add(ContentUris.WithAppendedId(collection, cursor.GetLong(0)));
            }
            foreach (Uri victim in victims) ContentResolver?.Delete(victim, null, null);
        }
        catch (Exception ex)
        {
            Log("覆盖同名文件失败：" + ex.Message);
        }
    }

    private static string MimeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".wav" => "audio/wav",
        ".txt" => "text/plain",
        ".zip" or ".tme" => "application/zip",
        _ => "application/octet-stream",
    };

    private void OpenLast()
    {
        if (_results.Count == 0) return;
        ResultItem item = _results[^1];
        if (item.Uri is null)
        {
            Log("无法打开 " + item.DisplayName);
            return;
        }
        try
        {
            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(item.Uri, MimeFor(item.DisplayName));
            intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
            StartActivity(intent);
        }
        catch (Exception ex)
        {
            Log("没有应用能打开此文件：" + ex.Message);
        }
    }

    private void ShareLast()
    {
        if (_results.Count == 0) return;
        ResultItem item = _results[^1];
        try
        {
            var intent = new Intent(Intent.ActionSend);
            intent.SetType(MimeFor(item.DisplayName));
            if (item.Uri is not null)
            {
                intent.PutExtra(Intent.ExtraStream, item.Uri);
                intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            }
            else
            {
                intent.PutExtra(Intent.ExtraText, item.Where);
            }
            StartActivity(Intent.CreateChooser(intent, "分享 " + item.DisplayName)!);
        }
        catch (Exception ex)
        {
            Log("分享失败：" + ex.Message);
        }
    }

    private void AcceptIncoming(Intent? intent)
    {
        if (intent is null) return;
        if (intent.Action != Intent.ActionSend && intent.Action != Intent.ActionView) return;

        var uris = new List<Uri>();
        if (intent.ClipData is not null)
        {
            for (int i = 0; i < intent.ClipData.ItemCount; i++)
            {
                if (intent.ClipData.GetItemAt(i)?.Uri is { } uri) uris.Add(uri);
            }
        }
        if (intent.Data is not null) uris.Add(intent.Data);
        if (uris.Count == 0) return;

        foreach (Uri uri in uris) AddFromUri(uri);
        RenderFiles();
    }

    // ------------------------------------------------------------------ output folder

    private string BaseDir() => GetExternalFilesDir(null)?.AbsolutePath ?? CacheDir!.AbsolutePath;

    private string OutputDirectory()
    {
        string dir = Path.Combine(BaseDir(), "out");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string OutputLabel()
    {
        if (_outputTree is null) return "Download/AeroflyConv";
        try
        {
            string id = DocumentsContract.GetTreeDocumentId(_outputTree)!;
            return id.Split(':').Last();
        }
        catch (Exception)
        {
            return "已选文件夹";
        }
    }

    // ------------------------------------------------------------------ state

    private void SaveState()
    {
        var prefs = GetSharedPreferences(Prefs, FileCreationMode.Private)!;
        using var editor = prefs.Edit()!;
        editor.PutInt("mode", RealMode);
        editor.PutInt("format", _formats.SelectedIndex);
        editor.PutInt("size", _sizes.SelectedIndex);
        editor.PutInt("quality", _qualities.SelectedIndex);
        editor.PutBoolean("flip", _swFlip.Checked);
        editor.PutBoolean("compress", _swCompress.Checked);
        editor.PutBoolean("ttcmips", _swTtcMips.Checked);
        editor.PutString("tree", _outputTree?.ToString());
        editor.Apply();
    }

    private void RestoreState()
    {
        var prefs = GetSharedPreferences(Prefs, FileCreationMode.Private)!;
        bool textureDefault = RealMode is 1 or 2 or 6 or 7;
        int savedMode = Math.Clamp(prefs.GetInt("mode", VisibleModeIndexes[0]), 0, ModeLabels.Length - 1);
        _modes.SetSelected(Math.Max(0, Array.IndexOf(VisibleModeIndexes, savedMode)));
        _formats.SetSelected(Math.Clamp(prefs.GetInt("format", 0), 0, FormatLabels.Length - 1));
        _sizes.SetSelected(Math.Clamp(prefs.GetInt("size", 0), 0, SizeLabels.Length - 1));
        _qualities.SetSelected(Math.Clamp(prefs.GetInt("quality", 1), 0, QualityLabels.Length - 1));
        _swFlip.Checked = prefs.GetBoolean("flip", true);
        _swCompress.Checked = prefs.GetBoolean("compress", true);
        _swTtcMips.Checked = prefs.GetBoolean("ttcmips", true);
        _ = textureDefault;

        if (prefs.GetString("tree", null) is { Length: > 0 } treeText)
        {
            try
            {
                _outputTree = Uri.Parse(treeText);
            }
            catch (Exception)
            {
                _outputTree = null;
            }
        }

        _tvOutput.Text = OutputLabel();
        SetLogOpen(prefs.GetBoolean("logopen", false));
        RenderFiles();
    }

    // ------------------------------------------------------------------ ui helpers

    private void Log(string message) => RunOnUiThread(() =>
    {
        Color color;
        string glyph;
        if (message.Contains("失败") || message.Contains("错误") || message.StartsWith("FAIL", StringComparison.Ordinal))
        {
            color = Color.ParseColor("#FF7A7A");
            glyph = "\u2715";
        }
        else if (message.Contains("完成") || message.Contains("成功") || message.StartsWith("OK", StringComparison.Ordinal))
        {
            color = Color.ParseColor("#5BE49B");
            glyph = "\u2713";
        }
        else if (message.Contains("警告") || message.Contains("无法"))
        {
            color = Color.ParseColor("#FFC46B");
            glyph = "!";
        }
        else
        {
            color = Color.ParseColor("#8A96A8");
            glyph = "\u00b7";
        }

        // one span per line so errors and successes stand out from the neutral chatter
        string line = glyph + "  " + message + "\n";
        var span = new SpannableStringBuilder(line);
        span.SetSpan(new ForegroundColorSpan(color), 0, line.Length, SpanTypes.ExclusiveExclusive);

        var buffer = new SpannableStringBuilder(_tvLog.Text ?? string.Empty);
        buffer.Append(span);
        // keep the panel from growing without bound
        if (buffer.Length() > 24000)
            buffer.Delete(0, buffer.Length() - 24000);
        _tvLog.TextFormatted = buffer;

        _logText.AppendLine(glyph + "  " + message);
        _logCount++;
        _tvLogCount.Text = _logCount + " 条";

        if (_scrollLog.ChildCount > 0)
            _scrollLog.Post(() => _scrollLog.FullScroll(FocusSearchDirection.Down));
    });

    private void ClearLog() => RunOnUiThread(() =>
    {
        _tvLog.TextFormatted = new SpannableStringBuilder();
        _logText.Clear();
        _logCount = 0;
        _tvLogCount.Text = "";
    });

    /// <summary>Expands / collapses the log panel; the choice is remembered.</summary>
    private void ToggleLog() => SetLogOpen(_scrollLog.Visibility != ViewStates.Visible);

    private void SetLogOpen(bool open)
    {
        _scrollLog.Visibility = open ? ViewStates.Visible : ViewStates.Gone;
        _tvLogChevron.Text = open ? "\u2304" : "\u203a";

        var prefs = GetSharedPreferences(Prefs, FileCreationMode.Private);
        prefs?.Edit()?.PutBoolean("logopen", open)?.Apply();
    }

    private void CopyLog()
    {
        string text = _logText.ToString();
        if (text.Length == 0)
        {
            Toast.MakeText(this, "日志是空的", ToastLength.Short)?.Show();
            return;
        }

        try
        {
            var clipboard = (ClipboardManager?)GetSystemService(ClipboardService);
            if (clipboard is not null)
                clipboard.PrimaryClip = ClipData.NewPlainText("Aerofly 日志", text);
            Toast.MakeText(this, "日志已复制", ToastLength.Short)?.Show();
        }
        catch (Exception ex)
        {
            Toast.MakeText(this, "复制失败：" + ex.Message, ToastLength.Short)?.Show();
        }
    }

    private void SetStatus(string text) => RunOnUiThread(() => _tvStatus.Text = text);

    private void MarkRow(Picked file, string glyph, string color)
    {
        RunOnUiThread(() =>
        {
            if (file.RowStatus is null) return;
            file.RowStatus.Text = glyph;
            file.RowStatus.SetTextColor(Color.ParseColor(color));
        });
    }

    // ------------------------------------------------------------------ headless test hook

    /// <summary>Fills the file list from the inbox so UI mode can be driven from adb.</summary>
    private async void RunUiDemoAsync()
    {
        string baseDir = GetExternalFilesDir(null)?.AbsolutePath ?? CacheDir!.AbsolutePath;
        string inputDir = Path.Combine(baseDir, "in");
        Directory.CreateDirectory(inputDir);

        string[] files;
        try
        {
            files = Directory.GetFiles(inputDir)
                .Where(f => !Path.GetFileName(f).StartsWith("result", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToArray();
        }
        catch (Exception ex)
        {
            Log("无法列出输入文件：" + ex.Message);
            return;
        }

        _picked.Clear();
        foreach (string file in files) _picked.Add(new Picked(file, Path.GetFileName(file)));
        RenderFiles();
        Log($"界面演示：{_picked.Count} 个文件");

        if (_picked.Count > 0) await OnConvertClicked();
    }

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
            WriteResult("无法列出输入文件：" + ex.Message);
            return;
        }

        Log($"自动运行：{files.Count} 个文件（{inputDir}）");
        if (files.Count == 0)
        {
            WriteResult("没有输入文件");
            return;
        }

        var lines = new List<string>();
        ConversionMode mode = SelectedMode(RealMode);
        ConvertOptions options = BuildOptions();
        string outDir = OutputDirectory();

        await Task.Run(() =>
        {
            foreach (string file in files)
            {
                ConversionOutcome outcome = Converter.ConvertFile(file, null, mode, options);
                if (outcome.Ok && outcome.Summary.StartsWith("skipped", StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add($"SKIP {Path.GetFileName(file)}: {outcome.Summary}");
                    Log($"跳过 {Path.GetFileName(file)}");
                }
                else if (outcome.Ok && !string.IsNullOrEmpty(outcome.Output))
                {
                    string dest = Path.Combine(outDir, Path.GetFileName(outcome.Output));
                    File.Copy(outcome.Output, dest, overwrite: true);
                    lines.Add($"OK {Path.GetFileName(file)} -> {dest} | {outcome.Summary}");
                    Log($"完成 {Path.GetFileName(file)} -> {Path.GetFileName(dest)}");
                }
                else
                {
                    lines.Add($"FAIL {Path.GetFileName(file)}: {outcome.Error}");
                    Log($"失败 {Path.GetFileName(file)}：{outcome.Error}");
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
            Log("结果已写入：" + path);
        }
        catch (Exception ex)
        {
            Log("结果写入失败：" + ex.Message);
        }
    }

    // ------------------------------------------------------------------ pill control

    private sealed class PillGroup
    {
        private readonly Activity _activity;
        private readonly LinearLayout _host;
        private readonly List<TextView> _pills = [];
        private readonly bool[] _enabled;
        private int _selected;

        public PillGroup(Activity activity, LinearLayout host, string[] labels, int selected, Action<int> onChanged)
        {
            _activity = activity;
            _host = host;
            _enabled = new bool[labels.Length];
            Array.Fill(_enabled, true);
            for (int i = 0; i < labels.Length; i++)
            {
                int index = i;
                var pill = new TextView(activity) { Text = labels[i], TextSize = 12.5f };
                pill.Gravity = GravityFlags.Center;
                pill.SetPadding(Dp(activity, 14), Dp(activity, 8), Dp(activity, 14), Dp(activity, 8));
                pill.Clickable = true;
                pill.Focusable = true;
                var lp = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
                lp.RightMargin = Dp(activity, 6);
                pill.Click += (_, _) =>
                {
                    if (!_enabled[index]) return;
                    SetSelected(index);
                    onChanged(index);
                };
                host.AddView(pill, lp);
                _pills.Add(pill);
            }

            SetSelected(selected);
        }

        public int SelectedIndex => _selected;
        public int Count => _pills.Count;

        public void SetSelected(int index)
        {
            if (index < 0 || index >= _pills.Count) return;
            _selected = index;
            for (int i = 0; i < _pills.Count; i++)
            {
                bool on = i == index;
                _pills[i].SetBackgroundResource(on ? Resource.Drawable.bg_pill_on : Resource.Drawable.bg_pill_off);
                _pills[i].SetTextColor(on ? Color.ParseColor("#00B8FF") : Color.ParseColor("#9AA4B2"));
            }
            ScrollToSelected();
        }

        // Keep the active pill inside the horizontal scroller (the row holds up to 8 modes).
        private void ScrollToSelected()
        {
            if (_selected < 0 || _selected >= _pills.Count) return;
            var pill = _pills[_selected];
            if (_host.Parent is not HorizontalScrollView scroller)
            {
                _host.Post(() =>
                {
                    if (_host.Parent is HorizontalScrollView s) s.SmoothScrollTo(0, 0);
                });
                return;
            }

            pill.Post(() =>
            {
                int margin = Dp(_activity, 12);
                int left = pill.Left - margin;
                int right = pill.Right + margin;
                int viewport = scroller.Width;
                int current = scroller.ScrollX;

                // scroll only as far as needed: keep the pill fully visible, never jump to the end
                int target = current;
                if (left < current)
                {
                    target = left;
                }
                else if (right > current + viewport)
                {
                    target = right - viewport;
                }

                scroller.SmoothScrollTo(Math.Max(0, target), 0);
            });
        }

        public void SetEnabled(int index, bool enabled)
        {
            if (index < 0 || index >= _pills.Count) return;
            _enabled[index] = enabled;
            _pills[index].Alpha = enabled ? 1f : 0.35f;
            _pills[index].Clickable = enabled;
        }

        private static int Dp(Activity activity, int value) =>
            (int)(value * (activity.Resources?.DisplayMetrics?.Density ?? 1f));
    }
}
