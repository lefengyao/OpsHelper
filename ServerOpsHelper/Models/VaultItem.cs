using System;
using System.Text;
using System.Text.Json.Serialization;

namespace ServerOpsHelper.Models;

/// <summary>
/// 密码箱条目：集中记录各类账号 / 密码 / 关联网址。
/// <para>
/// 密码字段以 DPAPI 密文落盘（带 DPAPI: 前缀），内存中始终为明文。
/// </para>
/// </summary>
public class VaultItem
{
    /// <summary>
    /// 唯一标识
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 名称，例如「阿里云控制台」
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 分类，例如「云平台 / 数据库 / 后台」
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// 登录账号
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// 登录密码（落盘为 DPAPI 密文）
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// 关联地址（登录页 / 控制台）
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// 备注（密保问题、绑定手机等）
    /// </summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>
    /// 是否置顶
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 最近修改时间
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// 密码强度提示：弱 / 中 / 强
    /// </summary>
    [JsonIgnore]
    public string StrengthLabel => EvaluateStrength(Password);

    /// <summary>
    /// 列表第二行的组合文案：分类 · 账号 · 密码强度
    /// </summary>
    [JsonIgnore]
    public string MetaText
    {
        get
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(Category))
            {
                parts.Add(Category);
            }

            if (!string.IsNullOrWhiteSpace(Username))
            {
                parts.Add(Username);
            }

            parts.Add($"密码强度 {StrengthLabel}");

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
            sb.AppendLine($"名称：{Title}");
            if (!string.IsNullOrWhiteSpace(Category))
            {
                sb.AppendLine($"分类：{Category}");
            }

            if (!string.IsNullOrWhiteSpace(Url))
            {
                sb.AppendLine($"地址：{Url}");
            }

            if (!string.IsNullOrWhiteSpace(Username))
            {
                sb.AppendLine($"账号：{Username}");
            }

            if (!string.IsNullOrWhiteSpace(Password))
            {
                sb.AppendLine($"密码：{Password}");
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
    /// 简单密码强度评估（长度 + 字符种类），仅用于给用户一个直观提示
    /// </summary>
    public static string EvaluateStrength(string? password)
    {
        var pwd = password ?? string.Empty;
        if (pwd.Length == 0)
        {
            return "空";
        }

        var kinds = 0;
        var hasLower = false;
        var hasUpper = false;
        var hasDigit = false;
        var hasSymbol = false;

        foreach (var c in pwd)
        {
            if (char.IsLower(c))
            {
                hasLower = true;
            }
            else if (char.IsUpper(c))
            {
                hasUpper = true;
            }
            else if (char.IsDigit(c))
            {
                hasDigit = true;
            }
            else
            {
                hasSymbol = true;
            }
        }

        if (hasLower)
        {
            kinds++;
        }

        if (hasUpper)
        {
            kinds++;
        }

        if (hasDigit)
        {
            kinds++;
        }

        if (hasSymbol)
        {
            kinds++;
        }

        if (pwd.Length < 8 || kinds <= 1)
        {
            return "弱";
        }

        return pwd.Length >= 12 && kinds >= 3 ? "强" : "中";
    }
}
