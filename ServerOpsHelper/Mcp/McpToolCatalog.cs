using ServerOpsHelper.Models;
using ServerOpsHelper.Services;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace ServerOpsHelper.Mcp;

/// <summary>
/// 一个 MCP 工具的定义。
/// </summary>
/// <param name="Name">工具名（MCP 要求 ^[a-zA-Z0-9_-]{1,64}$）</param>
/// <param name="Description">给模型看的说明，要写清"什么时候用、参数怎么填、返回什么"</param>
/// <param name="RequiresWriteAccess">是否属于「变更类」工具：用户没在设置里开启时一律拒绝</param>
/// <param name="InputSchema">JSON Schema（2020-12）</param>
internal sealed record McpToolSpec(
    string Name,
    string Description,
    bool RequiresWriteAccess,
    JsonObject InputSchema);

/// <summary>
/// 对外暴露的工具清单。
/// <para>
/// 设计原则（为什么是这几个）：
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       <b>只暴露"操作服务器"，不暴露"改本机配置"</b>：服务器增删改仍然只能在界面里做。
///       智能体不知道也不该知道密码长什么样，更不该能改密码箱 —— 这是最小权限。
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>读与写分成两档</b>：列机器、看指标、看文件是只读的，默认就可用；
///       执行命令、写文件属于变更类，必须在「系统设置 → 智能体接入」里显式打开才生效。
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>批量执行是独立工具</b>：管理多台机器时，模型最容易犯的错是一台一台串行跑，
///       慢且结果难对齐。<c>exec_command_on_servers</c> 一次给齐所有机器的结果，带并发上限。
///     </description>
///   </item>
/// </list>
/// <para>
/// 说明文字用英文：MCP 客户端背后的模型对英文工具描述的解析最稳，且更省 token。
/// </para>
/// </summary>
internal static class McpToolCatalog
{
    public const string ListServers = "list_servers";
    public const string GetServer = "get_server";
    public const string GetMetrics = "get_metrics";
    public const string ListFiles = "list_files";
    public const string ReadFile = "read_file";
    public const string ExecCommand = "exec_command";
    public const string ExecCommandOnServers = "exec_command_on_servers";
    public const string WriteFile = "write_file";
    public const string UploadFile = "upload_file";
    public const string DownloadFile = "download_file";

    private static readonly List<McpToolSpec> Tools = new()
    {
        new McpToolSpec(
            ListServers,
            """
            List every server saved in ServerOpsHelper (name, host, port, username, OS, tags).
            Start here when you need to know which machines are available, then pass the returned
            `id` or `name` to the other tools. Passwords are never included in the response.
            """,
            RequiresWriteAccess: false,
            ObjectSchema(
                new Dictionary<string, JsonObject>
                {
                    ["tag"] = Str("Only return servers whose tags contain this value (case-insensitive)."),
                    ["query"] = Str("Free-text filter matched against name, host and description.")
                })),

        new McpToolSpec(
            GetServer,
            """
            Get full details of one saved server (no password). Useful to confirm you picked the
            right machine before running a command on it.
            """,
            RequiresWriteAccess: false,
            ObjectSchema(
                new Dictionary<string, JsonObject>
                {
                    ["server"] = Str("Server id (GUID) or exact name. Partial name is accepted when unique.")
                },
                "server")),

        new McpToolSpec(
            GetMetrics,
            """
            Sample live metrics from one Linux server over SSH: CPU usage, load average, memory,
            root-disk usage, network throughput and uptime. Usage/throughput need two samples
            (~1s apart), so the call takes a couple of seconds.
            """,
            RequiresWriteAccess: false,
            ObjectSchema(
                new Dictionary<string, JsonObject>
                {
                    ["server"] = Str("Server id (GUID) or exact name.")
                },
                "server")),

        new McpToolSpec(
            ListFiles,
            """
            List a directory on a remote server over SFTP (directories first). Returns name,
            full path, type, size, modified time and permission string.
            """,
            RequiresWriteAccess: false,
            ObjectSchema(
                new Dictionary<string, JsonObject>
                {
                    ["server"] = Str("Server id (GUID) or exact name."),
                    ["path"] = Str("Absolute remote directory path. Defaults to \"/\".")
                },
                "server")),

        new McpToolSpec(
            ReadFile,
            """
            Read a remote text file over SFTP, in pages. `offset` is a 0-based byte offset and
            `maxBytes` is the page size (1-1048576, default 65536). The response returns
            `nextOffset` and `endOfFile`; keep calling with `nextOffset` until `endOfFile` is
            true to read a whole file without shell-quoting tricks. Page boundaries never split
            a UTF-8 character. Binary files return metadata only — use download_file for them.
            For very large logs prefer exec_command with grep/tail.
            """,
            RequiresWriteAccess: false,
            ObjectSchema(
                new Dictionary<string, JsonObject>
                {
                    ["server"] = Str("Server id (GUID) or exact name."),
                    ["path"] = Str("Absolute remote file path."),
                    ["offset"] = Long("Byte offset to start reading from (0-based).", 0, long.MaxValue, 0),
                    ["maxBytes"] = Int("Maximum bytes to return in this page (1-1048576).", 1, 1_048_576, 65_536)
                },
                "server", "path")),

        new McpToolSpec(
            ExecCommand,
            """
            Run a shell command on one Linux server over SSH and return exit code, stdout and
            stderr. Non-zero exit codes are reported as data (isError=false) so you can inspect
            the failure; only transport/auth problems set isError=true.
            Requires the user to have enabled "allow变更操作" in Settings.
            """,
            RequiresWriteAccess: true,
            ObjectSchema(
                new Dictionary<string, JsonObject>
                {
                    ["server"] = Str("Server id (GUID) or exact name."),
                    ["command"] = Str("Shell command, executed by the remote login shell. Keep it idempotent when possible."),
                    ["timeoutSeconds"] = Int("Kill the command after this many seconds (1-300).", 1, 300, 60)
                },
                "server", "command")),

        new McpToolSpec(
            ExecCommandOnServers,
            """
            Run the SAME command on several saved servers at once (bounded concurrency) and return
            a per-server result map. Use this instead of calling exec_command in a loop when you
            need to inspect or change a fleet — e.g. check disk usage on every web node.
            Select targets with `servers` (ids/names) or `tag` (all servers carrying that tag).
            Requires the user to have enabled "allow变更操作" in Settings.
            """,
            RequiresWriteAccess: true,
            ObjectSchema(
                new Dictionary<string, JsonObject>
                {
                    ["servers"] = StrArray("Server ids or exact names. Omit when using `tag`."),
                    ["tag"] = Str("Run on every server whose tags contain this value."),
                    ["command"] = Str("Shell command executed on each selected server."),
                    ["timeoutSeconds"] = Int("Per-server timeout in seconds (1-300).", 1, 300, 60),
                    ["maxConcurrency"] = Int("How many servers to hit in parallel (1-16).", 1, 16, 4)
                },
                "command")),

        new McpToolSpec(
            WriteFile,
            """
            Create, overwrite or append to a remote file over SFTP with UTF-8 text content.
            `mode` is "overwrite" (default, replaces the file) or "append" (adds to the end —
            use it to assemble a file too large for one request; each HTTP-transport request
            body is capped at 8 MiB). The parent directory must already exist. Prefer
            upload_file for large or binary files.
            Requires the user to have enabled "allow变更操作" in Settings.
            """,
            RequiresWriteAccess: true,
            ObjectSchema(
                new Dictionary<string, JsonObject>
                {
                    ["server"] = Str("Server id (GUID) or exact name."),
                    ["path"] = Str("Absolute remote file path."),
                    ["content"] = Str("UTF-8 text to write."),
                    ["mode"] = StrEnum("Write mode.", "overwrite", "append")
                },
                "server", "path", "content")),

        new McpToolSpec(
            UploadFile,
            """
            Upload a file or directory from THIS machine to a remote server over SFTP, streamed
            directly from disk — the content never goes through JSON, so there is no 8 MiB body
            limit and binary files work. Prefer this over write_file whenever the data already
            exists locally (build artifacts, images, archives, big configs).
            `localPath` is a Windows path on the machine running ServerOpsHelper (`~/...` expands
            to the user profile). When it is a directory, set `recursive`=true and its contents
            are copied into `remotePath` (which must be the destination directory). Parent
            directories on the server are created when `createDirectories`=true. Existing remote
            files are replaced unless `overwrite`=false. Per-file failures are reported without
            aborting the rest of the transfer.
            Requires the user to have enabled "allow变更操作" in Settings.
            """,
            RequiresWriteAccess: true,
            ObjectSchema(
                new Dictionary<string, JsonObject>
                {
                    ["server"] = Str("Server id (GUID) or exact name."),
                    ["localPath"] = Str("Source file or directory on this machine."),
                    ["remotePath"] = Str("Destination file path, or destination directory when uploading a directory."),
                    ["recursive"] = Bool("Required true when localPath is a directory.", false),
                    ["createDirectories"] = Bool("Create missing remote directories automatically.", true),
                    ["overwrite"] = Bool("Replace existing remote files.", true)
                },
                "server", "localPath", "remotePath")),

        new McpToolSpec(
            DownloadFile,
            """
            Download a file or directory from a remote server to THIS machine over SFTP, streamed
            directly to disk. Use it for large text, logs and binary files that read_file cannot
            return. When `remotePath` is a directory, set `recursive`=true to copy its contents
            into `localPath`. Parent directories on this machine are created when
            `createDirectories`=true; existing local files are replaced unless `overwrite`=false.
            Symlinked directories on the server are skipped. Per-file failures are reported
            without aborting the rest of the transfer.
            Requires the user to have enabled "allow变更操作" in Settings (it writes to local disk).
            """,
            RequiresWriteAccess: true,
            ObjectSchema(
                new Dictionary<string, JsonObject>
                {
                    ["server"] = Str("Server id (GUID) or exact name."),
                    ["remotePath"] = Str("Source file path, or source directory when downloading a directory."),
                    ["localPath"] = Str("Destination file or directory on this machine."),
                    ["recursive"] = Bool("Required true when remotePath is a directory.", false),
                    ["createDirectories"] = Bool("Create missing local directories automatically.", true),
                    ["overwrite"] = Bool("Replace existing local files.", true)
                },
                "server", "remotePath", "localPath"))
    };

    /// <summary>
    /// 全部工具 = 内置 10 个 + 用户扩展里的命令。
    /// <para>
    /// 扩展清单每次现读（<see cref="ExtensionService.Packs"/> 是加锁快照，扩展数量是个位数），
    /// 好处是「设置里点重新加载」立刻生效，不需要重启进程；代价可以忽略。
    /// </para>
    /// </summary>
    public static IReadOnlyList<McpToolSpec> All => Tools.Concat(BuildExtensionSpecs()).ToArray();

    public static McpToolSpec? Find(string name)
        => Tools.FirstOrDefault(t => string.Equals(t.Name, name, System.StringComparison.Ordinal))
           ?? BuildExtensionSpecs().FirstOrDefault(t => string.Equals(t.Name, name, System.StringComparison.Ordinal));

    // ───────────────────────── 扩展工具（清单式插件） ─────────────────────────

    /// <summary>
    /// 把扩展里的命令翻译成 MCP 工具。
    /// <para>
    /// 只暴露 <c>exposeToAgent</c> 为 true 的命令；<c>writeAccess</c> 原样映射到
    /// <see cref="McpToolSpec.RequiresWriteAccess"/>，所以写权限闸门对扩展同样生效。
    /// </para>
    /// </summary>
    private static IEnumerable<McpToolSpec> BuildExtensionSpecs()
    {
        foreach (var (pack, command) in ExtensionService.Instance.AllCommands())
        {
            if (!command.ExposeToAgent)
            {
                continue;
            }

            yield return new McpToolSpec(
                ExtensionService.ToolName(pack, command),
                ExtensionService.DescribeForAgent(pack, command),
                command.WriteAccess,
                BuildExtensionSchema(command));
        }
    }

    /// <summary>
    /// 扩展命令的输入 Schema：<c>server</c> + 清单里声明的参数。
    /// 没有默认值的参数进 required —— 让模型在调用前就补齐，而不是让命令带个空值跑出去。
    /// </summary>
    private static JsonObject BuildExtensionSchema(ExtensionCommand command)
    {
        var properties = new Dictionary<string, JsonObject>
        {
            ["server"] = Str("Server id (GUID) or exact name.")
        };

        var required = new List<string> { "server" };

        foreach (var parameter in command.Parameters)
        {
            var description = string.IsNullOrWhiteSpace(parameter.Description)
                ? parameter.Name
                : parameter.Description;

            properties[parameter.Name] = parameter.Type switch
            {
                "int" => Int(description, parameter.Min ?? int.MinValue, parameter.Max ?? int.MaxValue,
                    int.TryParse(parameter.Default, out var intDefault) ? intDefault : 0),
                "bool" => Bool(description, IsTruthy(parameter.Default)),
                _ => Str(description)
            };

            if (string.IsNullOrWhiteSpace(parameter.Default))
            {
                required.Add(parameter.Name);
            }
        }

        return ObjectSchema(properties, required.ToArray());
    }

    private static bool IsTruthy(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return v is "1" or "true" or "yes" or "on";
    }

    // ───────────────────────── Schema 构造辅助 ─────────────────────────

    private static JsonObject ObjectSchema(Dictionary<string, JsonObject> properties, params string[] required)
    {
        var props = new JsonObject();
        foreach (var (key, value) in properties)
        {
            props[key] = value;
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["additionalProperties"] = false
        };

        if (required.Length > 0)
        {
            schema["required"] = new JsonArray(required
                .Select(r => (JsonNode)JsonValue.Create(r)!)
                .ToArray());
        }

        return schema;
    }

    private static JsonObject Str(string description)
        => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Int(string description, int min, int max, int defaultValue)
        => new()
        {
            ["type"] = "integer",
            ["description"] = description,
            ["minimum"] = min,
            ["maximum"] = max,
            ["default"] = defaultValue
        };

    private static JsonObject Bool(string description, bool defaultValue)
        => new()
        {
            ["type"] = "boolean",
            ["description"] = description,
            ["default"] = defaultValue
        };

    private static JsonObject Long(string description, long min, long max, long defaultValue)
        => new()
        {
            ["type"] = "integer",
            ["description"] = description,
            ["minimum"] = min,
            ["maximum"] = max,
            ["default"] = defaultValue
        };

    private static JsonObject StrEnum(string description, params string[] values)
    {
        var array = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

        return new JsonObject
        {
            ["type"] = "string",
            ["description"] = description,
            ["enum"] = array,
            ["default"] = values[0]
        };
    }

    private static JsonObject StrArray(string description)
        => new()
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = new JsonObject { ["type"] = "string" }
        };
}
