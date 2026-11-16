using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.Net.Http;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ServerOpsHelper.Frp;

/// <summary>
/// 从 GitHub Releases 自动安装 / 更新 frpc / frps。
/// 流程：查询最新版本 → 下载对应平台压缩包 → 解压提取 frpc/frps → 覆盖到目标目录。
/// </summary>
public class FrpInstaller
{
    private readonly FrpState _state;
    private readonly FrpEnvironment _env;
    private readonly FrpProcess _process;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>下载/解压整体超时（弱网环境从 GitHub 拉取可能较慢）。</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private const string RepoOwner = "fatedier";
    private const string RepoName = "frp";

    /// <summary>
    /// 默认安装的稳定版本。0.71.0 存在未解决的心跳回归问题
    /// (issue #5526: frps 0.71.0 每 heartbeatTimeout 秒误杀健康客户端)，
    /// 故稳定版锁定在 0.70.1（最后一个纯 bug 修复版本，已运行近两个月）。
    /// 更新此常量时同步确认 GitHub issue 区无严重回归。
    /// </summary>
    public const string DefaultStableVersion = "0.70.1";

    public FrpInstaller(FrpState state, FrpEnvironment env, FrpProcess process)
    {
        _state = state;
        _env = env;
        _process = process;
    }

    // ---------- 版本查询 ----------

    /// <summary>本地已安装的 frp 版本（frpc -v 的输出；frpc 不存在则尝试 frps）。未安装返回 null。</summary>
    public string? GetLocalVersion()
    {
        foreach (var mode in new[] { "frpc", "frps" })
        {
            var exe = _env.GetExecutablePath(mode);
            var v = RunVersionCommand(exe);
            if (v != null) return v;
        }
        return null;
    }

    /// <summary>GitHub 上最新的 frp 版本号（如 "0.66.0"）。网络失败抛异常。</summary>
    public async Task<string> GetLatestVersionAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest");
        req.Headers.UserAgent.ParseAdd("FrpManager-Web");

        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, ct);
        var tag = release?.TagName;
        if (string.IsNullOrEmpty(tag)) throw new InvalidOperationException("GitHub 返回的 release 数据为空");
        return tag.TrimStart('v', 'V');
    }

    /// <summary>查询结果汇总：本地版本、远端最新版本、目标安装目录、平台标识。</summary>
    public async Task<VersionInfo> GetVersionInfoAsync(CancellationToken ct = default)
    {
        string? latest = null;
        string? latestError = null;
        try
        {
            latest = await GetLatestVersionAsync(ct);
        }
        catch (Exception ex)
        {
            latestError = ex.Message;
        }

        return new VersionInfo
        {
            Current = GetLocalVersion(),
            Stable = DefaultStableVersion,
            Latest = latest,
            LatestError = latestError,
            TargetDir = ResolveTargetDir(),
            Platform = GetPlatformAssetKey()
        };
    }

    // ---------- 安装 / 更新 ----------

    /// <summary>
    /// 安装或更新 frp。version 为空时安装最新版。
    /// 若 frp 进程正在运行会先自动停止（Windows 下运行中的 exe 无法覆盖）。
    /// </summary>
    public async Task<InstallResult> InstallAsync(string? version = null, CancellationToken ct = default)
    {
        try
        {
            // 1. 确定版本：缺省安装稳定版（不请求 GitHub，离线也可装）
            if (string.IsNullOrWhiteSpace(version))
            {
                version = DefaultStableVersion;
            }
            version = version.TrimStart('v', 'V');
            _state.AppendLog($"[系统] 目标版本: frp v{version}", "sys");

            // 2. 平台与资源名
            var (platform, arch, ext) = GetPlatformTarget();
            var assetName = $"frp_{version}_{platform}_{arch}.{ext}";
            var url = $"https://github.com/{RepoOwner}/{RepoName}/releases/download/v{version}/{assetName}";

            // 3. 目标目录：已有有效 frp 目录则就地更新，否则装到应用数据目录
            var targetDir = ResolveTargetDir();
            Directory.CreateDirectory(targetDir);

            // 4. 运行中先停（Windows 不允许覆盖运行中的 exe）
            if (_state.IsRunning)
            {
                _state.AppendLog("[系统] frp 进程运行中，先停止以便覆盖二进制…", "sys");
                _process.Stop();
            }

            // 5. 下载
            _state.AppendLog($"[系统] 正在下载 {assetName} …", "sys");
            var tmpFile = Path.Combine(Path.GetTempPath(), assetName);
            try
            {
                var downloadOk = false;
                string? downloadErr = null;
                for (var attempt = 1; attempt <= 3 && !downloadOk; attempt++)
                {
                    if (attempt > 1)
                    {
                        _state.AppendLog($"[系统] 下载失败（{downloadErr}），第 {attempt}/3 次重试…", "sys");
                        await Task.Delay(2000, ct);
                    }
                    try
                    {
                        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                        {
                            if (!resp.IsSuccessStatusCode)
                            {
                                downloadErr = $"HTTP {(int)resp.StatusCode}";
                                // 404 等客户端错误重试无意义，直接终止
                                if ((int)resp.StatusCode is >= 400 and < 500) break;
                                continue;
                            }
                            await using var fs = File.Create(tmpFile);
                            await resp.Content.CopyToAsync(fs, ct);
                            downloadOk = true;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        downloadErr = ex.Message;
                    }
                }

                if (!downloadOk)
                {
                    return new InstallResult
                    {
                        Ok = false,
                        Message = $"下载失败: {downloadErr}。请检查网络是否可达 GitHub（{url}）。"
                    };
                }

                // 6. 解压提取
                _state.AppendLog("[系统] 正在解压并安装…", "sys");
                var installed = ext == "zip"
                    ? ExtractZip(tmpFile, targetDir)
                    : ExtractTarGz(tmpFile, targetDir);
                if (installed.Count == 0)
                {
                    return new InstallResult { Ok = false, Message = $"压缩包中未找到 frpc/frps 二进制: {assetName}" };
                }

                // 7. 生效：指向新目录并记录
                _env.FrpDirectory = targetDir;
                _state.AppendLog($"[系统] frp v{version} 安装完成: {targetDir}（{string.Join(", ", installed)}）", "sys");
                return new InstallResult { Ok = true, Version = version, Path = targetDir };
            }
            finally
            {
                try { File.Delete(tmpFile); } catch { /* 临时文件清理失败忽略 */ }
            }
        }
        catch (Exception ex)
        {
            _state.AppendLog($"[错误] frp 安装失败: {ex.Message}", "err");
            return new InstallResult { Ok = false, Message = ex.Message };
        }
    }

    // ---------- 内部实现 ----------

    /// <summary>安装目标目录：已有有效目录就地更新；否则装到 %APPDATA%/FrpManager/frp/&lt;platform&gt;。</summary>
    private string ResolveTargetDir()
    {
        if (_env.Validate(out _))
        {
            return _env.FrpDirectory;
        }
        var (platform, arch, _) = GetPlatformTarget();
        return Path.Combine(_env.ConfigDirectory, "frp", $"{platform}-{arch}");
    }

    private static (string Platform, string Arch, string Ext) GetPlatformTarget()
    {
        string platform, arch, ext;
        if (OperatingSystem.IsWindows())
        {
            platform = "windows";
            arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
            ext = "zip";
        }
        else if (OperatingSystem.IsMacOS())
        {
            platform = "darwin";
            arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
            ext = "tar.gz";
        }
        else if (OperatingSystem.IsLinux())
        {
            platform = "linux";
            arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.Arm => "arm",
                _ => "amd64"
            };
            ext = "tar.gz";
        }
        else
        {
            throw new PlatformNotSupportedException($"不支持的平台: {RuntimeInformation.OSDescription}");
        }
        return (platform, arch, ext);
    }

    private static string GetPlatformAssetKey()
    {
        var (p, a, _) = GetPlatformTarget();
        return $"{p}-{a}";
    }

    /// <summary>运行 frpc/frps -v 获取版本号；二进制不存在或执行失败返回 null。</summary>
    private static string? RunVersionCommand(string exePath)
    {
        if (!File.Exists(exePath)) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "-v",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            var line = output.Trim().Split('\n')[0].Trim();
            return line.Length == 0 ? null : line;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从 zip 中提取 frpc/frps（兼容 frp_x.y.z_windows_amd64/ 顶层目录）。返回安装的文件名列表。</summary>
    private static List<string> ExtractZip(string zipPath, string targetDir)
    {
        var installed = new List<string>();
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var name = Path.GetFileName(entry.FullName); // 去掉顶层目录
            if (name is not ("frpc" or "frps" or "frpc.exe" or "frps.exe")) continue;
            if (name.Length == 0) continue;

            var dest = Path.Combine(targetDir, name);
            entry.ExtractToFile(dest, overwrite: true);
            MarkExecutable(dest);
            installed.Add(name);
        }
        return installed;
    }

    /// <summary>从 tar.gz 中提取 frpc/frps。返回安装的文件名列表。</summary>
    private static List<string> ExtractTarGz(string tarPath, string targetDir)
    {
        var installed = new List<string>();
        using var fs = File.OpenRead(tarPath);
        using var gz = new System.IO.Compression.GZipStream(fs, CompressionMode.Decompress);
        using var reader = new TarReader(gz);
        while (reader.GetNextEntry() is { } entry)
        {
            var name = Path.GetFileName(entry.Name);
            if (name is not ("frpc" or "frps" or "frpc.exe" or "frps.exe")) continue;

            var dest = Path.Combine(targetDir, name);
            entry.ExtractToFile(dest, overwrite: true);
            MarkExecutable(dest);
            installed.Add(name);
        }
        return installed;
    }

    private static void MarkExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch
        {
            // 平台不支持时忽略
        }
    }

    private record GitHubRelease
    {
        [System.Text.Json.Serialization.JsonPropertyName("tag_name")]
        public string? TagName { get; set; }
    }
}

public class VersionInfo
{
    public string? Current { get; set; }
    /// <summary>推荐安装的稳定版本（见 FrpInstaller.DefaultStableVersion）。</summary>
    public string? Stable { get; set; }
    public string? Latest { get; set; }
    public string? LatestError { get; set; }
    public string TargetDir { get; set; } = "";
    public string Platform { get; set; } = "";
}

public class InstallResult
{
    public bool Ok { get; set; }
    public string? Version { get; set; }
    public string? Path { get; set; }
    public string? Message { get; set; }
}