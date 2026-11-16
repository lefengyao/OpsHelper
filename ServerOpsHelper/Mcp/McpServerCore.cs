using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ServerOpsHelper.Mcp;

/// <summary>
/// MCP 协议核心（与传输无关）。
/// <para>
/// 只做三件事：版本协商、把 <c>tools/list</c> 翻译成工具清单、把 <c>tools/call</c> 转给
/// <see cref="McpToolExecutor"/>。stdio 和 HTTP 两条传输共用这一个实例逻辑，
/// 保证"同一套工具、同样的权限、同样的审计"。
/// </para>
/// <para>
/// 协议版本：优先回显客户端请求的版本（只要在支持列表里），否则回最新支持版本 ——
/// 这是规范要求的协商方式，客户端据此决定用哪套字段。
/// </para>
/// </summary>
internal sealed class McpServerCore
{
    public const string ServerName = "ServerOpsHelper";

    /// <summary>我们实现到的最新协议版本</summary>
    public const string LatestProtocolVersion = "2025-06-18";

    private static readonly HashSet<string> SupportedProtocolVersions = new(StringComparer.Ordinal)
    {
        "2025-06-18",
        "2025-03-26",
        "2024-11-05"
    };

    private readonly McpToolExecutor _executor = new();

    /// <summary>供客户端显示的版本号（取程序集版本，发版时自动跟着走）</summary>
    public static string ServerVersion
    {
        get
        {
            var version = typeof(McpServerCore).Assembly.GetName().Version;
            return version == null
                ? "0.0.0"
                : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    private const string Instructions =
        """
        This server exposes the machines the user has already saved in the ServerOpsHelper
        desktop app. Address a machine by its `id` (GUID) or exact `name`; call `list_servers`
        first when you do not know them yet. Passwords are never included in any response.

        Read-only tools (list_servers, get_server, get_metrics, list_files, read_file) are
        always available. Changing tools (exec_command, exec_command_on_servers, write_file,
        upload_file, download_file) only work after the user enables "允许智能体执行变更操作"
        in Settings → 智能体接入; if you get a permission error, ask the user to turn it on
        instead of retrying.

        File transfer: read_file returns text in pages — follow `nextOffset` until
        `endOfFile` for big files. To move a whole file or directory (especially binary),
        use upload_file / download_file, which stream over SFTP with no JSON size limit and
        run on the machine where ServerOpsHelper is installed.

        Prefer `exec_command_on_servers` over looping `exec_command` when acting on several
        machines. Every call is appended to logs/mcp-audit-*.log in the app's data directory.
        """;

    /// <summary>
    /// 处理一条 JSON-RPC 消息。
    /// </summary>
    /// <returns>需要回写给客户端的响应；通知（没有 id）返回 null。</returns>
    public async Task<JsonObject?> HandleAsync(JsonObject request, string transport, CancellationToken cancellationToken)
    {
        var hasId = request.ContainsKey("id");
        var id = hasId ? CloneNode(request["id"]) : null;

        var method = TryGetString(request, "method");
        if (string.IsNullOrEmpty(method))
        {
            return hasId ? BuildError(id, -32600, "Invalid Request：缺少 method。") : null;
        }

        // 通知一律不需要回应。目前只用到 notifications/initialized，
        // 以及客户端的 notifications/cancelled（长任务取消交给传输层断开处理）。
        if (!hasId)
        {
            return null;
        }

        try
        {
            return method switch
            {
                "initialize" => BuildOk(id, BuildInitializeResult(request["params"] as JsonObject)),
                "ping" => BuildOk(id, new JsonObject()),
                "tools/list" => BuildOk(id, BuildToolsResult()),
                "tools/call" => BuildOk(id, await CallToolAsync(request["params"] as JsonObject, transport, cancellationToken)),
                // 我们只提供 tools：这两个方法回空列表，比直接报 -32601 对客户端更友好
                "resources/list" => BuildOk(id, new JsonObject { ["resources"] = new JsonArray() }),
                "prompts/list" => BuildOk(id, new JsonObject { ["prompts"] = new JsonArray() }),
                _ => BuildError(id, -32601, $"Method not found：{method}")
            };
        }
        catch (McpProtocolException ex)
        {
            // 协议级问题（参数缺工具名、工具不存在）走 JSON-RPC error，
            // 和"工具跑了但失败"（isError=true）区分开
            return BuildError(id, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return BuildError(id, -32603, $"Internal error：{ex.Message}");
        }
    }

    // ───────────────────────── initialize ─────────────────────────

    private static JsonObject BuildInitializeResult(JsonObject? parameters)
    {
        var requested = parameters == null ? null : TryGetString(parameters, "protocolVersion");

        var negotiated = !string.IsNullOrEmpty(requested) && SupportedProtocolVersions.Contains(requested)
            ? requested!
            : LatestProtocolVersion;

        return new JsonObject
        {
            ["protocolVersion"] = negotiated,
            ["capabilities"] = new JsonObject
            {
                // listChanged=false：我们不会主动推 tools/list_changed 通知。
                // 内置 10 个工具是固定的，扩展重载后清单会变 —— 客户端要重新 tools/list 或重连才看得到。
                ["tools"] = new JsonObject { ["listChanged"] = false }
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = ServerName,
                ["title"] = "服务器运维助手",
                ["version"] = ServerVersion
            },
            ["instructions"] = Instructions
        };
    }

    // ───────────────────────── tools/list ─────────────────────────

    private static JsonObject BuildToolsResult()
    {
        var tools = new JsonArray();

        foreach (var spec in McpToolCatalog.All)
        {
            tools.Add(new JsonObject
            {
                ["name"] = spec.Name,
                ["description"] = spec.Description,
                ["inputSchema"] = spec.InputSchema.DeepClone(),
                // 给客户端一个"这是只读工具"的提示，UI 层可据此不做二次确认
                ["annotations"] = new JsonObject
                {
                    ["readOnlyHint"] = !spec.RequiresWriteAccess,
                    ["openWorldHint"] = true
                }
            });
        }

        return new JsonObject { ["tools"] = tools };
    }

    // ───────────────────────── tools/call ─────────────────────────

    private async Task<JsonObject> CallToolAsync(
        JsonObject? parameters,
        string transport,
        CancellationToken cancellationToken)
    {
        var name = parameters == null ? null : TryGetString(parameters, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new McpProtocolException(-32602, "Invalid params：缺少工具名 name。");
        }

        if (McpToolCatalog.Find(name) == null)
        {
            throw new McpProtocolException(-32602, $"Invalid params：未知工具 {name}。");
        }

        var arguments = parameters!["arguments"] as JsonObject ?? new JsonObject();

        var result = await _executor.ExecuteAsync(transport, name, arguments, cancellationToken);

        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = result.Text
                }
            },
            ["isError"] = result.IsError
        };
    }

    // ───────────────────────── 响应构造 ─────────────────────────

    private static JsonObject BuildOk(JsonNode? id, JsonObject result)
        => new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result
        };

    private static JsonObject BuildError(JsonNode? id, int code, string message, JsonNode? data = null)
    {
        var error = new JsonObject
        {
            ["code"] = code,
            ["message"] = message
        };

        if (data != null)
        {
            error["data"] = data;
        }

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = error
        };
    }

    /// <summary>JsonNode 只能有一个父节点，回写 id 前必须先复制一份。</summary>
    private static JsonNode? CloneNode(JsonNode? node)
        => node == null ? null : JsonNode.Parse(node.ToJsonString());

    private static string? TryGetString(JsonObject obj, string name)
    {
        try
        {
            return obj[name]?.GetValue<string>();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>协议层错误（与工具执行错误区分：前者改 JSON-RPC error，后者改 isError）。</summary>
internal sealed class McpProtocolException : Exception
{
    public McpProtocolException(int code, string message) : base(message)
    {
        Code = code;
    }

    public int Code { get; }
}
