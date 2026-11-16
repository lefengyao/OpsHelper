using ServerOpsHelper.Models;
using ServerOpsHelper.Services;
using System.Windows;

namespace ServerOpsHelper.Views;

/// <summary>
/// 下载管理面板：列出本次运行的下载、进度、路径，支持定位文件 / 打开目录 / 清空。
/// <para>
/// 独立顶层窗口（不是窗口内浮层）—— WebView2 是原生子窗口，会盖住任何 WPF 浮层。
/// </para>
/// </summary>
public partial class DownloadManagerWindow : Window
{
    private static DownloadManagerWindow? _instance;
    private readonly DownloadService _service = DownloadService.Instance;

    public DownloadManagerWindow()
    {
        InitializeComponent();
        Owner = Application.Current?.MainWindow;
        _service.Changed += OnDownloadsChanged;
        Closed += (s, _) =>
        {
            _service.Changed -= OnDownloadsChanged;
            // 只清掉"当前实例"，避免关闭间隙里新建的窗口被旧窗口的 Closed 误置 null
            if (ReferenceEquals(s, _instance))
            {
                _instance = null;
            }
        };
        Refresh();
    }

    /// <summary>显示（复用同一实例，避免重复订阅事件）。</summary>
    public static void ShowFor()
    {
        if (_instance == null || !_instance.IsLoaded)
        {
            _instance = new DownloadManagerWindow();
        }

        _instance.Refresh();
        _instance.Show();
        _instance.Activate();
    }

    private void OnDownloadsChanged(object? sender, System.EventArgs e) => Refresh();

    private void Refresh()
    {
        DirectoryTextBlock.Text = $"下载目录：{_service.GetDownloadDirectory()}";
        // 换成新列表，让 ListView 重新走绑定（DownloadRecord 的部分字段是直接读的属性，不实现 INotify）
        DownloadList.ItemsSource = null;
        DownloadList.ItemsSource = _service.Records;
    }

    private void RevealButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DownloadRecord record })
        {
            DownloadService.RevealFile(record.TargetPath);
        }
    }

    private void OpenDirButton_Click(object sender, RoutedEventArgs e) => _service.OpenDownloadDirectory();

    private void ClearButton_Click(object sender, RoutedEventArgs e) => _service.ClearFinished();
}
