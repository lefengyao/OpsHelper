using ServerOpsHelper.Models;
using ServerOpsHelper.Services;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ServerOpsHelper.Views;

/// <summary>
/// 服务器监控面板（三期）：每 3 秒刷新一次 CPU / 内存 / 磁盘 / 负载 / 网络。
/// <para>
/// 数据来自 <see cref="RemoteMonitorService"/> 的独立 SSH 连接 ——
/// 和终端、文件面板三条连接各管一摊，互不干扰。
/// </para>
/// </summary>
public partial class ServerMonitorPanel : UserControl
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);

    private readonly RemoteMonitorService _monitor = new();
    private bool _started;
    private bool _closed;

    public ServerMonitorPanel()
    {
        InitializeComponent();
    }

    /// <summary>开始采集。页面加载后调用一次。</summary>
    public async Task InitializeAsync(ServerInfo server)
    {
        if (_started)
        {
            return;
        }

        _started = true;

        // 采集在后台线程，更新控件必须回 UI 线程
        _monitor.Updated += m => Dispatcher.BeginInvoke(() => Apply(m));
        _monitor.Failed += text => Dispatcher.BeginInvoke(() => ShowStatus(text, error: true));

        try
        {
            await _monitor.StartAsync(server, RefreshInterval);
            ShowStatus($"每 {RefreshInterval.TotalSeconds:0} 秒刷新一次");
        }
        catch (Exception ex)
        {
            _started = false;
            ShowStatus($"监控连接失败：{ex.Message}", error: true);
        }
    }

    /// <summary>页面离开时调用。</summary>
    public void Close()
    {
        _closed = true;
        _monitor.Dispose();
    }

    private void Apply(ServerMetrics m)
    {
        if (_closed)
        {
            return;
        }

        HostTextBlock.Text = string.IsNullOrEmpty(m.HostName) ? "—" : m.HostName;
        OsTextBlock.Text = string.IsNullOrEmpty(m.OsName) ? "—" : m.OsName;
        UptimeTextBlock.Text = m.UptimeText;

        // CPU：首次采样没有基准，显示"—"
        if (m.HasCpu)
        {
            CpuTextBlock.Text = $"{m.CpuUsagePercent:0}%";
            CpuBar.Value = m.CpuUsagePercent;
        }
        else
        {
            CpuTextBlock.Text = "—";
            CpuBar.Value = 0;
        }

        LoadTextBlock.Text = m.CpuCores > 0
            ? $"负载 {m.Load1:0.00} / {m.Load5:0.00} / {m.Load15:0.00}（{m.CpuCores} 核）"
            : $"负载 {m.Load1:0.00} / {m.Load5:0.00} / {m.Load15:0.00}";

        MemTextBlock.Text = m.MemTotalMb > 0
            ? $"{ServerMetrics.FormatMb(m.MemUsedMb)} / {ServerMetrics.FormatMb(m.MemTotalMb)}"
            : "—";
        MemBar.Value = m.MemUsedPercent;

        DiskTextBlock.Text = m.DiskTotalMb > 0
            ? $"{ServerMetrics.FormatMb(m.DiskUsedMb)} / {ServerMetrics.FormatMb(m.DiskTotalMb)}"
            : "—";
        DiskBar.Value = m.DiskUsedPercent;

        NetTitleTextBlock.Text = string.IsNullOrEmpty(m.NetInterface)
            ? "网络"
            : $"网络（{m.NetInterface}）";
        NetRxTextBlock.Text = ServerMetrics.FormatRate(m.NetRxBytesPerSec);
        NetTxTextBlock.Text = ServerMetrics.FormatRate(m.NetTxBytesPerSec);

        ShowStatus($"更新于 {m.SampledAt:HH:mm:ss}");
    }

    private void ShowStatus(string text, bool error = false)
    {
        StatusTextBlock.Text = text;
        StatusTextBlock.Foreground = error
            ? (Brush)FindResource("SystemControlErrorTextForegroundBrush")
            : (Brush)FindResource("SystemControlForegroundBaseMediumBrush");
    }
}
