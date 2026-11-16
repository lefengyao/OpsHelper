using System;
using System.Text;
using System.Text.Json.Serialization;

namespace ServerOpsHelper.Models;

/// <summary>
/// 常用链接的类型
/// </summary>
public enum LinkKind
{
    /// <summary>
    /// 普通网页（后台、控制台、文档等）
    /// </summary>
    WebPage = 0,

    /// <summary>
    /// 宝塔面板专用链接（IP + 端口 + 安全入口）
    /// </summary>
    BtPanel = 1
}

/// <summary>
/// 常用链接条目：既可以是「经常打开的网页」，也可以是「宝塔面板专用链接」。
/// <para>
/// 密码字段以 DPAPI 密文落盘（带 <see cref="Services.CredentialService.EncryptedPrefix"/> 前缀），
/// 内存中始终为明文。
/// </para>
/// </summary>
public class LinkItem
{
    /// <summary>
    /// 唯一标识
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 显示名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 链接类型
    /// </summary>
    public LinkKind Kind { get; set; } = LinkKind.WebPage;

    /// <summary>
    /// 完整地址。为空时（宝塔类型）由 <see cref="Host"/> / <see cref="Port"/> / <see cref="SafeEntry"/> 自动拼出。
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// 宝塔面板：服务器地址或 IP（允许直接粘贴完整地址）
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// 宝塔面板：端口，默认 8888
    /// </summary>
    public int Port { get; set; } = 8888;

    /// <summary>
    /// 宝塔面板：安全入口（地址最后一段随机字符串）
    /// </summary>
    public string SafeEntry { get; set; } = string.Empty;

    /// <summary>
    /// 登录账号
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// 登录密码（落盘为 DPAPI 密文）
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// 备注
    /// </summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>
    /// 标签，逗号分隔
    /// </summary>
    public string Tags { get; set; } = string.Empty;

    /// <summary>
    /// 是否置顶
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 最近一次打开时间
    /// </summary>
    public DateTime? LastOpenedAt { get; set; }

    /// <summary>
    /// 累计打开次数（用于「经常打开」排序）
    /// </summary>
    public int OpenCount { get; set; }

    /// <summary>
    /// 类型的中文名
    /// </summary>
    [JsonIgnore]
    public string KindLabel => Kind == LinkKind.BtPanel ? "宝塔面板" : "常用网页";

    /// <summary>
    /// 真正用于打开 / 复制的地址：优先取 <see cref="Url"/>，否则由宝塔字段拼出
    /// </summary>
    [JsonIgnore]
    public string EffectiveUrl
    {
        get
        {
            var url = NormalizeUrl(Url);
            if (url.Length > 0)
            {
                return url;
            }

            return Kind == LinkKind.BtPanel ? BuildBtUrl(Host, Port, SafeEntry) : string.Empty;
        }
    }

    /// <summary>
    /// 列表中展示的地址（宝塔只显示 主机:端口/入口，更紧凑）
    /// </summary>
    [JsonIgnore]
    public string DisplayAddress
    {
        get
        {
            var url = EffectiveUrl;
            return url.Length == 0 ? "（未填写地址）" : url;
        }
    }

    /// <summary>
    /// 列表第三行的组合文案：类型 · 账号 · 标签 · 打开次数
    /// </summary>
    [JsonIgnore]
    public string MetaText
    {
        get
        {
            var parts = new List<string> { KindLabel };

            if (!string.IsNullOrWhiteSpace(Username))
            {
                parts.Add(Username);
            }

            if (!string.IsNullOrWhiteSpace(Tags))
            {
                parts.Add(Tags);
            }

            if (OpenCount > 0)
            {
                var suffix = LastOpenedAt.HasValue ? $"（{LastOpenedAt:MM-dd HH:mm}）" : string.Empty;
                parts.Add($"已打开 {OpenCount} 次{suffix}");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// 用于「复制登录信息」的多行文本
    /// </summary>
    [JsonIgnore]
    public string CredentialText
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine($"名称：{Name}");
            sb.AppendLine($"地址：{EffectiveUrl}");
            if (!string.IsNullOrWhiteSpace(Username))
            {
                sb.AppendLine($"账号：{Username}");
            }

            if (!string.IsNullOrWhiteSpace(Password))
            {
                sb.AppendLine($"密码：{Password}");
            }

            if (!string.IsNullOrWhiteSpace(SafeEntry))
            {
                sb.AppendLine($"安全入口：{SafeEntry}");
            }

            if (!string.IsNullOrWhiteSpace(Notes))
            {
                sb.AppendLine($"备注：{Notes}");
            }

            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// 是否具备可填充的登录信息
    /// </summary>
    [JsonIgnore]
    public bool HasCredential => !string.IsNullOrWhiteSpace(Username) || !string.IsNullOrWhiteSpace(Password);

    /// <summary>
    /// 补齐协议头，保证地址可被 WebView2 / 浏览器识别
    /// </summary>
    public static string NormalizeUrl(string? raw)
    {
        var url = (raw ?? string.Empty).Trim();
        if (url.Length == 0)
        {
            return string.Empty;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        return url;
    }

    /// <summary>
    /// 按「主机 + 端口 + 安全入口」拼出宝塔面板地址。
    /// <para>
    /// 容错处理：允许 <paramref name="host"/> 直接粘贴完整地址（含协议与路径）；
    /// 已带端口的地址不会重复追加端口；已带路径的地址优先保留原路径。
    /// </para>
    /// </summary>
    /// <returns>拼好的地址；<paramref name="host"/> 为空时返回空字符串</returns>
    public static string BuildBtUrl(string? host, int port, string? safeEntry)
    {
        var raw = (host ?? string.Empty).Trim().TrimEnd('/');
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        var scheme = "https://";
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            scheme = "http://";
            raw = raw[7..];
        }
        else if (raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[8..];
        }

        // 分离「主机[:端口]」与「已有路径」
        var slashIndex = raw.IndexOf('/');
        var authority = slashIndex >= 0 ? raw[..slashIndex] : raw;
        var path = slashIndex >= 0 ? raw[(slashIndex + 1)..].Trim('/') : string.Empty;

        if (authority.Length == 0)
        {
            return string.Empty;
        }

        if (!authority.Contains(':') && port > 0)
        {
            authority = $"{authority}:{port}";
        }

        if (path.Length == 0)
        {
            path = (safeEntry ?? string.Empty).Trim().Trim('/');
        }

        return path.Length == 0 ? $"{scheme}{authority}" : $"{scheme}{authority}/{path}";
    }
}
