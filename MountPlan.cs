using System.Text;

namespace S3fsWin;

/// <summary>一次挂载需要的全部信息：可执行文件、位置参数、去重后的 -o 列表。</summary>
internal sealed class MountPlan
{
    public string S3fsExePath { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;

    /// <summary>位置参数：桶名（原样，不做斜杠转换）。</summary>
    public string Bucket { get; init; } = string.Empty;

    /// <summary>位置参数：挂载点（原样，如 S:）。</summary>
    public string MountPoint { get; init; } = string.Empty;

    /// <summary>已按 GUI 默认值补好、去重、路径转正斜杠的 -o 项，顺序即命令行里的顺序。</summary>
    public List<string> Options { get; } = [];

    /// <summary>临时目录（Windows 原生写法），挂载前用它 CreateDirectory。</summary>
    public string TmpDirNative { get; set; } = string.Empty;

    /// <summary>凭据文件（Windows 原生写法）。</summary>
    public string PasswdFileNative { get; set; } = string.Empty;

    /// <summary>mime 表（Windows 原生写法），只为启动前检查文件在不在。</summary>
    public string MimeFileNative { get; set; } = string.Empty;

    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];

    public bool HasErrors => Errors.Count > 0;

    /// <summary>逐项参数（不含 exe 自身），直接喂给 ProcessStartInfo.ArgumentList。</summary>
    public IReadOnlyList<string> ToArgumentList()
    {
        var args = new List<string> { Bucket, MountPoint };
        foreach (string option in Options)
        {
            args.Add("-o");
            args.Add(option);
        }

        args.Add("-f");
        return args;
    }

    /// <summary>渲染成可复制粘贴的命令行（只用于显示与日志；实际启动走 ArgumentList）。</summary>
    public string RenderCommandLine()
    {
        var sb = new StringBuilder();
        sb.Append(Quote(S3fsExePath));
        AppendArg(sb, Bucket);
        AppendArg(sb, MountPoint);
        foreach (string option in Options)
        {
            AppendArg(sb, "-o");
            AppendArg(sb, option);
        }

        AppendArg(sb, "-f");
        return sb.ToString();
    }

    private static void AppendArg(StringBuilder sb, string arg)
    {
        sb.Append(' ').Append(Quote(arg));
    }

    /// <summary>带空格的项加双引号，跟 .NET 对 ArgumentList 的转义规则一致。</summary>
    private static string Quote(string arg)
        => arg.Length == 0 || arg.Contains(' ') ? "\"" + arg + "\"" : arg;
}

/// <summary>
/// 按 config 组装挂载参数。硬约束：
/// - 桶名 / 挂载点是位置参数，不经 -o 解析，保持原样；
/// - 所有进 -o 的路径都过 <see cref="FsPath.ForOption"/>（正斜杠 + 绝对路径）；
/// - 每个选项名在整个命令行里只出现一次，用户在 options 里写的同名项优先；
/// - tmpdir 始终存在（s3fs 硬编码 /tmp 且不读 TMP/TEMP）；use_cache 只在填了缓存目录时才加；
/// - umask=000 始终存在（WinFsp 的 ACL 翻译保险项，见 Build 里的注释）；
/// - no_check_certificate 始终存在（服务端是 https + 自签证书时，s3fs 无 CAfile/capath）。
/// </summary>
internal static class MountPlanBuilder
{
    /// <summary>GUI 托管的选项名。用户在 options 里写同名项即可覆盖 GUI 的值。</summary>
    public static readonly string[] GuiManagedKeys =
    [
        "passwd_file", "url", "no_check_certificate", "mime", "tmpdir", "use_cache", "umask", "dbglevel",
        "use_path_request_style",
    ];

    public static MountPlan Build(AppConfig cfg, string exeDir)
    {
        var plan = new MountPlan
        {
            S3fsExePath = Path.Combine(exeDir, "s3fs.exe"),
            WorkingDirectory = exeDir,
            Bucket = cfg.Bucket.Trim(),
            MountPoint = NormalizeMountPoint(cfg.MountPoint),
        };

        Validate(cfg, plan);

        string passwd = FsPath.ForOption(
            string.IsNullOrWhiteSpace(cfg.PasswdFile) ? Path.Combine(exeDir, "passwd-s3fs") : cfg.PasswdFile, exeDir);
        string mime = FsPath.ForOption(
            string.IsNullOrWhiteSpace(cfg.MimeFile) ? Path.Combine(exeDir, "mime.types") : cfg.MimeFile, exeDir);
        string tmpDir = FsPath.ForOption(
            string.IsNullOrWhiteSpace(cfg.TmpDir) ? Path.Combine(exeDir, "tmp") : cfg.TmpDir, exeDir);
        string cacheDir = FsPath.ForOption(cfg.CacheDir, exeDir);

        plan.PasswdFileNative = FsPath.ForFileSystem(passwd);
        plan.TmpDirNative = FsPath.ForFileSystem(tmpDir);
        plan.MimeFileNative = FsPath.ForFileSystem(mime);

        // 先放 GUI 托管的默认值，再放用户写的 options：同名时后者覆盖前者（位置保持首次出现处）。
        var ordered = new List<string>();
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Add(string token)
        {
            string key = AppConfig.KeyOf(token);
            if (key.Length == 0)
            {
                return;
            }

            if (indexByKey.TryGetValue(key, out int at))
            {
                ordered[at] = token;
                return;
            }

            indexByKey[key] = ordered.Count;
            ordered.Add(token);
        }

        Add($"passwd_file={passwd}");
        Add($"url={cfg.Endpoint.Trim()}");
        // 服务端 9000 只收 https，且证书是自签的；s3fs 1.97 没有 CAfile/capath，只能跳过校验。
        // 用户在 options 里写同名项即覆盖（值相同，去重后仍只出现一次）。
        Add("no_check_certificate");
        if (mime.Length > 0)
        {
            Add($"mime={mime}");
        }

        // tmpdir 无条件出现。
        Add($"tmpdir={tmpDir}");
        if (cacheDir.Length > 0)
        {
            Add($"use_cache={cacheDir}");
        }

        // umask=000 无条件出现（用户在 options 里写 umask=... 即覆盖）。
        // 原因：WinFsp 要把 POSIX 的 st_mode/uid/gid 翻译成 Windows ACL，默认 umask 下
        // 根目录可能被判定成「拒绝访问」。真机上这条因果还没确认（也可能是挂载还没起来
        // 时的时序问题），所以它是「出问题先加这个」的保险项，不是已证实的修复。
        Add("umask=000");

        if (cfg.Debug)
        {
            Add("dbglevel=info");
        }

        foreach (string token in AppConfig.SplitOptions(cfg.Options))
        {
            Add(token);
        }

        // use_path_request_style 也算 GUI 托管项，但它的默认值就在 options 文本框里，
        // 用户删掉即表示要用虚拟主机风格，这里不再强行补回（同名项只会出现一次已由上面的去重保证）。

        plan.Options.AddRange(ordered);
        return plan;
    }

    private static void Validate(AppConfig cfg, MountPlan plan)
    {
        if (cfg.Bucket.Trim().Length == 0)
        {
            plan.Errors.Add("桶名不能为空");
        }

        if (cfg.MountPoint.Trim().Length == 0)
        {
            plan.Errors.Add("挂载点不能为空（盘符写 S: 或选一个已存在的空目录）");
        }

        if (cfg.Endpoint.Trim().Length == 0)
        {
            plan.Errors.Add("端点 URL 不能为空，例如 https://s3.example.com:9000");
        }

        if (cfg.AccessKeyId.Trim().Length == 0 || cfg.SecretAccessKey.Trim().Length == 0)
        {
            plan.Warnings.Add("Access Key 或 Secret Key 为空：不会重写凭据文件，将沿用文件里已有的内容");
        }
    }

    /// <summary>盘符统一成 S: 这种写法（去掉尾部的 \ 或 /）；目录路径原样返回。</summary>
    public static string NormalizeMountPoint(string? raw)
    {
        string s = (raw ?? string.Empty).Trim().Trim('"');
        if (s.Length == 2 && char.IsAsciiLetter(s[0]) && s[1] == ':')
        {
            return s;
        }

        if (s.Length == 3 && char.IsAsciiLetter(s[0]) && s[1] == ':' && (s[2] == '\\' || s[2] == '/'))
        {
            return s[..2];
        }

        return s;
    }
}
