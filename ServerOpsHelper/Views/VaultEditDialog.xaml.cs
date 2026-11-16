using ServerOpsHelper.Models;
using ServerOpsHelper.Services;
using System;
using System.Windows;
using System.Windows.Controls;
using ModernWpf.Controls;

namespace ServerOpsHelper.Views;

/// <summary>
/// 密码箱编辑对话框。密码经 DPAPI 加密后落盘，输入不合法时禁用主按钮。
/// </summary>
public partial class VaultEditDialog : ContentDialog
{
    private VaultItem _item = new();

    /// <summary>
    /// XAML 加载期间控件尚未全部构造，事件处理器要等到加载完成后再生效
    /// </summary>
    private bool _uiReady;

    private VaultEditDialog()
    {
        InitializeComponent();
        _uiReady = true;
    }

    /// <summary>
    /// 弹出对话框编辑密码记录
    /// </summary>
    /// <param name="item">传入则编辑，传 null 则新增</param>
    /// <returns>用户确认后返回 VaultItem（同一个对象实例，已应用界面改动），取消返回 null</returns>
    public static async Task<VaultItem?> ShowEditAsync(VaultItem? item)
    {
        var isNew = item == null;
        var dialog = new VaultEditDialog();
        dialog.Setup(item ?? new VaultItem(), isNew);

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? dialog.BuildItem() : null;
    }

    private void Setup(VaultItem item, bool isNew)
    {
        _item = item;

        Title = isNew ? "新增密码记录" : "编辑密码记录";
        PrimaryButtonText = isNew ? "添加" : "保存";
        CloseButtonText = "取消";

        TitleTextBox.Text = item.Title;
        CategoryTextBox.Text = item.Category;
        UsernameTextBox.Text = item.Username;
        PasswordInput.Password = item.Password;
        UrlTextBox.Text = item.Url;
        NotesTextBox.Text = item.Notes;
        PinnedCheckBox.IsChecked = item.IsPinned;

        UpdateValidation();
    }

    private VaultItem BuildItem()
    {
        _item.Title = TitleTextBox.Text.Trim();
        _item.Category = CategoryTextBox.Text.Trim();
        _item.Username = UsernameTextBox.Text.Trim();
        _item.Password = PasswordInput.Password;
        _item.Url = UrlTextBox.Text.Trim();
        _item.Notes = NotesTextBox.Text.Trim();
        _item.IsPinned = PinnedCheckBox.IsChecked == true;

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
        IsPrimaryButtonEnabled = error == null;
    }

    private void Field_TextChanged(object sender, TextChangedEventArgs e) => UpdateValidation();

    private void SecretBox_PasswordChanged(object sender, RoutedEventArgs e) => UpdateValidation();

    private void GeneratePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        // 生成后直接明文显示，方便用户复制到目标系统里
        PasswordInput.SetPassword(PasswordGenerator.Generate(16), reveal: true);
        UpdateValidation();
    }
}
