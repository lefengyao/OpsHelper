using Renci.SshNet;
using ServerOpsHelper.Models;
using System;
using System.Threading.Tasks;

namespace ServerOpsHelper.Services;

/// <summary>
/// 远程命令执行服务，支持 SSH 连接与命令执行
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

        return await Task.Run(() =>
        {
            try
            {
                using var client = new SshClient(server.Host, server.Port, server.Username, server.Password);
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
                client.Connect();

                ServerConnectionTracker.MarkConnected(server);

                using var cmd = client.CreateCommand(command);
                cmd.Execute();

                client.Disconnect();

                var exitCode = cmd.ExitStatus ?? -1;

                return new RemoteCommandResult
                {
                    Success = exitCode == 0,
                    ExitCode = exitCode,
                    Output = cmd.Result,
                    Error = cmd.Error
                };
            }
            catch (Exception ex)
            {
                return new RemoteCommandResult
                {
                    Success = false,
                    Output = string.Empty,
                    Error = $"SSH 执行失败：{ex.Message}"
                };
            }
        });
    }
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
