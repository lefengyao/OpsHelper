using Renci.SshNet;
using Renci.SshNet.Common;
using ServerOpsHelper.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ServerOpsHelper.Services;

/// <summary>
/// 远程命令执行服务，支持 SSH 连接与命令执行。
/// <para>
/// 走 <see cref="SshSessionPool"/> 复用连接：扩展命令、MCP 命令共用同一条已认证会话，
/// 不再"一条命令一次握手"。命令取消/超时会把连接摘掉，不留僵尸会话。
/// </para>
/// </summary>
public class RemoteExecutionService
{
    /// <summary>
    /// 通过 SSH 在远程服务器上执行命令
    /// </summary>
    public async Task<RemoteCommandResult> ExecuteSshCommandAsync(ServerInfo server, string command, int timeoutSeconds = 30)
    {
        if (server.OsType != ServerOsType.Linux)
        {
            return new RemoteCommandResult
            {
                Success = false,
                Output = string.Empty,
                Error = "当前仅支持 Linux 服务器的 SSH 命令执行。"
            };
        }

        var timeout = Math.Clamp(timeoutSeconds, 1, 600);
        var connectTimeout = TimeSpan.FromSeconds(Math.Clamp(timeout, 10, 20));

        try
        {
            return await SshSessionPool.Shared.RunAsync(
                server,
                (client, token) => RunOnceAsync(client, command, timeout, token),
                connectTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 超时/取消后连接已经不可信（远端命令可能还在跑），摘掉重建更干净
            SshSessionPool.Shared.Discard(server);
            return Failure($"命令超过 {timeout} 秒未结束，已中断。");
        }
        catch (SshConnectionException ex)
        {
            // 握手/鉴权的说明由 pool 拼好（含重试次数与排查方向），直接透出
            return Failure(ex.Message);
        }
        catch (Exception ex)
        {
            return Failure($"SSH 执行失败：{ex.Message}");
        }
    }

    private static async Task<RemoteCommandResult> RunOnceAsync(
        SshClient client,
        string command,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(timeoutSeconds);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await cmd.ExecuteAsync(cts.Token).ConfigureAwait(false);
        }
        catch (SshOperationTimeoutException)
        {
            // 统一成取消，交给外层按"超时 + 摘连接"处理
            throw new OperationCanceledException($"命令超过 {timeoutSeconds} 秒未结束。");
        }

        var exitCode = cmd.ExitStatus ?? -1;

        return new RemoteCommandResult
        {
            Success = exitCode == 0,
            ExitCode = exitCode,
            Output = cmd.Result ?? string.Empty,
            Error = cmd.Error ?? string.Empty
        };
    }

    private static RemoteCommandResult Failure(string message) => new()
    {
        Success = false,
        ExitCode = -1,
        Output = string.Empty,
        Error = message
    };
}

/// <summary>
/// 远程命令执行结果
/// </summary>
public class RemoteCommandResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}
