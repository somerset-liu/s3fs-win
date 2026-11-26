namespace S3fsWin;

/// <summary>
/// 从 s3fs 的输出行里认致命错误。光靠「进程活着 N 秒」判定挂载成功太弱：
/// WinFsp 没装、凭据错、tmpdir 无效这几种情况都各有特征行，直接指出来比猜强。
/// </summary>
internal static class FatalLine
{
    private readonly record struct Rule(string Pattern, string Hint);

    private static readonly Rule[] Rules =
    [
        new("FSD not found", "WinFsp 未安装或驱动未加载：运行包内 winfsp-2.2.26323.msi（需管理员），装完不用重启"),
        new("Cannot create WinFsp-FUSE file system", "WinFsp 文件系统创建失败，通常是 WinFsp 未装或版本不匹配"),
        new("not enough disk space", "临时目录无效：-o tmpdir= 要给已存在的绝对路径"),
        new("unknown third option", "s3fs 不认某个 -o 选项（未知选项会直接退出，检查 options 里的拼写）"),
        new("invalid argument", "参数无效"),
        new("No such file or directory", "路径不存在：检查凭据文件 / mime.types / 临时目录（-o 里的路径要用正斜杠）"),
        new("could not determine", "读不到凭据：检查 passwd_file 内容是否为 桶名:AccessKey:SecretKey"),
        new("password file", "凭据文件有问题"),
        new("credentials", "凭据被服务端拒绝：检查 Access Key / Secret Key"),
        new("permission", "权限不足：检查 Access Key 对应的策略"),
        new("signature", "签名校验失败：Access Key / Secret Key 或 region 不匹配"),
        new("Unable to rename", "无法重命名临时文件（缓存/临时目录可能不可写）"),
        new("could not init curl", "curl 初始化失败"),
        new("curlCode: 7", "连不上端点：检查 URL、端口与防火墙"),
        new("curlCode: 6", "端点域名解析失败"),
        new("Failed to mount", "挂载失败"),
        new("fuse: ", "FUSE/WinFsp 层报错"),
    ];

    /// <summary>命中则返回可读的中文提示，否则返回 null。</summary>
    public static string? Detect(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        foreach (Rule rule in Rules)
        {
            if (line.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase))
            {
                return rule.Hint;
            }
        }

        return null;
    }
}
