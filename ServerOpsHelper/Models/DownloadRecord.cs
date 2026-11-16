using Microsoft.Web.WebView2.Core;
using System;

namespace ServerOpsHelper.Models;

/// <summary>
/// 一次浏览器下载的记录（只在本次运行里保留，不落盘）。
/// 对应 WebView2 的 <see cref="CoreWebView2DownloadOperation"/>，额外存了展示需要的字段。
/// </summary>
public sealed class DownloadRecord
{
    /// <summary>显示用的文件名</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>来源地址</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>最终保存的绝对路径</summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>开始时间</summary>
    public DateTime StartedAt { get; set; } = DateTime.Now;

    /// <summary>总字节数，未知为 -1</summary>
    public long TotalBytes { get; set; } = -1;

    /// <summary>已收到字节数</summary>
    public long ReceivedBytes { get; set; }

    /// <summary>0-100 的进度；总大小未知时用 -1 表示"进行中但无百分比"</summary>
    public int Progress { get; set; }

    /// <summary>当前状态文案（进行中 / 已完成 / 已取消 / 失败）</summary>
    public string StatusText { get; set; } = "进行中";

    /// <summary>是否已结束（完成 / 取消 / 失败）</summary>
    public bool IsFinished { get; set; }

    /// <summary>是否是危险文件类型（下载前给过提示）</summary>
    public bool IsSuspicious { get; set; }

    /// <summary>失败 / 中断原因说明</summary>
    public string? ErrorText { get; set; }

    /// <summary>底层的下载操作句柄（用于打开文件夹 / 取消）</summary>
    public CoreWebView2DownloadOperation? Operation { get; set; }

    /// <summary>人类可读的大小，如 "1.2 MB"</summary>
    public string SizeText => FormatSize(TotalBytes < 0 ? ReceivedBytes : TotalBytes);

    public static string FormatSize(long bytes)
    {
        if (bytes < 0)
        {
            return "未知大小";
        }

        double value = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }

        return i == 0 ? $"{bytes} B" : $"{value:F1} {units[i]}";
    }
}
