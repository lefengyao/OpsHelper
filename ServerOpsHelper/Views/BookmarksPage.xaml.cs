using ServerOpsHelper.Models;
using ServerOpsHelper.Services;
using ServerOpsHelper.ViewModels;
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace ServerOpsHelper.Views;

/// <summary>
/// 常用链接页：集中管理经常访问的网页与宝塔面板专用链接。
/// </summary>
public partial class BookmarksPage : Page
{
    private readonly LinkListViewModel _viewModel = new();

    public BookmarksPage()
    {
        InitializeComponent();
        DataContext = _viewModel;

        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await _viewModel.LoadAsync();

    private async void AddWebButton_Click(object sender, RoutedEventArgs e)
        => await AddAsync(LinkKind.WebPage);

    private async void AddBtButton_Click(object sender, RoutedEventArgs e)
        => await AddAsync(LinkKind.BtPanel);

    private async System.Threading.Tasks.Task AddAsync(LinkKind kind)
    {
        var link = await LinkEditDialog.ShowEditAsync(null, kind);
        if (link == null)
        {
            return;
        }

        await _viewModel.SaveAsync(link);
    }

    private async void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LinkItem link })
        {
            return;
        }

        var updated = await LinkEditDialog.ShowEditAsync(link);
        if (updated == null)
        {
            return;
        }

        await _viewModel.SaveAsync(updated);
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LinkItem link })
        {
            return;
        }

        var confirmed = await Dialogs.ConfirmAsync(
            "删除链接",
            $"确定要删除「{link.Name}」吗？该操作不可撤销。",
            "删除",
            danger: true);

        if (!confirmed)
        {
            return;
        }

        await _viewModel.DeleteAsync(link);
    }

    /// <summary>
    /// 在内置的 Web 控制台中打开（可复用 WebView2 的登录态）
    /// </summary>
    private async void OpenInConsoleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LinkItem link })
        {
            return;
        }

        await _viewModel.MarkOpenedAsync(link);

        // 复用主窗口里常驻的网页宿主，而不是新建一个页面（新建会重建 WebView2、丢掉正在看的页面）
        ShellNavigation.OpenWebConsole(link);
    }

    /// <summary>
    /// 用系统默认浏览器打开
    /// </summary>
    private async void OpenInBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LinkItem link })
        {
            return;
        }

        var url = link.EffectiveUrl;
        if (url.Length == 0)
        {
            Dialogs.Error("这条记录没有可用的地址，请先编辑补充。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            await _viewModel.MarkOpenedAsync(link);
        }
        catch (Exception ex)
        {
            Dialogs.Error($"调用系统浏览器失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 复制登录信息（地址 / 账号 / 密码 / 备注）
    /// </summary>
    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LinkItem link })
        {
            return;
        }

        var copied = ClipboardService.TryCopy(link.CredentialText);
        Dialogs.ReportCopyResult(copied, "已复制登录信息");
    }
}
