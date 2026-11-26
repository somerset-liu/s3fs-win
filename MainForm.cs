using System.Diagnostics;
using System.Text.RegularExpressions;

namespace S3fsWin;

/// <summary>
/// 主窗体 + 托盘。关闭按钮默认只是最小化到托盘，托盘菜单里的「退出」才真的结束程序。
/// 界面全部用代码搭，不用 .Designer.cs / .resx，交叉编译时少一类不确定因素。
/// </summary>
internal sealed class MainForm : Form
{
    private const int MaxLogLines = 500;

    /// <summary>日志超过上限这么多行再一次性裁剪，避免每行都重建整个文本。</summary>
    private const int LogTrimSlack = 49;

    /// <summary>进程活着且日志里没出现致命行，满这么久的毫秒数才认定「已挂载」。</summary>
    private const int MountSettleMs = 3000;

    private readonly ApplicationContext _context;
    private readonly AppConfig _cfg;
    private readonly string _exeDir;
    private readonly string _exePath;
    private readonly string _configPath;
    private readonly S3fsRunner _runner = new();
    private readonly Dictionary<TrayState, Icon> _icons = [];

    private readonly TextBox _txtBucket = new();
    private readonly TextBox _txtEndpoint = new();
    private readonly TextBox _txtAccessKey = new();
    private readonly TextBox _txtSecretKey = new();
    private readonly TextBox _txtMount = new();
    private readonly TextBox _txtPasswd = new();
    private readonly TextBox _txtMime = new();
    private readonly TextBox _txtTmp = new();
    private readonly TextBox _txtCache = new();
    private readonly TextBox _txtOptions = new();
    private readonly TextBox _txtLog = new();
    private readonly CheckBox _chkDebug = new();
    private readonly CheckBox _chkAutoStart = new();
    private readonly CheckBox _chkShowSecret = new();
    private readonly Label _lblStatus = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnMount = new();
    private readonly Button _btnUnmount = new();
    private readonly Button _btnOpen = new();
    private readonly Button _btnClearLog = new();
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly ToolStripMenuItem _miStatus = new();
    private readonly ToolStripMenuItem _miMount = new();
    private readonly ToolStripMenuItem _miUnmount = new();
    private readonly ToolStripMenuItem _miOpen = new();
    private readonly System.Windows.Forms.Timer _timer = new();

    private readonly Queue<string> _logQueue = new();
    private readonly object _logGate = new();

    private int _logLineCount;
    private bool _allowExit;
    private bool _trayHintShown;
    private bool _suppressAutoStartEvent;
    private bool _sawFatal;
    private DateTime _mountStartedAt;
    private TrayState _state = TrayState.NotMounted;

    public MainForm(ApplicationContext context, AppConfig cfg, string exeDir, string exePath, string configPath)
    {
        _context = context;
        _cfg = cfg;
        _exeDir = exeDir;
        _exePath = exePath;
        _configPath = configPath;

        foreach (TrayState state in Enum.GetValues<TrayState>())
        {
            _icons[state] = TrayIconFactory.Create(state);
        }

        Text = "s3fs-win — S3 挂载托盘";
        Icon = _icons[TrayState.NotMounted];
        ClientSize = new Size(740, 660);
        MinimumSize = new Size(620, 500);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        BuildTray();
        SyncFieldsFromConfig();

        _runner.LineReceived += OnChildLine;
        _runner.Exited += OnChildExit;

        _timer.Interval = 1000;
        _timer.Tick += (_, _) => PollState();
        _timer.Start();
    }

    /// <summary>启动后先不弹窗口（--min 或配置项 startInTray）。Program 在 Run 之前设置。</summary>
    public bool StartsHidden { get; set; }

    /// <summary>Program 调这个：即使不显示窗口也先把句柄建出来（否则日志与定时器无处落脚），再挂托盘图标。</summary>
    public void Prepare()
    {
        _ = Handle;
        FlushLog();
        _tray.Visible = true;
    }

    public void ShowFromTray()
    {
        if (!Visible)
        {
            Show();
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
    }

    /// <summary>启动时按配置自动挂载（配合 --min 做无人值守）。</summary>
    public void MaybeAutoMount()
    {
        if (_cfg.AutoMountOnStart)
        {
            Log("按配置 autoMountOnStart 自动挂载");
            RequestMount();
        }
    }

    /// <summary>把建窗前的日志（读配置、单实例检查）补进日志框。</summary>
    public void LogStartupMessages(IEnumerable<string> messages)
    {
        foreach (string message in messages)
        {
            Log(message);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        bool minimizeInstead = _cfg.MinimizeToTray && !_allowExit && e.CloseReason == CloseReason.UserClosing;
        if (minimizeInstead)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                ShowBalloon("已最小化到托盘", "关闭按钮只是隐藏窗口；要断开挂载并退出，请右键托盘图标选「退出」。");
            }

            return;
        }

        _allowExit = true;
        SaveConfig(log: false);
        if (_runner.IsRunning)
        {
            Log(_runner.Stop());
        }

        _context.ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 兜底：连点托盘或系统关机时，可能还没走到 OnFormClosing 就被释放。
            _runner.Dispose();
            _timer.Dispose();
            _trayMenu.Dispose();
            _tray.Dispose();
            foreach (Icon icon in _icons.Values)
            {
                icon.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    // ---------- 界面 ----------

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(12, 10, 12, 10),
        };
        for (int i = 0; i < 4; i++)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var fields = new TableLayoutPanel
        {
            ColumnCount = 3,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140f));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        AddField(fields, "桶名", _txtBucket);
        AddField(fields, "端点 URL", _txtEndpoint);
        AddField(fields, "Access Key", _txtAccessKey);
        _txtSecretKey.UseSystemPasswordChar = true;
        _chkShowSecret.Text = "显示";
        _chkShowSecret.AutoSize = true;
        _chkShowSecret.Margin = new Padding(4, 9, 3, 3);
        _chkShowSecret.CheckedChanged += (_, _) =>
            _txtSecretKey.UseSystemPasswordChar = !_chkShowSecret.Checked;
        AddField(fields, "Secret Key", _txtSecretKey, trailing: _chkShowSecret);
        AddField(fields, "挂载点", _txtMount, "如 S:");
        AddField(fields, "凭据文件", _txtPasswd);
        AddField(fields, "mime 文件", _txtMime);
        AddField(fields, "临时目录 tmpdir", _txtTmp);
        AddField(fields, "缓存目录 use_cache", _txtCache, "留空=不启用");
        _txtOptions.Multiline = true;
        _txtOptions.Height = 46;
        AddField(fields, "额外选项 options", _txtOptions);

        var pathHint = new Label
        {
            Text = "提示：-o 里的路径会自动转成正斜杠的绝对路径（反斜杠会被 s3fs 当转义符吃掉），桶名与挂载点是位置参数、原样传。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 2, 3, 6),
        };

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 2, 0, 2),
        };
        ConfigureButton(_btnSave, "保存配置", (_, _) => SaveConfig(log: true));
        ConfigureButton(_btnMount, "挂载", (_, _) => RequestMount());
        ConfigureButton(_btnUnmount, "卸载", (_, _) => RequestUnmount());
        ConfigureButton(_btnOpen, "打开挂载目录", (_, _) => OpenMountDirectory());
        ConfigureButton(_btnClearLog, "清空日志", (_, _) => ClearLog());
        _chkDebug.Text = "调试日志(dbglevel=info)";
        _chkDebug.AutoSize = true;
        _chkDebug.Margin = new Padding(12, 9, 4, 0);
        _chkAutoStart.Text = "开机自启";
        _chkAutoStart.AutoSize = true;
        _chkAutoStart.Margin = new Padding(12, 9, 4, 0);
        _chkAutoStart.CheckedChanged += OnAutoStartChanged;

        foreach (Control c in new Control[] { _btnSave, _btnMount, _btnUnmount, _btnOpen, _btnClearLog, _chkDebug, _chkAutoStart })
        {
            toolbar.Controls.Add(c);
        }

        _lblStatus.Text = "状态：未挂载";
        _lblStatus.AutoSize = true;
        _lblStatus.Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold);
        _lblStatus.Margin = new Padding(3, 4, 3, 6);

        _txtLog.Multiline = true;
        _txtLog.ReadOnly = true;
        _txtLog.ScrollBars = ScrollBars.Both;
        _txtLog.WordWrap = false;
        _txtLog.Dock = DockStyle.Fill;
        _txtLog.Font = new Font("Consolas", 9f);
        _txtLog.Margin = new Padding(3, 0, 3, 0);

        root.Controls.Add(fields, 0, 0);
        root.Controls.Add(pathHint, 0, 1);
        root.Controls.Add(toolbar, 0, 2);
        root.Controls.Add(_lblStatus, 0, 3);
        root.Controls.Add(_txtLog, 0, 4);

        Controls.Add(root);
    }

    private static void AddField(
        TableLayoutPanel panel,
        string caption,
        TextBox input,
        string? hint = null,
        Control? trailing = null)
    {
        int row = panel.RowCount;
        panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        panel.Controls.Add(new Label
        {
            Text = caption,
            AutoSize = true,
            Margin = new Padding(3, 8, 6, 3),
        }, 0, row);

        input.Dock = DockStyle.Top;
        input.Margin = new Padding(3, 4, 3, 4);
        panel.Controls.Add(input, 1, row);

        if (trailing is not null)
        {
            panel.Controls.Add(trailing, 2, row);
        }
        else if (hint is not null)
        {
            panel.Controls.Add(new Label
            {
                Text = hint,
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(3, 8, 3, 3),
            }, 2, row);
        }
    }

    private static void ConfigureButton(Button button, string text, EventHandler handler)
    {
        button.Text = text;
        button.AutoSize = true;
        button.Margin = new Padding(3, 4, 3, 4);
        button.Padding = new Padding(8, 2, 8, 2);
        button.Click += handler;
    }

    private void BuildTray()
    {
        _miStatus.Text = "状态：未挂载";
        _miStatus.Enabled = false;
        _miMount.Text = "挂载";
        _miMount.Click += (_, _) => RequestMount();
        _miUnmount.Text = "卸载";
        _miUnmount.Enabled = false;
        _miUnmount.Click += (_, _) => RequestUnmount();
        _miOpen.Text = "打开挂载目录";
        _miOpen.Click += (_, _) => OpenMountDirectory();

        var miExit = new ToolStripMenuItem("退出");
        miExit.Click += (_, _) => ExitApp();

        _trayMenu.Items.AddRange([
            _miStatus,
            new ToolStripSeparator(),
            _miMount,
            _miUnmount,
            _miOpen,
            new ToolStripSeparator(),
            miExit,
        ]);

        _tray.ContextMenuStrip = _trayMenu;
        _tray.Icon = _icons[_state];
        _tray.Text = "s3fs-win";
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.MouseUp += (object? sender, MouseEventArgs e) =>
        {
            // 左键单击/双击都唤起主窗口；右键交给上下文菜单。
            if (e.Button == MouseButtons.Left)
            {
                ShowFromTray();
            }
        };

        ApplyState(TrayState.NotMounted, "未挂载", notify: false);
    }

    // ---------- 配置 ----------

    private void SyncFieldsFromConfig()
    {
        _txtBucket.Text = _cfg.Bucket;
        _txtEndpoint.Text = _cfg.Endpoint;
        _txtAccessKey.Text = _cfg.AccessKeyId;
        _txtSecretKey.Text = _cfg.SecretAccessKey;
        _txtMount.Text = _cfg.MountPoint;
        _txtPasswd.Text = Pick(_cfg.PasswdFile, "passwd-s3fs");
        _txtMime.Text = Pick(_cfg.MimeFile, "mime.types");
        _txtTmp.Text = Pick(_cfg.TmpDir, "tmp");
        _txtCache.Text = _cfg.CacheDir;
        _txtOptions.Text = _cfg.Options;
        _chkDebug.Checked = _cfg.Debug;

        _suppressAutoStartEvent = true;
        string? registered = AutoStart.Read();
        string desired = AutoStart.DesiredCommand(_exePath);
        if (registered is null)
        {
            _chkAutoStart.Checked = false;
        }
        else if (string.Equals(registered.Trim(), desired, StringComparison.OrdinalIgnoreCase))
        {
            _chkAutoStart.Checked = true;
        }
        else
        {
            _chkAutoStart.Checked = false;
            Log($"[!] 注册表 Run 下已有同名项但内容不同，先按未启用处理：{registered}");
        }

        _suppressAutoStartEvent = false;

        string Pick(string configured, string leaf)
            => string.IsNullOrWhiteSpace(configured) ? Path.Combine(_exeDir, leaf) : configured.Trim();
    }

    private AppConfig Collect()
    {
        _cfg.Bucket = _txtBucket.Text.Trim();
        _cfg.Endpoint = _txtEndpoint.Text.Trim();
        _cfg.AccessKeyId = _txtAccessKey.Text.Trim();
        _cfg.SecretAccessKey = _txtSecretKey.Text.Trim();
        _cfg.MountPoint = _txtMount.Text.Trim();
        _cfg.PasswdFile = _txtPasswd.Text.Trim();
        _cfg.MimeFile = _txtMime.Text.Trim();
        _cfg.TmpDir = _txtTmp.Text.Trim();
        _cfg.CacheDir = _txtCache.Text.Trim();
        _cfg.Options = _txtOptions.Text.Trim();
        _cfg.Debug = _chkDebug.Checked;
        _cfg.AutoStart = _chkAutoStart.Checked;
        return _cfg;
    }

    private void SaveConfig(bool log)
    {
        Collect();
        _cfg.Save(_configPath, Log);
        if (log)
        {
            Log($"配置已保存：{_configPath}");
        }
    }

    private void OnAutoStartChanged(object? sender, EventArgs e)
    {
        if (_suppressAutoStartEvent)
        {
            return;
        }

        AutoStart.Result result = AutoStart.Set(_exePath, _chkAutoStart.Checked);
        Log((result.Ok ? string.Empty : "[x] ") + result.Message);
        if (!result.Ok)
        {
            _suppressAutoStartEvent = true;
            _chkAutoStart.Checked = !_chkAutoStart.Checked;
            _suppressAutoStartEvent = false;
            return;
        }

        _cfg.AutoStart = _chkAutoStart.Checked;
        _cfg.Save(_configPath, Log);
    }

    // ---------- 挂载 / 卸载 ----------

    private void RequestMount()
    {
        if (_runner.IsRunning)
        {
            Log("[!] s3fs 已在运行，忽略本次挂载请求");
            return;
        }

        MountPlan plan = MountPlanBuilder.Build(Collect(), _exeDir);
        SaveConfig(log: false);

        foreach (string warning in plan.Warnings)
        {
            Log($"[!] {warning}");
        }

        foreach (string error in plan.Errors)
        {
            Log($"[x] {error}");
        }

        if (plan.HasErrors)
        {
            ApplyState(TrayState.NotMounted, "配置不完整，未挂载");
            ShowBalloon("配置不完整", plan.Errors[0]);
            return;
        }

        if (!File.Exists(plan.S3fsExePath))
        {
            Log($"[x] 找不到 s3fs.exe：{plan.S3fsExePath}（要和 s3fs-win.exe、23 个 DLL 放在同一层）");
            ApplyState(TrayState.NotMounted, "缺少 s3fs.exe");
            return;
        }

        if (plan.MimeFileNative.Length > 0 && !File.Exists(plan.MimeFileNative))
        {
            Log($"[!] 找不到 mime.types：{plan.MimeFileNative}（不致命，但上传对象的 Content-Type 会不对）");
        }

        EnsureTmpDir(plan);

        CredentialFile.Result creds = CredentialFile.Ensure(
            plan.PasswdFileNative, plan.Bucket, _cfg.AccessKeyId, _cfg.SecretAccessKey);
        Log((creds.Ok ? string.Empty : "[x] ") + creds.Message);
        if (!creds.Ok)
        {
            ApplyState(TrayState.NotMounted, "凭据不可用");
            ShowBalloon("凭据不可用", creds.Message);
            return;
        }

        Log("$ " + plan.RenderCommandLine());
        _sawFatal = false;
        if (!_runner.Start(plan, out string? startError))
        {
            Log($"[x] 启动 s3fs.exe 失败：{startError}");
            ApplyState(TrayState.NotMounted, "启动失败");
            return;
        }

        _mountStartedAt = DateTime.UtcNow;
        ApplyState(TrayState.Mounting, $"挂载中（{plan.MountPoint}）");
    }

    /// <summary>临时目录建不出来只记日志，不弹异常窗（exe 放在 Program Files 这类只读目录时常见）。</summary>
    private void EnsureTmpDir(MountPlan plan)
    {
        if (plan.TmpDirNative.Length == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(plan.TmpDirNative);
        }
        catch (Exception ex)
        {
            Log($"[!] 创建临时目录失败：{plan.TmpDirNative}（{ex.GetType().Name}: {ex.Message}）— 继续尝试挂载");
        }

        if (!Directory.Exists(plan.TmpDirNative))
        {
            Log("[!] 临时目录仍然不存在，s3fs 很可能报 not enough disk space");
        }
    }

    private void RequestUnmount()
    {
        if (!_runner.IsRunning)
        {
            Log("当前没有由本程序启动的 s3fs 进程，视为已卸载");
            ApplyState(TrayState.NotMounted, "未挂载");
            return;
        }

        Log(_runner.Stop());
        ApplyState(TrayState.NotMounted, "已卸载");
    }

    private void OpenMountDirectory()
    {
        string mount = MountPlanBuilder.NormalizeMountPoint(_txtMount.Text);
        if (mount.Length == 0)
        {
            Log("[!] 挂载点是空的");
            return;
        }

        string target = Regex.IsMatch(mount, "^[A-Za-z]:$") ? mount + "\\" : mount;
        if (!Directory.Exists(target))
        {
            Log($"[!] 目录还不存在（是不是还没挂载？）：{target}");
            return;
        }

        try
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            psi.ArgumentList.Add(target);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log($"[!] 打开目录失败：{ex.Message}");
        }
    }

    /// <summary>每秒核对一次真实状态：进程是否还在、挂载是否已经稳定。</summary>
    private void PollState()
    {
        FlushLog();

        if (_runner.IsRunning)
        {
            if (_state == TrayState.Mounting && !_sawFatal
                && (DateTime.UtcNow - _mountStartedAt).TotalMilliseconds >= MountSettleMs)
            {
                ApplyState(TrayState.Mounted, $"已挂载（{MountPlanBuilder.NormalizeMountPoint(_txtMount.Text)}）");
            }

            return;
        }

        if (_state != TrayState.NotMounted)
        {
            // 进程没了：可能是挂载失败自己退出，也可能是外部把它杀了。
            ApplyState(TrayState.NotMounted, _sawFatal ? "未挂载（日志里有错误）" : "未挂载");
        }
    }

    private void ApplyState(TrayState state, string statusText, bool notify = true)
    {
        bool changed = state != _state;
        _state = state;
        Icon icon = _icons[state];
        _tray.Icon = icon;
        Icon = icon;

        string trayText = $"s3fs-win — {statusText}";
        _tray.Text = trayText.Length <= 63 ? trayText : trayText[..63];
        _lblStatus.Text = $"状态：{statusText}";
        _miStatus.Text = $"状态：{statusText}";
        _miMount.Enabled = state == TrayState.NotMounted;
        _miUnmount.Enabled = state != TrayState.NotMounted;
        _btnMount.Enabled = state == TrayState.NotMounted;
        _btnUnmount.Enabled = state != TrayState.NotMounted;

        if (notify && changed && state == TrayState.Mounted)
        {
            ShowBalloon("挂载完成", statusText + "，现在应该能在资源管理器里看到它。");
        }
    }

    private void ExitApp()
    {
        _allowExit = true;
        Close();
    }

    private void ShowBalloon(string title, string text)
    {
        try
        {
            _tray.ShowBalloonTip(2500, title, text, ToolTipIcon.Info);
        }
        catch (Exception)
        {
            // 托盘句柄还没建好时忽略。
        }
    }

    // ---------- 日志 ----------

    /// <summary>追加一行日志。非 UI 线程也能调，内部转回 UI 线程并排队。</summary>
    public void Log(string message)
    {
        lock (_logGate)
        {
            if (IsDisposed)
            {
                return;
            }

            _logQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        FlushLog();
    }

    private void FlushLog()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(FlushLog));
            }
            catch (Exception)
            {
                // 正在关闭，剩下的日志丢掉即可。
            }

            return;
        }

        while (true)
        {
            string? line;
            lock (_logGate)
            {
                line = _logQueue.Count == 0 ? null : _logQueue.Dequeue();
            }

            if (line is null)
            {
                return;
            }

            AppendLogLine(line);
        }
    }

    private void AppendLogLine(string line)
    {
        if (_txtLog.TextLength > 0)
        {
            _txtLog.AppendText("\r\n");
        }

        _txtLog.AppendText(line);
        _logLineCount++;

        if (_logLineCount > MaxLogLines + LogTrimSlack)
        {
            TrimLog();
        }

        _txtLog.SelectionStart = _txtLog.TextLength;
        _txtLog.SelectionLength = 0;
        _txtLog.ScrollToCaret();
    }

    private void TrimLog()
    {
        string[] lines = _txtLog.Lines;
        int drop = lines.Length - MaxLogLines;
        if (drop <= 0)
        {
            return;
        }

        _txtLog.Text = string.Join("\r\n", lines, drop, lines.Length - drop);
        _logLineCount = lines.Length - drop;
    }

    private void ClearLog()
    {
        _txtLog.Clear();
        _logLineCount = 0;
        lock (_logGate)
        {
            _logQueue.Clear();
        }

        Log("日志已清空");
    }

    /// <summary>s3fs 输出一行：先认致命错误，再进日志框（原始错误行一定保留）。</summary>
    private void OnChildLine(string line)
    {
        string hint = FatalLine.Detect(line) is string h ? "  ← " + h : string.Empty;
        bool fatal = hint.Length > 0;
        if (fatal)
        {
            _sawFatal = true;
        }

        Log((fatal ? "[!] " : "    ") + line + hint);
    }

    private void OnChildExit(int exitCode)
    {
        Log(_sawFatal
            ? $"s3fs 进程退出，退出码 {exitCode}；上面标 [!] 的行就是原因"
            : $"s3fs 进程退出，退出码 {exitCode}");
    }
}
