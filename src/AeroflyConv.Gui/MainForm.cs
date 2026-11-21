using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AeroflyConv.Core;
using AeroflyConv.Core.Conversion;
using AeroflyConv.Core.Media;
using AeroflyConv.Gui.Ui;

namespace AeroflyConv.Gui;

internal sealed class MainForm : Form
{
    private static readonly int[] ThreadChoices = [0, 1, 2, 4, 8, 16, 32];

    private readonly NavList _nav = new();
    private readonly GlassSelect _formatSelect = new();
    private readonly GlassSelect _threadsSelect = new();
    private readonly CheckBox _flipCheck = new();
    private readonly CheckBox _yUpCheck = new();
    private readonly CheckBox _spCheck = new();
    private readonly GlassSelect _qualitySelect = new();
    private Label? _qualityLabel;
    private readonly CheckBox _mipmapsCheck = new();
    private readonly CheckBox _compressCheck = new();
    private readonly GlassListBox _fileList = new();
    private readonly GlassButton _browseButton = new() { Text = "Add files", Width = 112 };
    private readonly GlassButton _clearButton = new() { Text = "Clear", Width = 86 };
    private readonly GlassButton _convertButton = new() { Text = "Convert", Primary = true, Width = 130 };
    private readonly GlassButton _openFolderButton = new() { Text = "Open output folder", Width = 168 };
    private readonly GlassButton _outDirButton = new() { Text = "Same folder as source", Width = 290 };
    private readonly GlassSelect _sizeSelect = new();
    private readonly GlassButton _outDirResetButton = new() { Text = "\u2715", Width = 40 };
    private string? _outputDirectory;
    private readonly GlowProgressBar _progress = new();
    private readonly RichTextBox _log = new();
    private readonly Label _hintLabel = new();
    private readonly Label _statusLabel = new();
    private readonly Label _formatLabel = new();
    private readonly Label _pageTitle = new();
    private readonly Label _pageHint = new();
    private GlassCard? _filesCard;
    private string? _lastOutputFolder;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _flipRelevant;
    private bool _mipmapsRelevant;
    private bool _yUpRelevant;
    private bool _spRelevant;
    private bool _compressRelevant;

    private static readonly (string Category, NavEntry[] Items)[] Modes =
    [
        ("Textures", [
            new NavEntry { Text = "TTX \u2192 PNG", Mode = ConversionMode.TtxToPng },
            new NavEntry { Text = "Image \u2192 TTX", Mode = ConversionMode.PngToTtx },
            new NavEntry { Text = "TTX \u2192 TTX", Mode = ConversionMode.TtxToTtx },
        ]),
        ("3D", [
            new NavEntry { Text = "TMB \u2192 OBJ", Mode = ConversionMode.TmbToObj },
        ]),
        ("Audio", [
            new NavEntry { Text = "TSB \u2192 WAV", Mode = ConversionMode.TsbToWav },
            new NavEntry { Text = "Audio \u2192 TSB", Mode = ConversionMode.AudioToTsb },
            new NavEntry { Text = "MP3 / OGG \u2192 WAV", Mode = ConversionMode.AudioToWav },
        ]),
        ("Scenery", [
            new NavEntry { Text = "TTC \u2192 PNG", Mode = ConversionMode.TtcToPng },
            new NavEntry { Text = "Image \u2192 TTC", Mode = ConversionMode.PngToTtc },
            new NavEntry { Text = "Compressed \u2192 TXT", Mode = ConversionMode.TmToTxt },
        ]),
        ("Packages", [
            new NavEntry { Text = "ZIP \u2192 TME", Mode = ConversionMode.ZipToTme },
            new NavEntry { Text = "TME \u2192 ZIP", Mode = ConversionMode.TmeToZip },
        ]),
    ];

    public MainForm()
    {
        Text = AeroflyConv.Core.ProductInfo.Banner;
        MinimumSize = new Size(1080, 720);
        Size = new Size(1260, 820);
        BackColor = Theme.BaseTop;
        ForeColor = Theme.Text;
        Font = Theme.FontBody;
        AllowDrop = true;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch (IOException) { }
        catch (ArgumentException) { }

        BuildLayout();
        BuildNav();
        UpdateModeUi();
        Shown += (_, _) => WarnAboutInjectedOsd();
    }

    /// <summary>
    /// Nahimic / Alienware Sound Center injects NahimicOSD.dll into every process and has a known
    /// access-violation bug (0xC0000005) that kills apps after a while. Tell the user when it is present.
    /// </summary>
    private void WarnAboutInjectedOsd()
    {
        string? module = null;
        try
        {
            foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
            {
                if (m.ModuleName.Contains("NahimicOSD", StringComparison.OrdinalIgnoreCase))
                {
                    module = m.FileName;
                    break;
                }
            }
        }
        catch (Exception)
        {
            // enumeration can fail on protected modules; not fatal
        }

        if (module is null) return;

        AppendLog($"Warning: {module} is injected into this process.", error: true);
        AppendLog("This Nahimic / Alienware Sound Center OSD module has a known crash bug (0xC0000005) that can take " +
                  "the app down after a while. Run \"stop-NahimicOSD.cmd\" from the program folder and try again.", error: true);
        _statusLabel.Text = "Nahimic OSD injected (known crash source)";

        bool batchMode = Environment.GetCommandLineArgs().Contains("--screenshot");
        if (!batchMode)
        {
            MessageBox.Show(this,
                "NahimicOSD.dll (Alienware Sound Center / Nahimic audio OSD) is injected into this process.\n\n" +
                "That module has a known defect that can crash the app after a while\n" +
                "(this is the 0xC0000005 entry you may see in the Windows event log).\n\n" +
                "Workaround:\n" +
                "1. Close this app;\n" +
                "2. Right-click \"stop-NahimicOSD.cmd\" in the program folder and run it as administrator;\n" +
                "3. Start Horizon Converter again.\n\n" +
                "(The script only stops the Nahimic service temporarily; \"restore-NahimicOSD.cmd\" undoes it.)",
                "Horizon Converter - compatibility warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.EnableModernWindow(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Theme.EnableModernWindow(Handle);
    }

    protected override void OnPaintBackground(PaintEventArgs e) => Theme.PaintBackdrop(e.Graphics, ClientRectangle);

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 268));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(BuildSidebar(), 0, 0);
        root.Controls.Add(BuildContent(), 1, 0);
    }

    private Control BuildSidebar()
    {
        var sidebar = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        sidebar.Paint += (_, e) =>
        {
            Graphics g = e.Graphics;
            using (var brush = new SolidBrush(Color.FromArgb(226, Theme.Sidebar)))
                g.FillRectangle(brush, sidebar.ClientRectangle);
            using var line = new Pen(Color.FromArgb(22, 255, 255, 255), 1f);
            g.DrawLine(line, sidebar.Width - 1, 0, sidebar.Width - 1, sidebar.Height);
        };

        var navHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(255, Theme.Sidebar),
            Padding = new Padding(8, 0, 8, 0),
            AutoScroll = true,
        };
        _nav.Dock = DockStyle.Top;
        _nav.SelectionChanged += (_, _) => UpdateModeUi();
        navHost.Controls.Add(_nav);
        sidebar.Controls.Add(navHost);
        _navHost = navHost;

        // Brand + tagline live in one bottom bar so the sidebar layout never depends on how many
        // nav categories the mode list happens to contain.
        var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 132, BackColor = Color.Transparent };
        var footer = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            Text = "C# / .NET 10  \u00b7  LZHAM",
            Font = Theme.FontSmall,
            ForeColor = Theme.TextDim,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(24, 0, 12, 8),
            BackColor = Color.Transparent,
        };
        bottomBar.Controls.Add(footer);
        bottomBar.Controls.Add(new BrandHeader { Dock = DockStyle.Top, Height = 90 });
        sidebar.Controls.Add(bottomBar);

        return sidebar;
    }

    private Panel? _navHost;

    private Control BuildContent()
    {
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            BackColor = Color.Transparent,
            Padding = new Padding(26, 18, 26, 18),
        };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 58));

        // ---- header
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 12), Height = 58 };
        _pageTitle.Text = "TTX \u2192 PNG";
        _pageTitle.Font = new Font("Segoe UI Semibold", 16f);
        _pageTitle.ForeColor = Theme.Text;
        _pageTitle.AutoSize = true;
        _pageTitle.Location = new Point(0, 0);
        _pageTitle.BackColor = Color.Transparent;
        _pageHint.Font = Theme.FontSmall;
        _pageHint.ForeColor = Theme.TextMuted;
        _pageHint.AutoSize = true;
        _pageHint.Location = new Point(2, 31);
        _pageHint.BackColor = Color.Transparent;
        header.Controls.Add(_pageTitle);
        header.Controls.Add(_pageHint);
        content.Controls.Add(header, 0, 0);

        // ---- options
        var options = new GlassCard { Dock = DockStyle.Fill, Title = "Options", Height = 172, Margin = new Padding(0, 0, 0, 12) };
        var optionsHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.CardTop };

        var row1 = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            AutoSize = false,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.CardTop,
            Padding = new Padding(0, 4, 0, 0),
        };
        var threadsLabel = new Label
        {
            Text = "Threads",
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Margin = new Padding(0, 9, 8, 0),
            BackColor = Color.Transparent,
        };
        _threadsSelect.Width = 130;
        _threadsSelect.Height = 30;
        _threadsSelect.Margin = new Padding(0, 2, 24, 0);
        _threadsSelect.SetItems(ThreadChoices.Select(n => n == 0 ? $"Auto ({Environment.ProcessorCount})" : $"{n} threads"));

        _formatLabel.Text = "Format";
        _formatLabel.ForeColor = Theme.TextMuted;
        _formatLabel.AutoSize = true;
        _formatLabel.Margin = new Padding(0, 9, 8, 0);
        _formatLabel.BackColor = Color.Transparent;

        _formatSelect.Width = 350;
        _formatSelect.Height = 30;
        _formatSelect.Margin = new Padding(0, 2, 0, 0);

        row1.Controls.Add(threadsLabel);
        row1.Controls.Add(_threadsSelect);
        row1.Controls.Add(_formatLabel);
        row1.Controls.Add(_formatSelect);

        var row2 = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 34,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.CardTop,
            Padding = new Padding(0, 10, 0, 0),
        };
        ConfigureCheck(_flipCheck, "Flip vertically (Aerofly stores textures bottom-up)");
        ConfigureCheck(_mipmapsCheck, "TTC mipmaps");
        ConfigureCheck(_compressCheck, "Compress TTX (LZHAM)");
        ConfigureCheck(_yUpCheck, "Y-up (Blender / glTF)");
        ConfigureCheck(_spCheck, "Substance-friendly (flat textures)");
        _yUpCheck.Margin = new Padding(0, 0, 26, 0);
        _yUpCheck.Visible = false;
        _spCheck.Margin = new Padding(0, 0, 8, 0);
        _spCheck.Visible = false;
        _mipmapsCheck.Checked = true;
        _compressCheck.Checked = true;
        _flipCheck.Margin = new Padding(0, 0, 26, 0);
        _mipmapsCheck.Margin = new Padding(0, 0, 26, 0);
        row2.Controls.Add(_flipCheck);
        row2.Controls.Add(_mipmapsCheck);
        row2.Controls.Add(_compressCheck);
        row2.Controls.Add(_yUpCheck);
        row2.Controls.Add(_spCheck);

        var row3 = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.CardTop,
            Padding = new Padding(0, 8, 0, 0),
        };
        var outLabel = new Label
        {
            Text = "Output",
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Margin = new Padding(0, 12, 8, 0),
            BackColor = Color.Transparent,
        };
        _outDirButton.Height = 30;
        _outDirButton.Margin = new Padding(0, 3, 6, 0);
        _outDirButton.Font = Theme.FontSmall;
        _outDirResetButton.Height = 30;
        _outDirResetButton.Margin = new Padding(0, 3, 0, 0);
        _outDirResetButton.Font = Theme.FontSmall;
        _outDirButton.Click += (_, _) => ChooseOutputDirectory();
        _outDirResetButton.Click += (_, _) =>
        {
            _outputDirectory = null;
            _outDirButton.Text = "Same folder as source";
        };
        var sizeLabel = new Label
        {
            Text = "Size",
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Margin = new Padding(10, 12, 8, 0),
            BackColor = Color.Transparent,
        };
        _sizeSelect.Width = 190;
        _sizeSelect.Height = 30;
        _sizeSelect.Margin = new Padding(0, 3, 0, 0);
        _sizeSelect.SetItems([
            "Match original",
            "4096 px",
            "2048 px",
            "1024 px",
            "Source (max PO2)",
        ]);
        _qualityLabel = new Label
        {
            Text = "ASTC",
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Margin = new Padding(10, 12, 8, 0),
            BackColor = Color.Transparent,
            Visible = false,
        };
        _qualitySelect.Width = 132;
        _qualitySelect.Height = 30;
        _qualitySelect.Margin = new Padding(0, 3, 0, 0);
        _qualitySelect.SetItems(["Fast", "Standard", "High"]);
        _qualitySelect.SelectedIndex = 1;
        _qualitySelect.Visible = false;
        row3.Controls.Add(_qualityLabel);
        row3.Controls.Add(_qualitySelect);

        row3.Controls.Add(outLabel);
        row3.Controls.Add(_outDirButton);
        row3.Controls.Add(_outDirResetButton);
        row3.Controls.Add(sizeLabel);
        row3.Controls.Add(_sizeSelect);

        optionsHost.Controls.Add(row3);
        optionsHost.Controls.Add(row2);
        optionsHost.Controls.Add(row1);
        options.Controls.Add(optionsHost);
        content.Controls.Add(options, 0, 1);
        // ---- files card
        var filesCard = new GlassCard { Dock = DockStyle.Fill, Title = "Files", Margin = new Padding(0, 0, 0, 12) };
        _filesCard = filesCard;
        var listHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(19, 23, 29),
            Padding = new Padding(6, 4, 6, 6),
        };
        _fileList.Dock = DockStyle.Fill;
        _fileList.AllowDrop = true;
        _fileList.DragEnter += OnDragEnter;
        _fileList.DragDrop += OnDragDrop;
        _fileList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) RemoveSelectedFiles();
        };
        _fileList.SelectedIndexChanged += (_, _) => UpdateFilesSubtitle();

        _hintLabel.Dock = DockStyle.Fill;
        _hintLabel.TextAlign = ContentAlignment.MiddleCenter;
        _hintLabel.ForeColor = Theme.TextDim;
        _hintLabel.Font = new Font("Segoe UI", 9.5f);
        _hintLabel.BackColor = Color.FromArgb(19, 23, 29);
        _hintLabel.Text = "Drag files or folders here";
        listHost.Controls.Add(_fileList);
        listHost.Controls.Add(_hintLabel);
        _hintLabel.BringToFront();
        filesCard.Controls.Add(listHost);
        content.Controls.Add(filesCard, 0, 2);

        // ---- buttons
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(0, 2, 0, 0),
        };
        _browseButton.Margin = new Padding(0, 0, 8, 0);
        _clearButton.Margin = new Padding(0, 0, 8, 0);
        _convertButton.Margin = new Padding(0, 0, 8, 0);
        _openFolderButton.Margin = new Padding(0);
        _browseButton.Click += (_, _) => BrowseFiles();
        _clearButton.Click += (_, _) => { _fileList.Items.Clear(); UpdateHint(); UpdateFilesSubtitle(); };
        _convertButton.Click += async (_, _) => await ConvertAsync();
        _openFolderButton.Click += (_, _) => OpenOutputFolder();
        _openFolderButton.Enabled = false;
        buttons.Controls.Add(_browseButton);
        buttons.Controls.Add(_clearButton);
        buttons.Controls.Add(_convertButton);
        buttons.Controls.Add(_openFolderButton);
        content.Controls.Add(buttons, 0, 3);

        // ---- progress
        var progressRow = new Panel { Dock = DockStyle.Fill, Height = 32, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 8) };
        _progress.Location = new Point(2, 10);
        _progress.Width = 520;
        _progress.Height = 10;
        _progress.Maximum = 1;
        _statusLabel.Text = "Ready";
        _statusLabel.ForeColor = Theme.TextMuted;
        _statusLabel.Font = Theme.FontSmall;
        _statusLabel.AutoSize = true;
        _statusLabel.Location = new Point(540, 6);
        _statusLabel.BackColor = Color.Transparent;
        progressRow.Controls.Add(_progress);
        progressRow.Controls.Add(_statusLabel);
        content.Controls.Add(progressRow, 0, 4);

        // ---- log card
        var logCard = new GlassCard { Dock = DockStyle.Fill, Title = "Log", Margin = new Padding(0) };
        var logHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.LogBack,
            Padding = new Padding(8, 4, 8, 8),
        };
        _log.Dock = DockStyle.Fill;
        _log.BackColor = Theme.LogBack;
        _log.ForeColor = Color.FromArgb(176, 214, 190);
        _log.BorderStyle = BorderStyle.None;
        _log.ReadOnly = true;
        _log.Font = Theme.FontMono;
        _log.WordWrap = false;
        _log.DetectUrls = false;
        _log.ScrollBars = RichTextBoxScrollBars.Vertical;
        logHost.Controls.Add(_log);
        logCard.Controls.Add(logHost);
        content.Controls.Add(logCard, 0, 5);

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        return content;
    }

    private void BuildNav()
    {
        var entries = new List<NavEntry>();
        foreach (var (category, items) in Modes)
        {
            entries.Add(new NavEntry { Text = category, IsHeader = true });
            entries.AddRange(items);
        }
        _nav.SetEntries(entries);
        _nav.Height = _nav.MeasureHeight();
    }

    private static void ConfigureCheck(CheckBox box, string text)
    {
        box.Text = text;
        box.ForeColor = Theme.Text;
        box.AutoSize = true;
        box.FlatStyle = FlatStyle.Standard;
        box.BackColor = Theme.CardTop;
        box.ForeColor = Color.FromArgb(215, 223, 235);
        box.Font = Theme.FontBody;
        box.Cursor = Cursors.Hand;
    }

    // ------------------------------------------------------------------ behaviour

    /// <summary>Selects a sidebar entry by flat index; used by the --screenshot / --nav test hook.</summary>
    internal void SelectNav(int index) => _nav.SelectIndex(index);

    private ConversionMode CurrentMode => _nav.Selected?.Mode ?? ConversionMode.TtxToPng;

    private int SelectedThreads
    {
        get
        {
            int index = Math.Clamp(_threadsSelect.SelectedIndex, 0, ThreadChoices.Length - 1);
            int value = ThreadChoices[index];
            return value == 0 ? Environment.ProcessorCount : value;
        }
    }

    private void UpdateModeUi()
    {
        ConversionMode mode = CurrentMode;
        _pageTitle.Text = _nav.Selected?.Text ?? "Convert";

        (string hint, string pageHint) = mode switch
        {
            ConversionMode.TtxToPng => ("Drag && drop .ttx textures here\nor use \"Add files\"",
                "DXT1/3/5, BC5 normal maps, ETC2, raw RGBA and R8 textures to PNG"),
            ConversionMode.PngToTtx => ("Drag && drop PNG / JPG / BMP / PSD / TIFF images here",
                "Any ImageSharp format: re-encodes into a TTX with a full mip chain (DXT / ETC2 / ASTC / RGBA)"),
            ConversionMode.TtxToTtx => ("Drag && drop .ttx textures here",
                "Decodes and re-encodes a texture on this machine: change format, size or compression"),
            ConversionMode.AudioToTsb => ("Drag && drop WAV / MP3 / OGG here",
                "Wraps any audio into an Aerofly TSB sound container"),
            ConversionMode.TmbToObj => ("Drag && drop .tmb models here",
                "3D model to OBJ + MTL with every part named and its textures matched automatically"),
            ConversionMode.TsbToWav => ("Drag && drop .tsb sounds here", "Mono / stereo PCM containers to WAV"),
            ConversionMode.WavToTsb => ("Drag && drop .wav files here", "Uncompressed PCM WAV to TSB"),
            ConversionMode.AudioToWav => ("Drag && drop .mp3 / .ogg files here", "Decodes to 16-bit PCM WAV"),
            ConversionMode.TmToTxt => ("Drag && drop .toc / .tsc / .wad / .tmb / .tsl files here",
                "Inflates the TM container and writes the generic type / name / value text tree"),
            ConversionMode.TtcToPng => ("Drag && drop .ttc scenery tiles here", "DXT1 / DXT5 / ETC2 / R8 tiles to PNG"),
            ConversionMode.PngToTtc => ("Drag && drop PNG / JPG scenery tiles here", "Writes a zlib-payload TTC with mipmaps"),
            ConversionMode.ZipToTme => ("Drag && drop .zip packages here", "Remaps the folder tree for the mobile loader"),
            ConversionMode.TmeToZip => ("Drag && drop .tme packages here", "Flattens the DLC tree back into a ZIP"),
            _ => ("Drag files here", ""),
        };
        _hintLabel.Text = hint;
        _pageHint.Text = pageHint;

        if (_filesCard is not null) _filesCard.Visible = true;
        _browseButton.Visible = true;
        _clearButton.Visible = true;
        _openFolderButton.Visible = true;
        _convertButton.Text = "Convert";
        _convertButton.Width = 130;

        _flipRelevant = mode is ConversionMode.TtxToPng or ConversionMode.PngToTtx or
            ConversionMode.TtcToPng or ConversionMode.PngToTtc or ConversionMode.TtxToTtx or
            ConversionMode.TmbToObj;
        _mipmapsRelevant = mode == ConversionMode.PngToTtc;
        _compressRelevant = mode is ConversionMode.PngToTtx or ConversionMode.TtxToTtx;
        _yUpRelevant = mode == ConversionMode.TmbToObj;
        _yUpCheck.Visible = _yUpRelevant;
        _yUpCheck.Enabled = _yUpRelevant;
        _spRelevant = mode == ConversionMode.TmbToObj;
        _spCheck.Visible = _spRelevant;
        _spCheck.Enabled = _spRelevant;
        _flipCheck.Enabled = _flipRelevant;
        _flipCheck.Checked = _flipRelevant && mode != ConversionMode.TmbToObj;
        _flipCheck.Text = mode == ConversionMode.TmbToObj
            ? "Flip UV vertically (Aerofly stores V bottom-up)"
            : "Flip vertically (Aerofly stores textures bottom-up)";
        _mipmapsCheck.Enabled = _mipmapsRelevant;
        _mipmapsCheck.Visible = _mipmapsRelevant;
        _compressCheck.Enabled = _compressRelevant;
        _compressCheck.Visible = _compressRelevant;

        if (mode is ConversionMode.PngToTtx or ConversionMode.TtxToTtx)
        {
            _formatSelect.SetItems([
                "FS4 PC \u2014 DXT1/5 (auto)",
                "FS4 PC \u2014 DXT1 (opaque)",
                "FS4 PC \u2014 DXT5 (alpha)",
                "Lossless RGBA",
                "Grayscale R8",
                "FSG Mobile \u2014 ETC2 RGB",
                "FSG Mobile \u2014 ETC2 RGBA",
                "FSG Mobile \u2014 ASTC 6x6 RGB",
                "FSG Mobile \u2014 ASTC 5x5 RGBA",
            ]);
            _formatSelect.Enabled = true;
            _formatLabel.Text = "Format";
            _formatLabel.Visible = true;
            _formatSelect.Visible = true;
        }
        else if (mode == ConversionMode.PngToTtc)
        {
            _formatSelect.SetItems([
                "FS4 PC \u2014 DXT1",
                "FS4 PC \u2014 DXT5",
                "FSG Mobile \u2014 ETC2",
                "R8 mask",
            ]);
            _formatSelect.Enabled = true;
            _formatLabel.Text = "Format";
            _formatLabel.Visible = true;
            _formatSelect.Visible = true;
        }
        else
        {
            _formatSelect.Enabled = false;
            _formatLabel.Visible = false;
            _formatSelect.Visible = false;
        }

        bool astcSelected = (mode is ConversionMode.PngToTtx or ConversionMode.TtxToTtx) &&
            _formatSelect.SelectedIndex >= 7;
        if (_qualityLabel is not null) _qualityLabel.Visible = astcSelected;
        _qualitySelect.Visible = astcSelected;

        UpdateHint();
        UpdateFilesSubtitle();
    }

    private void UpdateHint() => _hintLabel.Visible = _fileList.Items.Count == 0;

    private void UpdateFilesSubtitle()
    {
        if (_filesCard is null) return;
        int count = _fileList.Items.Count;
        _filesCard.Subtitle = count == 0 ? "nothing queued yet" : $"{count} file(s) queued";
        _filesCard.Invalidate();
    }

    private string SelectedTtxFormat => _formatSelect.SelectedIndex switch
    {
        0 => "dxt_auto",
        1 => "dxt1",
        2 => "dxt5",
        3 => "type_rgba",
        4 => "type_r",
        5 => "type_rgb_etc2",
        6 => "type_rgba_etc2",
        7 => "type_rgb_astc_6x6",
        8 => "type_rgba_astc_5x5",
        _ => "dxt_auto",
    };

    private string SelectedTtcFormat => _formatSelect.SelectedIndex switch
    {
        1 => "dxt5",
        2 => "etc2",
        3 => "r8",
        _ => "dxt1",
    };

    private int? SelectedTextureSize => _sizeSelect.SelectedIndex switch
    {
        1 => 4096,
        2 => 2048,
        3 => 1024,
        _ => null,
    };

    private void ChooseOutputDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose an output folder (the source tree is mirrored)",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _outputDirectory = dialog.SelectedPath;
        _outDirButton.Text = dialog.SelectedPath;
    }

    private void BrowseFiles()
    {
        ConversionMode mode = CurrentMode;
        string[] exts = Converter.InputExtensions(mode);
        using var dialog = new OpenFileDialog
        {
            Multiselect = true,
            CheckFileExists = true,
            Title = "Select files to convert",
            Filter = exts.Length > 0
                ? $"Supported files|{string.Join(";", exts.Select(e => "*" + e))}|All files|*.*"
                : "All files|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            AddFiles(dialog.FileNames);
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        var existing = new HashSet<string>(_fileList.Items.Cast<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (string file in Converter.Expand([path], CurrentMode))
                    if (existing.Add(file)) _fileList.Items.Add(file);
            }
            else if (File.Exists(path) && existing.Add(path))
            {
                _fileList.Items.Add(path);
            }
        }
        UpdateHint();
        UpdateFilesSubtitle();
        _statusLabel.Text = $"{_fileList.Items.Count} file(s) queued";
    }

    private void RemoveSelectedFiles()
    {
        foreach (string item in _fileList.SelectedItems.Cast<string>().ToList())
            _fileList.Items.Remove(item);
        UpdateHint();
        UpdateFilesSubtitle();
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = !_busy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (_busy) return;
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
            AddFiles(files);
    }

    private async Task ConvertAsync()
    {
        if (_busy) return;
        if (_fileList.Items.Count == 0)
        {
            using var dialog = new FolderBrowserDialog { Description = "Select a folder to convert" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            AddFiles([dialog.SelectedPath]);
            if (_fileList.Items.Count == 0) return;
        }

        ConversionMode mode = CurrentMode;
        var options = new ConvertOptions
        {
            Flip = _flipCheck.Checked,
            FlipExplicit = true,
            TtxFormat = SelectedTtxFormat,
            TtcFormat = SelectedTtcFormat,
            TtcMipmaps = _mipmapsCheck.Checked,
            CompressTtx = _compressCheck.Checked,
            MaxParallelism = SelectedThreads,
            OutputDirectory = _outputDirectory,
            TargetTextureSize = SelectedTextureSize,
            FitExistingTexture = _sizeSelect.SelectedIndex <= 0,
            ModelTextures = true,
            ModelFlipV = _flipCheck.Checked,
            ModelYUp = _yUpCheck.Checked,
            ModelSubstance = _spCheck.Checked,
            AstcQuality = _qualitySelect.SelectedIndex switch
            {
                0 => AeroflyConv.Core.Codecs.AstcQuality.Fast,
                2 => AeroflyConv.Core.Codecs.AstcQuality.High,
                _ => AeroflyConv.Core.Codecs.AstcQuality.Standard,
            },
        };

        var files = _fileList.Items.Cast<string>().ToList();
        _busy = true;
        SetBusy(true);
        _progress.Maximum = files.Count;
        _progress.Value = 0;
        _cts = new CancellationTokenSource();
        _log.Clear();
        AppendLog($"Mode: {_pageTitle.Text}    files: {files.Count}    threads: {options.MaxParallelism}");
        var sw = Stopwatch.StartNew();
        int failures = 0;
        int done = 0;

        var progress = new Progress<ConversionOutcome>(outcome =>
        {
            done++;
            _progress.Value = Math.Min(done, _progress.Maximum);
            string name = Path.GetFileName(outcome.Input);
            if (outcome.Ok)
            {
                AppendLog($"[{done}/{files.Count}] {name} \u2192 {Path.GetFileName(outcome.Output)}  " +
                          $"{outcome.Summary}  ({outcome.Milliseconds:0} ms)");
                if (outcome.Output is not null)
                {
                    _lastOutputFolder = Path.GetDirectoryName(outcome.Output) ?? _lastOutputFolder;
                    _openFolderButton.Enabled = true;
                }
            }
            else
            {
                failures++;
                AppendLog($"[{done}/{files.Count}] {name}  ERROR: {outcome.Error}", error: true);
            }
            _statusLabel.Text = $"{done}/{files.Count}  \u00b7  {failures} failed";
        });

        try
        {
            await Task.Run(() => Converter.ConvertAll(files, mode, options,
                outcome => ((IProgress<ConversionOutcome>)progress).Report(outcome),
                log: message =>
                {
                    if (message.Contains("WARNING", StringComparison.Ordinal) ||
                        message.Contains("resizing", StringComparison.Ordinal) ||
                        message.Contains("existing texture format", StringComparison.Ordinal) ||
                        message.StartsWith("note:", StringComparison.Ordinal))
                    {
                        AppendLog("    " + message, error: message.Contains("WARNING", StringComparison.Ordinal));
                    }
                },
                _cts.Token));
        }
        catch (OperationCanceledException)
        {
            AppendLog("Cancelled.");
        }
        finally
        {
            sw.Stop();
            _busy = false;
            SetBusy(false);
            double seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.0001);
            string summary = $"{files.Count - failures}/{files.Count} done in {seconds:0.00} s";
            AppendLog(summary + (failures > 0 ? $"  ({failures} failed)" : ""), error: failures > 0);
            _statusLabel.Text = summary;
        }
    }

    private void SetBusy(bool busy)
    {
        _convertButton.Enabled = !busy;
        _browseButton.Enabled = !busy;
        _clearButton.Enabled = !busy;
        _nav.Enabled = !busy;
        _formatSelect.Enabled = !busy && _formatSelect.Visible;
        _threadsSelect.Enabled = !busy;
        _flipCheck.Enabled = !busy && _flipRelevant;
        _mipmapsCheck.Enabled = !busy && _mipmapsRelevant;
        _compressCheck.Enabled = !busy && _compressRelevant;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void AppendLog(string message, bool error = false)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(message, error));
            return;
        }
        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor = error ? Theme.Danger : Color.FromArgb(176, 214, 190);
        _log.AppendText(message + Environment.NewLine);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void OpenOutputFolder()
    {
        if (_lastOutputFolder is null || !Directory.Exists(_lastOutputFolder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastOutputFolder}\"") { UseShellExecute = true });
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts?.Cancel();
        base.OnFormClosed(e);
    }
}













