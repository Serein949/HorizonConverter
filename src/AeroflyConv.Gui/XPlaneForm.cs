using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using AeroflyConv.Core.Conversion;
using AeroflyConv.Gui.Ui;

namespace AeroflyConv.Gui;

/// <summary>
/// One-window wizard for the X-Plane 12 → Aerofly FS 4 aircraft conversion: pick the aircraft, say where
/// it should land, press Convert. Everything else (materials, pivots, animation binding, textures) is
/// derived from the .acf and the object files.
/// </summary>
internal sealed class XPlaneForm : Form
{
    private readonly GlassSelect _aircraftSelect = new();
    private readonly GlassSelect _templateSelect = new();
    private readonly TextBox _nameBox = new();
    private readonly TextBox _outDirBox = new();
    private readonly CheckBox _installCheck = new();
    private readonly CheckBox _reportCheck = new();
    private readonly CheckBox _dumpCheck = new();
    private readonly CheckBox _interiorCheck = new();
    private readonly GlassButton _browseButton = new() { Text = "浏览…", Width = 90 };
    private readonly GlassButton _templateBrowse = new() { Text = "浏览…", Width = 90 };
    private readonly GlassButton _outDirButton = new() { Text = "选择…", Width = 90 };
    private readonly GlassButton _convertButton = new() { Text = "开始转换", Primary = true, Width = 150 };
    private readonly GlassButton _openButton = new() { Text = "打开输出目录", Width = 140, Enabled = false };
    private readonly GlassButton _closeButton = new() { Text = "关闭", Width = 90 };
    private readonly GlowProgressBar _progress = new();
    private readonly Label _status = new();
    private readonly Label _templateInfo = new();
    private readonly RichTextBox _log = new();

    private readonly List<string> _aircraftPaths = [];
    private string? _templateOverride;
    private string? _outDir;
    private string _lastOutput = "";
    private bool _busy;
    private CancellationTokenSource? _cts;

    public XPlaneForm()
    {
        Text = "X-Plane 12 机模 → Aerofly FS 4";
        Font = Theme.FontBody;
        ForeColor = Theme.Text;
        BackColor = Theme.BaseTop;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(880, 640);
        Size = new Size(940, 720);
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

        BuildLayout();
        DiscoverAircraft();
    }

    protected override void OnPaintBackground(PaintEventArgs e) => Theme.PaintBackdrop(e.Graphics, ClientRectangle);

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            BackColor = Color.Transparent,
            Padding = new Padding(24, 16, 24, 16),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // header
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // aircraft + options card
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // buttons
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // progress
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // log
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildOptionsCard(), 0, 1);
        root.Controls.Add(BuildButtons(), 0, 2);
        root.Controls.Add(BuildStatusRow(), 0, 3);
        root.Controls.Add(BuildLogCard(), 0, 4);
    }

    private Control BuildHeader()
    {
        var host = new Panel { Dock = DockStyle.Fill, AutoSize = true, BackColor = Color.Transparent, Height = 56 };
        var title = new Label
        {
            Text = "X-Plane 12 机模 → Aerofly FS 4",
            Font = new Font("Segoe UI Semibold", 15f),
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(0, 0),
            BackColor = Color.Transparent,
        };
        var hint = new Label
        {
            Text = "选机型 → 填安装名 → 开始转换。模型、材质、可动部件（副翼/襟翼/升降舵/方向舵/螺旋桨）与贴图一起生成。",
            Font = Theme.FontSmall,
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Location = new Point(2, 30),
            BackColor = Color.Transparent,
        };
        host.Controls.Add(title);
        host.Controls.Add(hint);
        return host;
    }

    private Control BuildOptionsCard()
    {
        var card = new GlassCard
        {
            Dock = DockStyle.Fill,
            Title = "Aircraft",
            Height = 300,
            Margin = new Padding(0, 0, 0, 12),
        };
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.CardTop, AutoScroll = true };

        // --- source
        var row1 = Row();
        _aircraftSelect.Width = 560;
        _aircraftSelect.Height = 30;
        _aircraftSelect.Margin = new Padding(0, 2, 8, 0);
        _browseButton.Height = 30;
        _browseButton.Margin = new Padding(0, 2, 0, 0);
        _browseButton.Click += (_, _) => BrowseAircraft();
        _aircraftSelect.SelectedIndexChanged += (_, _) => OnAircraftChanged();
        row1.Controls.Add(Caption("源机型", 210));
        row1.Controls.Add(_aircraftSelect);
        row1.Controls.Add(_browseButton);

        // --- install name
        var row2 = Row();
        _nameBox.Width = 250;
        _nameBox.Height = 26;
        _nameBox.BackColor = Theme.InputBack;
        _nameBox.ForeColor = Theme.Text;
        _nameBox.BorderStyle = BorderStyle.FixedSingle;
        _nameBox.Font = Theme.FontBody;
        _nameBox.Margin = new Padding(0, 4, 8, 0);
        _nameBox.TextChanged += (_, _) => UpdateTemplateInfo();
        var nameHint = new Label
        {
            Text = "Aerofly「飞机」列表里显示为这个文件夹名",
            Font = Theme.FontSmall,
            ForeColor = Theme.TextDim,
            AutoSize = true,
            Margin = new Padding(0, 9, 0, 0),
            BackColor = Color.Transparent,
        };
        row2.Controls.Add(Caption("安装名", 210));
        row2.Controls.Add(_nameBox);
        row2.Controls.Add(nameHint);

        // --- template
        var row3 = Row();
        _templateSelect.Width = 560;
        _templateSelect.Height = 30;
        _templateSelect.Margin = new Padding(0, 2, 8, 0);
        _templateSelect.SetItems(["自动（推荐）"]);
        _templateSelect.SelectedIndexChanged += (_, _) =>
        {
            _templateOverride = _templateSelect.SelectedIndex > 0
                ? _templatePaths[_templateSelect.SelectedIndex - 1]
                : null;
            UpdateTemplateInfo();
        };
        _templateBrowse.Height = 30;
        _templateBrowse.Margin = new Padding(0, 2, 0, 0);
        _templateBrowse.Click += (_, _) => BrowseTemplate();
        row3.Controls.Add(Caption("飞行模型模板", 210));
        row3.Controls.Add(_templateSelect);
        row3.Controls.Add(_templateBrowse);

        // --- template info
        var row4 = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.CardTop,
            Margin = new Padding(0),
            Padding = new Padding(210, 0, 0, 0),
        };
        _templateInfo.AutoSize = true;
        _templateInfo.Font = Theme.FontSmall;
        _templateInfo.ForeColor = Theme.TextDim;
        _templateInfo.Margin = new Padding(0, 0, 0, 6);
        _templateInfo.BackColor = Color.Transparent;
        row4.Controls.Add(_templateInfo);

        // --- output dir
        var row5 = Row();
        _outDirBox.Width = 520;
        _outDirBox.Height = 26;
        _outDirBox.BackColor = Theme.InputBack;
        _outDirBox.ForeColor = Theme.Text;
        _outDirBox.BorderStyle = BorderStyle.FixedSingle;
        _outDirBox.Font = Theme.FontBody;
        _outDirBox.Margin = new Padding(0, 4, 8, 0);
        _outDirBox.TextChanged += (_, _) => _outDir = _outDirBox.Text.Trim();
        var outHint = new Label
        {
            Text = "留空则用 %LOCALAPPDATA%\\AeroflyConv\\xp\\<name>",
            Font = Theme.FontSmall,
            ForeColor = Theme.TextDim,
            AutoSize = true,
            Margin = new Padding(0, 9, 0, 0),
            BackColor = Color.Transparent,
        };
        _outDirButton.Height = 30;
        _outDirButton.Margin = new Padding(0, 2, 0, 0);
        _outDirButton.Click += (_, _) => ChooseOutDir();
        row5.Controls.Add(Caption("输出目录", 210));
        row5.Controls.Add(_outDirBox);
        row5.Controls.Add(_outDirButton);

        // --- switches
        var row6 = Row(38);
        Configure(_installCheck, "转换后直接装进 Aerofly", true);
        Configure(_reportCheck, "生成报告 conversion.txt", true);
        Configure(_dumpCheck, "导出 .tmb.txt 文本镜像", false);
        Configure(_interiorCheck, "跳过座舱对象（减面数）", false);
        _installCheck.Margin = new Padding(210, 0, 28, 0);
        _reportCheck.Margin = new Padding(0, 0, 28, 0);
        _dumpCheck.Margin = new Padding(0, 0, 28, 0);
        row6.Controls.Add(_installCheck);
        row6.Controls.Add(_reportCheck);
        row6.Controls.Add(_dumpCheck);
        row6.Controls.Add(_interiorCheck);

        host.Controls.Add(row6);
        host.Controls.Add(row5);
        host.Controls.Add(row4);
        host.Controls.Add(row3);
        host.Controls.Add(row2);
        host.Controls.Add(row1);
        card.Controls.Add(host);
        return card;
    }

    private readonly List<string> _templatePaths = [];

    private Control BuildButtons()
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 8),
        };
        _convertButton.Margin = new Padding(0, 0, 10, 0);
        _openButton.Margin = new Padding(0, 0, 10, 0);
        _closeButton.Margin = new Padding(0);
        _convertButton.Click += async (_, _) => await ConvertAsync();
        _openButton.Click += (_, _) => OpenOutput();
        _closeButton.Click += (_, _) => { if (!_busy) Close(); };
        flow.Controls.Add(_convertButton);
        flow.Controls.Add(_openButton);
        flow.Controls.Add(_closeButton);
        return flow;
    }

    private Control BuildStatusRow()
    {
        var host = new Panel { Dock = DockStyle.Fill, Height = 30, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 8) };
        _progress.Location = new Point(2, 8);
        _progress.Width = 460;
        _progress.Height = 10;
        _progress.Maximum = 100;
        _status.Text = "就绪";
        _status.ForeColor = Theme.TextMuted;
        _status.Font = Theme.FontSmall;
        _status.AutoSize = true;
        _status.Location = new Point(474, 2);
        _status.BackColor = Color.Transparent;
        host.Controls.Add(_progress);
        host.Controls.Add(_status);
        return host;
    }

    private Control BuildLogCard()
    {
        var card = new GlassCard { Dock = DockStyle.Fill, Title = "Log", Margin = new Padding(0) };
        var host = new Panel
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
        host.Controls.Add(_log);
        card.Controls.Add(host);
        return card;
    }

    private static FlowLayoutPanel Row(int height = 34) => new()
    {
        Dock = DockStyle.Top,
        Height = height,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
        BackColor = Theme.CardTop,
        Margin = new Padding(0),
    };

    private static Label Caption(string text, int width = 90) => new()
    {
        Text = text,
        ForeColor = Theme.TextMuted,
        AutoSize = false,
        Width = width,
        Height = 22,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 6, 8, 0),
        BackColor = Color.Transparent,
    };

    private static void Configure(CheckBox box, string text, bool @checked)
    {
        box.Text = text;
        box.AutoSize = true;
        box.Checked = @checked;
        box.FlatStyle = FlatStyle.Standard;
        box.BackColor = Theme.CardTop;
        box.ForeColor = Color.FromArgb(215, 223, 235);
        box.Font = Theme.FontBody;
        box.Cursor = Cursors.Hand;
    }

    // ------------------------------------------------------------------ discovery

    private void DiscoverAircraft()
    {
        _aircraftPaths.Clear();
        foreach (string folder in XpPipeline.FindAircraftFolders())
            _aircraftPaths.Add(folder);

        var labels = _aircraftPaths
            .Select(p => Path.GetFileName(Path.TrimEndingDirectorySeparator(p)))
            .ToList();

        if (labels.Count == 0)
        {
            _aircraftSelect.SetItems(["未找到 X-Plane 12，请用「浏览…」选择机型文件夹"]);
            _aircraftSelect.Enabled = false;
            AppendLog("没有自动找到 X-Plane 12 的 Aircraft 目录。用「浏览…」指定机型文件夹或 .acf 文件。");
            return;
        }

        _aircraftSelect.SetItems(labels);
        AppendLog($"找到 {labels.Count} 个 X-Plane 机型。");
        OnAircraftChanged();
    }

    private void OnAircraftChanged()
    {
        if (_aircraftPaths.Count == 0) return;
        int index = Math.Clamp(_aircraftSelect.SelectedIndex, 0, _aircraftPaths.Count - 1);
        string folder = _aircraftPaths[index];
        string guess = XpModelConverter.Sanitize(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)))
            .ToLowerInvariant();
        _nameBox.Text = guess;
        PopulateTemplates();
        UpdateTemplateInfo();
    }

    private void PopulateTemplates()
    {
        _templatePaths.Clear();
        var labels = new List<string> { "自动（推荐）" };
        foreach (string root in XpPipeline.CandidateAeroflyRoots())
        {
            string aircraft = Path.Combine(root, "aircraft");
            if (!Directory.Exists(aircraft)) continue;
            foreach (string dir in Directory.GetDirectories(aircraft))
            {
                string name = Path.GetFileName(dir);
                string tmd = Path.Combine(dir, name + ".tmd");
                if (!File.Exists(tmd)) continue;
                _templatePaths.Add(tmd);
                labels.Add(name);
            }
        }
        _templateSelect.SetItems(labels);
        if (labels.Count == 1)
            AppendLog("没有找到 Aerofly 装机目录，.tmd 动画绑定会被跳过（可用「浏览…」手动指定模板）。");
    }

    private void UpdateTemplateInfo()
    {
        string template = ResolveTemplate();
        _templateInfo.Text = template.Length == 0
            ? "未找到可用的 Aerofly 模板 .tmd"
            : "模板: " + template;
    }

    private string ResolveTemplate()
    {
        if (_templateOverride is { Length: > 0 }) return _templateOverride;
        string name = _nameBox.Text.Trim();
        return name.Length == 0 ? "" : XpPipeline.FindTemplate(name);
    }

    private void BrowseAircraft()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 X-Plane 机型文件夹（包含 .acf 的那个）",
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string folder = dialog.SelectedPath;
        if (XpPipeline.ResolveAcf(folder).Length == 0)
        {
            using var file = new OpenFileDialog { Filter = "X-Plane aircraft|*.acf|All files|*.*" };
            if (file.ShowDialog(this) != DialogResult.OK) return;
            folder = file.FileName;
        }

        _aircraftPaths.Add(folder);
        var labels = _aircraftSelect.Items.ToList();
        if (labels.Count == 1 && labels[0].StartsWith("未找到", StringComparison.Ordinal)) labels.Clear();
        labels.Add(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)));
        _aircraftSelect.SetItems(labels, labels.Count - 1);
        _aircraftSelect.Enabled = true;
        OnAircraftChanged();
    }

    private void BrowseTemplate()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Aerofly dynamics|*.tmd|All files|*.*",
            Title = "选择用作骨架的 Aerofly .tmd",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _templateOverride = dialog.FileName;
        UpdateTemplateInfo();
    }

    private void ChooseOutDir()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择转换输出的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _outDir = dialog.SelectedPath;
        _outDirBox.Text = dialog.SelectedPath;
    }

    // ------------------------------------------------------------------ conversion

    private async Task ConvertAsync()
    {
        if (_busy) return;
        if (_aircraftPaths.Count == 0)
        {
            MessageBox.Show(this, "请先选择 X-Plane 机型。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int index = Math.Clamp(_aircraftSelect.SelectedIndex, 0, _aircraftPaths.Count - 1);
        var job = new XpJobOptions
        {
            Input = _aircraftPaths[index],
            OutputDirectory = _outDir,
            InstallName = _nameBox.Text.Trim() is { Length: > 0 } n ? n : null,
            Install = _installCheck.Checked,
            Report = _reportCheck.Checked,
            Dump = _dumpCheck.Checked,
            TmdTemplate = ResolveTemplate() is { Length: > 0 } t ? t : null,
        };
        job.Model.SkipInterior = _interiorCheck.Checked;

        _busy = true;
        SetBusy(true);
        _log.Clear();
        _progress.Value = 0;
        _status.Text = "正在转换…";
        _cts = new CancellationTokenSource();
        int lines = 0;

        var progress = new Progress<string>(message =>
        {
            AppendLog(message);
            lines++;
            _progress.Value = Math.Min(95, lines);
            if (message.StartsWith("tmc", StringComparison.Ordinal)) _status.Text = "写机型定义…";
            else if (message.StartsWith("textures", StringComparison.Ordinal)) _status.Text = "编码贴图…";
            else if (message.StartsWith("geometry", StringComparison.Ordinal)) _status.Text = "构建几何…";
        });

        try
        {
            XpJobResult result = await Task.Run(() =>
                XpPipeline.Run(job, message => ((IProgress<string>)progress).Report(message), _cts.Token));

            _progress.Value = 100;
            _lastOutput = result.OutputDirectory;
            _openButton.Enabled = true;
            AppendLog("");
            AppendLog($"完成：{result.Triangles} 三角面 / {result.GeometryCount} 几何 / " +
                      $"{result.MaterialCount} 材质 / {result.TextureCount} 贴图 / " +
                      $"{result.PartCount} 可动件 · {result.Seconds:0.0} s");
            _status.Text = result.Installed
                ? $"已装到 {result.InstallPath}"
                : "转换完成";

            if (result.Installed)
            {
                AppendLog("");
                AppendLog("下一步：关掉 Aerofly 再启动，主菜单「飞机」里选 " +
                          Path.GetFileName(result.InstallPath) + "，点右上播放键。");
                MessageBox.Show(this,
                    $"转换完成，已装到：\n{result.InstallPath}\n\n" +
                    "重启 Aerofly，在「飞机」里选中它，点右上角播放键即可飞行。",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (result.TextureTotal > 0 && result.TextureCount == 0)
            {
                AppendLog("提示：贴图未生成。", error: true);
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("已取消。", error: true);
            _status.Text = "已取消";
        }
        catch (Exception ex)
        {
            AppendLog("错误：" + ex.Message, error: true);
            _status.Text = "转换失败";
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _convertButton.Enabled = !busy;
        _closeButton.Enabled = !busy;
        _browseButton.Enabled = !busy;
        _aircraftSelect.Enabled = !busy && _aircraftPaths.Count > 0;
        _templateSelect.Enabled = !busy;
        _templateBrowse.Enabled = !busy;
        _outDirButton.Enabled = !busy;
        _nameBox.Enabled = !busy;
        _outDirBox.Enabled = !busy;
        _installCheck.Enabled = !busy;
        _reportCheck.Enabled = !busy;
        _dumpCheck.Enabled = !busy;
        _interiorCheck.Enabled = !busy;
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

    private void OpenOutput()
    {
        if (_lastOutput.Length == 0 || !Directory.Exists(_lastOutput)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastOutput}\"") { UseShellExecute = true });
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts?.Cancel();
        base.OnFormClosed(e);
    }
}
