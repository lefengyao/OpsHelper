using ServerOpsHelper.Services;
using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace ServerOpsHelper.Views;

/// <summary>
/// 扩展命令的执行结果窗口。
/// <para>
/// 为什么不直接用 MessageBox：命令输出动辄几百行，MessageBox 不能滚动也不能选中部分内容，
/// 这里用只读 TextBox 兜住，复制/粘贴/搜索都能用。
/// </para>
/// </summary>
public partial class ExtensionResultWindow : Window
{
    private string _allText = string.Empty;

    public ExtensionResultWindow(string title, RemoteCommandResult result)
    {
        InitializeComponent();

        Title = $"执行结果：{title}";
        HeaderTextBlock.Text = title;

        var state = result.Success ? "成功" : "失败";
        var exit = result.ExitCode >= 0 ? $"退出码 {result.ExitCode}" : "无退出码";

        StatusTextBlock.Text = result.Success
            ? $"{state}（{exit}）"
            : $"{state}（{exit}）：{FirstLine(result.Error)}";

        var builder = new StringBuilder();

        if (!string.IsNullOrEmpty(result.Output))
        {
            builder.Append(result.Output);
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("── stderr ──");
            }

            builder.Append(result.Error);
        }

        _allText = builder.Length == 0 ? "(没有输出)" : builder.ToString();
        OutputTextBox.Text = _allText;
        OutputTextBox.CaretIndex = 0;
        OutputTextBox.ScrollToHome();
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(无错误信息)";
        }

        var index = text.IndexOfAny(new[] { '\r', '\n' });
        return index < 0 ? text : text[..index];
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (ClipboardService.TryCopy(_allText))
        {
            StatusTextBlock.Text = "已复制到剪贴板。";
            return;
        }

        // 复制失败不弹窗打断，提示一句就够（用户还能手动 Ctrl+A/Ctrl+C）
        StatusTextBlock.Text = "复制失败，可以在下方选中后按 Ctrl+C。";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
