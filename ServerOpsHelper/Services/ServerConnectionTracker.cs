using ServerOpsHelper.Models;
using System;

namespace ServerOpsHelper.Services;

/// <summary>
/// 连接成功后的旁路记录。
/// <para>
/// 目前只记「最后一次连接时间」。之所以单独拎成一个类而不是散在各处：
/// 连接入口有六处（监控 / 终端 / 命令执行 / SFTP，桌面端和 MCP 各一套），
/// 统一走这里才能保证行为一致——少写一处，那个入口的
/// <see cref="ServerInfo.LastConnectedAt"/> 就永远是 null。
/// </para>
/// </summary>
internal static class ServerConnectionTracker
{
    /// <summary>
    /// 标记「刚连上了这台服务器」。
    /// <para>
    /// 同步返回，不阻塞连接流程：内存里的对象立刻更新（调用方手上的
    /// <paramref name="server"/> 直接就有值），落盘在后台进行。
    /// </para>
    /// <para>
    /// 落盘的异常由 <see cref="ConfigService.TouchServerAsync"/> 内部吞掉，
    /// 所以这里的 fire-and-forget 不会产生未观察异常。
    /// </para>
    /// </summary>
    public static void MarkConnected(ServerInfo server)
    {
        if (server == null)
        {
            return;
        }

        server.LastConnectedAt = DateTime.Now;
        _ = ConfigService.Instance.TouchServerAsync(server.Id);
    }
}
