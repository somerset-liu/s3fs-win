using System.Text;

namespace S3fsWin;

/// <summary>
/// 凭据文件（passwd-s3fs）。内容一行：桶名:AccessKey:SecretKey，也接受 AccessKey:SecretKey。
/// GUI 只在 Access Key 与 Secret Key 都非空时代写，用 UTF-8 无 BOM、LF 换行。
/// </summary>
internal static class CredentialFile
{
    public readonly record struct Result(bool Ok, string Message, bool Written);

    /// <summary>
    /// 需要时代写凭据文件，并检查文件是否可用。失败只返回消息，不抛异常。
    /// </summary>
    public static Result Ensure(string pathNative, string bucket, string accessKey, string secretAccessKey)
    {
        bool hasKeys = accessKey.Trim().Length > 0 && secretAccessKey.Trim().Length > 0;

        if (hasKeys)
        {
            string body = bucket.Trim().Length > 0
                ? $"{bucket.Trim()}:{accessKey.Trim()}:{secretAccessKey.Trim()}"
                : $"{accessKey.Trim()}:{secretAccessKey.Trim()}";

            try
            {
                if (!File.Exists(pathNative) || !string.Equals(ReadBody(pathNative), body, StringComparison.Ordinal))
                {
                    File.WriteAllText(pathNative, body + "\n", AppConfig.Utf8NoBom);
                    return new Result(true, $"已写入凭据文件：{pathNative}", true);
                }

                return new Result(true, $"凭据文件未变化：{pathNative}", false);
            }
            catch (Exception ex)
            {
                return new Result(false, $"写入凭据文件失败：{pathNative}（{ex.Message}）", false);
            }
        }

        // 没填密钥就不代写，只检查文件在不在（用户可能手改了 passwd-s3fs）。
        if (!File.Exists(pathNative))
        {
            return new Result(false, $"凭据文件不存在，且 Access Key / Secret Key 为空：{pathNative}", false);
        }

        return new Result(true, $"Access Key / Secret Key 为空，沿用凭据文件内容：{pathNative}", false);
    }

    /// <summary>读第一行非空内容并去掉行尾 \r，用于跟将要写入的内容比较。</summary>
    private static string ReadBody(string path)
    {
        foreach (string line in File.ReadLines(path, Encoding.UTF8))
        {
            string s = line.Trim();
            if (s.Length > 0)
            {
                return s;
            }
        }

        return string.Empty;
    }
}
