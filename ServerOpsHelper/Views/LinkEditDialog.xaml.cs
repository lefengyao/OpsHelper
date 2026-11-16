using ServerOpsHelper.Models;
using ServerOpsHelper.Services;
using System;
using System.Windows;
using System.Windows.Controls;
using ModernWpf.Controls;

namespace ServerOpsHelper.Views;

/// <summary>
/// 常用链接编辑对话框，同时兼容「常用网页」与「宝塔面板专用链接」两种类型。
/// <para>
/// 输入不合法时主按钮保持禁用，并给出具体原因，避免保存出打不开的链接。
/// </para>
/// </summary>
public partial class LinkEditDialog : ContentDialog
{
    private const int DefaultBtPort = 8888;

    private LinkItem _link = new();

    /// <summary>
    /// XAML 加载期间控件尚未全部构造，事件处理器要等到加载完成后再生效
    /// </summary>
    private bool _uiReady;

    private LinkEditDialog()
    {
        InitializeComponent();

        // 在 XAML 里挂 SelectionChanged 会在 ComboBox 首次选中时触发，
        // 那时后面的控件还没构造好，因此改为加载完成后再订阅
        KindComboBox.SelectionChanged += KindComboBox_SelectionChanged;
        _uiReady = true;
    }

    /// <summary>
    /// 弹出对话框编辑链接
    /// </summary>
    /// <param name="link">传入则编辑，传 null 则新增</param>
    /// <param name="defaultKind">新增时的默认类型</param>
    /// <returns>用户确认后返回 LinkItem（同一个对象实例，已应用界面改动），取消返回 null</returns>
    public static async Task<LinkItem?> ShowEditAsync(LinkItem? link, LinkKind defaultKind = LinkKind.WebPage)
    {
        var isNew = link == null;
        var dialog = new LinkEditDialog();
        dialog.Setup(link ?? new LinkItem { Kind = defaultKind }, isNew);

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? dialog.BuildLink() : null;
    }

    /// <summary>
    /// 当前选择的类型
    /// </summary>
    private LinkKind SelectedKind
        => KindComboBox.SelectedIndex == 1 ? LinkKind.BtPanel : LinkKind.WebPage;

    // ───────────────────────── 初始化 / 收集 ─────────────────────────

    private void Setup(LinkItem link, bool isNew)
    {
        _link = link;

        Title = isNew ? "新增链接" : "编辑链接";
        PrimaryButtonText = isNew ? "添加" : "保存";
        CloseButtonText = "取消";

        NameTextBox.Text = link.Name;
        KindComboBox.SelectedIndex = link.Kind == LinkKind.BtPanel ? 1 : 0;
        HostTextBox.Text = link.Host;
        PortTextBox.Text = link.Port > 0 ? link.Port.ToString() : DefaultBtPort.ToString();
        SafeEntryTextBox.Text = link.SafeEntry;
        UrlTextBox.Text = link.Url;
        UsernameTextBox.Text = link.Username;
        PasswordInput.Password = link.Password;
        TagsTextBox.Text = link.Tags;
        NotesTextBox.Text = link.Notes;
        PinnedCheckBox.IsChecked = link.IsPinned;

        UpdateBtPanelSectionVisibility();
        UpdateValidation();
    }

    /// <summary>
    /// 把界面上的值写回 <see cref="LinkItem"/>（仅在用户确认后调用）
    /// </summary>
    private LinkItem BuildLink()
    {
        _link.Name = NameTextBox.Text.Trim();
        _link.Kind = SelectedKind;
        _link.Username = UsernameTextBox.Text.Trim();
        _link.Password = PasswordInput.Password;
        _link.Tags = TagsTextBox.Text.Trim();
        _link.Notes = NotesTextBox.Text.Trim();
        _link.IsPinned = PinnedCheckBox.IsChecked == true;

        if (_link.Kind == LinkKind.BtPanel)
        {
            _link.Host = HostTextBox.Text.Trim();
            _link.SafeEntry = SafeEntryTextBox.Text.Trim();
            _link.Port = ParsePort();
        }

        _link.Url = UrlTextBox.Text.Trim();

        // 宝塔类型没填完整地址时，由拆分的字段补出来，保证一定能打开
        if (_link.Kind == LinkKind.BtPanel && _link.Url.Length == 0)
        {
            _link.Url = LinkItem.BuildBtUrl(_link.Host, _link.Port, _link.SafeEntry);
        }

        return _link;
    }

    private int ParsePort()
        => int.TryParse(PortTextBox.Text.Trim(), out var port) && port is > 0 and <= 65535
            ? port
            : DefaultBtPort;

    // ───────────────────────── 校验 ─────────────────────────

    /// <summary>
    /// 返回第一条不通过的校验信息，全部通过返回 null
    /// </summary>
    private string? GetValidationError()
    {
        if (NameTextBox.Text.Trim().Length == 0)
        {
            return "请填写名称，方便日后在列表里识别。";
        }

        if (SelectedKind == LinkKind.BtPanel)
        {
            if (HostTextBox.Text.Trim().Length == 0 && UrlTextBox.Text.Trim().Length == 0)
            {
                return "请填写服务器地址 / IP（会自动拼成 https://IP:端口/安全入口），或直接填写完整面板地址。";
            }
        }
        else if (UrlTextBox.Text.Trim().Length == 0)
        {
            return "请填写要打开的网页地址。";
        }

        return null;
    }

    private void UpdateValidation()
    {
        if (!_uiReady)
        {
            return;
        }

        var error = GetValidationError();

        HintTextBlock.Text = error ?? string.Empty;
        HintTextBlock.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        IsPrimaryButtonEnabled = error == null;
    }

    private void UpdateBtPanelSectionVisibility()
    {
        if (!_uiReady)
        {
            return;
        }

        BtPanelSection.Visibility = SelectedKind == LinkKind.BtPanel ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────────────────── 事件 ─────────────────────────

    private void Field_TextChanged(object sender, TextChangedEventArgs e) => UpdateValidation();

    private void SecretBox_PasswordChanged(object sender, RoutedEventArgs e) => UpdateValidation();

    private void KindComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateBtPanelSectionVisibility();
        UpdateValidation();
    }

    /// <summary>
    /// 由「IP / 端口 / 安全入口」拼出面板地址并回填到地址框
    /// </summary>
    private void BuildUrlButton_Click(object sender, RoutedEventArgs e)
    {
        var url = LinkItem.BuildBtUrl(HostTextBox.Text, ParsePort(), SafeEntryTextBox.Text);
        if (url.Length == 0)
        {
            HintTextBlock.Text = "请先填写服务器地址 / IP。";
            HintTextBlock.Visibility = Visibility.Visible;
            return;
        }

        UrlTextBox.Text = url;
        UpdateValidation();
    }

    private void GeneratePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        // 生成后直接明文显示，方便用户复制到目标系统里
        PasswordInput.SetPassword(PasswordGenerator.Generate(16), reveal: true);
        UpdateValidation();
    }
}
