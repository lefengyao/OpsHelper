using Microsoft.Web.WebView2.Core;
using ServerOpsHelper.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ServerOpsHelper.Services;

/// <summary>
/// 浏览器下载服务：接管 WebView2 的下载，把文件落到统一目录，跟踪进度并在界面展示。
/// <para>
/// 只保留本次运行里的下载记录（和访问记录的"近期访问"一个口径），不落盘 ——
/// 下载的文件本身在磁盘上，记录只是方便用户找到它。
/// </para>
/// </summary>
public sealed class DownloadService
{
    private static readonly Lazy<DownloadService> _lazy = new(() => new DownloadService());
    public static DownloadService Instance => _lazy.Value;

    /// <summary>下载记录（最新在前），界面直接绑定这个集合。</summary>
    public List<DownloadRecord> Records { get; } = new();

    /// <summary>下载记录变化（新增 / 进度 / 状态）时触发，界面据此刷新。</summary>
    public event EventHandler? Changed;

    /// <summary>默认下载目录：%USERPROFILE%\Downloads\ServerOpsHelper。用户可在设置里改。</summary>
    public static string DefaultDownloadDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ServerOpsHelper");

    /// <summary>需要从下载目录读配置的委托（由系统设置注入，避免服务直接依赖 ConfigService 时序）。</summary>
    private Func<string?>? _directoryProvider;

    private bool _confirmDanger = true;

    private DownloadService()
    {
    }

    /// <summary>下载可执行文件时是否提示（设置里可关）。</summary>
    public bool ConfirmDanger => _confirmDanger;

    /// <summary>由启动流程注入：读取用户配置的下载目录 + 危险文件是否提示。</summary>
    public void Configure(Func<string?> directoryProvider, bool confirmDanger)
    {
        _directoryProvider = directoryProvider;
        _confirmDanger = confirmDanger;
    }

    /// <summary>当前生效的下载目录（配置为空时回退默认值）。</summary>
    public string GetDownloadDirectory()
    {
        var configured = _directoryProvider?.Invoke();
        return string.IsNullOrWhiteSpace(configured) ? DefaultDownloadDirectory : configured!;
    }

    /// <summary>危险扩展名：执行文件 / 脚本 / 安装包，下载前提醒。</summary>
    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msp", ".msu", ".bat", ".cmd", ".com", ".ps1", ".vbs", ".js",
        ".jse", ".wsf", ".wsh", ".scr", ".pif", ".hta", ".jar", ".reg", ".dll", ".appref-ms",
    };

    /// <summary>判断文件名是否属于危险类型。</summary>
    public bool IsDangerous(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(ext) && DangerousExtensions.Contains(ext);
    }

    /// <summary>
    /// 生成一个不冲突的下载路径：目录不存在则创建；同名文件追加 (1)、(2)…。
    /// </summary>
    public string BuildTargetPath(string suggestedFileName)
    {
        var dir = GetDownloadDirectory();
        Directory.CreateDirectory(dir);

        // 去掉路径分隔符，防止网页用 ..\ 之类的名字逃出下载目录
        var safeName = Path.GetFileName(suggestedFileName);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = $"download_{DateTime.Now:yyyyMMdd_HHmmss}";
        }

        var fullPath = Path.Combine(dir, safeName);
        if (!File.Exists(fullPath))
        {
            return fullPath;
        }

        var nameOnly = Path.GetFileNameWithoutExtension(safeName);
        var ext = Path.GetExtension(safeName);
        for (var i = 1; i < 1000; i++)
        {
            fullPath = Path.Combine(dir, $"{nameOnly} ({i}){ext}");
            if (!File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return Path.Combine(dir, $"{nameOnly}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
    }

    /// <summary>
    /// 登记一次下载并订阅它的进度 / 状态事件。
    /// </summary>
    /// <param name="operation">WebView2 下载操作</param>
    /// <param name="targetPath">已经算好的目标路径</param>
    /// <param name="isSuspicious">是否为危险文件类型</param>
    public DownloadRecord Register(CoreWebView2DownloadOperation operation, string targetPath, bool isSuspicious)
    {
        var fileName = Path.GetFileName(targetPath);
        var record = new DownloadRecord
        {
            FileName = fileName,
            Url = operation.Uri,
            TargetPath = targetPath,
            TotalBytes = ToLong(operation.TotalBytesToReceive),
            ReceivedBytes = operation.BytesReceived,
            IsSuspicious = isSuspicious,
            Operation = operation,
        };
        UpdateProgress(record, operation.BytesReceived, ToLong(operation.TotalBytesToReceive));

        Records.Insert(0, record);

        operation.BytesReceivedChanged += (s, e) =>
        {
            if (s is CoreWebView2DownloadOperation op)
            {
                UpdateProgress(record, op.BytesReceived, ToLong(op.TotalBytesToReceive));
                Notify();
            }
        };

        operation.StateChanged += (s, e) =>
        {
            if (s is CoreWebView2DownloadOperation op)
            {
                ApplyState(record, op);
                Notify();
            }
        };

        Notify();
        return record;
    }

    /// <summary>TotalBytesToReceive 是 ulong?（未知大小时为 null），安全转成 long；未知返回 -1。</summary>
    private static long ToLong(ulong? value)
        => value.HasValue && value.Value <= long.MaxValue ? (long)value.Value : -1;

    private static void UpdateProgress(DownloadRecord record, long received, long total)
    {
        record.ReceivedBytes = received;
        record.TotalBytes = total;
        record.Progress = total > 0 ? (int)Math.Clamp(received * 100 / total, 0, 100) : -1;
    }

    private static void ApplyState(DownloadRecord record, CoreWebView2DownloadOperation op)
    {
        UpdateProgress(record, op.BytesReceived, ToLong(op.TotalBytesToReceive));
        switch (op.State)
        {
            case CoreWebView2DownloadState.Completed:
                record.IsFinished = true;
                record.StatusText = "已完成";
                record.Progress = 100;
                break;
            case CoreWebView2DownloadState.Interrupted:
                record.IsFinished = true;
                record.StatusText = "已中断";
                record.ErrorText = op.InterruptReason.ToString();
                break;
            default:
                record.StatusText = "进行中";
                break;
        }
    }

    /// <summary>清空已结束的记录（不影响正在下载的任务）。</summary>
    public void ClearFinished()
    {
        Records.RemoveAll(r => r.IsFinished);
        Notify();
    }

    /// <summary>在资源管理器里定位文件（文件不存在时回退到打开所在目录）。</summary>
    public static void RevealFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    System.Diagnostics.Process.Start("explorer.exe", dir);
                }
            }
        }
        catch
        {
            // 资源管理器没起来不算错误，用户还能从状态栏看到路径
        }
    }

    /// <summary>打开下载目录。</summary>
    public void OpenDownloadDirectory()
    {
        try
        {
            var dir = GetDownloadDirectory();
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", dir);
        }
        catch
        {
            // 忽略
        }
    }

    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
}
