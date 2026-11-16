using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace ServerOpsHelper.Frp;

/// <summary>
/// 进程内全局状态：当前模式、运行中标志、日志环形队列。
/// 线程安全：所有公开方法可在任意线程调用。
/// </summary>
public class FrpState
{
    public string CurrentMode { get; set; } = "frpc";
    public bool IsRunning { get; set; }
    public int? ProcessId { get; set; }
    public DateTime? StartedAt { get; set; }

    /// <summary>日志环形队列最大条数。超出后从头部丢弃。</summary>
    public const int MaxLogEntries = 1000;

    private readonly ConcurrentQueue<LogEntry> _logs = new();
    private long _nextSeq;          // 下一条待写入日志的单调递增序号
    private long _headDroppedSeq;   // 被丢弃的最小序号 + 1（用于推算历史序号对应关系）

    /// <summary>
    /// 当前最新序号。客户端可记录上次拉到的序号，下次增量拉取。
    /// </summary>
    public long LogRevision => Interlocked.Read(ref _nextSeq) - 1;

    /// <summary>
    /// 日志变更通知：每条新日志 / 清空时触发一次。SSE 消费者可以订阅它实现事件驱动推送。
    /// </summary>
    public event Action? LogChanged;

    public void AppendLog(string text, string level = "info")
    {
        var entry = new LogEntry
        {
            Seq = Interlocked.Increment(ref _nextSeq) - 1,
            Time = DateTime.Now,
            Level = level,
            Text = StripAnsi(text)
        };

        _logs.Enqueue(entry);
        while (_logs.Count > MaxLogEntries && _logs.TryDequeue(out var dropped))
        {
            // 队首出列时，更新"已丢弃序号边界"，便于客户端推算。
            // 注意：仅在并发场景下出现短暂不一致；下一次 AppendLog 之前都会自洽。
            Interlocked.Exchange(ref _headDroppedSeq, dropped.Seq + 1);
        }

        LogChanged?.Invoke();
    }

    /// <summary>
    /// 取序号 >= sinceSeq 的日志，按时间顺序，最多 maxCount 条。
    /// sinceSeq = 0 表示取全部当前可读日志。
    /// </summary>
    public List<LogEntry> GetLogs(long sinceSeq, int maxCount = 200)
    {
        if (maxCount <= 0) return new List<LogEntry>();
        var all = _logs.ToArray();
        var head = Interlocked.Read(ref _headDroppedSeq);
        // 如果 sinceSeq 小于已被丢弃的最小序号，只能从剩余日志的最小序号开始
        var start = Math.Max(sinceSeq, head);
        var result = new List<LogEntry>(Math.Min(maxCount, all.Length));
        foreach (var e in all)
        {
            if (e.Seq < start) continue;
            if (result.Count >= maxCount) break;
            result.Add(e);
        }
        return result;
    }

    public void ClearLogs()
    {
        while (_logs.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _headDroppedSeq, Interlocked.Read(ref _nextSeq));
        LogChanged?.Invoke();
    }

    // 匹配 CSI 序列：ESC [ + 参数字节 + 终结字母
    // 参数字节含 0x30-0x3F，中间字节 0x20-0x2F，终结字节 0x40-0x7E
    private static readonly Regex CsiRegex = new("\x1b\\[[\x30-\x3F]*[\x20-\x2F]*[\x40-\x7E]", RegexOptions.Compiled);
    // 匹配其它 ESC 序列：ESC + 单一终结字母（0x40-0x5F 范围）
    private static readonly Regex EscRegex = new("\x1b[\x40-\x5F]", RegexOptions.Compiled);

    /// <summary>清除 frp 输出中的 ANSI 颜色/光标转义码。</summary>
    private static string StripAnsi(string input)
    {
        if (string.IsNullOrEmpty(input) || input.IndexOf('\x1b') < 0) return input;
        var s = CsiRegex.Replace(input, string.Empty);
        s = EscRegex.Replace(s, string.Empty);
        return s;
    }
}

public class LogEntry
{
    /// <summary>单调递增序号。客户端用此字段做增量拉取。</summary>
    public long Seq { get; set; }
    public DateTime Time { get; set; }
    public string Level { get; set; } = "info";
    public string Text { get; set; } = "";
}