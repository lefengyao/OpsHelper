using Renci.SshNet;
using Renci.SshNet.Common;
using ServerOpsHelper.Models;
using System;
using System.Text;
using System.Threading.Tasks;

namespace ServerOpsHelper.Services;

/// <summary>
/// SSH 交互式终端会话（PTY）：**一条持久连接** + ShellStream，输入输出实时双向转发。
/// <para>
/// 与旧的「单命令执行」（<see cref="RemoteExecutionService"/>，每条命令新建一次连接）
/// 完全是两回事 —— 这是给真终端用的：vim、htop、Tab 补全、上下键历史、Ctrl+C
/// 这些都要一个**活的 shell 会话**才有可能。
/// </para>
/// <para>
/// 线程约定：SSH.NET 的 <c>DataReceived</c> 在后台线程回调，本类不做 UI 调度，
/// 订阅方（页面）自己 Dispatcher。所有公开方法都线程安全由页面单线程调用。
/// </para>
/// </summary>
public sealed class RemoteTerminalSession : IDisposable
{
    private SshClient? _client;
    private ShellStream? _shell;
    // SSH 数据是按包到达的，一个 UTF-8 多字节字符可能被拆在两个包里。
    // 用**增量解码器**跨包缓冲，中文/emoji 才不会被截断成乱码。
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private bool _disposed;

    /// <summary>远端输出（已增量 UTF-8 解码，含 ANSI 转义序列，原样透传给终端渲染）。</summary>
    public event Action<string>? OutputReceived;

    /// <summary>连接状态提示（连接中 / 已连接 / 已断开 / 失败原因）。</summary>
    public event Action<string>? StatusChanged;

    /// <summary>连接断开（含远端关闭与网络异常），触发一次。</summary>
    public event Action<string>? Disconnected;

    /// <summary>
    /// 连接并打开 PTY shell。重复调用无害（已连接时直接返回）。
    /// </summary>
    public async Task ConnectAsync(ServerInfo server, uint columns, uint rows)
    {
        if (_shell != null)
        {
            return;
        }

        if (server.OsType != ServerOsType.Linux)
        {
            StatusChanged?.Invoke("当前仅支持 Linux 服务器的 SSH 终端。");
            return;
        }

        await Task.Run(() =>
        {
            try
            {
                StatusChanged?.Invoke($"正在连接 {server.Host}:{server.Port} …");

                _client = new SshClient(server.Host, server.Port, server.Username, server.Password);
                _client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(10);
                _client.ErrorOccurred += (_, e) =>
                    Disconnected?.Invoke($"连接错误：{e.Exception.Message}");
                _client.Connect();

                if (!_client.IsConnected)
                {
                    Disconnected?.Invoke("无法建立 SSH 连接（请检查地址、端口、账号密码）。");
                    return;
                }

                // terminalName 用 xterm-256color：远端程序（vim/htop 等）据此启用 256 色
                _shell = _client.CreateShellStream("xterm-256color", columns, rows, 0, 0, 4096);
                _shell.DataReceived += OnShellData;

                // 拿到 shell 才算真的能用，时间点记在这里而不是 Connect() 之后
                ServerConnectionTracker.MarkConnected(server);

                StatusChanged?.Invoke("已连接。输入 exit 或点关闭可断开。");
            }
            catch (Exception ex)
            {
                Disconnected?.Invoke($"连接失败：{ex.Message}");
                DisposeClient();
            }
        });
    }

    /// <summary>把用户键入（含转义序列）写给远端 shell。</summary>
    public void Write(string data)
    {
        try
        {
            _shell?.Write(data);
            _shell?.Flush();
        }
        catch (Exception ex)
        {
            Disconnected?.Invoke($"写入失败（连接可能已断开）：{ex.Message}");
        }
    }

    /// <summary>终端尺寸变化时通知远端（vim/htop 依赖这个重绘）。</summary>
    public void Resize(uint columns, uint rows)
    {
        try
        {
            _shell?.ChangeWindowSize(columns, rows, 0, 0);
        }
        catch (Exception)
        {
            // 尺寸同步失败不影响主流程，忽略
        }
    }

    /// <summary>
    /// ShellStream 的数据回调（SSH.NET 后台线程）：
    /// 增量 UTF-8 解码后原样抛给 <see cref="OutputReceived"/>。
    /// </summary>
    private void OnShellData(object? sender, ShellDataEventArgs e)
    {
        if (e.Data is not { Length: > 0 } bytes || _disposed)
        {
            return;
        }

        var charCount = _decoder.GetCharCount(bytes, 0, bytes.Length);
        if (charCount == 0)
        {
            return; // 多字节字符的前半截，等下一个包拼齐
        }

        var chars = new char[charCount];
        _decoder.GetChars(bytes, 0, bytes.Length, chars, 0);
        OutputReceived?.Invoke(new string(chars));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        DisposeClient();
    }

    private void DisposeClient()
    {
        try
        {
            _shell?.Dispose();
            if (_client != null)
            {
                if (_client.IsConnected)
                {
                    _client.Disconnect();
                }
                _client.Dispose();
            }
        }
        catch (Exception)
        {
            // 断开时的清理异常没有上报价值
        }
        finally
        {
            _shell = null;
            _client = null;
        }
    }
}
