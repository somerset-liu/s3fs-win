using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace S3fsWin;

/// <summary>
/// config.json 的数据模型。属性的初始值就是默认值：文件不存在、字段缺失、JSON 读不懂，
/// 都退回这些默认值，不抛异常（用 <see cref="Load"/>，它会顺带记日志）。
/// </summary>
internal sealed class AppConfig
{
    /// <summary>默认额外选项。注意：1.97 没有 readdir_optimize，写上去会当未知选项致命退出。</summary>
    public const string DefaultOptions = "use_path_request_style,listobjectsv2,retries=5,connect_timeout=30";

    /// <summary>读旧配置时要剔除的历史选项（1.97 已不存在）。</summary>
    private static readonly string[] RemovedOptions = ["readdir_optimize"];

    public string Bucket { get; set; } = "mybucket";
    public string Endpoint { get; set; } = "https://s3.example.com:9000";
    public string AccessKeyId { get; set; } = string.Empty;
    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>挂载点：盘符（S:）或已存在的目录路径。位置参数，不做斜杠转换。</summary>
    public string MountPoint { get; set; } = "S:";

    /// <summary>凭据文件（-o passwd_file=）。空 = &lt;exe 目录&gt;\passwd-s3fs。</summary>
    public string PasswdFile { get; set; } = string.Empty;

    /// <summary>MIME 表（-o mime=）。空 = &lt;exe 目录&gt;\mime.types。</summary>
    public string MimeFile { get; set; } = string.Empty;

    /// <summary>临时目录（-o tmpdir=）。空 = &lt;exe 目录&gt;\tmp。Windows 上必给。</summary>
    public string TmpDir { get; set; } = string.Empty;

    /// <summary>磁盘缓存目录（-o use_cache=）。空 = 不启用。</summary>
    public string CacheDir { get; set; } = string.Empty;

    /// <summary>额外 -o 选项，逗号分隔。同名项优先于 GUI 自动生成的值。</summary>
    public string Options { get; set; } = DefaultOptions;

    /// <summary>勾选后追加 -o dbglevel=info（不用 -d）。</summary>
    public bool Debug { get; set; }

    /// <summary>开机（准确说是当前用户登录）自启，对应 HKCU 的 Run 项。</summary>
    public bool AutoStart { get; set; }

    /// <summary>程序启动后立即挂载（配合 --min 做无人值守）。</summary>
    public bool AutoMountOnStart { get; set; }

    /// <summary>启动即进托盘不弹主窗口；命令行 --min 等价于把它临时置真。</summary>
    public bool StartInTray { get; set; }

    /// <summary>点窗口关闭按钮时最小化到托盘而不是退出。默认开。</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>
    /// 读取配置。文件缺失/损坏都用默认值，只在 <paramref name="log"/> 里记一句。
    /// 同时剔除旧版本遗留的、1.97 不认识的选项。
    /// </summary>
    public static AppConfig Load(string path, Action<string>? log = null)
    {
        AppConfig cfg;
        if (!File.Exists(path))
        {
            log?.Invoke($"未找到配置文件，使用默认值：{path}");
            cfg = new AppConfig();
        }
        else
        {
            try
            {
                // File.ReadAllText(path, Encoding.UTF8) 会自动跳过 BOM，读什么写什么都不会带 BOM。
                string json = File.ReadAllText(path, Encoding.UTF8);
                cfg = JsonSerializer.Deserialize<AppConfig>(json, ReadOptions) ?? new AppConfig();
            }
            catch (Exception ex)
            {
                log?.Invoke($"读取 config.json 失败，改用默认值：{ex.Message}");
                cfg = new AppConfig();
            }
        }

        PruneLegacyOptions(cfg, log);
        return cfg;
    }

    /// <summary>写出 config.json：camelCase、缩进、UTF-8 无 BOM。</summary>
    public void Save(string path, Action<string>? log = null)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(this, WriteOptions), Utf8NoBom);
        }
        catch (Exception ex)
        {
            log?.Invoke($"写入 config.json 失败：{ex.Message}");
        }
    }

    private static void PruneLegacyOptions(AppConfig cfg, Action<string>? log)
    {
        string[] raw = SplitOptions(cfg.Options);
        var kept = new List<string>(raw.Length);
        var dropped = new List<string>();

        foreach (string token in raw)
        {
            bool legacy = false;
            foreach (string dead in RemovedOptions)
            {
                if (KeyOf(token).Equals(dead, StringComparison.OrdinalIgnoreCase))
                {
                    legacy = true;
                    break;
                }
            }

            if (legacy)
            {
                dropped.Add(token);
            }
            else
            {
                kept.Add(token);
            }
        }

        if (dropped.Count > 0)
        {
            cfg.Options = string.Join(",", kept);
            log?.Invoke($"已从 options 中剔除 s3fs 1.97 不支持的选项：{string.Join(",", dropped)}");
        }
    }

    /// <summary>逗号分隔（容忍中文逗号与空格）拆成 -o 单项；不做去重，去重在 MountPlanBuilder 里做。</summary>
    public static string[] SplitOptions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        string[] parts = raw.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = StripOptionPrefix(parts[i]);
        }

        return parts;
    }

    /// <summary>
    /// 容忍用户在 options 文本框里把 -o 一起写进来。GUI 会给每一项自己补 -o，
    /// 所以 "-o allow_other" 传下去会变成 -o "-o allow_other"：Linux 侧 libfuse 报
    /// unknown option，WinFsp 的 FUSE 层是静默忽略（真机实测：加了等于没加）。
    /// 支持 "-o x" / "-ox" / "-o=x" / "--o x"，不带前导 - 的项原样返回。
    /// </summary>
    private static string StripOptionPrefix(string token)
    {
        int i = 0;
        while (i < 2 && i < token.Length && '-' == token[i])
        {
            i++;
        }

        if (0 == i || i >= token.Length || 'o' != token[i])
        {
            return token;
        }

        i++;
        if (i < token.Length && '=' == token[i])
        {
            i++;
        }

        string rest = token[i..].Trim();
        return rest.Length > 0 ? rest : token;
    }

    /// <summary>取选项名：第一个 = 之前的部分（无 = 时就是整项）。</summary>
    public static string KeyOf(string token)
    {
        int eq = token.IndexOf('=');
        return eq < 0 ? token.Trim() : token[..eq].Trim();
    }

    public static readonly UTF8Encoding Utf8NoBom = new(false);

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 不转义非 ASCII：路径里有中文时 config.json 保持可读，也别留 \uXXXX。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
