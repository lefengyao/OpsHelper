using ServerOpsHelper.Models;
using ServerOpsHelper.Services;
using ServerOpsHelper.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace ServerOpsHelper.Views;

/// <summary>
/// 访问记录页：上方是本次打开应用后访问过的页面，下方是全部历史记录。
/// </summary>
public partial class HistoryPage : Page
{
    private readonly HistoryViewModel _viewModel = new();

    public HistoryPage()
    {
        InitializeComponent();
        DataContext = _viewModel;

        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await _viewModel.LoadAsync();

    private async void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        var confirmed = await Dialogs.ConfirmAsync(
            "清空访问记录",
            "确定要清空全部访问历史吗？该操作不可撤销（本次访问列表不受影响）。",
            "清空",
            danger: true);

        if (!confirmed)
        {
            return;
        }

        await _viewModel.ClearAsync();
    }

    private void OpenSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: VisitRecord record })
        {
            OpenInConsole(record.Url);
        }
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: VisitRecord record })
        {
            OpenInConsole(record.Url);
        }
    }

    /// <summary>
    /// 在内置 Web 控制台里打开（可复用 WebView2 的登录态）
    /// </summary>
    private void OpenInConsole(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            Dialogs.Error("这条记录没有可用的地址。");
            return;
        }

        // 复用主窗口里常驻的网页宿主，而不是新建一个页面（新建会重建 WebView2、丢掉正在看的页面）
        ShellNavigation.OpenWebConsole(url);
    }

    /// <summary>
    /// 把一条访问记录转存为常用链接
    /// </summary>
    private async void BookmarkButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VisitRecord record })
        {
            return;
        }

        var draft = new LinkItem
        {
            Name = record.DisplayTitle,
            Url = record.Url,
            Kind = LinkKind.WebPage,
            Notes = $"来自访问记录（{record.TimeLabel}）"
        };

        var link = await LinkEditDialog.ShowEditAsync(draft);
        if (link == null)
        {
            return;
        }

        await ConfigService.Instance.SaveLinkAsync(link);
        Dialogs.Info($"已把「{link.Name}」加入常用链接。", "已收藏");
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VisitRecord record })
        {
            return;
        }

        await _viewModel.DeleteAsync(record);
    }
}
