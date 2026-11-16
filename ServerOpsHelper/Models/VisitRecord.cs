using System;
using System.Text.Json.Serialization;

namespace ServerOpsHelper.Models;

/// <summary>
/// 一条网页访问记录。同一个地址只保留一条，用 <see cref="VisitCount"/> 与
/// <see cref="LastVisitedAt"/> 表示「访问过多少次、最近一次什么时候」。
/// </summary>
public class VisitRecord
{
    /// <summary>
    /// 唯一标识
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 访问的地址（已归一化，带协议头）
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// 页面标题（记录时的 <c>document.title</c>，可能被后续访问刷新）
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 来源常用链接的 Id（从「常用链接」打开的才有）
    /// </summary>
    public Guid? LinkId { get; set; }

    /// <summary>
    /// 来源常用链接的名称快照（链接被删掉后仍能显示「来自 xx」）
    /// </summary>
    public string LinkName { get; set; } = string.Empty;

    /// <summary>
    /// 首次访问时间
    /// </summary>
    public DateTime FirstVisitedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 最近一次访问时间
    /// </summary>
    public DateTime LastVisitedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 累计访问次数
    /// </summary>
    public int VisitCount { get; set; } = 1;

    /// <summary>
    /// 站点根（scheme://host:port），用于按站点归组与匹配密码箱
    /// </summary>
    [JsonIgnore]
    public string Origin => UrlHelper.GetOrigin(Url);

    /// <summary>
    /// 列表主标题：优先页面标题，没有就用主机名
    /// </summary>
    [JsonIgnore]
    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title))
            {
                return Title.Trim();
            }

            var host = UrlHelper.GetHost(Url);
            return host.Length > 0 ? host : Url;
        }
    }

    /// <summary>
    /// 列表次行：访问次数 · 最近时间 · 来源链接
    /// </summary>
    [JsonIgnore]
    public string MetaText
    {
        get
        {
            var parts = new List<string> { $"访问 {VisitCount} 次", $"最近 {TimeLabel}" };

            if (!string.IsNullOrWhiteSpace(LinkName))
            {
                parts.Add($"来自「{LinkName}」");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// 相对时间文案：刚刚 / N 分钟前 / 今天 HH:mm / 昨天 HH:mm / MM-dd HH:mm
    /// </summary>
    [JsonIgnore]
    public string TimeLabel => FormatTime(LastVisitedAt);

    /// <summary>
    /// 本次会话内是否访问过（供下拉列表标记，不落盘）
    /// </summary>
    [JsonIgnore]
    public bool VisitedThisSession { get; set; }

    /// <summary>
    /// 生成一份独立副本（会话列表不与被持久化的对象共享引用）
    /// </summary>
    public VisitRecord Copy() => new()
    {
        Id = Id,
        Url = Url,
        Title = Title,
        LinkId = LinkId,
        LinkName = LinkName,
        FirstVisitedAt = FirstVisitedAt,
        LastVisitedAt = LastVisitedAt,
        VisitCount = VisitCount,
        VisitedThisSession = VisitedThisSession
    };

    /// <summary>
    /// 把时间格式化成便于扫一眼的文案
    /// </summary>
    public static string FormatTime(DateTime time)
    {
        var now = DateTime.Now;
        var span = now - time;

        if (span < TimeSpan.Zero)
        {
            // 时钟回拨等异常情况，直接按绝对时间显示
            return time.ToString("MM-dd HH:mm");
        }

        if (span.TotalSeconds < 60)
        {
            return "刚刚";
        }

        if (span.TotalMinutes < 60)
        {
            return $"{(int)span.TotalMinutes} 分钟前";
        }

        if (time.Date == now.Date)
        {
            return $"今天 {time:HH:mm}";
        }

        if (time.Date == now.Date.AddDays(-1))
        {
            return $"昨天 {time:HH:mm}";
        }

        return time.Year == now.Year ? time.ToString("MM-dd HH:mm") : time.ToString("yyyy-MM-dd HH:mm");
    }
}
