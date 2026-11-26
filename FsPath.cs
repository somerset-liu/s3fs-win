namespace S3fsWin;

/// <summary>
/// 路径处理。核心是 <see cref="ForOption"/>：把路径转成能安全塞进 s3fs 的 -o 值的写法。
///
/// 两条已实测的硬性规则（详见包内 INSTALL.md）：
/// 1) -o 的值里反斜杠会被 s3fs 当转义符消耗掉，所以必须全部换成正斜杠；
///    例：-o passwd_file=C:\Soft\s3fs-win\passwd-s3fs 实际变成 stat(C:Softs3fs-winpasswd-s3fs)。
/// 2) -o tmpdir= 必须是绝对路径，相对路径在 mingw 下 statvfs 拿不到容量，
///    会误报 “There is not enough disk space ... already has 0.000 MB” 并退出。
/// </summary>
internal static class FsPath
{
    /// <summary>
    /// 转成 -o 选项用的路径：去引号 → 必要时按 <paramref name="baseDir"/> 绝对化 → 反斜杠换正斜杠 → 去尾斜杠。
    /// 传空串返回空串（由调用方决定用默认值还是干脆不传该选项）。
    /// </summary>
    public static string ForOption(string? raw, string? baseDir = null)
    {
        string s = Normalize((raw ?? string.Empty).Trim().Trim('"'), baseDir);
        if (s.Length == 0)
        {
            return string.Empty;
        }

        s = s.Replace('\\', '/');

        // 只剩盘符时补斜杠（C:/），否则去掉多余的尾斜杠。
        if (IsDriveRoot(s))
        {
            return s + "/";
        }

        return s.TrimEnd('/');
    }

    /// <summary>转成 Windows 原生路径（给 Directory/File/Process 用），顺手绝对化。</summary>
    public static string ForFileSystem(string? raw, string? baseDir = null)
    {
        string s = Normalize((raw ?? string.Empty).Trim().Trim('"'), baseDir);
        return s.Length == 0 ? string.Empty : s.Replace('/', '\\');
    }

    private static string Normalize(string s, string? baseDir)
    {
        if (s.Length == 0)
        {
            return string.Empty;
        }

        if (IsDriveRoot(s))
        {
            // 用户只写了 “C:” 这种驱动器相对写法，当成 “C:/” 处理。
            return s + "/";
        }

        if (IsAbsolute(s))
        {
            return s;
        }

        string root = string.IsNullOrEmpty(baseDir) ? AppContext.BaseDirectory : baseDir;
        return Path.GetFullPath(Path.Combine(root, s));
    }

    private static bool IsDriveRoot(string s)
        => s.Length == 2 && char.IsAsciiLetter(s[0]) && s[1] == ':';

    private static bool IsAbsolute(string s)
    {
        if (s.StartsWith("\\\\", StringComparison.Ordinal) || s.StartsWith('/'))
        {
            return true;
        }

        return s.Length >= 3
            && char.IsAsciiLetter(s[0])
            && s[1] == ':'
            && (s[2] == '\\' || s[2] == '/');
    }
}
