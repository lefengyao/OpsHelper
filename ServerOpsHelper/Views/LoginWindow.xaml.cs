using ServerOpsHelper.Services;
using System.Windows;

namespace ServerOpsHelper.Views;

/// <summary>
/// 登录窗口
/// </summary>
public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();

        Loaded += OnLoaded;
    }

    /// <summary>
    /// 底部那句「默认账号」提示只在首次运行（还没创建过账号）时才显示，
    /// 否则改过密码之后它会给出过时的误导信息。
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        DefaultAccountHintTextBlock.Visibility = await AuthService.Instance.HasAnyAccountAsync()
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        var username = UsernameTextBox.Text.Trim();
        var password = PasswordBox.Password;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            ShowError("请输入账号和密码。");
            return;
        }

        var ok = await AuthService.Instance.ValidateAsync(username, password);
        if (!ok)
        {
            ShowError("账号或密码错误。");
            return;
        }

        DialogResult = true;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorTextBlock.Text = message;
        ErrorTextBlock.Visibility = Visibility.Visible;
    }
}
