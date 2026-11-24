using Renci.SshNet;
using Renci.SshNet.Common;
using ServerOpsHelper.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ServerOpsHelper.Services;

/// <summary>
/// 按「主机 + 端口 + 账号」复用 SSH 长连接，并在**建立连接阶段**做有限重试。
/// <para>
/// 为什么要有它：MCP 的每一条 <c>exec_command</c> 曾经都是新建 SshClient → Connect → 执行 → 断开，
/// 一条命令一次完整握手。智能体排查问题时连着发几十条命令，等于在几秒内对 sshd 发起几十次
/// 新连接 —— 撞上 <c>MaxStartups</c>（默认 10:30:100）时，sshd 会在**版本协商阶段**就把连接丢掉，
/// 客户端看到的就是 <c>The server response does not contain an SSH identification string</c>。
/// 复用一条已认证的连接后，握手次数从"每条命令一次"降到"只在断线时一次"，这类抖动随之消失。
/// </para>
/// <para>
/// 线程约定：同一台服务器同一时刻只跑一条命令（<see cref="SemaphoreSlim"/> 串行化）。
/// SSH.NET 的会话对象本身不是线程安全的，串行换来的是"一条命令卡住不会污染另一条"。
/// 代价是同机并发会排队，这对"一个智能体操作一台机器"的场景没有影响。
/// </para>
/// <para>
/// 失效处理：连接断了/超时了不会被当成"下次也别试"——<see cref="Discard"/> 会把它从池里摘掉，
/// 下一次调用重新握手。**绝不重试已经提交的命令**：命令可能已经在远端跑过，盲重试会重复执行
/// <c>up -d</c> 这类变更操作。
/// </para>
/// </summary>
public sealed class SshSessionPool
{
    /// <summary>进程内共享一条池（MCP 与界面上的扩展执行共用，避免各自握一次手）</summary>
    public static SshSessionPool Shared { get; } = new();

    /// <summary>
    /// 连接阶段最多尝试几次（含首次）。
    /// <para>
    /// 定 4 次是照着真实故障来的：现场"连试 4-5 次才成功"的情况不少，
    /// 3 次会漏掉一部分，5 次又让真正连不上的机器多等好几秒。
    /// 总退避约 1.75 秒（250+500+1000，另加抖动），对一次工具调用来说完全可接受。
    /// </para>
    /// </summary>
    private const int MaxConnectAttempts = 4;

    /// <summary>保活间隔：NAT/防火墙对空闲连接通常 5 分钟才回收，30 秒足够让死连接尽早现形。</summary>
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public SshClient? Client;
        public string Fingerprint = string.Empty;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _mapGate = new();

    /// <summary>当前活着的连接数（探针/诊断用）</summary>
    public int LiveConnectionCount
    {
        get
        {
            lock (_mapGate)
            {
                var count = 0;
                foreach (var entry in _entries.Values)
                {
                    if (entry.Client is { IsConnected: true })
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    /// <summary>
    /// 在指定服务器上跑一段需要 SSH 会话的操作。
    /// <para>
    /// <paramref name="body"/> 抛出的传输层异常（连接断、通道开不出来、操作超时）会让这条连接
    /// 被摘掉，下一次调用重新建连；业务异常原样上抛。
    /// </para>
    /// </summary>
    /// <param name="connectTimeout">单次握手超时。设太短会在 sshd 稍慢时误判失败，建议 10-20 秒。</param>
    public async Task<T> RunAsync<T>(
        ServerInfo server,
        Func<SshClient, CancellationToken, Task<T>> body,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken = default)
    {
        var entry = GetEntry(server);

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = await EnsureConnectedAsync(entry, server, connectTimeout, cancellationToken)
                .ConfigureAwait(false);

            return await body(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsBrokenSession(ex))
        {
            // 会话已经不可信：立刻摘掉，别让下一条命令在死连接上再失败一次
            Discard(server);
            throw;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>
    /// 强制关闭并移除某台服务器的连接。
    /// <para>
    /// 两个用途：命令超时后主动断链（否则那条僵尸会话会一直占着服务端的槽位，越积越多），
    /// 以及外层兜底计时器判定"放弃等待"时把阻塞中的调用逼出来、释放串行锁。
    /// 可从任意线程调用，不需要持有 <see cref="Entry.Gate"/>。
    /// </para>
    /// </summary>
    public void Discard(ServerInfo server)
    {
        var entry = FindEntry(server);
        if (entry == null)
        {
            return;
        }

        CloseQuietly(Interlocked.Exchange(ref entry.Client, null));
        entry.Fingerprint = string.Empty;
    }

    /// <summary>关闭全部连接（进程退出/设置变更时调用）。</summary>
    public void DisposeAll()
    {
        List<Entry> entries;
        lock (_mapGate)
        {
            entries = new List<Entry>(_entries.Values);
            _entries.Clear();
        }

        foreach (var entry in entries)
        {
            CloseQuietly(Interlocked.Exchange(ref entry.Client, null));
        }
    }

    private async Task<SshClient> EnsureConnectedAsync(
        Entry entry,
        ServerInfo server,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken)
    {
        var fingerprint = Fingerprint(server);

        // 复用前提：连接还活着，且凭据/端口没变过。用户改了密码就直接重建，
        // 不能让旧凭据的连接继续跑（否则"改了密码却还连得上"会让人怀疑设置没生效）。
        if (entry.Client is { IsConnected: true } existing
            && string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return existing;
        }

        CloseQuietly(Interlocked.Exchange(ref entry.Client, null));
        entry.Fingerprint = string.Empty;

        var client = await ConnectWithRetryAsync(server, connectTimeout, cancellationToken).ConfigureAwait(false);

        // 用 Interlocked 发布：Discard 可能从别的线程（超时兜底）同时把条目摘空，
        // 直接赋值会出现"摘了又被写回去"的窗口
        Interlocked.Exchange(ref entry.Client, client);
        entry.Fingerprint = fingerprint;

        ServerConnectionTracker.MarkConnected(server);
        return client;
    }

    private static async Task<SshClient> ConnectWithRetryAsync(
        ServerInfo server,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken)
    {
        Exception? last = null;

        for (var attempt = 1; attempt <= MaxConnectAttempts; attempt++)
        {
            var client = new SshClient(server.Host, server.Port, server.Username, server.Password);
            client.ConnectionInfo.Timeout = connectTimeout;
            client.KeepAliveInterval = KeepAlive;

            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return client;
            }
            catch (Exception ex)
            {
                CloseQuietly(client);

                if (ex is OperationCanceledException)
                {
                    throw;
                }

                last = ex;

                // 认证失败重试一百次也还是错，直接带着明确提示上抛
                if (!SshDiagnostics.IsTransientHandshake(ex) || attempt == MaxConnectAttempts)
                {
                    throw new SshConnectionException(
                        SshDiagnostics.DescribeHandshakeFailure(server, ex, attempt));
                }

                await Task.Delay(SshDiagnostics.RetryDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        // 逻辑上到不了这里（循环内必然 return 或 throw），保底给编译器一个交代
        throw new SshConnectionException(
            SshDiagnostics.DescribeHandshakeFailure(server, last ?? new Exception("未知原因"), MaxConnectAttempts));
    }

    private Entry GetEntry(ServerInfo server)
    {
        var key = KeyOf(server);

        lock (_mapGate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            return entry;
        }
    }

    private Entry? FindEntry(ServerInfo server)
    {
        var key = KeyOf(server);

        lock (_mapGate)
        {
            return _entries.TryGetValue(key, out var entry) ? entry : null;
        }
    }

    private static string KeyOf(ServerInfo server)
        => $"{server.Host.Trim().ToLowerInvariant()}:{server.Port}:{server.Username.Trim().ToLowerInvariant()}";

    /// <summary>
    /// 凭据指纹。只用于"连接是不是还对应同一份配置"，不做安全用途，
    /// 所以取 SHA256 前 8 字节就够 —— 也避免密码本身在内存里再复制一份长字符串。
    /// </summary>
    private static string Fingerprint(ServerInfo server)
    {
        var raw = $"{server.Password}\n{server.Port}\n{server.Username}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>会话已经不可信（断链/超时/通道开不出来）—— 需要摘掉重建</summary>
    private static bool IsBrokenSession(Exception ex)
    {
        if (ex is SshConnectionException
            or SshOperationTimeoutException
            or SocketException
            or IOException
            or ObjectDisposedException)
        {
            return true;
        }

        // 通道打开失败（服务端 MaxSessions 满）SSH.NET 抛的是基类 SshException，
        // 只能按消息判断；这类连接本身还活着，但这条会话槽位已经不可用。
        return ex is SshException
               && ex.Message.Contains("channel", StringComparison.OrdinalIgnoreCase);
    }

    private static void CloseQuietly(SshClient? client)
    {
        if (client == null)
        {
            return;
        }

        try
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }
        }
        catch (Exception)
        {
            // 断开失败没有上报价值：连接本来就已经不可用了
        }

        try
        {
            client.Dispose();
        }
        catch (Exception)
        {
            // 同上
        }
    }
}

/// <summary>
/// SSH 连接故障的分类、退避与措辞。
/// <para>
/// 单独拎出来是因为 SFTP 通道（每条操作新建连接）也要用同一套判断：
/// 两条通道对"什么算抖动、什么算真的错"必须给出同一个答案，否则排查时会互相打脸。
/// </para>
/// </summary>
internal static class SshDiagnostics
{
    /// <summary>
    /// 是否属于**连接阶段**的瞬时故障，值得重试。
    /// <para>
    /// 认证失败不算：密码错了重试只会拖慢报错。
    /// 主机密钥/算法协商失败也不算：那是配置问题，重试不会好转。
    /// </para>
    /// </summary>
    public static bool IsTransientHandshake(Exception ex) => ex switch
    {
        SshAuthenticationException => false,
        SshConnectionException => true,
        SshOperationTimeoutException => true,
        SocketException => true,
        IOException => true,
        _ => false,
    };

    /// <summary>第 n 次失败后等多久：250ms / 500ms / 1s，各加 0-150ms 抖动。</summary>
    public static TimeSpan RetryDelay(int failedAttempt)
    {
        var exponent = Math.Clamp(failedAttempt, 1, 5);
        var baseMs = 250 * (1 << (exponent - 1));
        return TimeSpan.FromMilliseconds(baseMs + Random.Shared.Next(0, 150));
    }

    /// <summary>
    /// 把底层异常翻译成"能照着做点什么"的说明。
    /// <para>
    /// 这里刻意点明"命令没有执行"：握手阶段失败时命令根本没送出去，
    /// 模型如果误以为执行了一半，会去做无谓的回滚。
    /// </para>
    /// </summary>
    public static string DescribeHandshakeFailure(ServerInfo server, Exception ex, int attempts)
    {
        var text = new StringBuilder();
        text.Append($"{server.Name}（{server.Host}:{server.Port}）SSH 连接失败");

        if (attempts > 1)
        {
            // attempts 是"尝试次数"，重试次数要比它少 1 —— 否则报错里说重试了 4 次，
            // 实际只重试了 3 次，排查时会照着错的次数去对日志。
            text.Append($"，已自动重试 {attempts - 1} 次（共 {attempts} 次尝试）");
        }

        text.Append($"：{ex.Message}");

        // 底层消息自带句号（中文 .NET 常见），别再叠一个
        if (!text.ToString().EndsWith('。'))
        {
            text.Append('。');
        }

        // 握手阶段就断开：SSH.NET 有时报 "does not contain an SSH identification string"，
        // 有时（服务端 RST 而不是静默关闭）只报一个 SocketException 的连接被重置。
        // 两者是同一件事，提示也必须一致，否则同一故障会出现两套说法。
        var droppedDuringHandshake = LooksLikeIdentificationFailure(ex)
            || ex is SshConnectionException
            || ex is SocketException;

        if (droppedDuringHandshake)
        {
            text.Append("SSH 握手没有完成（banner 没吐出来，或连接被中途重置），命令没有送到服务器上。"
                        + "常见原因是 sshd 的 MaxStartups / 连接限流已满、服务器负载过高 fork 不出新会话，"
                        + "或中间网络设备打断了连接；等几秒再试通常就能过。若持续发生，请在服务器上看 sshd 日志"
                        + "（journalctl -u sshd -n 50，或 grep -i 'maxstartups\\|drop' /var/log/auth.log）。");
        }
        else if (ex is SshAuthenticationException)
        {
            text.Append("用户名或密码被拒绝，重试不会好转；请先在 ServerOpsHelper 界面里核对这台机器的凭据。");
        }
        else if (ex is SshOperationTimeoutException or TimeoutException)
        {
            text.Append("握手在超时时间内没有完成，通常是网络质量或 sshd 负载问题，可稍后重试。");
        }

        return text.ToString();
    }

    /// <summary>
    /// 是不是"服务端没给 banner"这一类失败。
    /// <para>
    /// SSH.NET 的原文是 "The server response does not contain an SSH identification string"；
    /// 不同版本措辞略有差别，所以按关键词判断而不是整句比较。
    /// </para>
    /// </summary>
    public static bool LooksLikeIdentificationFailure(Exception ex)
        => ex.Message.Contains("identification string", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("identify", StringComparison.OrdinalIgnoreCase)
           || ex.InnerException is { } inner && LooksLikeIdentificationFailure(inner);
}
