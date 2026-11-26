namespace S3fsWin;

/// <summary>
/// 路径基准。单文件发布在 self-extract 模式下 <c>AppContext.BaseDirectory</c> 可能指向临时解压目录，
/// 而 config.json、s3fs.exe、mime.types 都必须跟 exe 同目录，所以一律以 <c>Environment.ProcessPath</c> 为准。
/// </summary>
internal static class AppLayout
{
    public static string ExePath { get; } = ResolveExePath();

    public static string ExeDir { get; } = ResolveExeDir();

    public static string ConfigPath => Path.Combine(ExeDir, "config.json");

    private static string ResolveExePath()
    {
        string? path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                // 拿不到就退回 Application.ExecutablePath。
            }
        }

        try
        {
            return Path.GetFullPath(Application.ExecutablePath);
        }
        catch (Exception)
        {
            return AppContext.BaseDirectory;
        }
    }

    private static string ResolveExeDir()
    {
        string? dir = Path.GetDirectoryName(ExePath);
        if (!string.IsNullOrEmpty(dir))
        {
            return dir;
        }

        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
