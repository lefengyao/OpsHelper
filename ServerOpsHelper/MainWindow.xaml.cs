using ModernWpf.Controls;
using ServerOpsHelper.Models;
using ServerOpsHelper.Services;
using ServerOpsHelper.ViewModels;
using ServerOpsHelper.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ServerOpsHelper;

public partial class MainWindow : Window, IShellNavigator
{
    /// <summary>
    /// 侧边栏「最近访问」最多显示几条
    /// </summary>
    private const int MaxRecentVisits = 6;

    private readonly ServiceControlService _serviceControl;

    /// <summary>
    /// 侧边栏底部「最近访问」的数据源
    /// </summary>
    private readonly ObservableCollection<RecentEntry> _recentVisits = new();

    public MainWindow()
    {
        InitializeComponent();
        _serviceControl = new ServiceControlService();

        RecentVisitsList.ItemsSource = _recentVisits;

        // 内置浏览器每打开一个页面都会触发，侧边栏据此刷新
        VisitHistoryService.Instance.Changed += OnVisitHistoryChanged;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 注册导航入口：其它页面要靠它复用这里常驻的网页宿主，而不是新建一个
        ShellNavigation.Current = this;

        ShowPage("Servers");
        _ = RefreshRecentVisitsAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        VisitHistoryService.Instance.Changed -= OnVisitHistoryChanged;

        if (ReferenceEquals(ShellNavigation.Current, this))
        {
            ShellNavigation.Current = null;
        }
    }

    private void OnVisitHistoryChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(new Action(() => _ = RefreshRecentVisitsAsync()));

    // ───────────────────────── 导航 ─────────────────────────

    /// <summary>
    /// 侧边栏菜单点击事件
    /// </summary>
    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem item)
        {
            return;
        }

        var tag = item.Tag?.ToString();

        if (tag == "Restart")
        {
            _ = ShowRestartConfirmDialogAsync();
            return;
        }

        ShowPage(tag);
    }

    /// <summary>
    /// 切换功能页。
    /// <para>
    /// 注意「Web 控制台」这一支：内置浏览器是**常驻实例**，切过去只是把它显示出来，
    /// 切走只是折叠，实例与 WebView2 都不销毁 —— 所以正在看的页面不会丢，也不用重新输地址。
    /// </para>
    /// </summary>
    private void ShowPage(string? tag)
    {
        if (string.Equals(tag, "WebConsole", StringComparison.Ordinal))
        {
            MainFrame.Visibility = Visibility.Collapsed;
            ConsoleHost.Visibility = Visibility.Visible;

            // 首次切过来才真正创建 WebView2，并补做挂起的导航
            _ = ConsoleHost.ActivateAsync();
            return;
        }

        ConsoleHost.Visibility = Visibility.Collapsed;
        MainFrame.Visibility = Visibility.Visible;

        switch (tag)
        {
            case "Servers":
                MainFrame.Navigate(new ServerListPage());
                break;

            case "Bookmarks":
                MainFrame.Navigate(new BookmarksPage());
                break;

            case "History":
                MainFrame.Navigate(new HistoryPage());
                break;

            case "Vault":
                MainFrame.Navigate(new VaultPage());
                break;

            case "Frp":
                MainFrame.Navigate(new FrpPage());
                break;

            case "Settings":
                MainFrame.Navigate(new SettingsPage());
                break;
        }
    }

    // ───────────────────────── IShellNavigator ─────────────────────────

    /// <summary>
    /// 切换到指定功能页（同时同步侧边栏选中态）
    /// </summary>
    public void NavigateTo(string tag)
    {
        SelectNavItem(tag);
        ShowPage(tag);
    }

    /// <summary>
    /// 切到内置浏览器并打开一条常用链接
    /// </summary>
    public void OpenWebConsole(LinkItem link)
    {
        SelectNavItem("WebConsole");
        ShowPage("WebConsole");
        ConsoleHost.OpenLink(link);
    }

    /// <summary>
    /// 切到内置浏览器并打开一个地址
    /// </summary>
    public void OpenWebConsole(string url)
    {
        SelectNavItem("WebConsole");
        ShowPage("WebConsole");
        ConsoleHost.OpenUrl(url);
    }

    /// <summary>
    /// 把侧边栏选中态同步到某个 Tag 对应的菜单项
    /// </summary>
    private void SelectNavItem(string tag)
    {
        foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                NavView.SelectedItem = item;
                return;
            }
        }
    }

    // ───────────────────────── 侧边栏「最近访问」 ─────────────────────────

    private async Task RefreshRecentVisitsAsync()
    {
        var entries = await BuildRecentVisitsAsync();

        _recentVisits.Clear();
        foreach (var entry in entries)
        {
            _recentVisits.Add(entry);
        }

        RecentEmptyHint.Visibility = _recentVisits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 组装侧边栏列表：本次会话打开过的页面优先，再用最近的历史补足
    /// </summary>
    private static async Task<List<RecentEntry>> BuildRecentVisitsAsync()
    {
        var entries = new List<RecentEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var visit in VisitHistoryService.Instance.SessionVisits.Take(MaxRecentVisits))
        {
            if (seen.Add(visit.Url))
            {
                entries.Add(RecentEntry.FromVisit(visit, session: true));
            }
        }

        if (entries.Count >= MaxRecentVisits)
        {
            return entries;
        }

        var history = await ConfigService.Instance.LoadHistoryAsync();
        foreach (var record in history)
        {
            if (entries.Count >= MaxRecentVisits)
            {
                break;
            }

            if (seen.Add(record.Url))
            {
                entries.Add(RecentEntry.FromVisit(record, session: false));
            }
        }

        return entries;
    }

    /// <summary>
    /// 点侧边栏「最近访问」里的某一条：切到常驻的浏览器并打开它
    /// </summary>
    private void RecentVisitButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RecentEntry entry })
        {
            return;
        }

        if (entry.Link != null)
        {
            OpenWebConsole(entry.Link);
            return;
        }

        if (entry.Url.Length == 0)
        {
            return;
        }

        OpenWebConsole(entry.Url);
    }

    /// <summary>
    /// 二次确认对话框：重启 Windows 服务
    /// </summary>
    private async Task ShowRestartConfirmDialogAsync()
    {
        var serviceNameTextBox = new TextBox();
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "此操作将重启指定的本地 Windows 服务，期间服务将中断。请在非业务高峰期执行。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        panel.Children.Add(serviceNameTextBox);

        var dialog = new ContentDialog
        {
            Title = "确认重启服务？",
            Content = panel,
            PrimaryButtonText = "确认重启",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonStyle = (Style)FindResource("DangerButtonStyle")
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            var serviceName = serviceNameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                MessageBox.Show("请输入有效的服务名。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var operationResult = await _serviceControl.RestartServiceAsync(serviceName);
            MessageBox.Show(
                operationResult.Message,
                operationResult.Success ? "成功" : "失败",
                MessageBoxButton.OK,
                operationResult.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
        }
    }
}
