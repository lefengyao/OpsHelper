using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using ServerOpsHelper.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ServerOpsHelper.Services;

/// <summary>
/// SFTP 文件管理服务：一个独立的 SFTP 连接，负责浏览与增删改传。
/// <para>
/// 与终端用的 <see cref="RemoteTerminalSession"/>（ShellStream）是**两个独立连接** ——
/// 一条通道跑交互 shell、一条通道跑文件传输，互不阻塞：传输大文件时终端照样能用。
/// </para>
/// <para>线程约定：所有公开方法可在任意线程调用（内部 Task.Run），结果不可直接碰 UI。</para>
/// </summary>
public sealed class SftpService : IDisposable
{
    private SftpClient? _client;
    private bool _disposed;

    public bool IsConnected => _client?.IsConnected == true;

    /// <summary>连接 SFTP（与终端各自独立认证一次）。重复调用无害。</summary>
    public async Task ConnectAsync(ServerInfo server)
    {
        if (IsConnected)
        {
            return;
        }

        await Task.Run(() =>
        {
            _client = new SftpClient(server.Host, server.Port, server.Username, server.Password);
            _client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(10);
            _client.OperationTimeout = TimeSpan.FromSeconds(30);
            _client.Connect();

            ServerConnectionTracker.MarkConnected(server);
        });
    }

    /// <summary>
    /// 列目录。目录排在文件前面，同类按名称不区分大小写排序。
    /// 返回的路径一律用 <see cref="NormalizePath"/> 规范化（保证以 / 开头、不带尾斜杠）。
    /// </summary>
    public async Task<List<SftpEntry>> ListAsync(string path)
    {
        var client = RequireClient();

        return await Task.Run(() =>
        {
            var dir = NormalizePath(path);
            var list = new List<SftpEntry>();

            foreach (var f in client.ListDirectory(dir))
            {
                // SFTP 列表里必然带 "." 与 ".."，界面上用「上级」按钮表达，这里过滤掉
                if (f.Name == "." || f.Name == "..")
                {
                    continue;
                }

                list.Add(new SftpEntry
                {
                    Name = f.Name,
                    FullPath = NormalizePath(dir + "/" + f.Name),
                    IsDirectory = f.IsDirectory,
                    Size = f.IsDirectory ? 0 : f.Length,
                    Modified = f.LastWriteTime,
                    Permissions = FormatPermissions(f),
                });
            }

            return list
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });
    }

    /// <summary>下载远端文件到本地（覆盖同名）。</summary>
    public async Task DownloadAsync(string remotePath, string localPath, CancellationToken token = default)
    {
        var client = RequireClient();

        await Task.Run(() =>
        {
            using var fs = File.Create(localPath);
            client.DownloadFile(remotePath, fs);
        }, token);
    }

    /// <summary>上传本地文件到远端（覆盖同名）。</summary>
    public async Task UploadAsync(string localPath, string remotePath, CancellationToken token = default)
    {
        var client = RequireClient();

        await Task.Run(() =>
        {
            using var fs = File.OpenRead(localPath);
            client.UploadFile(fs, remotePath, canOverride: true);
        }, token);
    }

    /// <summary>删除文件</summary>
    public async Task DeleteFileAsync(string remotePath)
    {
        var client = RequireClient();
        await Task.Run(() => client.DeleteFile(remotePath));
    }

    /// <summary>
    /// 删除目录（**递归**删掉里面所有内容）。
    /// 界面上必须先向用户二次确认 —— 这一步不可撤销。
    /// </summary>
    public async Task DeleteDirectoryAsync(string remotePath)
    {
        var client = RequireClient();

        await Task.Run(() =>
        {
            foreach (var entry in client.ListDirectory(remotePath))
            {
                if (entry.Name == "." || entry.Name == "..")
                {
                    continue;
                }

                if (entry.IsDirectory)
                {
                    DeleteDirectoryRecursive(client, entry.FullName);
                }
                else
                {
                    client.DeleteFile(entry.FullName);
                }
            }

            client.DeleteDirectory(remotePath);
        });
    }

    private static void DeleteDirectoryRecursive(SftpClient client, string path)
    {
        foreach (var entry in client.ListDirectory(path))
        {
            if (entry.Name == "." || entry.Name == "..")
            {
                continue;
            }

            if (entry.IsDirectory)
            {
                DeleteDirectoryRecursive(client, entry.FullName);
            }
            else
            {
                client.DeleteFile(entry.FullName);
            }
        }

        client.DeleteDirectory(path);
    }

    /// <summary>重命名 / 移动</summary>
    public async Task RenameAsync(string oldPath, string newPath)
    {
        var client = RequireClient();
        await Task.Run(() => client.RenameFile(oldPath, newPath));
    }

    /// <summary>新建文件夹</summary>
    public async Task CreateDirectoryAsync(string remotePath)
    {
        var client = RequireClient();
        await Task.Run(() => client.CreateDirectory(remotePath));
    }

    /// <summary>
    /// 判断远端路径是否已存在（新建/重命名前查重用）。
    /// </summary>
    public async Task<bool> ExistsAsync(string remotePath)
    {
        var client = RequireClient();

        return await Task.Run(() =>
        {
            try
            {
                client.GetAttributes(remotePath);
                return true;
            }
            catch (SftpPathNotFoundException)
            {
                return false;
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            if (_client != null)
            {
                if (_client.IsConnected)
                {
                    _client.Disconnect();
                }
                _client.Dispose();
            }
        }
        catch (Exception)
        {
            // 断开清理异常无上报价值
        }
        finally
        {
            _client = null;
        }
    }

    private SftpClient RequireClient()
    {
        if (_client is not { IsConnected: true })
        {
            throw new InvalidOperationException("SFTP 尚未连接（或连接已断开）。");
        }
        return _client;
    }

    /// <summary>路径规范化：保证以 / 开头、去掉多余与结尾的斜杠。"" 与 "/" 都返回 "/"。</summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var p = path.Replace('\\', '/').Trim();
        if (!p.StartsWith('/'))
        {
            p = "/" + p;
        }

        while (p.Length > 1 && p.EndsWith('/'))
        {
            p = p[..^1];
        }

        return p.Length == 0 ? "/" : p;
    }

    /// <summary>取父目录（根目录的父目录仍是根目录）。</summary>
    public static string ParentOf(string path)
    {
        var p = NormalizePath(path);
        if (p == "/")
        {
            return "/";
        }

        var idx = p.LastIndexOf('/');
        return idx <= 0 ? "/" : p[..idx];
    }

    /// <summary>
    /// 拼 rwx 字符串（如 drwxr-xr-x），对齐 ls -l 的第一列。
    /// <para>公开是给 MCP 工具复用：智能体看到的权限串要和界面上一致，不能两套格式。</para>
    /// </summary>
    public static string FormatPermissions(ISftpFile file)
    {
        var a = file.Attributes;
        var s = new char[10];
        s[0] = file.IsDirectory ? 'd' : (file.IsSymbolicLink ? 'l' : '-');
        s[1] = a.OwnerCanRead ? 'r' : '-';
        s[2] = a.OwnerCanWrite ? 'w' : '-';
        s[3] = a.OwnerCanExecute ? 'x' : '-';
        s[4] = a.GroupCanRead ? 'r' : '-';
        s[5] = a.GroupCanWrite ? 'w' : '-';
        s[6] = a.GroupCanExecute ? 'x' : '-';
        s[7] = a.OthersCanRead ? 'r' : '-';
        s[8] = a.OthersCanWrite ? 'w' : '-';
        s[9] = a.OthersCanExecute ? 'x' : '-';
        return new string(s);
    }

    /// <summary>把字节数格式化成人类可读（对齐文件管理器的习惯）。</summary>
    public static string FormatSize(long bytes, bool isDirectory)
    {
        if (isDirectory)
        {
            return "";
        }

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        return i == 0 ? $"{bytes} B" : $"{v:0.#} {units[i]}";
    }
}

/// <summary>目录里的一项（文件或文件夹）。</summary>
public sealed class SftpEntry
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    public string Permissions { get; set; } = string.Empty;

    /// <summary>界面上显示的类型/大小文本</summary>
    public string SizeText => SftpService.FormatSize(Size, IsDirectory);

    public string TypeText => IsDirectory ? "文件夹" : "文件";

    public string ModifiedText => Modified == default
        ? ""
        : Modified.ToString("yyyy-MM-dd HH:mm");
}
