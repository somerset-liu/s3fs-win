using System.Diagnostics;
using System.Text;

namespace S3fsWin;

/// <summary>
/// 持有 s3fs.exe 子进程：以 -f 前台运行，由 GUI 负责结束它（结束进程即卸载）。
/// stdout/stderr 都按 UTF-8 读，逐行回调。
/// </summary>
internal sealed class S3fsRunner : IDisposable
{
    private const int KillWaitMs = 3000;

    private Process? _proc;

    /// <summary>s3fs 输出的一行（已去掉行尾换行）。</summary>
    public event Action<string>? LineReceived;

    /// <summary>进程退出，参数是退出码（拿不到时为 -1）。</summary>
    public event Action<int>? Exited;

    /// <summary>本次启动用的命令行（仅用于显示）。</summary>
    public string? CommandLine { get; private set; }

    public bool IsRunning
    {
        get
        {
            Process? p = _proc;
            if (p is null)
            {
                return false;
            }

            try
            {
                return !p.HasExited;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                return false;
            }
        }
    }

    /// <summary>按 <paramref name="plan"/> 启动子进程。失败只返回 false + 原因，不抛异常。</summary>
    public bool Start(MountPlan plan, out string? error)
    {
        error = null;
        CommandLine = plan.RenderCommandLine();

        var psi = new ProcessStartInfo
        {
            FileName = plan.S3fsExePath,
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };

        // 只用 ArgumentList 逐项加，绝不手拼 Arguments 字符串：路径里有空格也不会断。
        foreach (string arg in plan.ToArgumentList())
        {
            psi.ArgumentList.Add(arg);
        }

        Process? proc = null;
        try
        {
            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    LineReceived?.Invoke(e.Data);
                }
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    LineReceived?.Invoke(e.Data);
                }
            };
            proc.Exited += (_, _) => Exited?.Invoke(SafeExitCode(proc));

            if (!proc.Start())
            {
                error = "Process.Start 返回 false";
                proc.Dispose();
                return false;
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _proc = proc;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            proc?.Dispose();
            return false;
        }
    }

    /// <summary>结束整个进程树并等待退出，返回给用户看的一句话。</summary>
    public string Stop()
    {
        Process? p = Interlocked.Exchange(ref _proc, null);
        if (p is null)
        {
            return "没有由本程序启动的 s3fs 进程（视为已卸载）";
        }

        try
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // 已经在退出中，忽略。
            }

            bool gone = false;
            try
            {
                gone = p.WaitForExit(KillWaitMs);
            }
            catch (InvalidOperationException)
            {
                gone = true;
            }

            if (!gone)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // 尽力而为。
                }

                try
                {
                    gone = p.WaitForExit(1000);
                }
                catch (InvalidOperationException)
                {
                    gone = true;
                }
            }

            return gone ? "已结束 s3fs 进程，挂载点卸载" : "s3fs 进程在 3 秒内没退干净，已再次强制结束";
        }
        catch (Exception ex)
        {
            return $"结束 s3fs 进程失败：{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            p.Dispose();
        }
    }

    public void Dispose()
    {
        Process? p = Interlocked.Exchange(ref _proc, null);
        try
        {
            if (p is { HasExited: false })
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(1000);
            }
        }
        catch (Exception)
        {
            // 退出路径上不再抛。
        }

        p?.Dispose();
    }

    private static int SafeExitCode(Process p)
    {
        try
        {
            return p.ExitCode;
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
