using Microsoft.Win32;

namespace S3fsWin;

/// <summary>
/// 开机自启：HKCU\Software\Microsoft\Windows\CurrentVersion\Run 下的字符串值 s3fs-win，
/// 内容是 “&lt;exe 绝对路径&gt;” --min。写的是 HKCU，所以只要当前用户能登录就能改，不要求管理员。
/// 语义上这是「登录时」启动，不是「开机时」启动。
/// </summary>
internal static class AutoStart
{
    public const string ValueName = "s3fs-win";
    public const string Arguments = "--min";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public readonly record struct Result(bool Ok, string Message);

    public static string DesiredCommand(string exePath) => $"\"{exePath}\" {Arguments}";

    /// <summary>读当前注册值；读不到或出错都返回 null（当作未启用）。</summary>
    public static string? Read(string valueName = ValueName)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static Result Set(string exePath, bool enable, string valueName = ValueName)
    {
        string command = DesiredCommand(exePath);
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException($"打不开注册表项 HKCU\\{RunKeyPath}");

            if (enable)
            {
                key.SetValue(valueName, command, RegistryValueKind.String);
                return new Result(true, $"已写入开机自启：{valueName} = {command}");
            }

            key.DeleteValue(valueName, throwOnMissingValue: false);
            return new Result(true, "已删除开机自启项");
        }
        catch (Exception ex)
        {
            return new Result(false, $"开机自启设置失败（{ex.GetType().Name}）：{ex.Message}");
        }
    }
}
