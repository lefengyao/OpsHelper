using ServerOpsHelper.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ServerOpsHelper.Services;

/// <summary>一次扩展扫描的结果（给界面和探针看）。</summary>
public sealed class ExtensionLoadSummary
{
    public List<ExtensionPack> Packs { get; } = new();

    public List<string> Errors { get; } = new();

    /// <summary>可用（已启用且无错误）的扩展数量</summary>
    public int ActivePackCount => Packs.Count(p => p.IsValid && p.Enabled);

    /// <summary>可用（已启用且无错误）的命令数量</summary>
    public int ActiveCommandCount => Packs.Where(p => p.IsValid && p.Enabled).Sum(p => p.Commands.Count);
}

/// <summary>
/// 扩展包加载与执行。
/// <para>
/// 目录：<c>%LOCALAPPDATA%\ServerOpsHelper\extensions\</c>（可用
/// <c>SERVEROPSHELPER_CONFIG_DIR</c> 整体改到别处，探针就靠这个隔离）。
/// 两种写法任选：<c>extensions/&lt;名字&gt;/extension.json</c>（可以带脚本等附件）
/// 或 <c>extensions/&lt;名字&gt;.json</c>（单文件）。
/// </para>
/// <para>
/// 设计约束（改动前先读这个）：
/// </para>
/// <list type="number">
///   <item><description>
///     <b>不加载任何外来代码</b>。扩展只描述命令，执行走 <see cref="RemoteExecutionService"/>，
///     因此写权限闸门、审计、双层超时全部天然继承，不需要另建安全模型。
///   </description></item>
///   <item><description>
///     <b>参数必须做 shell 引用</b>。模板占位符替换成 <c>'值'</c>（内部单引号转义成
///     <c>'\''</c>），并拒绝换行/控制字符 —— 否则一个带 <c>;</c> 的参数就能在服务器上
///     执行任意命令，等于把 MCP 的权限闸门绕过去了。
///   </description></item>
///   <item><description>
///     <b>单个扩展坏了不影响别的、更不影响主程序</b>。解析失败只记进
///     <see cref="ExtensionLoadSummary.Errors"/>，界面显示红色说明，其余照常加载。
///   </description></item>
/// </list>
/// </summary>
public sealed class ExtensionService
{
    private static readonly Lazy<ExtensionService> _lazy = new(() => new ExtensionService());
    public static ExtensionService Instance => _lazy.Value;

    /// <summary>扩展标识：小写字母/数字开头，允许下划线与短横线，最长 32</summary>
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9_-]{0,31}$", RegexOptions.Compiled);

    /// <summary>参数名：字母或下划线开头</summary>
    private static readonly Regex ParamNamePattern = new("^[A-Za-z_][A-Za-z0-9_]{0,31}$", RegexOptions.Compiled);

    /// <summary>模板占位符 <c>${name}</c></summary>
    private static readonly Regex PlaceholderPattern =
        new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    /// <summary>MCP 工具名上限（协议规定 1~64）</summary>
    private const int MaxToolNameLength = 64;

    /// <summary>工具名前缀：ext_&lt;扩展&gt;_&lt;命令&gt;</summary>
    public const string ToolNamePrefix = "ext_";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    private readonly object _gate = new();
    private ExtensionLoadSummary _current = new();

    private ExtensionService()
    {
    }

    /// <summary>扩展根目录（随配置目录走，跟着 SERVEROPSHELPER_CONFIG_DIR 隔离）</summary>
    public string ExtensionsDirectory
        => Path.Combine(ConfigService.Instance.ConfigDirectory, "extensions");

    /// <summary>当前已加载的扩展（快照，线程安全读）</summary>
    public IReadOnlyList<ExtensionPack> Packs
    {
        get
        {
            lock (_gate)
            {
                return _current.Packs.ToArray();
            }
        }
    }

    /// <summary>最近一次扫描的错误与统计</summary>
    public ExtensionLoadSummary Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// 重新扫描扩展目录。任何异常都只记成错误，绝不抛出 —— 启动流程不能因为一个坏扩展挂掉。
    /// </summary>
    public ExtensionLoadSummary Reload()
    {
        var summary = new ExtensionLoadSummary();
        var directory = ExtensionsDirectory;

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            summary.Errors.Add($"无法创建扩展目录：{ex.Message}");
        }

        var seenIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var disabledByUser = LoadDisabledIds();

        foreach (var file in EnumerateManifests(directory))
        {
            ExtensionPack? pack;
            try
            {
                // 允许 BOM / 注释 / 尾逗号：这是给人手写的文件，不是机器产物
                var json = File.ReadAllText(file);
                pack = JsonSerializer.Deserialize<ExtensionPack>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                pack = new ExtensionPack
                {
                    Id = Path.GetFileNameWithoutExtension(file).ToLowerInvariant(),
                    Name = Path.GetFileName(file),
                    LoadError = $"JSON 解析失败：{ex.Message}"
                };
                summary.Packs.Add(pack);
                summary.Errors.Add($"{pack.Name}：{pack.LoadError}");
                continue;
            }

            if (pack == null)
            {
                summary.Errors.Add($"{file}：内容为空");
                continue;
            }

            pack.SourcePath = file;

            // 目录名/文件名兜底成 Id，减少手写负担
            if (string.IsNullOrWhiteSpace(pack.Id))
            {
                pack.Id = Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty);
                if (!string.Equals(Path.GetFileName(file), "extension.json", StringComparison.OrdinalIgnoreCase))
                {
                    pack.Id = Path.GetFileNameWithoutExtension(file);
                }

                pack.Id = (pack.Id ?? string.Empty).Trim().ToLowerInvariant();
            }
            else
            {
                pack.Id = pack.Id.Trim().ToLowerInvariant();
            }

            if (string.IsNullOrWhiteSpace(pack.Name))
            {
                pack.Name = pack.Id;
            }

            pack.LoadError = Validate(pack) ?? string.Empty;

            // 界面上点过「停用」的：清单本身不改，用 .state.json 覆盖
            if (disabledByUser.Contains(pack.Id))
            {
                pack.DisabledByUser = true;
                pack.Enabled = false;
            }

            if (string.IsNullOrEmpty(pack.LoadError))
            {
                if (seenIds.TryGetValue(pack.Id, out var firstPath))
                {
                    pack.LoadError = $"扩展 id「{pack.Id}」重复（已被 {firstPath} 使用）";
                }
                else
                {
                    seenIds[pack.Id] = file;
                }
            }

            if (!string.IsNullOrEmpty(pack.LoadError))
            {
                summary.Errors.Add($"{pack.Name}（{Path.GetFileName(file)}）：{pack.LoadError}");
            }

            summary.Packs.Add(pack);
        }

        lock (_gate)
        {
            _current = summary;
        }

        return summary;
    }

    // ───────────────────────── 安装 / 卸载 / 启停 ─────────────────────────

    private string StateFilePath => Path.Combine(ExtensionsDirectory, ".state.json");

    private HashSet<string> LoadDisabledIds()
    {
        try
        {
            if (!File.Exists(StateFilePath))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var state = JsonSerializer.Deserialize<DisabledState>(File.ReadAllText(StateFilePath), JsonOptions);
            return new HashSet<string>(state?.Disabled ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveDisabledIds(HashSet<string> ids)
    {
        try
        {
            Directory.CreateDirectory(ExtensionsDirectory);

            File.WriteAllText(
                StateFilePath,
                JsonSerializer.Serialize(new DisabledState { Disabled = ids.OrderBy(i => i).ToList() }, JsonOptions),
                new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // 记不住"停用"状态不该阻断操作，下次加载会恢复成启用
        }
    }

    /// <summary>停用 / 启用一个扩展（只改 .state.json，不动用户的清单文件）</summary>
    public ExtensionInstallResult SetEnabled(string id, bool enabled)
    {
        var ids = LoadDisabledIds();

        if (enabled)
        {
            ids.Remove(id);
        }
        else
        {
            ids.Add(id);
        }

        SaveDisabledIds(ids);
        Reload();

        return ExtensionInstallResult.Success(enabled ? $"已启用「{id}」。" : $"已停用「{id}」。");
    }

    /// <summary>安装内置推荐扩展（把清单写进 extensions/&lt;id&gt;/extension.json）</summary>
    public ExtensionInstallResult InstallBuiltIn(string id)
    {
        var builtIn = ExtensionCatalog.Find(id);

        if (builtIn == null)
        {
            return ExtensionInstallResult.Failure("没有这个内置扩展。");
        }

        var targetDir = Path.Combine(ExtensionsDirectory, builtIn.Id);

        if (Directory.Exists(targetDir) || File.Exists(Path.Combine(ExtensionsDirectory, builtIn.Id + ".json")))
        {
            return ExtensionInstallResult.Failure("这个扩展已经安装过了。如需重装，请先卸载。");
        }

        try
        {
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(Path.Combine(targetDir, "extension.json"), builtIn.Json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            return ExtensionInstallResult.Failure($"写入失败：{ex.Message}");
        }

        return FinishInstall(builtIn.Id);
    }

    /// <summary>安装一个扩展包文件（.zip 压缩包或单个 .json 清单）</summary>
    public ExtensionInstallResult InstallFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return ExtensionInstallResult.Failure("文件不存在。");
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => InstallSingleFile(path),
            ".zip" => InstallFromZip(path),
            _ => ExtensionInstallResult.Failure("只支持 .zip 扩展包或 .json 清单文件。")
        };
    }

    private ExtensionInstallResult InstallFromZip(string zipPath)
    {
        var temp = Path.Combine(Path.GetTempPath(), "opshelper-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var root = Path.GetFullPath(temp) + Path.DirectorySeparatorChar;

                foreach (var entry in archive.Entries)
                {
                    var target = Path.GetFullPath(Path.Combine(temp, entry.FullName));

                    // 防 zip slip：解压后必须仍在临时目录里
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        return ExtensionInstallResult.Failure("压缩包里有非法路径（试图写到目录外），已拒绝安装。");
                    }
                }
            }

            ZipFile.ExtractToDirectory(zipPath, temp, overwriteFiles: true);

            var manifest = Path.Combine(temp, "extension.json");

            if (!File.Exists(manifest))
            {
                var nested = Directory.GetDirectories(temp)
                    .Select(d => Path.Combine(d, "extension.json"))
                    .Where(File.Exists)
                    .ToList();

                if (nested.Count == 1)
                {
                    manifest = nested[0];
                }
                else if (nested.Count > 1)
                {
                    return ExtensionInstallResult.Failure("压缩包里有多套扩展，请一次打包一个。");
                }
                else
                {
                    var singleJson = Directory.GetFiles(temp, "*.json").FirstOrDefault();

                    return singleJson == null
                        ? ExtensionInstallResult.Failure("压缩包里没有 extension.json。")
                        : InstallSingleFile(singleJson);
                }
            }

            return InstallPackDirectory(Path.GetDirectoryName(manifest)!);
        }
        catch (Exception ex)
        {
            return ExtensionInstallResult.Failure($"解压失败：{ex.Message}");
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private ExtensionInstallResult InstallSingleFile(string jsonPath)
    {
        ExtensionPack? pack;

        try
        {
            pack = JsonSerializer.Deserialize<ExtensionPack>(File.ReadAllText(jsonPath), JsonOptions);
        }
        catch (Exception ex)
        {
            return ExtensionInstallResult.Failure($"清单解析失败：{ex.Message}");
        }

        if (pack == null)
        {
            return ExtensionInstallResult.Failure("清单是空的。");
        }

        pack.Id = (pack.Id ?? string.Empty).Trim().ToLowerInvariant();

        var error = Validate(pack);
        if (error != null)
        {
            return ExtensionInstallResult.Failure(error);
        }

        var target = Path.Combine(ExtensionsDirectory, pack.Id + ".json");

        if (File.Exists(target) || Directory.Exists(Path.Combine(ExtensionsDirectory, pack.Id)))
        {
            return ExtensionInstallResult.Failure("这个扩展已经安装过了。如需重装，请先卸载。");
        }

        try
        {
            Directory.CreateDirectory(ExtensionsDirectory);
            File.Copy(jsonPath, target, overwrite: false);
        }
        catch (Exception ex)
        {
            return ExtensionInstallResult.Failure($"复制失败：{ex.Message}");
        }

        return FinishInstall(pack.Id);
    }

    private ExtensionInstallResult InstallPackDirectory(string sourceDir)
    {
        ExtensionPack? pack;

        try
        {
            pack = JsonSerializer.Deserialize<ExtensionPack>(
                File.ReadAllText(Path.Combine(sourceDir, "extension.json")), JsonOptions);
        }
        catch (Exception ex)
        {
            return ExtensionInstallResult.Failure($"清单解析失败：{ex.Message}");
        }

        if (pack == null)
        {
            return ExtensionInstallResult.Failure("清单是空的。");
        }

        pack.Id = (pack.Id ?? string.Empty).Trim().ToLowerInvariant();

        var error = Validate(pack);
        if (error != null)
        {
            return ExtensionInstallResult.Failure(error);
        }

        var target = Path.Combine(ExtensionsDirectory, pack.Id);

        if (Directory.Exists(target) || File.Exists(Path.Combine(ExtensionsDirectory, pack.Id + ".json")))
        {
            return ExtensionInstallResult.Failure("这个扩展已经安装过了。如需重装，请先卸载。");
        }

        try
        {
            CopyDirectory(sourceDir, target);
        }
        catch (Exception ex)
        {
            return ExtensionInstallResult.Failure($"安装失败：{ex.Message}");
        }

        return FinishInstall(pack.Id);
    }

    private ExtensionInstallResult FinishInstall(string id)
    {
        var pack = Reload().Packs
            .FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

        if (pack == null)
        {
            return ExtensionInstallResult.Failure("安装后没有读到这个扩展，请检查扩展目录。");
        }

        if (!pack.IsValid)
        {
            return ExtensionInstallResult.Failure($"安装后校验失败：{pack.LoadError}");
        }

        return ExtensionInstallResult.Success($"已安装「{pack.Name}」，共 {pack.Commands.Count} 个命令。");
    }

    /// <summary>卸载：目录形式删整个目录，单文件形式删文件</summary>
    public ExtensionInstallResult Uninstall(string id)
    {
        var pack = Packs.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

        if (pack == null || string.IsNullOrEmpty(pack.SourcePath))
        {
            return ExtensionInstallResult.Failure("没找到这个扩展。");
        }

        try
        {
            var manifest = pack.SourcePath;

            if (string.Equals(Path.GetFileName(manifest), "extension.json", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(manifest);

                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            else
            {
                File.Delete(manifest);
            }
        }
        catch (Exception ex)
        {
            return ExtensionInstallResult.Failure($"卸载失败：{ex.Message}");
        }

        // 从"用户停用"名单里摘掉，避免同 id 重装后莫名是停用状态
        var ids = LoadDisabledIds();
        if (ids.Remove(id))
        {
            SaveDisabledIds(ids);
        }

        Reload();

        return ExtensionInstallResult.Success("已卸载。");
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // 临时目录删不掉就留给系统清理
        }
    }

    private sealed class DisabledState
    {
        public List<string> Disabled { get; set; } = new();
    }

    /// <summary>
    /// 列出所有清单文件：子目录里的 extension.json + 根目录下的 *.json。
    /// <para>
    /// 跳过 <c>_</c> 或 <c>.</c> 开头的文件/目录：方便把 <c>_disabled</c>、
    /// <c>_example.json.txt</c> 这类东西放进去而不被加载。
    /// </para>
    /// </summary>
    private static IEnumerable<string> EnumerateManifests(string directory)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        var files = new List<string>();

        try
        {
            files.AddRange(Directory
                .GetDirectories(directory)
                .Where(d => !Path.GetFileName(d).StartsWith("_") && !Path.GetFileName(d).StartsWith("."))
                .Select(d => Path.Combine(d, "extension.json"))
                .Where(File.Exists));

            files.AddRange(Directory
                .GetFiles(directory, "*.json")
                .Where(f => !Path.GetFileName(f).StartsWith("_") && !Path.GetFileName(f).StartsWith(".")));
        }
        catch (Exception)
        {
            // 目录读不了就当成没有扩展，不抛
        }

        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            yield return file;
        }
    }

    /// <summary>
    /// 校验一个扩展包。返回 null 表示可用；否则是给人看的中文原因。
    /// </summary>
    private static string? Validate(ExtensionPack pack)
    {
        if (string.IsNullOrWhiteSpace(pack.Id))
        {
            return "缺少 id";
        }

        if (!IdPattern.IsMatch(pack.Id))
        {
            return $"id「{pack.Id}」不合法：只能用小写字母/数字/下划线/短横线，且不超过 32 个字符";
        }

        if (pack.Commands.Count == 0)
        {
            return "commands 为空";
        }

        var commandIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var command in pack.Commands)
        {
            command.Id = (command.Id ?? string.Empty).Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(command.Id))
            {
                return $"「{command.Name}」缺少 id";
            }

            if (!IdPattern.IsMatch(command.Id))
            {
                return $"命令 id「{command.Id}」不合法：只能用小写字母/数字/下划线/短横线，且不超过 32 个字符";
            }

            if (!commandIds.Add(command.Id))
            {
                return $"命令 id「{command.Id}」重复";
            }

            if (string.IsNullOrWhiteSpace(command.Command))
            {
                return $"「{command.Name}」缺少 command";
            }

            if (string.IsNullOrWhiteSpace(command.Name))
            {
                command.Name = command.Id;
            }

            if (ToolName(pack, command).Length > MaxToolNameLength)
            {
                return $"工具名 {ToolName(pack, command)} 超过 {MaxToolNameLength} 个字符，请缩短扩展或命令的 id";
            }

            // 参数名合法且不重复
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in command.Parameters)
            {
                parameter.Name = (parameter.Name ?? string.Empty).Trim();

                if (!ParamNamePattern.IsMatch(parameter.Name))
                {
                    return $"「{command.Name}」的参数名「{parameter.Name}」不合法";
                }

                if (!names.Add(parameter.Name))
                {
                    return $"「{command.Name}」的参数名「{parameter.Name}」重复";
                }

                parameter.Type = (parameter.Type ?? "string").Trim().ToLowerInvariant();
                if (parameter.Type is not ("string" or "int" or "bool" or "path"))
                {
                    return $"「{command.Name}」的参数「{parameter.Name}」类型「{parameter.Type}」不支持（只能是 string/int/bool/path）";
                }
            }

            // 模板里出现的占位符必须有定义：否则执行时才报错，用户很难查
            foreach (Match match in PlaceholderPattern.Matches(command.Command))
            {
                var placeholder = match.Groups[1].Value;
                if (!names.Contains(placeholder))
                {
                    return $"「{command.Name}」的模板用了 ${{{placeholder}}}，但没有在 parameters 里声明";
                }
            }
        }

        return null;
    }

    /// <summary>扩展命令对应的 MCP 工具名：<c>ext_&lt;扩展&gt;_&lt;命令&gt;</c></summary>
    public static string ToolName(ExtensionPack pack, ExtensionCommand command)
        => $"{ToolNamePrefix}{pack.Id}_{command.Id}";

    /// <summary>按工具名找回扩展与命令</summary>
    public (ExtensionPack Pack, ExtensionCommand Command)? Resolve(string toolName)
    {
        if (!toolName.StartsWith(ToolNamePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        // 不能用 Split('_')：id 里本来就允许下划线。逐个比完整工具名最稳。
        foreach (var pack in Packs)
        {
            if (!pack.IsValid || !pack.Enabled)
            {
                continue;
            }

            foreach (var command in pack.Commands)
            {
                if (string.Equals(ToolName(pack, command), toolName, StringComparison.Ordinal))
                {
                    return (pack, command);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 把所有扩展命令摊平成「显示 → 命令」的列表（服务器页的下拉框用）。
    /// </summary>
    public IReadOnlyList<(ExtensionPack Pack, ExtensionCommand Command)> AllCommands()
    {
        var list = new List<(ExtensionPack, ExtensionCommand)>();

        foreach (var pack in Packs.Where(p => p.IsValid && p.Enabled))
        {
            foreach (var command in pack.Commands)
            {
                list.Add((pack, command));
            }
        }

        return list;
    }

    /// <summary>
    /// 把模板里的占位符替换成安全引用后的参数值。
    /// <para>
    /// 参数值一律包单引号（内部单引号变成 <c>'\''</c>），并且拒绝换行与 NUL ——
    /// 这两条是防止参数逃逸成第二条命令的关键，不要为了"方便"去掉。
    /// </para>
    /// </summary>
    /// <exception cref="ExtensionException">参数缺失、类型不对、值不安全。</exception>
    public static string Substitute(ExtensionCommand command, IReadOnlyDictionary<string, string> values)
    {
        return PlaceholderPattern.Replace(command.Command, match =>
        {
            var name = match.Groups[1].Value;
            var parameter = command.Parameters.FirstOrDefault(
                p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

            if (parameter == null)
            {
                // Validate 已经拦过一遍，这里是防御（扩展可能在运行期被改成别的样子）
                throw new ExtensionException($"命令「{command.Name}」引用了未声明的参数 ${{{name}}}");
            }

            var raw = values.TryGetValue(name, out var provided) && !string.IsNullOrWhiteSpace(provided)
                ? provided!.Trim()
                : parameter.Default;

            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new ExtensionException($"缺少参数「{name}」（{parameter.Description}）");
            }

            raw = Normalize(parameter, raw);

            return ShellQuote(raw);
        });
    }

    /// <summary>按类型校验并规整参数值</summary>
    private static string Normalize(ExtensionParameter parameter, string raw)
    {
        if (raw.IndexOfAny(new[] { '\n', '\r', '\0' }) >= 0)
        {
            throw new ExtensionException($"参数「{parameter.Name}」不能包含换行或控制字符");
        }

        switch (parameter.Type)
        {
            case "int":
            {
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    throw new ExtensionException($"参数「{parameter.Name}」必须是整数，收到「{raw}」");
                }

                if (parameter.Min.HasValue && number < parameter.Min.Value)
                {
                    throw new ExtensionException($"参数「{parameter.Name}」不能小于 {parameter.Min.Value}");
                }

                if (parameter.Max.HasValue && number > parameter.Max.Value)
                {
                    throw new ExtensionException($"参数「{parameter.Name}」不能大于 {parameter.Max.Value}");
                }

                return number.ToString(CultureInfo.InvariantCulture);
            }

            case "bool":
            {
                var lower = raw.ToLowerInvariant();
                return lower switch
                {
                    "1" or "true" or "yes" or "on" => "1",
                    "0" or "false" or "no" or "off" => "0",
                    _ => throw new ExtensionException($"参数「{parameter.Name}」必须是 true/false，收到「{raw}」")
                };
            }

            default:
                return raw;
        }
    }

    /// <summary>单引号引用：POSIX shell 里这是唯一没有副作用的转义方式</summary>
    private static string ShellQuote(string value)
        => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>
    /// 在指定服务器上执行一条扩展命令（参数已替换、已安全引用）。
    /// </summary>
    /// <param name="server">目标服务器</param>
    /// <param name="command">命令定义</param>
    /// <param name="values">参数值（键为参数名，大小写不敏感）</param>
    public async Task<RemoteCommandResult> ExecuteAsync(
        ServerInfo server,
        ExtensionCommand command,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        var resolved = Substitute(command, values);
        var timeout = Math.Clamp(command.TimeoutSeconds <= 0 ? 60 : command.TimeoutSeconds, 5, 600);

        // 走既有通道：MarkConnected、连接复用与异常包装都在里面，不另起一套
        var runner = new RemoteExecutionService();
        var work = runner.ExecuteSshCommandAsync(server, resolved, timeout);

        // 外层硬兜底：RemoteExecutionService 内部已经有取消，但取消信号偶有"发了不返回"的情况，
        // 界面就会一直转圈。这里到点就**主动断链**，而不是丢下不管 ——
        // 丢下不管的旧行为会让那条会话一直占着服务端的槽位，拖累后续所有新建连接。
        var guard = Task.Delay(TimeSpan.FromSeconds(timeout + 10));
        var finished = await Task.WhenAny(work, guard).ConfigureAwait(false);

        if (finished != work)
        {
            SshSessionPool.Shared.Discard(server);

            _ = work.ContinueWith(
                t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return new RemoteCommandResult
            {
                Success = false,
                ExitCode = -1,
                Output = string.Empty,
                Error = $"命令在 {timeout} 秒内没有结束，已放弃等待并断开这条 SSH 连接。"
            };
        }

        return await work.ConfigureAwait(false);
    }

    /// <summary>把扩展命令的说明拼成给模型看的一段文字</summary>
    public static string DescribeForAgent(ExtensionPack pack, ExtensionCommand command)
    {
        var text = string.IsNullOrWhiteSpace(command.Description)
            ? command.Name
            : command.Description;

        return $"{text}\n\nFrom the user's custom extension pack \"{pack.Name}\" ({pack.Id}). " +
               $"Runs this shell template on the target Linux server:\n{command.Command}\n\n" +
               "Placeholders are substituted with shell-quoted values; do not add quotes yourself.";
    }
}

/// <summary>安装 / 卸载 / 启停的结果（消息直接给用户看）</summary>
public sealed class ExtensionInstallResult
{
    public bool Ok { get; private init; }

    public string Message { get; private init; } = string.Empty;

    public static ExtensionInstallResult Success(string message) => new() { Ok = true, Message = message };

    public static ExtensionInstallResult Failure(string message) => new() { Ok = false, Message = message };
}

/// <summary>扩展执行期错误（参数缺失/类型不对等），对用户可读。</summary>
public sealed class ExtensionException : Exception
{
    public ExtensionException(string message) : base(message)
    {
    }
}
