using ServerOpsHelper.Models;
using ServerOpsHelper.Services;
using ServerOpsHelper.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace ServerOpsHelper.Views;

/// <summary>
/// 服务器列表页面
/// </summary>
public partial class ServerListPage : Page
{
    private readonly ServerListViewModel _viewModel;
    private readonly ConfigService _configService;

    public ServerListPage()
    {
        InitializeComponent();
        _viewModel = new ServerListViewModel();
        _configService = ConfigService.Instance;
        DataContext = _viewModel;

        Loaded += async (_, _) => await _viewModel.LoadServersAsync();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _ = _viewModel.LoadServersAsync();
    }

    /// <summary>
    /// 新增服务器
    /// </summary>
    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var server = await ServerEditDialog.ShowEditAsync(null);
        if (server == null) return;

        await _configService.SaveServerAsync(server);
        await _viewModel.LoadServersAsync();
    }

    /// <summary>
    /// 编辑服务器
    /// </summary>
    private async void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ServerInfo server }) return;

        var updated = await ServerEditDialog.ShowEditAsync(server);
        if (updated == null) return;

        await _configService.SaveServerAsync(updated);
        await _viewModel.LoadServersAsync();
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ServerInfo server })
        {
            return;
        }

        var confirmed = await Dialogs.ConfirmAsync(
            "删除服务器",
            $"确定要删除「{server.Name}」吗？该操作不可撤销。",
            "删除",
            danger: true);

        if (!confirmed)
        {
            return;
        }

        await _viewModel.DeleteAsync(server);
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ServerInfo server })
        {
            NavigationService?.Navigate(new ServerDetailPage(server));
        }
    }

    private void ServerListView_ItemClick(object sender, ModernWpf.Controls.ItemClickEventArgs e)
    {
        if (e.ClickedItem is ServerInfo server)
        {
            NavigationService?.Navigate(new ServerDetailPage(server));
        }
    }
}
