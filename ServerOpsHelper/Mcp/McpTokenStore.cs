using ServerOpsHelper.Services;
using System;
using System.IO;
using System.Security.Cryptography;

namespace ServerOpsHelper.Mcp;

/// <summary>
/// MCP 访问令牌的存取。
/// <para>
/// 令牌是本地 HTTP 传输的唯一凭据（stdio 传输不需要 —— 那是客户端亲手拉起的子进程，
/// 继承的就是当前用户的权限）。落盘用 DPAPI 当前用户范围加密，与密码箱同一套机制。
/// </para>
/// <para>
/// 单独存一个 <c>mcp-token.txt</c> 而不是塞进 settings.json：settings 是可以随手
/// 复制 / 分享的普通配置，令牌不该跟着它到处跑。
/// </para>
/// </summary>
internal static class McpTokenStore
{
    /// <summary>令牌长度（十六进制字符数）。32 字节随机 = 256 位，本地场景远超够用。</summary>
    private const int TokenBytes = 32;

    private static readonly CredentialService Credentials = new();

    public static string TokenFilePath
        => Path.Combine(ConfigService.Instance.ConfigDirectory, "mcp-token.txt");

    /// <summary>读取现有令牌；文件不存在或读不出来时返回 null（调用方决定是否新建）。</summary>
    public static string? TryLoad()
    {
        try
        {
            if (!File.Exists(TokenFilePath))
            {
                return null;
            }

            var stored = File.ReadAllText(TokenFilePath).Trim();
            if (stored.Length == 0)
            {
                return null;
            }

            var plain = Credentials.Decrypt(stored);
            return string.IsNullOrWhiteSpace(plain) ? null : plain;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>取令牌，没有就生成一个并落盘。</summary>
    public static string EnsureToken()
    {
        var existing = TryLoad();
        if (!string.IsNullOrEmpty(existing))
        {
            return existing;
        }

        return Regenerate();
    }

    /// <summary>重新生成令牌（旧令牌立即失效）。</summary>
    public static string Regenerate()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenBytes)).ToLowerInvariant();

        try
        {
            Directory.CreateDirectory(ConfigService.Instance.ConfigDirectory);

            // 必须用 EncryptIfNeeded：它会补上 "DPAPI:" 前缀。
            // 少了这个前缀，下次 Decrypt 会把它当"历史明文数据"原样返回 ——
            // 表现为"每次读到的令牌都不一样"（读回来的是 Base64 密文本身）。
            File.WriteAllText(TokenFilePath, Credentials.EncryptIfNeeded(token));
        }
        catch (Exception)
        {
            // 写不进去也不影响本次会话使用：令牌已在内存里，只是下次要重新生成
        }

        return token;
    }
}
