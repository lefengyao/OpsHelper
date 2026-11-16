using System.Windows;

namespace ServerOpsHelper.Views;

/// <summary>
/// 通用「单行文本输入」窗口。
/// <para>
/// 用独立窗口而不是 ContentDialog：内置浏览器是 HwndHost 原生子窗口，
/// 窗口内的浮层会被它盖住（详见 <see cref="CredentialCaptureWindow"/> 的说明）。
/// </para>
/// </summary>
public partial class TextPromptWindow : Window
{
    private TextPromptWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 弹出输入窗口（模态）
    /// </summary>
    /// <param name="owner">宿主窗口</param>
    /// <param name="title">窗口标题与提示文案</param>
    /// <param name="initialText">初始内容</param>
    /// <returns>确定时返回输入内容，取消返回 null</returns>
    public static string? Prompt(Window? owner, string title, string initialText = "")
    {
        var window = new TextPromptWindow
        {
            Title = title
        };

        window.TitleTextBlock.Text = title;
        window.InputTextBox.Text = initialText;

        if (owner != null)
        {
            window.Owner = owner;
        }

        window.Loaded += (_, _) =>
        {
            window.InputTextBox.Focus();
            window.InputTextBox.SelectAll();
        };

        return window.ShowDialog() == true ? window.InputTextBox.Text.Trim() : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
