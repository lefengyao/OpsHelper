using ModernWpf.Controls;
using System.Threading.Tasks;
using System.Windows;

namespace ServerOpsHelper.Views;

/// <summary>
/// 统一的对话框 / 提示封装，避免各页面重复拼装 ContentDialog。
/// </summary>
internal static class Dialogs
{
    /// <summary>
    /// 二次确认对话框
    /// </summary>
    /// <param name="danger">true 时主按钮使用红色危险样式</param>
    /// <returns>用户确认返回 true</returns>
    public static async Task<bool> ConfirmAsync(string title, string message, string primaryText = "确定", bool danger = false)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (danger && Application.Current?.TryFindResource("DangerButtonStyle") is Style dangerStyle)
        {
            dialog.PrimaryButtonStyle = dangerStyle;
        }

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    /// <summary>
    /// 轻量提示（信息）
    /// </summary>
    public static void Info(string message, string title = "提示")
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>
    /// 轻量提示（错误）
    /// </summary>
    public static void Error(string message, string title = "出错了")
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    /// <summary>
    /// 复制结果提示：成功静默返回 true，失败给出提示
    /// </summary>
    public static bool ReportCopyResult(bool success, string successHint)
    {
        if (success)
        {
            return true;
        }

        Error("复制失败，剪贴板可能被其它程序占用，请稍后再试。");
        return false;
    }
}
