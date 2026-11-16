using System.Windows;
using System.Windows.Controls;

namespace ServerOpsHelper.Controls;

/// <summary>
/// 密码输入控件：默认遮挡显示，勾选「显示」后切换为明文，方便核对是否输错。
/// <para>
/// 对外的唯一数据入口是 <see cref="Password"/>，内部两个输入控件始终同步，
/// 调用方不需要关心当前处于遮挡还是明文状态。
/// </para>
/// </summary>
public partial class SecretBox : UserControl
{
    /// <summary>
    /// 同步两个输入控件时用于抑制递归触发
    /// </summary>
    private bool _syncing;

    private string _password = string.Empty;

    public SecretBox()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 密码明文
    /// </summary>
    public string Password
    {
        get => _password;
        set
        {
            var normalized = value ?? string.Empty;
            if (_password == normalized)
            {
                return;
            }

            _password = normalized;
            SyncToControls();
            RaisePasswordChanged();
        }
    }

    /// <summary>
    /// 当前是否以明文显示
    /// </summary>
    public bool IsRevealed => RevealCheckBox.IsChecked == true;

    /// <summary>
    /// 密码内容变化时触发（供外部做实时校验）
    /// </summary>
    public event RoutedEventHandler? PasswordChanged;

    /// <summary>
    /// 切换明文 / 遮挡显示
    /// </summary>
    public void Reveal(bool reveal)
    {
        RevealCheckBox.IsChecked = reveal;
        ApplyRevealState(reveal);
        SyncToControls();
    }

    /// <summary>
    /// 设置密码并可同时切到明文（例如刚由「生成」按钮产出）
    /// </summary>
    public void SetPassword(string value, bool reveal)
    {
        Password = value;
        Reveal(reveal);
    }

    private void SyncToControls()
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        PasswordInput.Password = _password;
        PasswordRevealed.Text = _password;
        _syncing = false;
    }

    private void ApplyRevealState(bool reveal)
    {
        PasswordInput.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
        PasswordRevealed.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        _password = PasswordInput.Password;
        PasswordRevealed.Text = _password;
        _syncing = false;

        RaisePasswordChanged();
    }

    private void PasswordRevealed_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        _password = PasswordRevealed.Text;
        PasswordInput.Password = _password;
        _syncing = false;

        RaisePasswordChanged();
    }

    private void RevealCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        ApplyRevealState(RevealCheckBox.IsChecked == true);
        SyncToControls();
    }

    private void RaisePasswordChanged()
        => PasswordChanged?.Invoke(this, new RoutedEventArgs());
}
