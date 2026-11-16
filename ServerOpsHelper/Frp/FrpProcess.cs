using System.IO;
using System.Diagnostics;
using System.Text;
using Process = System.Diagnostics.Process;

namespace ServerOpsHelper.Frp;

/// <summary>
/// frpc / frps 子进程管理器。
/// 启动时把 stdout/stderr 重定向到 <see cref="FrpState"/> 日志队列，停止时按整棵进程树 Kill。
/// </summary>
public class FrpProcess
{
    private readonly FrpState _state;
    private readonly FrpEnvironment _env;
    private readonly object _lock = new();

    // _process 仅在 _lock 保护下读写；退出事件回调里用 Interlocked.Exchange 把
    // 局部引用原子换出，避免 Stop() 与 OnExited() 双重清理。
    private Process? _process;

    public event Action? LogUpdated;
    public event Action? StatusChanged;

    public FrpProcess(FrpState state, FrpEnvironment env)
    {
        _state = state;
        _env = env;
    }

    public bool Start(string mode)
    {
        lock (_lock)
        {
            if (IsProcessAlive(_process)) return false;

            if (!_env.Validate(out var message))
            {
                _state.AppendLog($"[错误] {message}", "err");
                LogUpdated?.Invoke();
                return false;
            }

            var exePath = _env.GetExecutablePath(mode);
            if (!File.Exists(exePath))
            {
                _state.AppendLog($"[错误] 未找到可执行文件: {exePath}", "err");
                LogUpdated?.Invoke();
                return false;
            }

            var configPath = _env.GetConfigPath(mode);
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"-c \"{configPath}\"",
                WorkingDirectory = _env.FrpDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += OnOutput;
            proc.ErrorDataReceived += OnError;
            proc.Exited += (_, _) => OnExited(proc);

            try
            {
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _state.AppendLog($"[错误] 启动失败: {ex.Message}", "err");
                proc.Dispose();
                LogUpdated?.Invoke();
                return false;
            }

            _process = proc;
            _state.CurrentMode = mode;
            _state.IsRunning = true;
            _state.ProcessId = proc.Id;
            _state.StartedAt = DateTime.Now;
            _state.AppendLog($"[系统] 已启动 {mode} (PID {proc.Id})", "sys");
            StatusChanged?.Invoke();
            LogUpdated?.Invoke();
            return true;
        }
    }

    public bool Stop()
    {
        // 取出当前进程引用并清空字段，避免 OnExited 与本方法重复清理
        var proc = Interlocked.Exchange(ref _process, null);
        if (proc == null) return false;

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                // 最多等 5 秒；超时也不再阻塞 —— OnExited 后续会兜底清理状态
                try { proc.WaitForExit(5000); } catch { }
            }
        }
        catch (Exception ex)
        {
            _state.AppendLog($"[错误] 停止进程时出错: {ex.Message}", "err");
        }
        finally
        {
            // 这里只能安全地 dispose；状态字段的清理由 OnExited 负责（它会再次
            // 校验 _process == proc，避免双重更新）。
            try { proc.Dispose(); } catch { }
            ResetStateFields();
            _state.AppendLog("[系统] 进程已停止", "sys");
            StatusChanged?.Invoke();
            LogUpdated?.Invoke();
        }

        return true;
    }

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data)) return;
        _state.AppendLog(e.Data, "ok");
        LogUpdated?.Invoke();
    }

    private void OnError(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data)) return;
        _state.AppendLog(e.Data, "err");
        LogUpdated?.Invoke();
    }

    /// <summary>
    /// 子进程退出事件回调。OnExited 与 Stop() 可能并发；用 Interlocked.Exchange
    /// 确保只有一个分支真正清理状态字段。
    /// </summary>
    private void OnExited(Process proc)
    {
        // 抢占清理权：返回值是被换出的引用，若仍是传入的 proc 则本分支负责清理
        var current = Interlocked.CompareExchange(ref _process, null, proc);
        if (!ReferenceEquals(current, proc))
        {
            // 已被 Stop() 取走，本分支只 dispose，不动状态字段
            try { proc.Dispose(); } catch { }
            return;
        }

        int code = 0;
        try { code = proc.ExitCode; } catch { }
        try { proc.Dispose(); } catch { }

        ResetStateFields();
        _state.AppendLog($"[系统] 进程已退出，退出码: {code}", "sys");
        StatusChanged?.Invoke();
        LogUpdated?.Invoke();
    }

    private static bool IsProcessAlive(Process? p)
    {
        if (p == null) return false;
        try { return !p.HasExited; } catch { return false; }
    }

    private void ResetStateFields()
    {
        lock (_lock)
        {
            _state.IsRunning = false;
            _state.ProcessId = null;
            _state.StartedAt = null;
        }
    }
}