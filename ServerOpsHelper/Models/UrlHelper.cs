using System;

namespace ServerOpsHelper.Models;

/// <summary>
/// 网址解析小工具，集中处理「站点归属」的判断。
/// <para>
/// 「同一个站点」一律按 <c>scheme://host[:port]</c> 比较：路径与查询串视为同一站点的不同页面。
/// 访问记录去重、密码箱按站点匹配都依赖这里。
/// </para>
/// </summary>
public static class UrlHelper
{
    /// <summary>
    /// 取站点根（例如 <c>https://1.2.3.4:8888</c>）；无法解析时返回空字符串
    /// </summary>
    public static string GetOrigin(string? url)
    {
        var normalized = LinkItem.NormalizeUrl(url);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Authority}"
            : string.Empty;
    }

    /// <summary>
    /// 取主机名（例如 <c>1.2.3.4</c>、<c>www.baidu.com</c>）；无法解析时退回空字符串
    /// </summary>
    public static string GetHost(string? url)
    {
        var normalized = LinkItem.NormalizeUrl(url);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
    }

    /// <summary>
    /// 两个地址是否属于同一个站点
    /// </summary>
    public static bool IsSameSite(string? left, string? right)
    {
        var a = GetOrigin(left);
        var b = GetOrigin(right);
        return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 是否是不该记入访问历史的地址（空白页、浏览器内部页等）
    /// </summary>
    public static bool IsNoise(string? url)
    {
        var raw = (url ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return true;
        }

        foreach (var prefix in new[] { "about:", "edge:", "chrome:", "data:", "javascript:", "blob:", "view-source:" })
        {
            if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 解析不出站点归属的地址（例如手输的半截字符串）同样不记录
        return GetOrigin(raw).Length == 0;
    }
}
