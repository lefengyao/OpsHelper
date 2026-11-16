using Renci.SshNet;
using Renci.SshNet.Sftp;
using ServerOpsHelper.Models;
using ServerOpsHelper.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ServerOpsHelper.Mcp;

/// <summary>一次工具调用的结果（对应 MCP 的 <c>tools/call</c> 返回体）。</summary>
internal sealed class McpToolResult
{
    public bool IsError { get; private init; }
    public string Text { get; private init; } = string.Empty;

    public static McpToolResult Ok(JsonNode payload)
        => new()
        {
            IsError = false,
            Text = payload.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                // 中文不要被转成 \uXXXX：模型读起来更省 token，人看审计日志也舒服
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            })
        };

    public static McpToolResult Fail(string message)
        => new() { IsError = true, Text = message };
}

/// <summary>
/// 业务层可预期的失败（找不到服务器、参数不对、权限未开）。
/// 这类错误以 <c>isError: true</c> 的工具结果返回，而不是 JSON-RPC 协议错误 ——
/// 前者模型能读懂并自我纠正，后者在很多客户端里只会显示成"工具崩了"。
/// </summary>
internal sealed class McpToolException : Exception
{
    public McpToolException(string message) : base(message)
    {
    }
}

/// <summary>
/// 工具的真正实现：把 MCP 调用翻译成对已保存服务器的 SSH / SFTP 操作。
/// <para>
/// 线程约定：全部方法可在任意线程调用，内部自己 <see cref="Task.Run(Func{Task})"/>，
/// 结果不碰 UI。每次调用独立建连、用完即断 —— 桌面端可能同时开着终端和监控，
/// 连接池化换来的性能抵不过"连接状态互相污染"带来的排查成本。
/// </para>
/// </summary>
internal sealed class McpToolExecutor
{
    /// <summary>单条命令回传的最大字符数（超出截断并标注，避免把模型上下文一次打爆）</summary>
    private const int MaxCommandOutputChars = 100_000;

    /// <summary>read_file 单页硬上限：再大就该用 download_file，而不是往模型上下文里灌。</summary>
    private const int MaxReadBytes = 1_048_576;

    /// <summary>批量传输结果里最多逐条列出多少个失败文件（其余只给计数）。</summary>
    private const int MaxTransferFailuresReported = 50;

    /// <summary>
    /// 一次工具调用里由实现回填、供审计使用的信息。
    /// <para>
    /// 用可变对象而不是 <c>ref</c> 参数：async 方法不允许 ref/out 参数，
    /// 而且审计只需要"最后是什么"，不需要调用方逐个接住。
    /// </para>
    /// </summary>
    private sealed class ToolContext
    {
        /// <summary>目标服务器（批量时是"3 台：a,b,c"）</summary>
        public string Target { get; set; } = string.Empty;

        /// <summary>参数摘要（命令原文 / 文件路径等，**绝不含密码**）</summary>
        public string Summary { get; set; } = string.Empty;
    }

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task<McpToolResult> ExecuteAsync(
        string transport,
        string toolName,
        JsonObject args,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var context = new ToolContext();
        var success = false;

        try
        {
            var spec = McpToolCatalog.Find(toolName)
                       ?? throw new McpToolException($"未知工具：{toolName}");

            // 变更类工具的闸门：默认关闭，必须在设置里显式打开
            var settings = ConfigService.Instance.LoadSettings();
            if (spec.RequiresWriteAccess && !settings.McpAllowWriteTools)
            {
                throw new McpToolException(
                    $"工具 {toolName} 属于变更类操作，当前已被禁用。\n" +
                    "请让用户在 ServerOpsHelper 的「系统设置 → 智能体接入」中打开" +
                    "「允许智能体执行变更操作」后重试。只读工具（列机器、看指标、看文件）不受影响。");
            }

            var result = toolName switch
            {
                McpToolCatalog.ListServers => await ListServersAsync(args, context),
                McpToolCatalog.GetServer => await GetServerAsync(args, context),
                McpToolCatalog.GetMetrics => await GetMetricsAsync(args, context, cancellationToken),
                McpToolCatalog.ListFiles => await ListFilesAsync(args, context),
                McpToolCatalog.ReadFile => await ReadFileAsync(args, context),
                McpToolCatalog.ExecCommand => await ExecCommandAsync(args, context, cancellationToken),
                McpToolCatalog.ExecCommandOnServers => await ExecCommandOnServersAsync(args, context, cancellationToken),
                McpToolCatalog.WriteFile => await WriteFileAsync(args, context),
                McpToolCatalog.UploadFile => await UploadFileAsync(args, context, cancellationToken),
                McpToolCatalog.DownloadFile => await DownloadFileAsync(args, context, cancellationToken),
                // 用户扩展：ext_<扩展>_<命令>，能力与 exec_command 同级（同样受写权限闸门约束）
                _ when toolName.StartsWith(ExtensionService.ToolNamePrefix, StringComparison.Ordinal)
                    => await RunExtensionAsync(toolName, args, context, cancellationToken),
                _ => throw new McpToolException($"未知工具：{toolName}")
            };

            success = !result.IsError;
            return result;
        }
        catch (McpToolException ex)
        {
            return McpToolResult.Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            return McpToolResult.Fail("调用已取消（客户端断开或超时）。");
        }
        catch (Exception ex)
        {
            return McpToolResult.Fail($"执行失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            McpAuditLog.Write(transport, toolName, context.Target, context.Summary, success, started.Elapsed);
        }
    }

    // ───────────────────────── 只读工具 ─────────────────────────

    private static async Task<McpToolResult> ListServersAsync(JsonObject args, ToolContext context)
    {
        var tag = OptionalString(args, "tag");
        var query = OptionalString(args, "query");

        var servers = await ConfigService.Instance.LoadServersAsync();
        IEnumerable<ServerInfo> filtered = servers;

        if (!string.IsNullOrWhiteSpace(tag))
        {
            filtered = filtered.Where(s => ContainsTag(s.Tags, tag));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            filtered = filtered.Where(s =>
                s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || s.Host.Contains(q, StringComparison.OrdinalIgnoreCase)
                || s.Description.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var list = new JsonArray();
        foreach (var server in filtered.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(ToServerNode(server));
        }

        context.Target = $"{list.Count} 台";

        return McpToolResult.Ok(new JsonObject
        {
            ["count"] = list.Count,
            ["servers"] = list,
            ["note"] = list.Count == 0
                ? "没有符合条件的服务器。服务器需要在 ServerOpsHelper 界面里先添加。"
                : "用返回的 id 或 name 作为其它工具的 server 参数。密码不在返回内容里。"
        });
    }

    private static async Task<McpToolResult> GetServerAsync(JsonObject args, ToolContext context)
    {
        var key = RequireString(args, "server");
        var server = await ResolveServerAsync(key);
        context.Target = Describe(server);

        var node = ToServerNode(server);
        node["hasSavedPassword"] = !string.IsNullOrEmpty(server.Password);

        return McpToolResult.Ok(node);
    }

    private static async Task<McpToolResult> GetMetricsAsync(
        JsonObject args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var key = RequireString(args, "server");
        var server = await ResolveServerAsync(key);
        context.Target = Describe(server);

        if (server.OsType != ServerOsType.Linux)
        {
            throw new McpToolException($"服务器「{server.Name}」标记为 Windows，实时指标采集目前只支持 Linux。");
        }

        // 复用桌面端监控那套采集 + 解析：CPU 使用率和网络速率都必须靠两次采样求差，
        // 所以这里以 1 秒间隔连采两次，等到第一次带 CPU 的样本就收工。
        var service = new RemoteMonitorService();
        var tcs = new TaskCompletionSource<ServerMetrics>(TaskCreationOptions.RunContinuationsAsynchronously);
        ServerMetrics? firstSample = null;

        service.Updated += metrics =>
        {
            firstSample ??= metrics;
            if (metrics.HasCpu)
            {
                tcs.TrySetResult(metrics);
            }
        };

        try
        {
            await service.StartAsync(server, TimeSpan.FromSeconds(1));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            // 默认退回第一次样本：它没有 CPU/网络（差值类指标第一枪必然没基准），
            // 但内存、磁盘、负载、运行时长都是齐的，比整体失败强。
            ServerMetrics? metrics = firstSample;

            using (timeout.Token.Register(() => tcs.TrySetCanceled()))
            {
                try
                {
                    // 必须取 **tcs 的结果**（第二次采样，带 CPU 与网络差值）。
                    // 2026-10-18 修：原先这里用的是 firstSample，而 firstSample 恰恰是
                    // 没有差值的那一枪 —— 于是 cpu.usagePercent 和网络吞吐永远 null，
                    // 采集本身没问题，是取错了对象。
                    metrics = await tcs.Task;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // 10 秒还没拿到第二次采样：能拿到多少给多少，别让调用直接失败
                    metrics = firstSample;
                }
            }

            if (metrics == null)
            {
                throw new McpToolException(
                    $"已连上「{server.Name}」但没采到指标，可能是 /proc 不可读或采集脚本被限制。");
            }

            return McpToolResult.Ok(ToMetricsNode(server, metrics));
        }
        finally
        {
            service.Dispose();
        }
    }

    private static async Task<McpToolResult> ListFilesAsync(JsonObject args, ToolContext context)
    {
        var key = RequireString(args, "server");
        var path = OptionalString(args, "path") ?? "/";
        var server = await ResolveServerAsync(key);
        context.Target = Describe(server);

        var entries = await WithSftpAsync(server, client =>
        {
            var dir = SftpService.NormalizePath(path);
            var list = new List<JsonObject>();

            foreach (var file in client.ListDirectory(dir))
            {
                if (file.Name is "." or "..")
                {
                    continue;
                }

                list.Add(new JsonObject
                {
                    ["name"] = file.Name,
                    ["path"] = SftpService.NormalizePath(dir + "/" + file.Name),
                    ["type"] = file.IsDirectory ? "directory" : "file",
                    ["size"] = file.IsDirectory ? 0 : file.Length,
                    ["modified"] = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    ["permissions"] = SftpService.FormatPermissions(file)
                });
            }

            return list
                .OrderByDescending(e => e!["type"]!.GetValue<string>() == "directory")
                .ThenBy(e => e!["name"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
                .ToList();
        });

        return McpToolResult.Ok(new JsonObject
        {
            ["server"] = server.Name,
            ["path"] = SftpService.NormalizePath(path),
            ["count"] = entries.Count,
            ["entries"] = new JsonArray(entries.Cast<JsonNode>().ToArray())
        });
    }

    private static async Task<McpToolResult> ReadFileAsync(JsonObject args, ToolContext context)
    {
        var key = RequireString(args, "server");
        var path = RequireString(args, "path");
        var offset = OptionalLong(args, "offset", 0, long.MaxValue, 0);
        var maxBytes = OptionalInt(args, "maxBytes", 1, MaxReadBytes, 65_536);
        var server = await ResolveServerAsync(key);
        context.Target = Describe(server);
        context.Summary = $"{path} (offset {offset}, max {maxBytes} bytes)";

        var payload = await WithSftpAsync(server, client =>
        {
            var attributes = client.GetAttributes(path);
            if (attributes.IsDirectory)
            {
                throw new McpToolException($"「{path}」是目录，请用 list_files。");
            }

            var totalSize = attributes.Size;
            var start = Math.Min(offset, totalSize);

            using var stream = client.OpenRead(path);
            if (start > 0)
            {
                stream.Seek(start, SeekOrigin.Begin);
            }

            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            var remaining = (long)maxBytes;

            while (remaining > 0)
            {
                var read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining));
                if (read <= 0)
                {
                    break;
                }
                buffer.Write(chunk, 0, read);
                remaining -= read;
            }

            var bytes = buffer.ToArray();
            var endOfFile = start + bytes.Length >= totalSize;

            // 二进制文件（图片、压缩包、可执行文件）硬按 UTF-8 解只会得到乱码，
            // 既浪费模型上下文又毫无用处 —— 识别出来就只报元信息。
            if (LooksBinary(bytes))
            {
                return new JsonObject
                {
                    ["server"] = server.Name,
                    ["path"] = path,
                    ["sizeBytes"] = totalSize,
                    ["offset"] = start,
                    ["returnedBytes"] = 0,
                    ["truncated"] = !endOfFile,
                    ["endOfFile"] = endOfFile,
                    ["nextOffset"] = null,
                    ["binary"] = true,
                    ["message"] = "该文件看起来是二进制，未返回内容。请用 download_file 取到本机，或用 exec_command 在服务器侧处理。"
                };
            }

            // 按字节分页可能把末尾的多字节汉字切成半截：退回到完整字符边界，
            // 下一页从 nextOffset 接着读，既不丢字节也不出现替换字符。
            var contentLength = TrimIncompleteUtf8Length(bytes, endOfFile);
            if (contentLength == 0 && bytes.Length > 0 && !endOfFile)
            {
                // 页小到连一个字符都放不下时至少前进 1 字节，避免分页死循环
                contentLength = bytes.Length;
            }

            var nextOffset = endOfFile ? (long?)null : start + contentLength;

            return new JsonObject
            {
                ["server"] = server.Name,
                ["path"] = path,
                ["sizeBytes"] = totalSize,
                ["offset"] = start,
                ["returnedBytes"] = contentLength,
                ["truncated"] = !endOfFile,
                ["endOfFile"] = endOfFile,
                ["nextOffset"] = nextOffset,
                ["binary"] = false,
                ["content"] = Encoding.UTF8.GetString(bytes, 0, contentLength)
            };
        });

        return McpToolResult.Ok(payload);
    }

    /// <summary>
    /// 若这一页正好切在多字节 UTF-8 字符中间，返回可安全解码的长度。
    /// <para>只有整个文件已读完（<paramref name="endOfFile"/>）才允许把残缺尾巴交给解码器 ——
    /// 那是文件本身损坏，不是分页造成的。</para>
    /// </summary>
    private static int TrimIncompleteUtf8Length(byte[] bytes, bool endOfFile)
    {
        if (endOfFile || bytes.Length == 0)
        {
            return bytes.Length;
        }

        // 从末尾往前找首字节：连续字节（10xxxxxx）最多 3 个
        for (var back = 1; back <= Math.Min(4, bytes.Length); back++)
        {
            var b = bytes[^back];
            if ((b & 0xC0) == 0x80)
            {
                continue;
            }

            var needed = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
            return needed > back ? bytes.Length - back : bytes.Length;
        }

        return bytes.Length;
    }

    /// <summary>
    /// 粗略判断一段字节是不是二进制。
    /// <para>
    /// 判据刻意宽松：出现 NUL 直接判二进制；其余控制字符要占到 10% 以上才判，
    /// 否则带 ANSI 颜色的日志、含少量控制符的文本会被误伤。
    /// UTF-8 中文是多字节高位字符（0xE4 以上），不会落进控制字符区间。
    /// </para>
    /// </summary>
    private static bool LooksBinary(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return false;
        }

        var control = 0;
        foreach (var b in bytes)
        {
            if (b == 0)
            {
                return true;
            }

            // 放行 \t(9) \n(10) \v(11) \f(12) \r(13)
            if (b < 0x09 || (b > 0x0D && b < 0x20))
            {
                control++;
            }
        }

        return control > bytes.Length / 10;
    }

    // ───────────────────────── 变更类工具 ─────────────────────────

    private static async Task<McpToolResult> ExecCommandAsync(
        JsonObject args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var key = RequireString(args, "server");
        var command = RequireString(args, "command");
        var timeoutSeconds = OptionalInt(args, "timeoutSeconds", 1, 300, 60);

        var server = await ResolveServerAsync(key);
        context.Target = Describe(server);
        context.Summary = command;

        var run = await RunSshCommandAsync(server, command, timeoutSeconds, cancellationToken);

        return McpToolResult.Ok(new JsonObject
        {
            ["server"] = server.Name,
            ["host"] = server.Host,
            ["command"] = command,
            ["exitCode"] = run.ExitCode,
            ["stdout"] = Truncate(run.StdOut, MaxCommandOutputChars),
            ["stderr"] = Truncate(run.StdErr, MaxCommandOutputChars),
            ["timedOut"] = run.TimedOut
        });
    }

    private static async Task<McpToolResult> ExecCommandOnServersAsync(
        JsonObject args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var command = RequireString(args, "command");
        var timeoutSeconds = OptionalInt(args, "timeoutSeconds", 1, 300, 60);
        var concurrency = OptionalInt(args, "maxConcurrency", 1, 16, 4);
        var keys = OptionalStringList(args, "servers");
        var tag = OptionalString(args, "tag");

        var servers = await ConfigService.Instance.LoadServersAsync();
        List<ServerInfo> selected;

        if (keys.Count > 0)
        {
            selected = new List<ServerInfo>();
            foreach (var key in keys)
            {
                var resolved = Resolve(servers, key)
                               ?? throw new McpToolException(BuildNotFoundMessage(servers, key));
                if (!selected.Contains(resolved))
                {
                    selected.Add(resolved);
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(tag))
        {
            selected = servers.Where(s => ContainsTag(s.Tags, tag)).ToList();
            if (selected.Count == 0)
            {
                throw new McpToolException($"没有标签包含「{tag}」的服务器。");
            }
        }
        else
        {
            throw new McpToolException("必须提供 servers（id/名称数组）或 tag 之一来指定目标。");
        }

        context.Target = $"{selected.Count} 台：{string.Join(",", selected.Select(s => s.Name))}";
        context.Summary = command;

        // 结果按「名称(主机)」做 key：模型读起来直观，也不怕重名服务器互相覆盖
        var results = new JsonObject();
        var okCount = 0;
        var failCount = 0;

        await Parallel.ForEachAsync(
            selected,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrency,
                CancellationToken = cancellationToken
            },
            async (server, token) =>
            {
                JsonObject item;
                try
                {
                    var run = await RunSshCommandAsync(server, command, timeoutSeconds, token);
                    item = new JsonObject
                    {
                        ["ok"] = run.ExitCode == 0 && !run.TimedOut,
                        ["exitCode"] = run.ExitCode,
                        ["timedOut"] = run.TimedOut,
                        ["stdout"] = Truncate(run.StdOut, MaxCommandOutputChars),
                        ["stderr"] = Truncate(run.StdErr, MaxCommandOutputChars)
                    };
                }
                catch (Exception ex)
                {
                    item = new JsonObject
                    {
                        ["ok"] = false,
                        ["error"] = $"{ex.GetType().Name}: {ex.Message}"
                    };
                }

                var key = $"{server.Name} ({server.Host})";
                lock (results)
                {
                    results[key] = item;
                    if (item["ok"]!.GetValue<bool>())
                    {
                        okCount++;
                    }
                    else
                    {
                        failCount++;
                    }
                }
            });

        return McpToolResult.Ok(new JsonObject
        {
            ["command"] = command,
            ["total"] = selected.Count,
            ["succeeded"] = okCount,
            ["failed"] = failCount,
            ["results"] = results
        });
    }

    private static async Task<McpToolResult> WriteFileAsync(JsonObject args, ToolContext context)
    {
        var key = RequireString(args, "server");
        var path = RequireString(args, "path");
        var content = RequireString(args, "content", allowEmpty: true);
        var mode = (OptionalString(args, "mode") ?? "overwrite").ToLowerInvariant();

        if (mode is not ("overwrite" or "append"))
        {
            throw new McpToolException("参数「mode」只支持 overwrite 或 append。");
        }

        var server = await ResolveServerAsync(key);
        context.Target = Describe(server);
        context.Summary = $"{path} ({(mode == "append" ? "append" : "overwrite")}, {Encoding.UTF8.GetByteCount(content)} bytes)";

        var bytes = Encoding.UTF8.GetBytes(content);

        await WithSftpAsync(server, client =>
        {
            if (mode == "append")
            {
                using var stream = client.Open(path, FileMode.Append, FileAccess.Write);
                stream.Write(bytes, 0, bytes.Length);
            }
            else
            {
                using var stream = new MemoryStream(bytes);
                client.UploadFile(stream, path, canOverride: true);
            }

            return true;
        });

        return McpToolResult.Ok(new JsonObject
        {
            ["server"] = server.Name,
            ["path"] = path,
            ["mode"] = mode,
            ["bytesWritten"] = bytes.Length,
            ["success"] = true
        });
    }

    // ───────────────────────── 批量文件传输 ─────────────────────────

    private sealed class TransferReport
    {
        public int Files { get; set; }
        public long Bytes { get; set; }
        public int DirectoriesCreated { get; set; }
        public int Failed { get; set; }
        public string? Destination { get; set; }
        public bool SourceIsDirectory { get; set; }
        public List<JsonObject> Failures { get; } = new();

        public void Fail(string source, string destination, string error)
        {
            Failed++;
            if (Failures.Count < MaxTransferFailuresReported)
            {
                Failures.Add(new JsonObject
                {
                    ["source"] = source,
                    ["destination"] = destination,
                    ["error"] = error
                });
            }
        }

        public JsonObject ToResultNode(string serverName, string sourceLabel, string source, string destLabel, string destination, bool recursive, long elapsedMs)
            => new()
            {
                ["server"] = serverName,
                [sourceLabel] = source,
                [destLabel] = destination,
                ["recursive"] = recursive,
                ["filesTransferred"] = Files,
                ["bytesTransferred"] = Bytes,
                ["directoriesCreated"] = DirectoriesCreated,
                ["failed"] = Failed,
                ["failures"] = new JsonArray(Failures.Cast<JsonNode>().ToArray()),
                ["failuresTruncated"] = Failed > Failures.Count,
                ["elapsedMs"] = elapsedMs
            };
    }

    private static async Task<McpToolResult> UploadFileAsync(
        JsonObject args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var key = RequireString(args, "server");
        var localInput = RequireString(args, "localPath");
        var remoteInput = RequireString(args, "remotePath");
        var recursive = OptionalBool(args, "recursive", false);
        var createDirectories = OptionalBool(args, "createDirectories", true);
        var overwrite = OptionalBool(args, "overwrite", true);

        var localPath = ResolveLocalPath(localInput, "localPath");
        var remotePath = SftpService.NormalizePath(remoteInput);

        var isDirectory = Directory.Exists(localPath);
        if (!isDirectory && !File.Exists(localPath))
        {
            throw new McpToolException($"本机找不到「{localPath}」。");
        }

        if (isDirectory && !recursive)
        {
            throw new McpToolException($"「{localPath}」是目录，整体上传需要 recursive=true。");
        }

        var server = await ResolveServerAsync(key);
        context.Target = Describe(server);
        context.Summary = $"{localPath} → {remotePath}{(isDirectory ? "（目录）" : string.Empty)}";

        var watch = Stopwatch.StartNew();

        var report = await WithSftpAsync(server, client =>
        {
            var result = new TransferReport();

            if (isDirectory)
            {
                if (client.Exists(remotePath) && !client.GetAttributes(remotePath).IsDirectory)
                {
                    throw new McpToolException($"远端「{remotePath}」不是目录，不能作为整目录上传的目标。");
                }

                if (!createDirectories && !client.Exists(remotePath))
                {
                    throw new McpToolException($"远端目录「{remotePath}」不存在且 createDirectories=false。");
                }

                UploadLocalDirectory(client, localPath, remotePath, createDirectories, overwrite, result, cancellationToken);
            }
            else
            {
                var remoteFile = remotePath;

                // 目标是已存在的目录时，按本地文件名放进去（对齐 scp 的直觉）
                if (client.Exists(remoteFile) && client.GetAttributes(remoteFile).IsDirectory)
                {
                    remoteFile = CombineRemote(remoteFile, Path.GetFileName(localPath));
                }

                result.Destination = remoteFile;

                if (createDirectories)
                {
                    result.DirectoriesCreated += EnsureRemoteDirectory(client, SftpService.ParentOf(remoteFile));
                }

                if (!overwrite && client.Exists(remoteFile))
                {
                    throw new McpToolException($"远端已存在「{remoteFile}」且 overwrite=false。");
                }

                using var fs = File.OpenRead(localPath);
                client.UploadFile(fs, remoteFile, canOverride: true);

                result.Files = 1;
                result.Bytes = fs.Length;
            }

            return result;
        }, cancellationToken);

        return McpToolResult.Ok(report.ToResultNode(
            server.Name, "localPath", localPath, "remotePath", report.Destination ?? remotePath,
            isDirectory, watch.ElapsedMilliseconds));
    }

    private static void UploadLocalDirectory(
        SftpClient client,
        string localDir,
        string remoteDir,
        bool createDirectories,
        bool overwrite,
        TransferReport report,
        CancellationToken token)
    {
        if (createDirectories)
        {
            report.DirectoriesCreated += EnsureRemoteDirectory(client, remoteDir);
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            // 不跟符号链接：Windows 上的目录联接可能成环
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false
        };

        // 先建目录：这样空目录也能忠实还原，而不是只留下有文件的那些
        foreach (var dir in Directory.EnumerateDirectories(localDir, "*", options))
        {
            token.ThrowIfCancellationRequested();

            if (!createDirectories)
            {
                continue;
            }

            var relative = Path.GetRelativePath(localDir, dir).Replace('\\', '/');
            report.DirectoriesCreated += EnsureRemoteDirectory(client, CombineRemote(remoteDir, relative));
        }

        foreach (var file in Directory.EnumerateFiles(localDir, "*", options))
        {
            token.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(localDir, file).Replace('\\', '/');
            var remoteFile = CombineRemote(remoteDir, relative);

            try
            {
                if (createDirectories)
                {
                    report.DirectoriesCreated += EnsureRemoteDirectory(client, SftpService.ParentOf(remoteFile));
                }

                if (!overwrite && client.Exists(remoteFile))
                {
                    report.Fail(file, remoteFile, "远端已存在且 overwrite=false。");
                    continue;
                }

                using var fs = File.OpenRead(file);
                client.UploadFile(fs, remoteFile, canOverride: true);

                report.Files++;
                report.Bytes += fs.Length;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                report.Fail(file, remoteFile, ex.Message);
            }
        }
    }

    private static async Task<McpToolResult> DownloadFileAsync(
        JsonObject args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var key = RequireString(args, "server");
        var remoteInput = RequireString(args, "remotePath");
        var localInput = RequireString(args, "localPath");
        var recursive = OptionalBool(args, "recursive", false);
        var createDirectories = OptionalBool(args, "createDirectories", true);
        var overwrite = OptionalBool(args, "overwrite", true);

        var server = await ResolveServerAsync(key);
        var remotePath = SftpService.NormalizePath(remoteInput);
        var localPath = ResolveLocalPath(localInput, "localPath");

        context.Target = Describe(server);
        context.Summary = $"{remotePath} → {localPath}";

        var watch = Stopwatch.StartNew();

        var report = await WithSftpAsync(server, client =>
        {
            var attributes = client.GetAttributes(remotePath);
            var result = new TransferReport();

            if (attributes.IsDirectory)
            {
                if (!recursive)
                {
                    throw new McpToolException($"远端「{remotePath}」是目录，整体下载需要 recursive=true。");
                }

                result.SourceIsDirectory = true;

                if (File.Exists(localPath))
                {
                    throw new McpToolException($"本地「{localPath}」是已存在的文件，不能作为目录目标。");
                }

                if (!createDirectories && !Directory.Exists(localPath))
                {
                    throw new McpToolException($"本地目录「{localPath}」不存在且 createDirectories=false。");
                }

                if (createDirectories && !Directory.Exists(localPath))
                {
                    Directory.CreateDirectory(localPath);
                    result.DirectoriesCreated++;
                }

                result.Destination = localPath;
                DownloadRemoteDirectory(client, remotePath, localPath, overwrite, result, cancellationToken);
            }
            else
            {
                // 目标是已存在的目录时，按远端文件名落进去，符合"下载到某文件夹"的直觉
                var target = Directory.Exists(localPath)
                    ? Path.Combine(localPath, Path.GetFileName(remotePath))
                    : localPath;

                result.Destination = target;

                if (createDirectories)
                {
                    EnsureLocalDirectory(target, result);
                }

                if (!overwrite && File.Exists(target))
                {
                    throw new McpToolException($"本地已存在「{target}」且 overwrite=false。");
                }

                DownloadRemoteFile(client, remotePath, target, attributes.Size, result);
            }

            return result;
        }, cancellationToken);

        return McpToolResult.Ok(report.ToResultNode(
            server.Name, "remotePath", remotePath, "localPath", report.Destination ?? localPath,
            report.SourceIsDirectory, watch.ElapsedMilliseconds));
    }

    private static void DownloadRemoteDirectory(
        SftpClient client,
        string remoteDir,
        string localDir,
        bool overwrite,
        TransferReport report,
        CancellationToken token)
    {
        foreach (var entry in client.ListDirectory(remoteDir))
        {
            token.ThrowIfCancellationRequested();

            if (entry.Name is "." or "..")
            {
                continue;
            }

            var remoteEntry = CombineRemote(remoteDir, entry.Name);
            var localEntry = Path.Combine(localDir, entry.Name);

            // 目录软链不跟：既可能成环，也可能把下载范围带出目标目录
            if (entry.IsSymbolicLink && entry.IsDirectory)
            {
                continue;
            }

            if (entry.IsDirectory)
            {
                try
                {
                    if (!Directory.Exists(localEntry))
                    {
                        Directory.CreateDirectory(localEntry);
                        report.DirectoriesCreated++;
                    }

                    DownloadRemoteDirectory(client, remoteEntry, localEntry, overwrite, report, token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    report.Fail(remoteEntry, localEntry, ex.Message);
                }
            }
            else
            {
                try
                {
                    if (!overwrite && File.Exists(localEntry))
                    {
                        report.Fail(remoteEntry, localEntry, "本地已存在且 overwrite=false。");
                        continue;
                    }

                    EnsureLocalDirectory(localEntry, report);
                    DownloadRemoteFile(client, remoteEntry, localEntry, entry.Length, report);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    report.Fail(remoteEntry, localEntry, ex.Message);
                }
            }
        }
    }

    private static void DownloadRemoteFile(SftpClient client, string remoteFile, string localFile, long size, TransferReport report)
    {
        using var fs = File.Create(localFile);
        client.DownloadFile(remoteFile, fs);

        report.Files++;
        report.Bytes += size >= 0 ? size : fs.Length;
    }

    private static void EnsureLocalDirectory(string localFile, TransferReport report)
    {
        var dir = Path.GetDirectoryName(localFile);
        if (string.IsNullOrEmpty(dir) || Directory.Exists(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        report.DirectoriesCreated++;
    }

    /// <summary>逐级创建远端目录，返回新建了几层（已存在的不计）。</summary>
    private static int EnsureRemoteDirectory(SftpClient client, string dir)
    {
        dir = SftpService.NormalizePath(dir);
        if (dir == "/" || client.Exists(dir))
        {
            return 0;
        }

        var missing = new Stack<string>();
        var current = dir;
        while (current != "/" && !client.Exists(current))
        {
            missing.Push(current);
            current = SftpService.ParentOf(current);
        }

        var created = 0;
        while (missing.Count > 0)
        {
            var path = missing.Pop();
            try
            {
                client.CreateDirectory(path);
                created++;
            }
            catch (Exception)
            {
                // 并发下可能刚被别人建好；确实建不出来就让后面的上传去报错
                if (!client.Exists(path))
                {
                    throw;
                }
            }
        }

        return created;
    }

    private static string CombineRemote(string dir, string relative)
        => SftpService.NormalizePath(dir) == "/"
            ? "/" + relative
            : SftpService.NormalizePath(dir) + "/" + relative;

    /// <summary>把本机路径展开成绝对路径（支持 `~`，其余交给系统规则）。</summary>
    private static string ResolveLocalPath(string input, string parameterName)
    {
        var path = input.Trim();

        if (path == "~")
        {
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        else if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            throw new McpToolException($"参数「{parameterName}」不是有效路径：{ex.Message}");
        }
    }

    // ───────────────────────── SSH 执行 ─────────────────────────

    private sealed record RemoteRun(int ExitCode, string StdOut, string StdErr, bool TimedOut);

    /// <summary>
    /// 在 Linux 服务器上跑一条命令，带**两层**超时：
    /// <list type="bullet">
    ///   <item>SSH.NET 的 <c>CommandTimeout</c>（正常情况下由它抛超时）；</item>
    ///   <item>外层 <see cref="Task.WhenAny"/> 硬兜底 —— 同步 <c>Execute()</c> 在某些版本的
    ///         SSH.NET 上不会老老实实响应 CommandTimeout，没有这层就会永久挂住，
    ///         而 MCP 客户端那边只会看到"一直没返回"。</item>
    /// </list>
    /// </summary>
    private static async Task<RemoteRun> RunSshCommandAsync(
        ServerInfo server,
        string command,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (server.OsType != ServerOsType.Linux)
        {
            throw new McpToolException($"服务器「{server.Name}」标记为 Windows，命令执行目前只支持 Linux（SSH）。");
        }

        var work = Task.Run(() => RunBlocking(server, command, timeoutSeconds), cancellationToken);

        // 兜底计时器**不挂 cancellationToken**：一旦取消，Delay 会变成 canceled task，
        // 被下面判成"超时"，把"请求被取消"误报成"命令超时"，误导排查方向。
        var guard = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds + 10));
        var finished = await Task.WhenAny(work, guard);

        if (finished != work)
        {
            // 后台那条连接已不受控，只能放弃等待；SSH.NET 自身最终也会因 CommandTimeout 退出
            throw new McpToolException($"命令在 {timeoutSeconds} 秒内没有结束，已放弃等待（连接会被回收）。");
        }

        return await work;
    }

    private static RemoteRun RunBlocking(ServerInfo server, string command, int timeoutSeconds)
    {
        try
        {
            using var client = new SshClient(server.Host, server.Port, server.Username, server.Password);
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(Math.Min(15, Math.Max(5, timeoutSeconds)));
            client.Connect();

            ServerConnectionTracker.MarkConnected(server);

            using var cmd = client.CreateCommand(command);
            cmd.CommandTimeout = TimeSpan.FromSeconds(timeoutSeconds);

            try
            {
                cmd.Execute();
            }
            catch (Renci.SshNet.Common.SshOperationTimeoutException)
            {
                return new RemoteRun(-1, string.Empty, $"命令超过 {timeoutSeconds} 秒未结束，已中断。", TimedOut: true);
            }

            return new RemoteRun(
                cmd.ExitStatus ?? -1,
                cmd.Result ?? string.Empty,
                cmd.Error ?? string.Empty,
                TimedOut: false);
        }
        catch (Exception ex)
        {
            throw new McpToolException($"连接或执行失败：{ex.Message}");
        }
    }

    // ───────────────────────── SFTP ─────────────────────────

    /// <summary>建一条独立 SFTP 连接，执行一次操作后断开。</summary>
    private static async Task<T> WithSftpAsync<T>(
        ServerInfo server,
        Func<SftpClient, T> action,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var client = new SftpClient(server.Host, server.Port, server.Username, server.Password);
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(10);
                client.OperationTimeout = TimeSpan.FromSeconds(60);
                client.Connect();

                ServerConnectionTracker.MarkConnected(server);

                var result = action(client);

                if (client.IsConnected)
                {
                    client.Disconnect();
                }

                return result;
            }
            catch (McpToolException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new McpToolException($"SFTP 操作失败：{ex.Message}");
            }
        }, cancellationToken);
    }

    // ───────────────────────── 用户扩展 ─────────────────────────

    /// <summary>
    /// 执行一条用户扩展命令。
    /// <para>
    /// 参数替换在 <see cref="ExtensionService.Substitute"/> 里做（带 shell 引用与类型校验），
    /// 这里只负责解析服务器、跑命令、把结果按内置工具的格式返回 —— 输出截断规则也一并复用，
    /// 免得扩展把模型上下文一次打爆。
    /// </para>
    /// </summary>
    private static async Task<McpToolResult> RunExtensionAsync(
        string toolName,
        JsonObject args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var resolved = ExtensionService.Instance.Resolve(toolName)
                       ?? throw new McpToolException(
                           $"扩展工具 {toolName} 不存在，或对应扩展已被禁用/卸载。请让用户在「系统设置 → 扩展」里检查。");

        var (pack, command) = resolved;

        var key = RequireString(args, "server");
        var server = await ResolveServerAsync(key);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in command.Parameters)
        {
            var node = args[parameter.Name];
            if (node == null)
            {
                continue;
            }

            // 字符串取原值，数字/布尔取 JSON 字面量（"50" / "true"），统一交给 Normalize 校验
            values[parameter.Name] = node.GetValueKind() == JsonValueKind.String
                ? node.GetValue<string>()
                : node.ToJsonString();
        }

        string shell;
        try
        {
            shell = ExtensionService.Substitute(command, values);
        }
        catch (ExtensionException ex)
        {
            throw new McpToolException(ex.Message);
        }

        context.Target = Describe(server);
        context.Summary = $"[{pack.Id}/{command.Id}] {shell}";

        var timeoutSeconds = Math.Clamp(
            command.TimeoutSeconds <= 0 ? 60 : command.TimeoutSeconds, 5, 600);

        var run = await RunSshCommandAsync(server, shell, timeoutSeconds, cancellationToken);

        return McpToolResult.Ok(new JsonObject
        {
            ["server"] = server.Name,
            ["host"] = server.Host,
            ["extension"] = pack.Id,
            ["command"] = command.Id,
            ["shell"] = shell,
            ["exitCode"] = run.ExitCode,
            ["stdout"] = Truncate(run.StdOut, MaxCommandOutputChars),
            ["stderr"] = Truncate(run.StdErr, MaxCommandOutputChars),
            ["timedOut"] = run.TimedOut
        });
    }

    // ───────────────────────── 服务器解析 ─────────────────────────

    private static async Task<ServerInfo> ResolveServerAsync(string key)
    {
        var servers = await ConfigService.Instance.LoadServersAsync();
        return Resolve(servers, key) ?? throw new McpToolException(BuildNotFoundMessage(servers, key));
    }

    /// <summary>
    /// 把用户/模型给的"id 或名称"落到唯一一台服务器。
    /// 匹配优先级：GUID → 名称精确 → 主机精确 → 名称包含（仅当唯一）。
    /// </summary>
    private static ServerInfo? Resolve(List<ServerInfo> servers, string key)
    {
        var k = key.Trim();
        if (k.Length == 0)
        {
            return null;
        }

        if (Guid.TryParse(k, out var id))
        {
            var byId = servers.FirstOrDefault(s => s.Id == id);
            if (byId != null)
            {
                return byId;
            }
        }

        var byName = servers.FirstOrDefault(s => string.Equals(s.Name, k, StringComparison.OrdinalIgnoreCase));
        if (byName != null)
        {
            return byName;
        }

        var byHost = servers.FirstOrDefault(s => string.Equals(s.Host, k, StringComparison.OrdinalIgnoreCase));
        if (byHost != null)
        {
            return byHost;
        }

        var partial = servers
            .Where(s => s.Name.Contains(k, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return partial.Count == 1 ? partial[0] : null;
    }

    private static string BuildNotFoundMessage(List<ServerInfo> servers, string key)
    {
        if (servers.Count == 0)
        {
            return "还没有保存任何服务器。请让用户在 ServerOpsHelper 界面里先添加服务器。";
        }

        var partial = servers
            .Where(s => s.Name.Contains(key, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Name)
            .Take(8)
            .ToList();

        if (partial.Count > 1)
        {
            return $"「{key}」匹配到多台服务器：{string.Join("、", partial)}。请改用完整的 id 或名称。";
        }

        var available = string.Join("、", servers.Take(10).Select(s => s.Name));
        return $"找不到服务器「{key}」。当前已保存：{available}{(servers.Count > 10 ? " 等" : string.Empty)}。";
    }

    // ───────────────────────── 输出构造 ─────────────────────────

    /// <summary>
    /// 服务器的对外表示。**显式列字段**，绝不序列化整个 <see cref="ServerInfo"/> ——
    /// 那样有一天加了新敏感字段就会自动泄漏出去。
    /// </summary>
    private static JsonObject ToServerNode(ServerInfo server)
        => new()
        {
            ["id"] = server.Id.ToString(),
            ["name"] = server.Name,
            ["host"] = server.Host,
            ["port"] = server.Port,
            ["username"] = server.Username,
            ["osType"] = server.OsType.ToString(),
            ["tags"] = server.Tags,
            ["description"] = server.Description,
            ["lastConnectedAt"] = server.LastConnectedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        };

    private static JsonObject ToMetricsNode(ServerInfo server, ServerMetrics m)
        => new()
        {
            ["server"] = server.Name,
            ["host"] = server.Host,
            ["sampledAt"] = m.SampledAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["hostname"] = m.HostName,
            ["os"] = m.OsName,
            ["uptime"] = m.UptimeText,
            ["cpu"] = new JsonObject
            {
                ["cores"] = m.CpuCores,
                ["usagePercent"] = m.HasCpu ? Math.Round(m.CpuUsagePercent, 1) : null,
                ["load1"] = Math.Round(m.Load1, 2),
                ["load5"] = Math.Round(m.Load5, 2),
                ["load15"] = Math.Round(m.Load15, 2)
            },
            ["memory"] = new JsonObject
            {
                ["totalMb"] = Math.Round(m.MemTotalMb),
                ["usedMb"] = Math.Round(m.MemUsedMb),
                ["usedPercent"] = Math.Round(m.MemUsedPercent, 1)
            },
            ["disk"] = new JsonObject
            {
                ["mount"] = "/",
                ["totalMb"] = Math.Round(m.DiskTotalMb),
                ["usedMb"] = Math.Round(m.DiskUsedMb),
                ["usedPercent"] = Math.Round(m.DiskUsedPercent, 1)
            },
            ["network"] = new JsonObject
            {
                ["interface"] = m.NetInterface,
                ["rxBytesPerSec"] = m.HasNet ? Math.Round(m.NetRxBytesPerSec) : null,
                ["txBytesPerSec"] = m.HasNet ? Math.Round(m.NetTxBytesPerSec) : null
            }
        };

    private static string Describe(ServerInfo server)
        => $"{server.Name} ({server.Host}:{server.Port})";

    // ───────────────────────── 参数读取 ─────────────────────────

    private static string RequireString(JsonObject args, string name, bool allowEmpty = false)
    {
        var value = args[name]?.GetValue<string>();
        if (value == null || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
        {
            throw new McpToolException($"缺少必需参数「{name}」。");
        }
        return value;
    }

    private static string? OptionalString(JsonObject args, string name)
    {
        if (args[name] is not JsonNode node)
        {
            return null;
        }

        try
        {
            var value = node.GetValue<string>();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int OptionalInt(JsonObject args, string name, int min, int max, int defaultValue)
    {
        if (args[name] is not JsonNode node)
        {
            return defaultValue;
        }

        int value;
        try
        {
            value = node.GetValue<int>();
        }
        catch (Exception)
        {
            var asString = node.GetValue<string?>();
            if (!int.TryParse(asString, out value))
            {
                throw new McpToolException($"参数「{name}」必须是整数。");
            }
        }

        return Math.Clamp(value, min, max);
    }

    private static long OptionalLong(JsonObject args, string name, long min, long max, long defaultValue)
    {
        if (args[name] is not JsonNode node)
        {
            return defaultValue;
        }

        long value;
        try
        {
            value = node.GetValue<long>();
        }
        catch (Exception)
        {
            var asString = node.GetValue<string?>();
            if (!long.TryParse(asString, out value))
            {
                throw new McpToolException($"参数「{name}」必须是整数。");
            }
        }

        return Math.Clamp(value, min, max);
    }

    private static bool OptionalBool(JsonObject args, string name, bool defaultValue)
    {
        if (args[name] is not JsonNode node)
        {
            return defaultValue;
        }

        try
        {
            return node.GetValue<bool>();
        }
        catch (Exception)
        {
            var asString = node.GetValue<string?>();
            if (bool.TryParse(asString, out var value))
            {
                return value;
            }

            throw new McpToolException($"参数「{name}」必须是 true 或 false。");
        }
    }

    private static List<string> OptionalStringList(JsonObject args, string name)
    {
        var result = new List<string>();

        if (args[name] is not JsonArray array)
        {
            // 容许模型传单个字符串，省得为了一个目标包一层数组
            var single = OptionalString(args, name);
            if (single != null)
            {
                result.Add(single);
            }
            return result;
        }

        foreach (var item in array)
        {
            var value = item?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value.Trim());
            }
        }

        return result;
    }

    private static bool ContainsTag(string tags, string tag)
        => tags.Split(new[] { ',', '，', ';', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(t => string.Equals(t.Trim(), tag.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
        {
            return text;
        }

        return text[..max] + $"\n...（输出已截断，原长 {text.Length} 字符）";
    }
}
