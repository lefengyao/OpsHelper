using Renci.SshNet;
using ServerOpsHelper.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ServerOpsHelper.Services;

/// <summary>
/// 服务器实时指标采集（三期）：一条持久 SSH 连接，按固定间隔跑**一条聚合命令**，
/// 把 CPU / 内存 / 磁盘 / 负载 / 网络 / 运行时长一次拿回来并解析。
/// <para>
/// 为什么用 /proc 而不是 top/free/vmstat：那些工具未必装（最小化系统很常见），
/// 而 <c>/proc</c> 是内核接口，永远在。df 用 coreutils 自带的，同样基本必有。
/// </para>
/// <para>
/// CPU 使用率与网络速率都需要**两次采样**算差值，所以第一次采集后这两项才有值
/// （此前显示"—"）。
/// </para>
/// </summary>
public sealed class RemoteMonitorService : IDisposable
{
    /// <summary>
    /// 一条命令拿全部指标，段与段之间用 ##TAG 分隔，解析时按 TAG 切。
    /// 每段都做了容错（文件缺失/权限不足时该段为空，不影响其他段）。
    /// </summary>
    private const string CollectScript = """
        echo '##LOAD'; cat /proc/loadavg 2>/dev/null;
        echo '##STAT'; head -1 /proc/stat 2>/dev/null;
        echo '##MEM'; grep -E '^(MemTotal|MemAvailable):' /proc/meminfo 2>/dev/null;
        echo '##DISK'; df -m / 2>/dev/null | tail -1;
        echo '##UP'; cut -d' ' -f1 /proc/uptime 2>/dev/null;
        echo '##CPU'; nproc 2>/dev/null || getconf _NPROCESSORS_ONLN 2>/dev/null;
        echo '##HOST'; hostname 2>/dev/null;
        echo '##OS'; grep '^PRETTY_NAME=' /etc/os-release 2>/dev/null | cut -d= -f2- | tr -d '"';
        echo '##NET'; grep -v 'lo:' /proc/net/dev 2>/dev/null | grep ':' | head -1
        """;

    private SshClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    // 两次采样的基准值（CPU 用 jiffies，网络用字节数）
    private long _prevTotal = -1;
    private long _prevIdle = -1;
    private long _prevRx = -1;
    private long _prevTx = -1;
    private DateTime _prevSampleAt;

    /// <summary>采集成功（后台线程回调，订阅方自行调度到 UI 线程）。</summary>
    public event Action<ServerMetrics>? Updated;

    /// <summary>采集失败（连接断开等），触发一次即停止。</summary>
    public event Action<string>? Failed;

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>连接并开始周期性采集。重复调用会先停掉上一次。</summary>
    public async Task StartAsync(ServerInfo server, TimeSpan interval)
    {
        Stop();

        await Task.Run(() =>
        {
            _client = new SshClient(server.Host, server.Port, server.Username, server.Password);
            _client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(10);
            _client.Connect();

            // 连上了才记时间：连接失败不该污染「最后连接时间」
            ServerConnectionTracker.MarkConnected(server);
        });

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => LoopAsync(interval, token), token);
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch (Exception)
        {
            // 取消时的异常无上报价值
        }
        finally
        {
            _cts = null;
            _loop = null;
        }

        try
        {
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
            // 断开清理异常无上报价值
        }
        finally
        {
            _client = null;
        }
    }

    private async Task LoopAsync(TimeSpan interval, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var metrics = await CollectAsync(token);
                if (metrics != null)
                {
                    Updated?.Invoke(metrics);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Failed?.Invoke($"监控采集失败：{ex.Message}");
                return; // 连接类错误没必要每秒重试，交给用户重进页面
            }

            try
            {
                await Task.Delay(interval, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<ServerMetrics?> CollectAsync(CancellationToken token)
    {
        var client = _client;
        if (client is not { IsConnected: true })
        {
            return null;
        }

        var raw = await Task.Run(() =>
        {
            using var cmd = client.CreateCommand(CollectScript);
            cmd.CommandTimeout = TimeSpan.FromSeconds(10);
            cmd.Execute();
            return cmd.Result ?? string.Empty;
        }, token);

        return Parse(raw);
    }

    /// <summary>把 ##TAG 分段的输出解析成指标对象，并算出差值类指标（CPU 使用率、网络速率）。</summary>
    private ServerMetrics Parse(string raw)
    {
        var sections = SplitSections(raw);
        var m = new ServerMetrics
        {
            SampledAt = DateTime.Now,
            HostName = Section(sections, "HOST").Trim(),
            OsName = Section(sections, "OS").Trim(),
        };

        // 负载
        var load = Section(sections, "LOAD").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (load.Length >= 3)
        {
            m.Load1 = Num(load[0]);
            m.Load5 = Num(load[1]);
            m.Load15 = Num(load[2]);
        }

        // 核数
        m.CpuCores = (int)Num(Section(sections, "CPU").Trim());

        // 运行时长（秒）
        var up = Num(Section(sections, "UP").Trim());
        if (up > 0)
        {
            m.Uptime = TimeSpan.FromSeconds(up);
        }

        // 内存（/proc/meminfo 单位是 kB）
        foreach (var line in Section(sections, "MEM").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                continue;
            }
            var kb = Num(parts[1].Replace("kB", "", StringComparison.OrdinalIgnoreCase).Trim());
            if (parts[0].StartsWith("MemTotal", StringComparison.Ordinal))
            {
                m.MemTotalMb = kb / 1024;
            }
            else if (parts[0].StartsWith("MemAvailable", StringComparison.Ordinal))
            {
                m.MemAvailableMb = kb / 1024;
            }
        }
        m.MemUsedMb = Math.Max(0, m.MemTotalMb - m.MemAvailableMb);

        // 磁盘（df -m：Filesystem 1M-blocks Used Available Use% Mounted）
        var disk = Section(sections, "DISK").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (disk.Length >= 3)
        {
            m.DiskTotalMb = Num(disk[1]);
            m.DiskUsedMb = Num(disk[2]);
            m.DiskAvailableMb = Num(disk[3]);
        }

        // CPU 使用率：靠两次 /proc/stat 的差值
        var stat = Section(sections, "STAT").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (stat.Length >= 5 && stat[0] == "cpu")
        {
            long total = 0;
            foreach (var s in stat.Skip(1))
            {
                total += (long)Num(s);
            }
            var idle = (long)Num(stat[4]);                    // idle
            if (stat.Length >= 6)
            {
                idle += (long)Num(stat[5]);                   // iowait 也算空闲
            }

            if (_prevTotal > 0 && total > _prevTotal)
            {
                var totalDiff = total - _prevTotal;
                var idleDiff = idle - _prevIdle;
                m.CpuUsagePercent = Math.Clamp(100.0 * (totalDiff - idleDiff) / totalDiff, 0, 100);
            }

            _prevTotal = total;
            _prevIdle = idle;
        }

        // 网络速率：靠两次 /proc/net/dev 的字节数差值
        var net = Section(sections, "NET");
        var colon = net.IndexOf(':');
        if (colon > 0)
        {
            m.NetInterface = net[..colon].Trim();
            var nums = net[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (nums.Length >= 9)
            {
                var rx = (long)Num(nums[0]);
                var tx = (long)Num(nums[8]);
                var now = DateTime.Now;

                if (_prevRx >= 0 && _prevSampleAt != default)
                {
                    var seconds = (now - _prevSampleAt).TotalSeconds;
                    if (seconds > 0.5)
                    {
                        // 负值说明接口计数器被重置过，按 0 处理
                        m.NetRxBytesPerSec = Math.Max(0, (rx - _prevRx) / seconds);
                        m.NetTxBytesPerSec = Math.Max(0, (tx - _prevTx) / seconds);
                    }
                }

                _prevRx = rx;
                _prevTx = tx;
                _prevSampleAt = now;
            }
        }

        return m;
    }

    private static Dictionary<string, string> SplitSections(string raw)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var buffer = new List<string>();

        foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("##", StringComparison.Ordinal))
            {
                if (current != null)
                {
                    map[current] = string.Join('\n', buffer);
                }
                current = line[2..].Trim();
                buffer.Clear();
            }
            else if (current != null)
            {
                buffer.Add(line);
            }
        }

        if (current != null)
        {
            map[current] = string.Join('\n', buffer);
        }

        return map;
    }

    private static string Section(Dictionary<string, string> map, string key)
        => map.TryGetValue(key, out var v) ? v : string.Empty;

    private static double Num(string s)
        => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public void Dispose()
    {
        // 幂等：页面可能因为导航来回触发多次关闭，重复调用必须无害
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Stop();
    }
}

/// <summary>一次采样得到的服务器指标。</summary>
public sealed class ServerMetrics
{
    public DateTime SampledAt { get; set; }
    public string HostName { get; set; } = "";
    public string OsName { get; set; } = "";
    public TimeSpan Uptime { get; set; }
    public int CpuCores { get; set; }
    /// <summary>CPU 使用率（%）。首次采样无基准，值为 -1 表示"暂无"。</summary>
    public double CpuUsagePercent { get; set; } = -1;
    public double Load1 { get; set; }
    public double Load5 { get; set; }
    public double Load15 { get; set; }
    public double MemTotalMb { get; set; }
    public double MemUsedMb { get; set; }
    public double MemAvailableMb { get; set; }
    public double DiskTotalMb { get; set; }
    public double DiskUsedMb { get; set; }
    public double DiskAvailableMb { get; set; }
    public string NetInterface { get; set; } = "";
    public double NetRxBytesPerSec { get; set; } = -1;
    public double NetTxBytesPerSec { get; set; } = -1;

    public double MemUsedPercent => MemTotalMb > 0 ? MemUsedMb / MemTotalMb * 100 : 0;
    public double DiskUsedPercent => DiskTotalMb > 0 ? DiskUsedMb / DiskTotalMb * 100 : 0;
    public bool HasCpu => CpuUsagePercent >= 0;
    public bool HasNet => NetRxBytesPerSec >= 0;

    /// <summary>把运行时长写成人话（3 天 4 小时 / 2 小时 15 分 / 31 分钟）。</summary>
    public string UptimeText
    {
        get
        {
            if (Uptime <= TimeSpan.Zero)
            {
                return "—";
            }
            if (Uptime.TotalDays >= 1)
            {
                return $"{(int)Uptime.TotalDays} 天 {Uptime.Hours} 小时";
            }
            if (Uptime.TotalHours >= 1)
            {
                return $"{Uptime.Hours} 小时 {Uptime.Minutes} 分";
            }
            return $"{Math.Max(1, Uptime.Minutes)} 分钟";
        }
    }

    public static string FormatMb(double mb)
    {
        if (mb >= 1024 * 1024)
        {
            return $"{mb / 1024 / 1024:0.##} TB";
        }
        return mb >= 1024 ? $"{mb / 1024:0.##} GB" : $"{mb:0} MB";
    }

    public static string FormatRate(double bytesPerSec)
    {
        if (bytesPerSec < 0)
        {
            return "—";
        }
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        double v = bytesPerSec;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return $"{v:0.#} {units[i]}";
    }
}
