using System.Security.Cryptography;
using System.Text;

namespace ServerOpsHelper.Services;

/// <summary>
/// 凭据加密服务，使用 Windows DPAPI（当前用户范围）加密敏感数据
/// </summary>
public class CredentialService
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ServerOpsHelper.Credential.V1");

    /// <summary>
    /// 使用 DPAPI 加密字符串，返回 Base64
    /// </summary>
    public string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return string.Empty;
        }

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var encrypted = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>
    /// 使用 DPAPI 解密带前缀的 Base64 字符串；失败或为明文时返回原文（兼容历史明文数据）
    /// </summary>
    public string Decrypt(string encryptedText)
    {
        if (string.IsNullOrEmpty(encryptedText))
        {
            return string.Empty;
        }

        // 无前缀视为明文（历史数据）
        if (!encryptedText.StartsWith(EncryptedPrefix))
        {
            return encryptedText;
        }

        try
        {
            var payload = encryptedText[EncryptedPrefix.Length..];
            var encryptedBytes = Convert.FromBase64String(payload);
            var decrypted = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            // 解密失败时返回原文，避免阻断使用
            return encryptedText;
        }
    }

    /// <summary>
    /// 幂等加密：已带前缀的密文原样返回，避免重复保存时被二次加密
    /// </summary>
    public string EncryptIfNeeded(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText) || IsEncrypted(plainText))
        {
            return plainText ?? string.Empty;
        }

        return EncryptedPrefix + Encrypt(plainText);
    }

    /// <summary>
    /// 判断字符串是否为本服务产出的 DPAPI 密文
    /// </summary>
    public bool IsEncrypted(string? text)
        => !string.IsNullOrEmpty(text) && text.StartsWith(EncryptedPrefix, StringComparison.Ordinal);

    /// <summary>
    /// 是否为已加密的凭据（密文带前缀标记，便于区分明文历史数据）
    /// </summary>
    public const string EncryptedPrefix = "DPAPI:";
}
