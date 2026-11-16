using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ServerOpsHelper.Mcp;

/// <summary>
/// stdio 传输：MCP 客户端把本程序当子进程拉起来，一行一条 JSON-RPC 消息。
/// <para>
/// 这是接入桌面型智能体（Claude Desktop、CodeBuddy、Cline 等）最省事的方式 ——
/// 客户端只需要一个 <c>command</c> + <c>args</c>，不用管端口、令牌、防火墙。
/// 权限模型也对得上：子进程继承的就是当前 Windows 用户的权限，能解 DPAPI 凭据。
/// </para>
/// <para>
/// ⚠️ <b>stdout 是协议通道，除了 JSON-RPC 响应什么都不能写。</b>
/// 一行多余的日志就会让客户端解析失败、直接判定服务器不可用。诊断信息一律走 stderr。
/// </para>
/// </summary>
internal static class McpStdioHost
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 跑 stdio 主循环，直到 stdin 关闭（客户端退出）为止。
    /// </summary>
    /// <returns>进程退出码</returns>
    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        using var stdin = new StreamReader(
            Console.OpenStandardInput(), utf8, detectEncodingFromByteOrderMarks: false, 8192, leaveOpen: true);

        using var stdout = new StreamWriter(
            Console.OpenStandardOutput(), utf8, 8192, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        WriteDiagnostic($"[mcp] stdio 模式启动，版本 {McpServerCore.ServerVersion}，数据目录 {Services.ConfigService.Instance.ConfigDirectory}");

        var core = new McpServerCore();

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await stdin.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line == null)
            {
                break;
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            JsonObject? response;

            try
            {
                var node = JsonNode.Parse(line);

                if (node is JsonObject request)
                {
                    response = await core.HandleAsync(request, "stdio", cancellationToken);
                }
                else
                {
                    response = BuildParseError($"消息必须是 JSON 对象，实际收到 {node?.GetType().Name ?? "null"}。");
                }
            }
            catch (JsonException ex)
            {
                response = BuildParseError($"JSON 解析失败：{ex.Message}");
            }
            catch (Exception ex)
            {
                // 单条消息处理失败不能拖垮整条连接
                WriteDiagnostic($"[mcp] 处理消息异常：{ex.GetType().Name}: {ex.Message}");
                response = BuildParseError(ex.Message);
            }

            if (response == null)
            {
                continue;
            }

            try
            {
                await stdout.WriteLineAsync(response.ToJsonString(CompactJson));
            }
            catch (IOException)
            {
                // 客户端已经关掉管道，正常退出即可
                break;
            }
        }

        WriteDiagnostic("[mcp] stdio 会话结束");
        return 0;
    }

    private static JsonObject BuildParseError(string message)
        => new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = null,
            ["error"] = new JsonObject
            {
                ["code"] = -32700,
                ["message"] = message
            }
        };

    /// <summary>
    /// 写 stderr 诊断。
    /// <para>
    /// 这里刻意自己开一个 UTF-8 的 stderr 写入器，而不是用 <see cref="Console.Error"/>：
    /// 控制台默认编码是系统 OEM 代码页（中文 Windows 上是 936），
    /// 而 MCP 客户端读子进程 stderr 时按 UTF-8 解 —— 不换编码中文会全成乱码。
    /// </para>
    /// <para>
    /// 无控制台 / 管道已关时会抛异常，所以整体吞掉：诊断日志的重要性永远低于协议通道本身。
    /// </para>
    /// </summary>
    private static void WriteDiagnostic(string message)
    {
        try
        {
            _stdErr ??= new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false))
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            _stdErr.WriteLine(message);
        }
        catch (Exception)
        {
        }
    }

    private static StreamWriter? _stdErr;
}
