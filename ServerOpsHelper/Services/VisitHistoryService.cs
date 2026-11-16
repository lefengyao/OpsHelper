using ServerOpsHelper.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ServerOpsHelper.Services;

/// <summary>
/// 访问记录服务：统一处理「本次会话」与「历史记录」两份数据。
/// <para>
/// 历史记录落盘到 <c>history.json</c>（见 <see cref="ConfigService"/>）；
/// 本次会话只存在内存里，应用一关就没了 —— 这正是「近期访问」想要的口径。
/// </para>
/// </summary>
public class VisitHistoryService
{
    private static readonly Lazy<VisitHistoryService> _lazy = new(() => new VisitHistoryService());
    public static VisitHistoryService Instance => _lazy.Value;

    /// <summary>
    /// 本次会话最多记住多少个页面
    /// </summary>
    public const int MaxSessionCount = 30;

    private readonly List<VisitRecord> _session = new();

    private bool _enabled = true;

    private VisitHistoryService()
    {
    }

    /// <summary>
    /// 访问记录发生变化（侧边栏的「最近访问」列表据此刷新）
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 本次打开应用后访问过的页面（最近访问在前）
    /// </summary>
    public IReadOnlyList<VisitRecord> SessionVisits => _session;

    /// <summary>
    /// 是否记录访问历史（由系统设置控制）
    /// </summary>
    public bool IsEnabled => _enabled;

    /// <summary>
    /// 应用设置
    /// </summary>
    public void Configure(bool enabled) => _enabled = enabled;

    /// <summary>
    /// 按「设置 + 地址有效性」判断这次访问是否值得记录
    /// </summary>
    public bool ShouldRecord(string? url) => _enabled && !UrlHelper.IsNoise(url);

    /// <summary>
    /// 记录一次访问：同时更新落盘历史与本次会话列表。
    /// </summary>
    /// <param name="url">页面地址</param>
    /// <param name="title">页面标题</param>
    /// <param name="source">来源常用链接（从收藏打开时传入）</param>
    /// <returns>落盘后的记录；未记录时返回 null</returns>
    public async Task<VisitRecord?> RecordAsync(string? url, string? title, LinkItem? source = null)
    {
        var normalized = LinkItem.NormalizeUrl(url);
        if (!ShouldRecord(normalized))
        {
            return null;
        }

        var record = await ConfigService.Instance.RecordVisitAsync(normalized, title ?? string.Empty, source);

        // 会话列表：同一地址只留一条，且始终排在最前
        _session.RemoveAll(r => string.Equals(r.Url, record.Url, StringComparison.OrdinalIgnoreCase));

        var sessionCopy = record.Copy();
        sessionCopy.VisitedThisSession = true;
        sessionCopy.VisitCount = 1;
        _session.Insert(0, sessionCopy);

        while (_session.Count > MaxSessionCount)
        {
            _session.RemoveAt(_session.Count - 1);
        }

        Changed?.Invoke(this, EventArgs.Empty);

        return record;
    }

    /// <summary>
    /// 清空本次会话记录（切换登录用户等场景可调用）
    /// </summary>
    public void ClearSession()
    {
        if (_session.Count == 0)
        {
            return;
        }

        _session.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 该地址本次会话是否访问过
    /// </summary>
    public bool WasVisitedThisSession(string? url)
    {
        var origin = UrlHelper.GetOrigin(url);
        return origin.Length > 0
               && _session.Any(r => string.Equals(r.Origin, origin, StringComparison.OrdinalIgnoreCase));
    }
}
