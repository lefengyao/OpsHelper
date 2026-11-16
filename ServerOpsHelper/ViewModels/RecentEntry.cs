using ServerOpsHelper.Models;

namespace ServerOpsHelper.ViewModels;

/// <summary>
/// 「近期访问」下拉里的一条候选：可能是本次会话刚打开过的页面、已保存的常用链接，
/// 也可能只是历史记录里的一条。三者归一成同一个形状，方便在同一个下拉里展示。
/// </summary>
public class RecentEntry
{
    /// <summary>
    /// 主标题
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// 次行说明（地址 / 访问次数 / 时间）
    /// </summary>
    public string Subtitle { get; init; } = string.Empty;

    /// <summary>
    /// 来源标记：本次 / 常用 / 历史
    /// </summary>
    public string Badge { get; init; } = string.Empty;

    /// <summary>
    /// 要打开的地址
    /// </summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>
    /// 命中的常用链接（有值时打开后可复用它的账号密码，并计入打开次数）
    /// </summary>
    public LinkItem? Link { get; init; }

    /// <summary>
    /// 从常用链接构造
    /// </summary>
    public static RecentEntry FromLink(LinkItem link) => new()
    {
        Title = string.IsNullOrWhiteSpace(link.Name) ? link.EffectiveUrl : link.Name,
        Subtitle = link.MetaText,
        Badge = "常用",
        Url = link.EffectiveUrl,
        Link = link
    };

    /// <summary>
    /// 从访问记录构造
    /// </summary>
    /// <param name="record">访问记录</param>
    /// <param name="session">是否属于本次会话</param>
    public static RecentEntry FromVisit(VisitRecord record, bool session) => new()
    {
        Title = record.DisplayTitle,
        Subtitle = record.Url,
        Badge = session ? "本次" : "历史",
        Url = record.Url
    };
}
