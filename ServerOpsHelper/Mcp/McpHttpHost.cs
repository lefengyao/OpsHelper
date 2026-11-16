using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ServerOpsHelper.Mcp;

/// <summary>
/// 本地 HTTP 传输（MCP Streamable HTTP 的简化实现）。
/// <para>
/// 为什么不直接用 <see cref="HttpListener"/>：它对 URL 前缀有 ACL 要求，
/// 非管理员运行时装到某些机器上会直接抛"拒绝访问"；而这里只需要一个 <c>POST /mcp</c>，
/// 用 <see cref="TcpListener"/> 手写几十行 HTTP 反而更省事、行为更可预测。
/// </para>
/// <para>
/// 安全边界（三条，缺一不可）：
/// <list type="number">
///   <item><b>只绑 127.0.0.1</b>：不给"改成 0.0.0.0"的选项，局域网连不上。</item>
///   <item><b>必须带 Bearer 令牌</b>：令牌 256 位随机，DPAPI 加密落盘。</item>
///   <item><b>每次工具调用都进审计日志</b>：见 <see cref="McpAuditLog"/>。</item>
/// </list>
/// </para>
/// </summary>
internal sealed class McpHttpHost : IDisposable
{
    private const int MaxHeaderBytes = 32 * 1024;
    private const int MaxBodyBytes = 8 * 1024 * 1024;
    private const int ClientTimeoutMs = 60_000;

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly McpServerCore _core = new();
    private readonly object _gate = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private string _token = string.Empty;

    public bool IsRunning { get; private set; }

    public int Port { get; private set; }

    /// <summary>启动失败的原因（端口被占用等），供设置页展示。</summary>
    public string? LastError { get; private set; }

    /// <summary>已经处理过的请求数（仅用于状态展示，不做业务判断）。</summary>
    public long RequestCount => Interlocked.Read(ref _requestCount);

    private long _requestCount;

    public string Endpoint => $"http://127.0.0.1:{Port}/mcp";

    /// <summary>
    /// 启动监听。已在运行时先停掉旧的（改端口 / 换令牌都走这条路）。
    /// </summary>
    public void Start(int port, string token)
    {
        Stop();

        lock (_gate)
        {
            LastError = null;
            _token = token ?? string.Empty;

            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();

                _listener = listener;
                Port = port;
                _cts = new CancellationTokenSource();
                IsRunning = true;
                _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            }
            catch (SocketException ex)
            {
                IsRunning = false;
                LastError = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? $"端口 {port} 已被占用，换一个端口再试。"
                    : $"无法监听 127.0.0.1:{port}：{ex.Message}";
                throw new InvalidOperationException(LastError, ex);
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!IsRunning && _listener == null)
            {
                return;
            }

            IsRunning = false;

            try
            {
                _cts?.Cancel();
            }
            catch (Exception)
            {
            }

            try
            {
                _listener?.Stop();
            }
            catch (Exception)
            {
            }

            _listener = null;
            _acceptLoop = null;

            try
            {
                _cts?.Dispose();
            }
            catch (Exception)
            {
            }

            _cts = null;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                // 监听被 Stop() 关掉时会走到这里
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client, token), token);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        Interlocked.Increment(ref _requestCount);

        try
        {
            using (client)
            {
                client.ReceiveTimeout = ClientTimeoutMs;
                client.SendTimeout = ClientTimeoutMs;

                await Task.Run(() => HandleClient(client, token), token);
            }
        }
        catch (Exception)
        {
            // 单个连接的任何异常都不该影响监听循环
        }
    }

    private void HandleClient(TcpClient client, CancellationToken token)
    {
        using var stream = new BufferedStream(client.GetStream(), 8192);

        HttpRequest? request;

        try
        {
            request = ReadRequest(stream);
        }
        catch (RequestBodyTooLargeException)
        {
            // 明确告诉调用方"接下来该换什么办法"，而不是只丢一个 400 Bad Request
            var hint = Encoding.UTF8.GetBytes(
                $"请求体超过 {MaxBodyBytes / (1024 * 1024)} MiB 上限。" +
                "传大文件请改用 upload_file（按本机路径直接走 SFTP），" +
                "或把 write_file 拆成多次 mode=append。");
            WriteResponse(stream, 413, "text/plain; charset=utf-8", hint);
            return;
        }

        if (request == null)
        {
            WriteResponse(stream, 400, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Bad Request"));
            return;
        }

        // 路径宽容一点：/mcp 是规范路径，/ 也认（有的客户端配 baseUrl 时会省掉后缀）
        if (request.Method == "OPTIONS")
        {
            WriteResponse(stream, 204, null, Array.Empty<byte>());
            return;
        }

        if (!request.Path.Equals("/mcp", StringComparison.OrdinalIgnoreCase)
            && !request.Path.Equals("/", StringComparison.OrdinalIgnoreCase))
        {
            WriteResponse(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not Found"));
            return;
        }

        if (request.Method != "POST")
        {
            // MCP 允许服务端不提供 SSE 流；给个明确的 405，客户端会退回纯 POST
            WriteResponse(
                stream, 405, "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes("Only POST /mcp is supported."),
                new Dictionary<string, string> { ["Allow"] = "POST, OPTIONS" });
            return;
        }

        if (!IsAuthorized(request.Headers))
        {
            WriteResponse(
                stream, 401, "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes("""{"error":"invalid or missing bearer token"}"""),
                new Dictionary<string, string> { ["WWW-Authenticate"] = "Bearer" });
            return;
        }

        JsonObject? response;
        string bodyText;

        try
        {
            bodyText = Encoding.UTF8.GetString(request.Body);
            var node = JsonNode.Parse(bodyText);

            if (node is JsonObject obj)
            {
                // HandleAsync 是异步的（SSH 操作都在里面），同步等待即可
                response = _core.HandleAsync(obj, "http", token).GetAwaiter().GetResult();
            }
            else
            {
                response = BuildParseError("消息必须是 JSON 对象。");
            }
        }
        catch (JsonException ex)
        {
            response = BuildParseError($"JSON 解析失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = null,
                ["error"] = new JsonObject
                {
                    ["code"] = -32603,
                    ["message"] = $"Internal error：{ex.Message}"
                }
            };
        }

        if (response == null)
        {
            // 通知：MCP 规定回 202 且无正文
            WriteResponse(stream, 202, null, Array.Empty<byte>());
            return;
        }

        var json = response.ToJsonString(CompactJson);

        // 客户端只接受 SSE 时按 SSE 帧回；两种都接受时优先纯 JSON（更省事）
        var acceptsJson = AcceptsJson(request.Headers);
        if (acceptsJson)
        {
            WriteResponse(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
        }
        else
        {
            var sse = $"event: message\ndata: {json}\n\n";
            WriteResponse(stream, 200, "text/event-stream; charset=utf-8", Encoding.UTF8.GetBytes(sse));
        }
    }

    // ───────────────────────── 鉴权 ─────────────────────────

    private bool IsAuthorized(Dictionary<string, string> headers)
    {
        if (string.IsNullOrEmpty(_token))
        {
            return false;
        }

        if (!headers.TryGetValue("authorization", out var authorization))
        {
            return false;
        }

        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TokenEquals(authorization[prefix.Length..].Trim(), _token);
    }

    /// <summary>定长比较，避免逐字符提前返回泄漏前缀（本地场景意义不大，但没成本）。</summary>
    private static bool TokenEquals(string provided, string expected)
    {
        var a = Encoding.UTF8.GetBytes(provided);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static bool AcceptsJson(Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("accept", out var accept) || string.IsNullOrWhiteSpace(accept))
        {
            return true;
        }

        return accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
               || accept.Contains("*/*", StringComparison.OrdinalIgnoreCase)
               || accept.Contains("text/*", StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────── HTTP 读写 ─────────────────────────

    private sealed class HttpRequest
    {
        public string Method { get; init; } = "GET";
        public string Path { get; init; } = "/";
        public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public byte[] Body { get; init; } = Array.Empty<byte>();
    }

    /// <summary>请求体超过 <see cref="MaxBodyBytes"/>：要回 413 并给出替代方案，不能当坏请求吞掉。</summary>
    private sealed class RequestBodyTooLargeException : Exception
    {
    }

    private static HttpRequest? ReadRequest(Stream stream)
    {
        var headerText = ReadHeaderBlock(stream);
        if (headerText == null)
        {
            return null;
        }

        var lines = headerText.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0)
        {
            return null;
        }

        var requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            headers[key] = value;
        }

        byte[] body;
        try
        {
            body = ReadBody(stream, headers);
        }
        catch (RequestBodyTooLargeException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        // 去掉查询串（MCP 不用，但客户端可能带 ?foo=bar）
        var path = requestLine[1];
        var questionMark = path.IndexOf('?');
        if (questionMark >= 0)
        {
            path = path[..questionMark];
        }

        return new HttpRequest
        {
            Method = requestLine[0].ToUpperInvariant(),
            Path = path,
            Headers = headers,
            Body = body
        };
    }

    /// <summary>读到空行为止（含结尾的 CRLFCRLF），返回头部文本。</summary>
    private static string? ReadHeaderBlock(Stream stream)
    {
        var buffer = new MemoryStream();
        var tail = new byte[4];
        var tailLen = 0;

        while (buffer.Length < MaxHeaderBytes)
        {
            var b = stream.ReadByte();
            if (b < 0)
            {
                return null;
            }

            buffer.WriteByte((byte)b);

            if (tailLen < 4)
            {
                tail[tailLen++] = (byte)b;
            }
            else
            {
                tail[0] = tail[1];
                tail[1] = tail[2];
                tail[2] = tail[3];
                tail[3] = (byte)b;
            }

            if (tailLen == 4 && tail[0] == '\r' && tail[1] == '\n' && tail[2] == '\r' && tail[3] == '\n')
            {
                return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length - 4);
            }
        }

        return null;
    }

    private static byte[] ReadBody(Stream stream, Dictionary<string, string> headers)
    {
        if (headers.TryGetValue("transfer-encoding", out var te)
            && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            return ReadChunkedBody(stream);
        }

        if (!headers.TryGetValue("content-length", out var lengthText)
            || !int.TryParse(lengthText, out var length)
            || length <= 0)
        {
            return Array.Empty<byte>();
        }

        if (length > MaxBodyBytes)
        {
            throw new RequestBodyTooLargeException();
        }

        var body = new byte[length];
        ReadExactly(stream, body);
        return body;
    }

    private static byte[] ReadChunkedBody(Stream stream)
    {
        var result = new MemoryStream();

        while (true)
        {
            var sizeLine = ReadAsciiLine(stream);
            if (sizeLine == null)
            {
                break;
            }

            var semicolon = sizeLine.IndexOf(';');
            if (semicolon >= 0)
            {
                sizeLine = sizeLine[..semicolon];
            }

            if (!int.TryParse(sizeLine.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var size) || size < 0)
            {
                break;
            }

            if (size == 0)
            {
                ReadAsciiLine(stream);  // 结尾的空行 / trailer
                break;
            }

            var chunk = new byte[size];
            ReadExactly(stream, chunk);
            result.Write(chunk);

            if (result.Length > MaxBodyBytes)
            {
                throw new RequestBodyTooLargeException();
            }

            ReadAsciiLine(stream);      // 每块后面的 CRLF
        }

        return result.ToArray();
    }

    private static string? ReadAsciiLine(Stream stream)
    {
        var bytes = new List<byte>(32);
        while (bytes.Count < 1024)
        {
            var b = stream.ReadByte();
            if (b < 0)
            {
                return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            }

            if (b == '\n')
            {
                break;
            }

            if (b != '\r')
            {
                bytes.Add((byte)b);
            }
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read <= 0)
            {
                throw new EndOfStreamException("请求体不完整。");
            }
            offset += read;
        }
    }

    private static void WriteResponse(
        Stream stream,
        int statusCode,
        string? contentType,
        byte[] body,
        Dictionary<string, string>? extraHeaders = null)
    {
        var sb = new StringBuilder(192);
        sb.Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(ReasonPhrase(statusCode)).Append("\r\n");
        sb.Append("Server: ServerOpsHelper-MCP\r\n");

        if (!string.IsNullOrEmpty(contentType))
        {
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        }

        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        sb.Append("Connection: close\r\n");

        if (extraHeaders != null)
        {
            foreach (var (key, value) in extraHeaders)
            {
                sb.Append(key).Append(": ").Append(value).Append("\r\n");
            }
        }

        sb.Append("\r\n");

        var head = Encoding.UTF8.GetBytes(sb.ToString());
        stream.Write(head, 0, head.Length);

        if (body.Length > 0)
        {
            stream.Write(body, 0, body.Length);
        }

        stream.Flush();
    }

    private static string ReasonPhrase(int statusCode) => statusCode switch
    {
        200 => "OK",
        202 => "Accepted",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        404 => "Not Found",
        405 => "Method Not Allowed",
        413 => "Payload Too Large",
        _ => "Internal Server Error"
    };

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

    public void Dispose() => Stop();
}
