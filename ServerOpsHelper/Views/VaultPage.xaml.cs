using ServerOpsHelper.Models;
using ServerOpsHelper.Services;
using ServerOpsHelper.ViewModels;
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace ServerOpsHelper.Views;

/// <summary>
/// 密码箱页：左侧记录列表，右侧详情（账号 / 密码 / 备注 + 显隐与复制）。
/// </summary>
public partial class VaultPage : Page
{
    private readonly VaultViewModel _viewModel = new();

    public VaultPage()
    {
        InitializeComponent();
        DataContext = _viewModel;

        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }

    /*
      下面这些事件处理器都是 async void —— 异常一旦逃出去就没人能接，
      会直接终结进程（2026-09-25 之前应用没有任何全局兜底，真崩过）。
      App 里现在有全局兜底，但那只会在最外层弹一句笼统的"操作出错"；
      在这里就地捕获，能说清是"保存密码箱失败"还是"读取失败"、失败原因是什么，
      用户更容易判断要不要重试。所以逐个包 try-catch，不做成笼统的一层。
    */

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            ReportFailure("读取密码箱", ex);
        }
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var item = await VaultEditDialog.ShowEditAsync(null);
            if (item == null)
            {
                return;
            }

            await _viewModel.SaveAsync(item);
        }
        catch (Exception ex)
        {
            ReportFailure("新增密码记录", ex);
        }
    }

    private async void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelected(out var item))
        {
            return;
        }

        try
        {
            var updated = await VaultEditDialog.ShowEditAsync(item);
            if (updated == null)
            {
                return;
            }

            await _viewModel.SaveAsync(updated);
        }
        catch (Exception ex)
        {
            ReportFailure("保存密码记录", ex);
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelected(out var item))
        {
            return;
        }

        try
        {
            var confirmed = await Dialogs.ConfirmAsync(
                "删除密码记录",
                $"确定要删除「{item.Title}」吗？删除后该密码无法恢复。",
                "删除",
                danger: true);

            if (!confirmed)
            {
                return;
            }

            await _viewModel.DeleteAsync(item);
        }
        catch (Exception ex)
        {
            ReportFailure("删除密码记录", ex);
        }
    }

    /// <summary>
    /// 把失败摆到台面上：说清是哪个动作失败、失败原因是什么
    /// </summary>
    private static void ReportFailure(string action, Exception ex)
        => Dialogs.Error($"{action}失败：{ex.Message}");

    /// <summary>
    /// 切换密码明文 / 遮挡
    /// </summary>
    private void TogglePasswordButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.IsPasswordVisible = !_viewModel.IsPasswordVisible;

    private void CopyUsernameButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetSelected(out var item))
        {
            Dialogs.ReportCopyResult(ClipboardService.TryCopy(item.Username), "已复制账号");
        }
    }

    private void CopyPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetSelected(out var item))
        {
            Dialogs.ReportCopyResult(ClipboardService.TryCopy(item.Password), "已复制密码");
        }
    }

    private void CopyUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetSelected(out var item))
        {
            Dialogs.ReportCopyResult(ClipboardService.TryCopy(item.Url), "已复制地址");
        }
    }

    private void CopyAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetSelected(out var item))
        {
            Dialogs.ReportCopyResult(ClipboardService.TryCopy(item.CredentialText), "已复制全部信息");
        }
    }

    /// <summary>
    /// 用系统默认浏览器打开登录地址
    /// </summary>
    private void OpenUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelected(out var item))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(item.Url))
        {
            Dialogs.Error("这条记录没有填写登录地址。");
            return;
        }

        var url = LinkItem.NormalizeUrl(item.Url);

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Dialogs.Error($"调用系统浏览器失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 取当前选中项，未选中时给出提示
    /// </summary>
    private bool TryGetSelected(out VaultItem item)
    {
        if (_viewModel.SelectedItem is { } selected)
        {
            item = selected;
            return true;
        }

        item = null!;
        Dialogs.Info("请先在左侧选择一条记录。");
        return false;
    }
}
