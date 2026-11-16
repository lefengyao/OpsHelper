using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace ServerOpsHelper.Services;

/// <summary>
/// 本地账号认证服务
/// 账号信息存储在 LocalAppData 的 users.json，密码为 PBKDF2 哈希
/// </summary>
public class AuthService
{
    private static readonly Lazy<AuthService> _lazy = new(() => new AuthService());
    public static AuthService Instance => _lazy.Value;

    private readonly string _usersFilePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private AuthService()
    {
        // 与 ConfigService 共用同一个目录：这样 SERVEROPSHELPER_CONFIG_DIR 一旦重定向，
        // 账号文件也跟着走（自动化测试才不会写到用户的真实账号文件里）
        _usersFilePath = Path.Combine(ConfigService.Instance.ConfigDirectory, "users.json");
    }

    public string CurrentUser { get; private set; } = string.Empty;

    /// <summary>
    /// 默认账号名（首次运行自动创建）
    /// </summary>
    public const string DefaultUserName = "admin";

    /// <summary>
    /// 默认密码（首次运行自动创建）
    /// </summary>
    public const string DefaultPassword = "admin123";

    /// <summary>
    /// 新密码的最小长度要求
    /// </summary>
    public const int MinPasswordLength = 8;

    /// <summary>
    /// 校验账号密码
    /// </summary>
    public async Task<bool> ValidateAsync(string username, string password)
    {
        var users = await LoadUsersAsync();

        if (users.TryGetValue(username, out var stored))
        {
            if (VerifyHash(password, stored.Salt, stored.Hash))
            {
                CurrentUser = username;
                return true;
            }
            return false;
        }

        // 首次运行：创建默认账号 admin / admin123，并在首次登录后提示修改密码
        if (users.Count == 0 && username == DefaultUserName && password == DefaultPassword)
        {
            var salt = GenerateSalt();
            var hash = ComputeHash(password, salt);
            users[username] = new UserCredential { Salt = salt, Hash = hash };
            await SaveUsersAsync(users);
            CurrentUser = username;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 判断账号是否仍在使用默认密码（用于给出「建议立即修改」的提示）
    /// </summary>
    public async Task<bool> IsUsingDefaultPasswordAsync(string username)
    {
        if (string.IsNullOrEmpty(username))
        {
            return false;
        }

        var users = await LoadUsersAsync();
        return users.TryGetValue(username, out var stored)
               && VerifyHash(DefaultPassword, stored.Salt, stored.Hash);
    }

    /// <summary>
    /// 是否已经创建过账号（false 表示首次运行，默认账号尚未写入）
    /// </summary>
    public async Task<bool> HasAnyAccountAsync() => (await LoadUsersAsync()).Count > 0;

    /// <summary>
    /// 修改密码
    /// </summary>
    /// <returns>旧密码不正确、账号不存在或新密码不合规时返回 false</returns>
    public async Task<bool> ChangePasswordAsync(string username, string oldPassword, string newPassword)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < MinPasswordLength)
        {
            return false;
        }

        var users = await LoadUsersAsync();
        if (!users.TryGetValue(username, out var stored))
        {
            return false;
        }

        if (oldPassword == newPassword || !VerifyHash(oldPassword, stored.Salt, stored.Hash))
        {
            return false;
        }

        var salt = GenerateSalt();
        users[username] = new UserCredential { Salt = salt, Hash = ComputeHash(newPassword, salt) };
        await SaveUsersAsync(users);
        return true;
    }

    private async Task<Dictionary<string, UserCredential>> LoadUsersAsync()
    {
        if (!File.Exists(_usersFilePath))
        {
            return new Dictionary<string, UserCredential>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_usersFilePath);
            return JsonSerializer.Deserialize<Dictionary<string, UserCredential>>(json, _jsonOptions)
                   ?? new Dictionary<string, UserCredential>();
        }
        catch
        {
            return new Dictionary<string, UserCredential>();
        }
    }

    private async Task SaveUsersAsync(Dictionary<string, UserCredential> users)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_usersFilePath)!);
        var json = JsonSerializer.Serialize(users, _jsonOptions);
        await File.WriteAllTextAsync(_usersFilePath, json);
    }

    private static string GenerateSalt()
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(salt);
    }

    private static string ComputeHash(string password, string saltBase64)
    {
        var salt = Convert.FromBase64String(saltBase64);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(hash);
    }

    private static bool VerifyHash(string password, string saltBase64, string hashBase64)
    {
        try
        {
            var salt = Convert.FromBase64String(saltBase64);
            var expected = Convert.FromBase64String(hashBase64);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    private class UserCredential
    {
        public string Salt { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
    }
}
