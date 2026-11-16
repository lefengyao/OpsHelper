using System.IO;
namespace ServerOpsHelper.Frp;

/// <summary>
/// FRP 运行环境：frp 二进制目录、配置档案（profile）目录。
/// 支持多套配置档案，每套独立保存 frpc.toml / frps.toml，可随时切换。
/// 启动时按优先级恢复 FRP 目录：持久化文件 > 自动定位 > 空。
/// </summary>
public class FrpEnvironment
{
    private string? _frpDirectory;
    private string _activeProfile = DefaultProfileName;
    private readonly string _stateFile;

    public const string DefaultProfileName = "default";

    /// <summary>应用数据根目录（%APPDATA%/FrpManager 等）。</summary>
    public string ConfigDirectory { get; }

    /// <summary>所有配置档案所在目录。</summary>
    public string ProfilesDirectory => Path.Combine(ConfigDirectory, "profiles");

    /// <summary>当前激活的档案目录。</summary>
    public string ActiveProfileDirectory => Path.Combine(ProfilesDirectory, _activeProfile);

    public FrpEnvironment()
    {
        ConfigDirectory = GetAppConfigDirectory();
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(ProfilesDirectory);
        _stateFile = Path.Combine(ConfigDirectory, "state.json");
    }

    /// <summary>当前激活的档案名。</summary>
    public string ActiveProfile => _activeProfile;

    public string FrpDirectory
    {
        get => _frpDirectory ?? "";
        set
        {
            _frpDirectory = string.IsNullOrWhiteSpace(value) ? null : value;
            PersistState();
        }
    }

    /// <summary>
    /// 启动时调用：加载状态、迁移旧的单配置文件、确保存档存在、
    /// 按"持久化 → 自动定位"恢复 FRP 目录。
    /// </summary>
    public void Restore()
    {
        LoadState();
        MigrateLegacyConfigIfNeeded();
        EnsureActiveProfileExists();

        if (!string.IsNullOrEmpty(_frpDirectory) && Validate(out _))
        {
            return;
        }

        // 持久化目录失效（被删/移动）时，重新尝试自动定位
        TryLocateAutomatically();
    }

    // ---------- 配置档案管理 ----------

    /// <summary>列出所有档案名（目录形式），default 排在首位。</summary>
    public List<string> ListProfiles()
    {
        if (!Directory.Exists(ProfilesDirectory)) return new List<string>();
        return Directory.EnumerateDirectories(ProfilesDirectory)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n == DefaultProfileName ? 0 : 1)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>校验档案名是否合法（非空、长度、无路径非法字符）。</summary>
    public bool IsValidProfileName(string name, out string message)
    {
        message = "";
        if (string.IsNullOrWhiteSpace(name))
        {
            message = "配置名不能为空";
            return false;
        }
        var trimmed = name.Trim();
        if (trimmed.Length > 50)
        {
            message = "配置名不能超过 50 个字符";
            return false;
        }
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || trimmed.Contains(".."))
        {
            message = "配置名不能包含 \\ / : * ? \" < > | 等字符";
            return false;
        }
        return true;
    }

    /// <summary>新建档案。copyFrom 指定时从该档案复制现有配置。</summary>
    public bool CreateProfile(string name, string? copyFrom, out string message)
    {
        message = "";
        if (!IsValidProfileName(name, out message)) return false;

        var trimmed = name.Trim();
        var dir = Path.Combine(ProfilesDirectory, trimmed);
        if (Directory.Exists(dir))
        {
            message = $"配置「{trimmed}」已存在";
            return false;
        }

        Directory.CreateDirectory(dir);

        // 从来源档案复制配置文件（存在才复制）
        if (!string.IsNullOrWhiteSpace(copyFrom))
        {
            var srcDir = Path.Combine(ProfilesDirectory, copyFrom.Trim());
            if (Directory.Exists(srcDir))
            {
                foreach (var mode in new[] { "frpc", "frps" })
                {
                    var src = Path.Combine(srcDir, $"{mode}.toml");
                    if (File.Exists(src)) File.Copy(src, Path.Combine(dir, $"{mode}.toml"), overwrite: true);
                }
            }
        }

        message = $"已创建配置「{trimmed}」";
        return true;
    }

    /// <summary>删除档案目录及其配置（至少保留一个）。</summary>
    public bool DeleteProfile(string name, out string message)
    {
        message = "";
        var trimmed = (name ?? "").Trim();

        if (ListProfiles().Count <= 1)
        {
            message = "至少需要保留一个配置";
            return false;
        }

        var dir = Path.Combine(ProfilesDirectory, trimmed);
        if (!Directory.Exists(dir))
        {
            message = $"配置「{trimmed}」不存在";
            return false;
        }

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            message = $"删除失败: {ex.Message}";
            return false;
        }

        // 删除的是当前激活档案时，切到第一个可用档案
        if (string.Equals(_activeProfile, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            var next = ListProfiles().FirstOrDefault();
            if (next != null) SetActiveProfile(next, out _);
        }

        message = $"已删除配置「{trimmed}」";
        return true;
    }

    /// <summary>重命名档案目录。</summary>
    public bool RenameProfile(string name, string newName, out string message)
    {
        message = "";
        var from = (name ?? "").Trim();
        if (!IsValidProfileName(newName, out message)) return false;
        var to = newName.Trim();

        if (string.Equals(from, to, StringComparison.Ordinal)) return true;

        var srcDir = Path.Combine(ProfilesDirectory, from);
        var dstDir = Path.Combine(ProfilesDirectory, to);
        if (!Directory.Exists(srcDir))
        {
            message = $"配置「{from}」不存在";
            return false;
        }
        if (Directory.Exists(dstDir))
        {
            message = $"配置「{to}」已存在";
            return false;
        }

        try
        {
            Directory.Move(srcDir, dstDir);
        }
        catch (Exception ex)
        {
            message = $"重命名失败: {ex.Message}";
            return false;
        }

        if (string.Equals(_activeProfile, from, StringComparison.OrdinalIgnoreCase))
        {
            _activeProfile = to;
            PersistState();
        }

        message = $"已重命名为「{to}」";
        return true;
    }

    /// <summary>切换当前激活的档案。</summary>
    public bool SetActiveProfile(string name, out string message)
    {
        message = "";
        var trimmed = (name ?? "").Trim();
        var dir = Path.Combine(ProfilesDirectory, trimmed);
        if (!Directory.Exists(dir))
        {
            message = $"配置「{trimmed}」不存在";
            return false;
        }
        _activeProfile = trimmed;
        PersistState();
        message = $"已切换到「{trimmed}」";
        return true;
    }

    // ---------- 环境校验 ----------

    public bool Validate(out string message)
    {
        message = "";
        if (string.IsNullOrEmpty(_frpDirectory))
        {
            message = "未选择 FRP 目录";
            return false;
        }
        if (!Directory.Exists(_frpDirectory))
        {
            message = $"FRP 目录不存在: {_frpDirectory}";
            return false;
        }
        if (!File.Exists(GetExecutablePath("frpc")) && !File.Exists(GetExecutablePath("frps")))
        {
            message = $"目录中未找到 frpc 或 frps: {_frpDirectory}";
            return false;
        }
        return true;
    }

    public string GetExecutablePath(string mode)
    {
        var exe = OperatingSystem.IsWindows() ? $"{mode}.exe" : mode;
        return Path.Combine(FrpDirectory, exe);
    }

    /// <summary>当前档案下的配置文件路径。</summary>
    public string GetConfigPath(string mode) => Path.Combine(ActiveProfileDirectory, $"{mode}.toml");

    /// <summary>指定档案下的配置文件路径（档案不存在时抛异常，配合 TryResolveProfileDirectory 使用）。</summary>
    public string GetConfigPath(string mode, string profile)
    {
        if (!TryResolveProfileDirectory(profile, out var dir, out _))
            throw new DirectoryNotFoundException($"配置「{profile}」不存在");
        return Path.Combine(dir, $"{mode}.toml");
    }

    /// <summary>
    /// 解析档案目录：profile 为空时回退到当前激活档案。
    /// 返回 false 表示档案目录不存在（resolvedName 为规范化后的档案名）。
    /// </summary>
    public bool TryResolveProfileDirectory(string? profile, out string directory, out string resolvedName)
    {
        var name = string.IsNullOrWhiteSpace(profile) ? _activeProfile : profile.Trim();
        resolvedName = name;
        var dir = Path.Combine(ProfilesDirectory, name);
        directory = dir;
        return Directory.Exists(dir);
    }

    /// <summary>
    /// 在几个约定位置找 frpc/frps：
    /// 程序目录旁、**按需组件目录**、安装器装过的地方、PATH。
    /// 支持 frp/win-x64、frp/windows-amd64 这类"平台子目录"布局（向下扫一层）。
    /// </summary>
    public bool TryLocateAutomatically()
    {
        var baseDir = AppContext.BaseDirectory;

        var candidates = new List<string>
        {
            Path.Combine(baseDir, "frp"),
            Path.Combine(baseDir, "..", "frp"),
            Path.Combine(baseDir, "..", "..", "frp"),
        };

        // 按需组件目录：frp 不再随安装包分发，开发机/预置的二进制放这里
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "ServerOpsHelper", "components", "frp"));
        }

        // 安装器下载的目标位置（%APPDATA%\FrpManager\frp\<platform>-<arch>）
        candidates.Add(Path.Combine(ConfigDirectory, "frp"));

        foreach (var c in candidates)
        {
            var dir = Path.GetFullPath(c);
            if (!Directory.Exists(dir)) continue;
            if (DirectoryContainsFrpBinary(dir))
            {
                FrpDirectory = dir;
                return true;
            }
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (!DirectoryContainsFrpBinary(sub)) continue;
                FrpDirectory = sub;
                return true;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var p in path.Split(Path.PathSeparator))
            {
                var dir = p.Trim();
                if (string.IsNullOrEmpty(dir)) continue;
                if (DirectoryContainsFrpBinary(dir))
                {
                    FrpDirectory = dir;
                    return true;
                }
            }
        }

        return false;
    }

    // ---------- 内部 ----------

    /// <summary>
    /// 升级到多档案模式时，把旧的 ConfigDirectory/frpc.toml 等
    /// 迁入 profiles/default/，避免用户已保存的配置丢失。
    /// </summary>
    private void MigrateLegacyConfigIfNeeded()
    {
        try
        {
            var legacyFiles = new[] { "frpc.toml", "frps.toml" }
                .Select(f => Path.Combine(ConfigDirectory, f))
                .Where(File.Exists)
                .ToList();
            if (legacyFiles.Count == 0) return;

            var defaultDir = Path.Combine(ProfilesDirectory, DefaultProfileName);
            Directory.CreateDirectory(defaultDir);

            foreach (var src in legacyFiles)
            {
                var dst = Path.Combine(defaultDir, Path.GetFileName(src));
                if (File.Exists(dst)) continue; // 已存在则不覆盖
                try
                {
                    File.Move(src, dst);
                }
                catch
                {
                    // 移动失败退化为复制，保留原文件
                    try { File.Copy(src, dst, overwrite: false); } catch { }
                }
            }
        }
        catch
        {
            // 迁移失败不影响启动
        }
    }

    private void EnsureActiveProfileExists()
    {
        // 激活档案目录不存在时回退到第一个可用档案
        if (!Directory.Exists(Path.Combine(ProfilesDirectory, _activeProfile)))
        {
            _activeProfile = ListProfiles().FirstOrDefault() ?? DefaultProfileName;
        }

        // 一个档案都没有时创建默认档案
        if (!Directory.Exists(Path.Combine(ProfilesDirectory, _activeProfile)))
        {
            _activeProfile = DefaultProfileName;
            Directory.CreateDirectory(ActiveProfileDirectory);
            PersistState();
        }
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_stateFile)) return;
            var json = File.ReadAllText(_stateFile).Trim();
            if (string.IsNullOrEmpty(json)) return;

            var saved = System.Text.Json.JsonSerializer.Deserialize<StateFile>(json);
            if (saved is null) return;

            if (saved.FrpDirectory is { Length: > 0 } dir && Directory.Exists(dir))
            {
                _frpDirectory = dir;
            }
            if (!string.IsNullOrWhiteSpace(saved.ActiveProfile))
            {
                _activeProfile = saved.ActiveProfile.Trim();
            }
        }
        catch
        {
            // 状态文件损坏时忽略，使用默认值
        }
    }

    private void PersistState()
    {
        try
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new StateFile
            {
                FrpDirectory = _frpDirectory,
                ActiveProfile = _activeProfile
            });
            File.WriteAllText(_stateFile, payload);
        }
        catch
        {
            // 持久化失败时不影响运行时（只丢重启恢复能力）
        }
    }

    private static bool DirectoryContainsFrpBinary(string dir)
    {
        try
        {
            return File.Exists(Path.Combine(dir, OperatingSystem.IsWindows() ? "frpc.exe" : "frpc"))
                || File.Exists(Path.Combine(dir, OperatingSystem.IsWindows() ? "frps.exe" : "frps"));
        }
        catch
        {
            return false;
        }
    }

    private static string GetAppConfigDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            return Path.Combine(appData, "FrpManager");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "FrpManager");
    }

    private record StateFile
    {
        public string? FrpDirectory { get; init; }
        public string? ActiveProfile { get; init; }
    }
}