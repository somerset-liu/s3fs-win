using System.Text;

namespace S3fsWin;

/// <summary>
/// 无窗口的参数拼装自检（s3fs-win.exe --selftest）。没有引第三方测试框架，
/// 用一条命令就能在 Linux/wine 上验掉那些「旧版踩过坑」的硬约束。
/// 全部通过返回 true（进程退出码 0）。
/// </summary>
internal static class SelfTest
{
    private const string FakeExeDir = @"C:\Soft\s3fs-win";

    private static readonly List<string> Failures = [];
    private static readonly StringBuilder Output = new();

    public static bool Run()
    {
        Output.AppendLine("默认配置渲染出的命令行：");
        Output.AppendLine("  " + Build(new AppConfig()).RenderCommandLine());
        Output.AppendLine();

        DefaultConfigRendersForwardSlashAbsolutePaths();
        EveryOptionAppearsExactlyOnce();
        NoCheckCertificateIsAlwaysPresentOnce();
        UmaskIsAlwaysPresentOnce();
        UserOptionOverridesGuiDefault();
        CacheDirOnlyWhenFilled();
        DebugAddsDbglevelOnce();
        LegacyOptionIsDroppedOnLoad();
        RelativeTmpDirBecomesAbsolute();
        BackslashTmpDirBecomesForwardSlash();
        ArgumentListShapeIsCorrect();
        MountPointKeepsDriveLetter();
        EmptyRequiredFieldsAreErrors();
        PositionalArgsAreNotRewritten();
        AutoStartRoundTripsThroughRegistry();

        string report = Output.ToString() + (Failures.Count == 0
            ? "SELFTEST: 全部通过"
            : $"SELFTEST: {Failures.Count} 项失败");
        Console.Write(report + "\n");
        WriteSidecar(report);
        return Failures.Count == 0;
    }

    /// <summary>顺带落一份报告文件，方便在 wine 下没接管 stdout 时也能看结果。</summary>
    private static void WriteSidecar(string report)
    {
        try
        {
            File.WriteAllText(Path.Combine(AppLayout.ExeDir, "s3fs-win.selftest.txt"), report + "\n", AppConfig.Utf8NoBom);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] 写自检报告失败：{ex.Message}");
        }
    }

    private static void DefaultConfigRendersForwardSlashAbsolutePaths()
    {
        MountPlan plan = Build(new AppConfig());
        string line = plan.RenderCommandLine();

        Check("默认配置：命令行里 -o 的值不含反斜杠", !HasBackslashInOptions(plan), line);
        Check("默认配置：tmpdir 是绝对路径", Get(plan, "tmpdir").Length > 0 && Get(plan, "tmpdir").Contains(':'), Get(plan, "tmpdir"));
        Check("默认配置：tmpdir 指向 exe 目录下的 tmp",
            Get(plan, "tmpdir") == "C:/Soft/s3fs-win/tmp", Get(plan, "tmpdir"));
        Check("默认配置：passwd_file 指向 exe 目录下的 passwd-s3fs",
            Get(plan, "passwd_file") == "C:/Soft/s3fs-win/passwd-s3fs", Get(plan, "passwd_file"));
        Check("默认配置：mime 指向 exe 目录下的 mime.types",
            Get(plan, "mime") == "C:/Soft/s3fs-win/mime.types", Get(plan, "mime"));
        Check("默认配置：url 取端点", Get(plan, "url") == "https://s3.example.com:9000", Get(plan, "url"));
        Check("默认配置：含默认 options 四项",
            plan.Options.Contains("use_path_request_style") && plan.Options.Contains("listobjectsv2")
            && plan.Options.Contains("retries=5") && plan.Options.Contains("connect_timeout=30"),
            string.Join(",", plan.Options));
        Check("默认配置：不含 readdir_optimize", !plan.Options.Any(o => o.StartsWith("readdir_optimize", StringComparison.Ordinal)));
        Check("默认配置：不带 dbglevel", Get(plan, "dbglevel").Length == 0);
        Check("默认配置：不带 use_cache", Get(plan, "use_cache").Length == 0);
        Check("默认配置：命令行以 -f 结尾", line.EndsWith(" -f", StringComparison.Ordinal), line);
    }

    private static void EveryOptionAppearsExactlyOnce()
    {
        var cfg = new AppConfig
        {
            Options = "use_path_request_style,listobjectsv2,url=http://override:9000,tmpdir=C:/mine/tmp,mime=C:/mine/mime.types,passwd_file=C:/mine/passwd",
        };
        MountPlan plan = Build(cfg);
        var dup = plan.Options.GroupBy(o => AppConfig.KeyOf(o), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Check("同名选项只出现一次", dup.Count == 0, string.Join(",", dup));
    }

    /// <summary>
    /// 服务端 9000 只收 https 且证书自签，s3fs 1.97 又没有 CAfile/capath，
    /// 所以 no_check_certificate 必须始终在命令行上，且只能出现一次。
    /// </summary>
    private static void NoCheckCertificateIsAlwaysPresentOnce()
    {
        MountPlan plan = Build(new AppConfig());
        string line = plan.RenderCommandLine();
        Check("默认配置：命令行含 url=https://", line.Contains("url=https://", StringComparison.Ordinal), line);
        Check("默认配置：带 no_check_certificate",
            plan.Options.Any(o => o.Equals("no_check_certificate", StringComparison.Ordinal)),
            string.Join(",", plan.Options));
        Check("默认配置：no_check_certificate 只出现一次", Count(plan, "no_check_certificate") == 1,
            string.Join(",", plan.Options));
        Check("默认配置：整条命令行无重复选项名",
            DuplicatedKeys(plan).Count == 0, string.Join(",", DuplicatedKeys(plan)));

        MountPlan user = Build(new AppConfig { Options = "use_path_request_style,no_check_certificate,retries=5" });
        Check("用户 options 里自己写了 no_check_certificate 也只出现一次",
            Count(user, "no_check_certificate") == 1, string.Join(",", user.Options));

        MountPlan off = Build(new AppConfig { Options = "no_check_certificate=" });
        Check("用户用空值覆盖后仍只有一个 no_check_certificate",
            Count(off, "no_check_certificate") == 1 && Get(off, "no_check_certificate").Length == 0,
            string.Join(",", off.Options));

        // 老 config.json 里存的是 http://，读到就照用，不静默改写。
        string dir = Path.Combine(Path.GetTempPath(), "s3fs-win-selftest-ep-" + Environment.ProcessId);
        string path = Path.Combine(dir, "config.json");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, """
                { "bucket": "mybucket", "endpoint": "http://s3.example.com:9000" }
                """, AppConfig.Utf8NoBom);
            AppConfig legacy = AppConfig.Load(path, null);
            Check("老配置的 http 端点原样保留（不自动改 https）",
                legacy.Endpoint == "http://s3.example.com:9000", legacy.Endpoint);
            Check("老配置渲染出的 url 就是 http（用户自己负责改成 https）",
                Get(Build(legacy), "url") == "http://s3.example.com:9000", Get(Build(legacy), "url"));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录删不掉不影响结论。
            }
        }
    }

    /// <summary>
    /// WinFsp 要把 POSIX 的 st_mode/uid/gid 翻译成 Windows ACL，默认 umask 下根目录可能被
    /// 判成「拒绝访问」，所以命令行上始终带 umask=000，且只能出现一次。
    /// 注意：真机上「拒绝访问消失」与 umask=000 之间的因果还没确认，这条只是保险项。
    /// </summary>
    private static void UmaskIsAlwaysPresentOnce()
    {
        MountPlan plan = Build(new AppConfig());
        Check("默认配置：带 umask=000", Get(plan, "umask") == "000", Get(plan, "umask"));
        Check("默认配置：umask 只出现一次", Count(plan, "umask") == 1, string.Join(",", plan.Options));
        Check("默认配置：整条命令行无重复选项名（含 umask）",
            DuplicatedKeys(plan).Count == 0, string.Join(",", DuplicatedKeys(plan)));

        MountPlan debug = Build(new AppConfig { Debug = true, CacheDir = @"C:\cache" });
        Check("调试与缓存目录都开时 umask 仍是 000 且只出现一次",
            Get(debug, "umask") == "000" && Count(debug, "umask") == 1, string.Join(",", debug.Options));

        MountPlan user = Build(new AppConfig { Options = "use_path_request_style,umask=022" });
        Check("用户 options 里的 umask 覆盖 GUI 默认", Get(user, "umask") == "022", Get(user, "umask"));
        Check("覆盖后 umask 只出现一次", Count(user, "umask") == 1, string.Join(",", user.Options));
    }

    private static void UserOptionOverridesGuiDefault()
    {
        MountPlan plan = Build(new AppConfig { Options = "url=http://override:9000,use_path_request_style" });
        Check("用户 options 里的 url 覆盖 GUI 默认", Get(plan, "url") == "http://override:9000", Get(plan, "url"));
        Check("覆盖后 url 只出现一次",
            plan.Options.Count(o => AppConfig.KeyOf(o).Equals("url", StringComparison.OrdinalIgnoreCase)) == 1,
            string.Join(",", plan.Options));

        MountPlan cachePlan = Build(new AppConfig { CacheDir = @"C:\cache", Options = "use_cache=D:/other" });
        Check("用户 options 里的 use_cache 覆盖缓存目录", Get(cachePlan, "use_cache") == "D:/other", Get(cachePlan, "use_cache"));
    }

    private static void CacheDirOnlyWhenFilled()
    {
        Check("缓存目录留空时不带 use_cache", Get(Build(new AppConfig { CacheDir = "  " }), "use_cache").Length == 0);

        MountPlan plan = Build(new AppConfig { CacheDir = @"C:\Soft\cache" });
        Check("填了缓存目录则带正斜杠的 use_cache", Get(plan, "use_cache") == "C:/Soft/cache", Get(plan, "use_cache"));
    }

    private static void DebugAddsDbglevelOnce()
    {
        Check("勾选调试 → dbglevel=info", Get(Build(new AppConfig { Debug = true }), "dbglevel") == "info",
            Get(Build(new AppConfig { Debug = true }), "dbglevel"));

        MountPlan plan = Build(new AppConfig { Debug = true, Options = "dbglevel=debug,use_path_request_style" });
        Check("用户写了 dbglevel 就只用用户的，且不重复",
            Get(plan, "dbglevel") == "debug"
            && plan.Options.Count(o => AppConfig.KeyOf(o).Equals("dbglevel", StringComparison.OrdinalIgnoreCase)) == 1,
            string.Join(",", plan.Options));
    }

    private static void LegacyOptionIsDroppedOnLoad()
    {
        string dir = Path.Combine(Path.GetTempPath(), "s3fs-win-selftest-" + Environment.ProcessId);
        string path = Path.Combine(dir, "config.json");
        try
        {
            Directory.CreateDirectory(dir);
            // 旧配置：带 readdir_optimize、少数字段、还有多余字段。
            File.WriteAllText(path, """
                { "bucket": "b1", "options": "use_path_request_style,readdir_optimize,retries=5", "unknownField": 1 }
                """, AppConfig.Utf8NoBom);

            var log = new List<string>();
            AppConfig cfg = AppConfig.Load(path, log.Add);
            Check("读旧配置剔除 readdir_optimize",
                cfg.Options == "use_path_request_style,retries=5", cfg.Options);
            Check("读旧配置：缺失字段取默认值", cfg.Endpoint == "https://s3.example.com:9000", cfg.Endpoint);
            Check("读旧配置：已给的字段照收", cfg.Bucket == "b1", cfg.Bucket);
            Check("读旧配置：有剔除提示", log.Count > 0, string.Join(" | ", log));

            File.WriteAllText(path, "{ this is not json", AppConfig.Utf8NoBom);
            Check("config.json 语法坏了也不抛，退回默认值",
                AppConfig.Load(path, null).MountPoint == "S:");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录删不掉不影响结论。
            }
        }
    }

    private static void RelativeTmpDirBecomesAbsolute()
    {
        MountPlan plan = Build(new AppConfig { TmpDir = "tmp" });
        string tmp = Get(plan, "tmpdir");
        Check("相对 tmpdir 绝对化成 exe 目录下的 tmp", tmp == "C:/Soft/s3fs-win/tmp", tmp);
    }

    private static void BackslashTmpDirBecomesForwardSlash()
    {
        MountPlan plan = Build(new AppConfig
        {
            TmpDir = @"C:\Soft\s3fs-win\tmp",
            PasswdFile = @"C:\Soft\s3fs-win\passwd-s3fs",
            MimeFile = @"C:\Soft\s3fs-win\mime.types",
        });
        Check("反斜杠 tmpdir 转正斜杠", Get(plan, "tmpdir") == "C:/Soft/s3fs-win/tmp", Get(plan, "tmpdir"));
        Check("反斜杠 passwd_file 转正斜杠", Get(plan, "passwd_file") == "C:/Soft/s3fs-win/passwd-s3fs", Get(plan, "passwd_file"));
        Check("反斜杠 mime 转正斜杠", Get(plan, "mime") == "C:/Soft/s3fs-win/mime.types", Get(plan, "mime"));
        Check("尾斜杠会被去掉",
            Get(Build(new AppConfig { TmpDir = @"C:\Soft\tmp\" }), "tmpdir") == "C:/Soft/tmp",
            Get(Build(new AppConfig { TmpDir = @"C:\Soft\tmp\" }), "tmpdir"));
    }

    private static void ArgumentListShapeIsCorrect()
    {
        MountPlan plan = Build(new AppConfig { CacheDir = @"C:\cache", Debug = true });
        IReadOnlyList<string> args = plan.ToArgumentList();
        int dashO = args.Count(a => a == "-o");
        int tail = args.Count - 1;

        Check("参数列表末尾是 -f", args[tail] == "-f");
        Check("-o 的个数等于选项个数", dashO == plan.Options.Count, $"{dashO} vs {plan.Options.Count}");
        Check("-f 只出现一次", args.Count(a => a == "-f") == 1);
        Check("位置参数在最前面（桶名、挂载点）",
            args[0] == plan.Bucket && args[1] == plan.MountPoint, $"{args[0]} {args[1]}");
        bool paired = true;
        for (int i = 2; i < tail; i += 2)
        {
            if (args[i] != "-o" || AppConfig.KeyOf(args[i + 1]).Length == 0)
            {
                paired = false;
                break;
            }
        }

        Check("-o 后面紧跟一个选项项", paired);
    }

    private static void MountPointKeepsDriveLetter()
    {
        Check("挂载点 S:\\ 归一成 S:", MountPlanBuilder.NormalizeMountPoint(@"S:\") == "S:");
        Check("挂载点 s:/ 归一成 s:", MountPlanBuilder.NormalizeMountPoint("s:/") == "s:");
        Check("目录型挂载点原样保留",
            MountPlanBuilder.NormalizeMountPoint(@"D:\mount\s3") == @"D:\mount\s3",
            MountPlanBuilder.NormalizeMountPoint(@"D:\mount\s3"));
    }

    private static void EmptyRequiredFieldsAreErrors()
    {
        MountPlan plan = Build(new AppConfig { Bucket = "  ", Endpoint = "", MountPoint = "" });
        Check("空桶名/端点/挂载点报错误", plan.Errors.Count == 3, string.Join(" | ", plan.Errors));
        Check("空 AK/SK 只是警告不是错误",
            Build(new AppConfig()).Warnings.Count == 1, string.Join(" | ", Build(new AppConfig()).Warnings));
    }

    private static void PositionalArgsAreNotRewritten()
    {
        MountPlan plan = Build(new AppConfig { Bucket = "my-bucket", MountPoint = @"S:\" });
        Check("桶名原样传", plan.Bucket == "my-bucket", plan.Bucket);
        Check("挂载点保持盘符写法（不转正斜杠、不加斜杠）", plan.MountPoint == "S:", plan.MountPoint);
        string line = plan.RenderCommandLine();
        Check("命令行里位置参数不带引号", line.Contains(" my-bucket S: ", StringComparison.Ordinal), line);
    }

    /// <summary>开机自启走的是真注册表（HKCU），用探针值名跑一遍写→读→删，别碰用户已有的 s3fs-win 值。</summary>
    private static void AutoStartRoundTripsThroughRegistry()
    {
        const string probeValue = "s3fs-win-selftest";
        string fakeExe = FakeExeDir + @"\s3fs-win.exe";
        try
        {
            AutoStart.Result write = AutoStart.Set(fakeExe, true, probeValue);
            Check("开机自启：写 HKCU Run 不报错", write.Ok, write.Message);

            string? read = AutoStart.Read(probeValue);
            Check("开机自启：值内容就是 \"<exe>\" --min",
                string.Equals(read, AutoStart.DesiredCommand(fakeExe), StringComparison.Ordinal),
                read ?? "(读不到)");

            AutoStart.Set(fakeExe, false, probeValue);
            Check("开机自启：取消勾选即删除该值", AutoStart.Read(probeValue) is null, AutoStart.Read(probeValue) ?? "(已删除)");
        }
        catch (Exception ex)
        {
            Check("开机自启：注册表可用", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                AutoStart.Set(fakeExe, false, probeValue);
            }
            catch (Exception)
            {
                // 探针值没清掉也不影响结论。
            }
        }
    }

    // ---------- 工具 ----------

    private static MountPlan Build(AppConfig cfg) => MountPlanBuilder.Build(cfg, FakeExeDir);

    private static string Get(MountPlan plan, string key)
        => plan.Options
            .Where(o => AppConfig.KeyOf(o).Equals(key, StringComparison.OrdinalIgnoreCase))
            .Select(o => o[(AppConfig.KeyOf(o).Length + 1)..])
            .SingleOrDefault() ?? string.Empty;

    private static int Count(MountPlan plan, string key)
        => plan.Options.Count(o => AppConfig.KeyOf(o).Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>命令行里出现了两次以上的选项名（正常应恒为空）。</summary>
    private static List<string> DuplicatedKeys(MountPlan plan)
        => plan.Options
            .GroupBy(o => AppConfig.KeyOf(o), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

    private static bool HasBackslashInOptions(MountPlan plan)
        => plan.Options.Any(o => o.Contains('\\', StringComparison.Ordinal));

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (!ok)
        {
            Failures.Add(name + (detail is null ? string.Empty : " => " + detail));
        }

        Output.AppendLine($"  [{(ok ? "OK  " : "FAIL")}] {name}{(detail is null ? string.Empty : "  <" + detail + ">")}");
    }
}
