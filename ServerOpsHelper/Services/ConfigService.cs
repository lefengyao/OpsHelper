using ServerOpsHelper.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ServerOpsHelper.Services;

/// <summary>
/// 应用配置持久化服务，负责服务器列表、常用链接、密码箱与设置的读写。
/// <para>
/// 约定：Password 字段在落盘前统一用 DPAPI 加密（带 <see cref="CredentialService.EncryptedPrefix"/> 前缀），
/// 读取时解密；内存中始终是明文，方便直接展示与复制。
/// </para>
/// </summary>
public class ConfigService
{
    private static readonly Lazy<ConfigService> _lazy = new(() => new ConfigService());
    public static ConfigService Instance => _lazy.Value;

    private readonly string _configDirectory;
    private readonly string _serversFilePath;
    private readonly string _settingsFilePath;
    private readonly string _linksFilePath;
    private readonly string _vaultFilePath;
    private readonly string _historyFilePath;
    private readonly CredentialService _credentialService;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// 串行化「读-改-写」服务器列表。
    /// <para>
    /// <see cref="TouchServerAsync"/> 是「读全量 → 改一项 → 写全量」，多台服务器同时连上
    /// （<c>exec_command_on_servers</c> 并发场景）时两个写操作会互相覆盖，表现是
    /// 时间戳只剩最后一台。用信号量把这段临界区串起来即可。
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _serversWriteLock = new(1, 1);

    /// <summary>
    /// 访问历史最多保留的条数（按唯一地址计），超出后丢弃最久未访问的
    /// </summary>
    public const int MaxHistoryCount = 300;

    private ConfigService()
    {
        _configDirectory = ResolveConfigDirectory();

        _serversFilePath = Path.Combine(_configDirectory, "servers.json");
        _settingsFilePath = Path.Combine(_configDirectory, "settings.json");
        _linksFilePath = Path.Combine(_configDirectory, "links.json");
        _vaultFilePath = Path.Combine(_configDirectory, "vault.json");
        _historyFilePath = Path.Combine(_configDirectory, "history.json");

        _credentialService = new CredentialService();

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// 配置文件目录（默认 `%LOCALAPPDATA%\ServerOpsHelper`）
    /// </summary>
    public string ConfigDirectory => _configDirectory;

    /// <summary>
    /// 解析配置目录。
    /// <para>
    /// 若设置了环境变量 <c>SERVEROPSHELPER_CONFIG_DIR</c> 则优先使用它：
    /// 便携部署（数据随程序走）和自动化测试（隔离到临时目录，绝不碰真实数据）都靠这个开关。
    /// </para>
    /// </summary>
    private static string ResolveConfigDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable("SERVEROPSHELPER_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden.Trim();
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ServerOpsHelper");
    }

    /// <summary>
    /// 确保配置目录存在
    /// </summary>
    private void EnsureConfigDirectory()
    {
        if (!Directory.Exists(_configDirectory))
        {
            Directory.CreateDirectory(_configDirectory);
        }
    }

    // ───────────────────────── 通用 JSON 读写 ─────────────────────────

    /// <summary>
    /// 读取 JSON 数组；文件不存在或内容损坏时返回空列表，避免启动崩溃
    /// </summary>
    private async Task<List<T>> LoadListAsync<T>(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new List<T>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            return JsonSerializer.Deserialize<List<T>>(json, _jsonOptions) ?? new List<T>();
        }
        catch (JsonException)
        {
            return new List<T>();
        }
        catch (IOException)
        {
            return new List<T>();
        }
    }

    /// <summary>
    /// 写入 JSON 数组。
    /// <para>
    /// 采用「先克隆快照、再加工快照」的方式落盘：加密等副作用不会污染调用方持有的内存对象，
    /// 从而避免保存后界面上的密码变成密文。
    /// </para>
    /// </summary>
    private async Task SaveListAsync<T>(string filePath, List<T> items, Action<T>? prepareForSave = null)
    {
        EnsureConfigDirectory();

        var snapshot = Clone(items);
        if (prepareForSave != null)
        {
            foreach (var item in snapshot)
            {
                prepareForSave(item);
            }
        }

        var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
        BackupExistingFile(filePath);
        await File.WriteAllTextAsync(filePath, json);
    }

    /// <summary>
    /// 覆盖写入前先给上一版留一份 <c>*.bak</c>。
    /// <para>
    /// 这些配置文件都是「全量覆盖」式写入，一旦写进去的内容不对（程序缺陷、误操作、外部脚本），
    /// 原数据就没了。留一份上一版备份是最省事的保险：出事时把 <c>xxx.json.bak</c> 改回
    /// <c>xxx.json</c> 即可恢复。
    /// </para>
    /// </summary>
    private static void BackupExistingFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Copy(filePath, filePath + ".bak", overwrite: true);
            }
        }
        catch (IOException)
        {
            // 备份失败不应该阻断正常保存
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 通过 JSON 往返做一次深拷贝
    /// </summary>
    private static List<T> Clone<T>(List<T> items)
    {
        var json = JsonSerializer.Serialize(items);
        return JsonSerializer.Deserialize<List<T>>(json) ?? new List<T>();
    }

    // ───────────────────────── 服务器列表 ─────────────────────────

    /// <summary>
    /// 读取服务器列表
    /// </summary>
    public async Task<List<ServerInfo>> LoadServersAsync()
    {
        var servers = await LoadListAsync<ServerInfo>(_serversFilePath);

        foreach (var server in servers)
        {
            server.Password = _credentialService.Decrypt(server.Password);
        }

        return servers;
    }

    /// <summary>
    /// 保存服务器列表
    /// </summary>
    public Task SaveServersAsync(List<ServerInfo> servers)
        => SaveListAsync(_serversFilePath, servers,
            server => server.Password = _credentialService.EncryptIfNeeded(server.Password));

    /// <summary>
    /// 新增或更新服务器
    /// </summary>
    public async Task SaveServerAsync(ServerInfo server)
    {
        var servers = await LoadServersAsync();
        servers.RemoveAll(s => s.Id == server.Id);
        servers.Add(server);
        await SaveServersAsync(servers);
    }

    /// <summary>
    /// 删除服务器
    /// </summary>
    public async Task DeleteServerAsync(Guid id)
    {
        var servers = await LoadServersAsync();
        servers.RemoveAll(s => s.Id == id);
        await SaveServersAsync(servers);
    }

    /// <summary>
    /// 记录「最后一次成功连接时间」并落盘。
    /// <para>
    /// 只动 <see cref="ServerInfo.LastConnectedAt"/> 一个字段，其余原样写回。
    /// <b>任何异常都在这里吞掉</b>：这只是个辅助信息，不能因为写盘失败
    /// （文件被占用、磁盘满、多进程同时写）就把一次已经建立好的连接判成失败。
    /// </para>
    /// <para>
    /// 找不到该 Id（刚被删掉）时直接返回，不补写。
    /// </para>
    /// </summary>
    public async Task TouchServerAsync(Guid id)
    {
        await _serversWriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var servers = await LoadServersAsync().ConfigureAwait(false);
            var target = servers.Find(s => s.Id == id);
            if (target == null)
            {
                return;
            }

            target.LastConnectedAt = DateTime.Now;
            await SaveServersAsync(servers).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 见方法注释：记录时间是锦上添花，失败不影响业务
        }
        finally
        {
            _serversWriteLock.Release();
        }
    }

    // ───────────────────────── 常用链接（网页 / 宝塔） ─────────────────────────

    /// <summary>
    /// 读取常用链接
    /// </summary>
    public async Task<List<LinkItem>> LoadLinksAsync()
    {
        var links = await LoadListAsync<LinkItem>(_linksFilePath);

        foreach (var link in links)
        {
            link.Password = _credentialService.Decrypt(link.Password);
        }

        return links;
    }

    /// <summary>
    /// 保存常用链接
    /// </summary>
    public Task SaveLinksAsync(List<LinkItem> links)
        => SaveListAsync(_linksFilePath, links,
            link => link.Password = _credentialService.EncryptIfNeeded(link.Password));

    /// <summary>
    /// 新增或更新一条链接
    /// </summary>
    public async Task SaveLinkAsync(LinkItem link)
    {
        var links = await LoadLinksAsync();
        links.RemoveAll(l => l.Id == link.Id);
        links.Add(link);
        await SaveLinksAsync(links);
    }

    /// <summary>
    /// 删除一条链接
    /// </summary>
    public async Task DeleteLinkAsync(Guid id)
    {
        var links = await LoadLinksAsync();
        links.RemoveAll(l => l.Id == id);
        await SaveLinksAsync(links);
    }

    /// <summary>
    /// 记录一次打开：累计次数 + 最近打开时间（用于「经常打开」排序与展示）
    /// </summary>
    public async Task MarkLinkOpenedAsync(LinkItem link)
    {
        link.LastOpenedAt = DateTime.Now;
        link.OpenCount++;
        await SaveLinkAsync(link);
    }

    // ───────────────────────── 密码箱 ─────────────────────────

    /// <summary>
    /// 读取密码箱
    /// </summary>
    public async Task<List<VaultItem>> LoadVaultAsync()
    {
        var items = await LoadListAsync<VaultItem>(_vaultFilePath);

        foreach (var item in items)
        {
            item.Password = _credentialService.Decrypt(item.Password);
        }

        return items;
    }

    /// <summary>
    /// 保存密码箱
    /// </summary>
    public Task SaveVaultAsync(List<VaultItem> items)
        => SaveListAsync(_vaultFilePath, items,
            item => item.Password = _credentialService.EncryptIfNeeded(item.Password));

    /// <summary>
    /// 新增或更新一条密码记录
    /// </summary>
    public async Task SaveVaultItemAsync(VaultItem item)
    {
        var items = await LoadVaultAsync();
        items.RemoveAll(v => v.Id == item.Id);
        items.Add(item);
        await SaveVaultAsync(items);
    }

    /// <summary>
    /// 删除一条密码记录
    /// </summary>
    public async Task DeleteVaultItemAsync(Guid id)
    {
        var items = await LoadVaultAsync();
        items.RemoveAll(v => v.Id == id);
        await SaveVaultAsync(items);
    }

    // ───────────────────────── 访问记录（历史） ─────────────────────────

    /// <summary>
    /// 读取访问历史（按最近访问时间倒序）
    /// </summary>
    public async Task<List<VisitRecord>> LoadHistoryAsync()
    {
        var records = await LoadListAsync<VisitRecord>(_historyFilePath);

        return records
            .OrderByDescending(r => r.LastVisitedAt)
            .ToList();
    }

    /// <summary>
    /// 保存访问历史
    /// </summary>
    public Task SaveHistoryAsync(List<VisitRecord> records)
        => SaveListAsync(_historyFilePath, records);

    /// <summary>
    /// 记录一次访问：同一地址累加次数并刷新时间，超出上限时丢弃最久未访问的。
    /// </summary>
    /// <returns>落盘后的那条记录</returns>
    public async Task<VisitRecord> RecordVisitAsync(string url, string title, LinkItem? source = null)
    {
        var normalized = LinkItem.NormalizeUrl(url);
        var records = await LoadHistoryAsync();
        var now = DateTime.Now;

        var record = records.FirstOrDefault(r =>
            string.Equals(r.Url, normalized, StringComparison.OrdinalIgnoreCase));

        if (record == null)
        {
            record = new VisitRecord
            {
                Url = normalized,
                Title = (title ?? string.Empty).Trim(),
                FirstVisitedAt = now,
                VisitCount = 0
            };
            records.Add(record);
        }
        else if (!string.IsNullOrWhiteSpace(title))
        {
            record.Title = title.Trim();
        }

        record.LastVisitedAt = now;
        record.VisitCount++;

        if (source != null)
        {
            record.LinkId = source.Id;
            record.LinkName = source.Name;
        }

        var trimmed = OrderByRecencyForSave(records).Take(MaxHistoryCount).ToList();

        await SaveHistoryAsync(trimmed);
        return record;
    }

    /// <summary>
    /// 落盘前的排序：最近访问的在前。
    /// <para>
    /// <see cref="DateTime.Now"/> 在 Windows 上只有约 15ms 精度，
    /// 同一毫秒内连续记录会撞时间戳，所以并列时按「后加入的算更新」打破平局，
    /// 否则刚访问的那条会被排到后面去。
    /// </para>
    /// </summary>
    private static IEnumerable<VisitRecord> OrderByRecencyForSave(List<VisitRecord> records)
        => records
            .Select((record, index) => (record, index))
            .OrderByDescending(pair => pair.record.LastVisitedAt)
            .ThenByDescending(pair => pair.index)
            .Select(pair => pair.record);

    /// <summary>
    /// 删除一条访问记录
    /// </summary>
    public async Task DeleteVisitAsync(Guid id)
    {
        var records = await LoadHistoryAsync();
        records.RemoveAll(r => r.Id == id);
        await SaveHistoryAsync(records);
    }

    /// <summary>
    /// 清空全部访问历史
    /// </summary>
    public Task ClearHistoryAsync() => SaveHistoryAsync(new List<VisitRecord>());

    // ───────────────────────── 应用设置 ─────────────────────────

    /// <summary>
    /// 读取应用设置（同步版本，仅供启动阶段恢复主题等场景使用）
    /// </summary>
    public AppSettings LoadSettings()
    {
        if (!File.Exists(_settingsFilePath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_settingsFilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// 读取应用设置
    /// </summary>
    public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(LoadSettings());

    /// <summary>
    /// 保存应用设置
    /// </summary>
    public async Task SaveSettingsAsync(AppSettings settings)
    {
        EnsureConfigDirectory();
        var json = JsonSerializer.Serialize(settings, _jsonOptions);
        BackupExistingFile(_settingsFilePath);
        await File.WriteAllTextAsync(_settingsFilePath, json);
    }
}
