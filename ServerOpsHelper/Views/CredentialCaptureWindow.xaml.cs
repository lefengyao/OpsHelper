using ServerOpsHelper.Models;
using System;
using System.Windows;
using System.Windows.Controls;

namespace ServerOpsHelper.Views;

/// <summary>
/// 捕获到登录信息后，用户可以选择怎么处理
/// </summary>
public enum CredentialCaptureChoice
{
    /// <summary>
    /// 保存到密码箱
    /// </summary>
    Save,

    /// <summary>
    /// 本次运行内不再提示这个站点
    /// </summary>
    IgnoreSite,

    /// <summary>
    /// 稍后再说（下次触发时仍可再问）
    /// </summary>
    Later
}

/// <summary>
/// 捕获结果：选择 + 用户确认后的记录
/// </summary>
public sealed class CredentialCaptureResult
{
    public CredentialCaptureChoice Choice { get; init; }

    /// <summary>
    /// 选择保存时为用户补全后的记录，其余情况为 null
    /// </summary>
    public VaultItem? Item { get; init; }
}

/// <summary>
/// 「检测到登录信息，是否保存到密码箱」确认窗口。
/// <para>
/// **必须用独立窗口而不是 ContentDialog**：内置浏览器是 HwndHost 原生子窗口，
/// 窗口内的浮层永远会被它盖住（就是"对话框跑到网页后面"那个问题）。
/// </para>
/// </summary>
public partial class CredentialCaptureWindow : Window
{
    private VaultItem _item = new();
    private CredentialCaptureChoice _choice = CredentialCaptureChoice.Later;

    /// <summary>
    /// XAML 加载期间控件尚未全部构造，事件处理器要等到加载完成后再生效
    /// </summary>
    private bool _uiReady;

    public CredentialCaptureWindow()
    {
        InitializeComponent();
        _uiReady = true;
    }

    /// <summary>
    /// 弹出确认窗口（模态）
    /// </summary>
    /// <param name="owner">宿主窗口</param>
    /// <param name="draft">根据页面内容预填的记录</param>
    /// <param name="siteLabel">用于提示文案的站点名</param>
    /// <returns>用户的选择；选择保存时带回补全后的记录</returns>
    public static CredentialCaptureResult ShowCapture(Window? owner, VaultItem draft, string siteLabel)
    {
        var window = new CredentialCaptureWindow();
        window.Setup(draft, siteLabel);

        if (owner != null && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        window.ShowDialog();

        return new CredentialCaptureResult
        {
            Choice = window._choice,
            Item = window._choice == CredentialCaptureChoice.Save ? window.BuildItem() : null
        };
    }

    private void Setup(VaultItem draft, string siteLabel)
    {
        _item = draft;

        IntroTextBlock.Text =
            $"在「{siteLabel}」检测到登录信息，是否保存到密码箱？"
            + "保存后可用「填充账号密码」一键填入，密码以 Windows DPAPI 加密落盘。";

        TitleTextBox.Text = draft.Title;
        CategoryTextBox.Text = draft.Category;
        UsernameTextBox.Text = draft.Username;
        PasswordInput.Password = draft.Password;
        UrlTextBox.Text = draft.Url;

        UpdateValidation();
    }

    private VaultItem BuildItem()
    {
        _item.Title = TitleTextBox.Text.Trim();
        _item.Category = CategoryTextBox.Text.Trim();
        _item.Username = UsernameTextBox.Text.Trim();
        _item.Password = PasswordInput.Password;
        _item.Url = UrlTextBox.Text.Trim();
        _item.UpdatedAt = DateTime.Now;

        return _item;
    }

    private void UpdateValidation()
    {
        if (!_uiReady)
        {
            return;
        }

        var error = TitleTextBox.Text.Trim().Length == 0
            ? "请填写名称，方便日后在列表里识别。"
            : null;

        HintTextBlock.Text = error ?? string.Empty;
        HintTextBlock.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.IsEnabled = error == null;
    }

    private void Field_TextChanged(object sender, TextChangedEventArgs e) => UpdateValidation();

    private void SecretBox_PasswordChanged(object sender, RoutedEventArgs e) => UpdateValidation();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _choice = CredentialCaptureChoice.Save;
        Close();
    }

    private void IgnoreSite_Click(object sender, RoutedEventArgs e)
    {
        _choice = CredentialCaptureChoice.IgnoreSite;
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        _choice = CredentialCaptureChoice.Later;
        Close();
    }
}
