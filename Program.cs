using System.Text;

namespace S3fsWin;

internal static class Program
{
    /// <summary>
    /// 用普通名（不加 Global\ 前缀）：落在当前会话的命名空间里，正好是「每个登录会话一个实例」的语义，
    /// 也不需要 SeCreateGlobalPrivilege。wine 对 Local\ 前缀支持不全，所以不加。
    /// </summary>
    private const string MutexName = "s3fs-win-gui-single-instance";

    private const string WakeEventName = "s3fs-win-gui-wake";

    /// <summary>单实例锁。静态持有到进程结束，防止 GC 提前回收内核句柄（见 Main 里的注释）。</summary>
    private static Mutex? InstanceMutex { get; set; }

    /// <summary>唤醒事件。同样是静态持有，主进程靠它把重复启动的实例「弹回」窗口。</summary>
    private static EventWaitHandle? WakeEvent { get; set; }

    private const string HelpText = """
        s3fs-win — s3fs.exe（WinFsp 版 s3fs-fuse 1.97）的托盘前端

        用法：s3fs-win.exe [选项]
          --min          启动即进托盘，不弹主窗口（开机自启用的就是这个）
          --print-args   按当前 config.json 渲染出将要执行的命令行后退出，同时写 s3fs-win.args.txt
          --selftest     跑参数拼装自检（无窗口），全部通过退出码 0
          --probe-instance 诊断用：报告单实例互斥体是否已被别的进程持有
          -h, --help     显示本帮助

        配置文件 config.json 与本程序同目录；已有实例在跑时再次启动只会把原窗口唤起来。
        """;

    [STAThread]
    private static void Main(string[] args)
    {
        bool min = false;
        foreach (string raw in args)
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "--min":
                    min = true;
                    break;
                case "--print-args":
                    PrintArgs();
                    return;
                case "--selftest":
                    Environment.ExitCode = SelfTest.Run() ? 0 : 1;
                    return;
                case "--probe-instance":
                    // 诊断用：只验「命名互斥体是否已被别的进程持有」，不建窗口。
                    ProbeInstance();
                    return;
                case "-h":
                case "--help":
                    Console.WriteLine(HelpText);
                    return;
                default:
                    // 未知参数（比如旧版的 --mount）忽略即可，别因为多一个参数就起不来。
                    Console.WriteLine($"[!] 忽略未识别的参数：{raw}");
                    break;
            }
        }

        string exeDir = AppLayout.ExeDir;
        string exePath = AppLayout.ExePath;
        string configPath = AppLayout.ConfigPath;

        // 单实例：拿到互斥体的那个才是主进程，其他只负责把主窗口叫出来。
        // 句柄必须存进静态字段：主进程停在 Application.Run 里再也不会碰这个局部变量，
        // GC 一回收，终结器就把内核句柄释放了，第二个实例会误判成自己是第一个（wine 下实测到）。
        InstanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            Console.WriteLine("s3fs-win 已在运行：唤起它的主窗口后退出。");
            WakeExistingInstance();
            InstanceMutex.Dispose();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var startupLog = new List<string>();
        AppConfig cfg = AppConfig.Load(configPath, startupLog.Add);
        if (!File.Exists(configPath))
        {
            // 首次运行就落一份 config.json，用户不用先点一次「保存配置」才能改。
            cfg.Save(configPath, startupLog.Add);
        }

        if (min)
        {
            cfg.StartInTray = true;
            startupLog.Add("命令行 --min：启动后只进托盘");
        }

        try
        {
            WakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName, out _);
        }
        catch (Exception ex)
        {
            startupLog.Add($"[!] 唤醒事件创建失败（重复启动只会看不到唤起效果）：{ex.Message}");
        }

        var context = new ApplicationContext();
        var form = new MainForm(context, cfg, exeDir, exePath, configPath);
        try
        {
            form.LogStartupMessages(startupLog);
            form.Prepare();
            if (WakeEvent is not null)
            {
                StartWakeListener(WakeEvent, form);
            }

            if (!cfg.StartInTray)
            {
                form.ShowFromTray();
            }

            form.MaybeAutoMount();
            Application.Run(context);
        }
        finally
        {
            WakeEvent?.Dispose();
            WakeEvent = null;
            InstanceMutex?.Dispose();
            InstanceMutex = null;
            form.Dispose();
        }
    }

    /// <summary>
    /// 诊断用：报告命名互斥体是否已被其他进程持有。设环境变量 S3FS_PROBE_HOLD=秒数 可以让它占住不放，
    /// 方便在 wine 里跑两个进程对照（Windows 上用不上，纯粹是给交叉验证用的）。
    /// </summary>
    private static void ProbeInstance()
    {
        // 用跟正式路径一样的静态字段，验的是「长时间持有」下的真实行为。
        InstanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        string hold = Environment.GetEnvironmentVariable("S3FS_PROBE_HOLD") ?? "0";
        Console.WriteLine($"PROBE createdNew={createdNew} hold={hold} pid={Environment.ProcessId} name={MutexName}");
        if (createdNew && int.TryParse(hold, out int seconds) && seconds > 0)
        {
            Thread.Sleep(seconds * 1000);
        }

        InstanceMutex.Dispose();
        InstanceMutex = null;
    }

    /// <summary>第二个实例走的路：点亮唤醒事件，让已有实例把窗口显示出来。</summary>
    private static void WakeExistingInstance()
    {
        try
        {
            using var wake = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName, out bool created);
            if (!created)
            {
                wake.Set();
                // 给对方一点时间处理，否则进程一退，管道/事件可能还没被看到。
                Thread.Sleep(200);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] 唤醒已有实例失败：{ex.Message}");
        }
    }

    private static void StartWakeListener(EventWaitHandle wake, MainForm form)
    {
        var thread = new Thread(() =>
        {
            while (!form.IsDisposed)
            {
                bool signaled;
                try
                {
                    signaled = wake.WaitOne(300);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (!signaled)
                {
                    continue;
                }

                try
                {
                    form.BeginInvoke(new Action(() =>
                    {
                        form.Log("收到重复启动的请求：把主窗口显示出来");
                        form.ShowFromTray();
                    }));
                }
                catch (Exception)
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "s3fs-win-wake",
        };

        thread.Start();
    }

    /// <summary>--print-args：把最终命令行同时打到 stdout 和 exe 旁边的 s3fs-win.args.txt。</summary>
    private static void PrintArgs()
    {
        string exeDir = AppLayout.ExeDir;
        var sb = new StringBuilder();
        var startupLog = new List<string>();
        AppConfig cfg = AppConfig.Load(AppLayout.ConfigPath, startupLog.Add);
        MountPlan plan = MountPlanBuilder.Build(cfg, exeDir);

        sb.AppendLine($"[exe] {AppLayout.ExePath}");
        foreach (string message in startupLog)
        {
            sb.AppendLine($"[log] {message}");
        }

        foreach (string warning in plan.Warnings)
        {
            sb.AppendLine($"[warn] {warning}");
        }

        foreach (string error in plan.Errors)
        {
            sb.AppendLine($"[error] {error}");
        }

        sb.AppendLine(plan.RenderCommandLine());
        sb.AppendLine();
        sb.AppendLine("ProcessStartInfo.ArgumentList（逐项，不手拼字符串）：");
        foreach (string arg in plan.ToArgumentList())
        {
            sb.AppendLine("  " + arg);
        }

        string text = sb.ToString();
        Console.Write(text);

        string outPath = Path.Combine(exeDir, "s3fs-win.args.txt");
        try
        {
            File.WriteAllText(outPath, text, AppConfig.Utf8NoBom);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] 写 {outPath} 失败：{ex.Message}");
        }
    }
}
