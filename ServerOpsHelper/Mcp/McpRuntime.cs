using ServerOpsHelper.Models;
using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ServerOpsHelper.Mcp;

/// <summary>
/// MCP 服务的运行时门面：谁都不直接碰传输对象，启停都从这里走。
/// <para>
/// 两种传输的生命周期完全不同：
/// </para>
/// <list type="bullet">
///   <item><b>stdio</b> —— 客户端拉起进程，进程活多久就服务多久，由 <see cref="McpStdioHost"/> 全程接管；</item>
///   <item><b>HTTP</b> —— 由桌面程序按设置开关启停，所以要有这一层统一处理"改端口 / 换令牌 / 关掉"。</item>
/// </list>
/// </summary>
internal static class McpRuntime
{
    private static readonly McpHttpHost Http = new();

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static bool IsHttpRunning => Http.IsRunning;

    public static string? HttpLastError => Http.LastError;

    public static int HttpPort => Http.Port;

    public static long HttpRequestCount => Http.RequestCount;

    public static string HttpEndpoint => Http.Port > 0
        ? $"http://127.0.0.1:{Http.Port}/mcp"
        : "—";

    /// <summary>
    /// 按设置启停 HTTP 服务。**不抛异常** —— 启动失败（端口被占）只记录原因，
    /// 由设置页展示，不能因为一个可选的接入功能影响主程序启动。
    /// </summary>
    public static void Apply(AppSettings settings)
    {
        if (!settings.McpHttpEnabled)
        {
            StopHttp();
            return;
        }

        try
        {
            var token = McpTokenStore.EnsureToken();
            Http.Start(settings.McpHttpPort, token);
        }
        catch (Exception)
        {
            // LastError 已由 McpHttpHost 记下
        }
    }

    public static void StopHttp() => Http.Stop();

    /// <summary>
    /// 当前令牌（没有就生成）。设置页展示用。
    /// </summary>
    public static string CurrentToken() => McpTokenStore.EnsureToken();

    /// <summary>重置令牌；若 HTTP 服务在跑，立即用新令牌重启。</summary>
    public static string ResetToken(AppSettings settings)
    {
        var token = McpTokenStore.Regenerate();

        if (settings.McpHttpEnabled)
        {
            try
            {
                Http.Start(settings.McpHttpPort, token);
            }
            catch (Exception)
            {
            }
        }

        return token;
    }

    // ───────────────────────── 客户端配置 ─────────────────────────

    /// <summary>
    /// 生成可直接粘进 MCP 客户端配置文件的 JSON（stdio 方式，推荐）。
    /// </summary>
    public static string BuildStdioClientConfig()
    {
        var exePath = Environment.ProcessPath ?? "ServerOpsHelper.exe";

        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["serveropshelper"] = new JsonObject
                {
                    ["command"] = exePath,
                    ["args"] = new JsonArray("--mcp-stdio")
                }
            }
        };

        return config.ToJsonString(PrettyJson);
    }

    /// <summary>
    /// 生成 HTTP 方式的客户端配置（需要桌面程序在运行、且开关已打开）。
    /// </summary>
    public static string BuildHttpClientConfig(AppSettings settings, string token)
    {
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["serveropshelper"] = new JsonObject
                {
                    ["type"] = "streamable-http",
                    ["url"] = $"http://127.0.0.1:{settings.McpHttpPort}/mcp",
                    ["headers"] = new JsonObject
                    {
                        ["Authorization"] = $"Bearer {token}"
                    }
                }
            }
        };

        return config.ToJsonString(PrettyJson);
    }
}
