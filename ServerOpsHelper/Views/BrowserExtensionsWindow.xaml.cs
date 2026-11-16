using Microsoft.Web.WebView2.Core;
using ServerOpsHelper.ViewModels;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace ServerOpsHelper.Views;

/// <summary>
/// 扩展项的展示模型：包住 CoreWebView2BrowserExtension，加上界面要显示的文案。
/// </summary>
public sealed class BrowserExtensionItem
{
    public CoreWebView2BrowserExtension Extension { get; }

    public string Id => Extension.Id;

    public string Name => string.IsNullOrWhiteSpace(Extension.Name) ? Extension.Id : Extension.Name;

    public bool IsEnabled => Extension.IsEnabled;

    public string StateLabel => Extension.IsEnabled ? "已启用" : "已禁用";

    public string ToggleLabel => Extension.IsEnabled ? "禁用" : "启用";

    public BrowserExtensionItem(CoreWebView2BrowserExtension extension)
    {
        Extension = extension;
    }
}

/// <summary>
/// 浏览器扩展管理窗口：列出 / 安装 / 启用 / 禁用 / 删除 Edge 插件。
/// <para>
/// 扩展是 profile 级别的，所有标签共享，所以从当前活动标签的 CoreWebView2.Profile 操作即可。
/// 窗口每次打开都重新拉一次列表（扩展可能在外部被改过）。
/// </para>
/// </summary>
public partial class BrowserExtensionsWindow : Window
{
    private readonly WebConsolePage _page;
    private List<BrowserExtensionItem> _items = new();

    private BrowserExtensionsWindow(WebConsolePage page)
    {
        InitializeComponent();
        _page = page;
        Owner = Application.Current?.MainWindow;
    }

    /// <summary>从工具菜单打开；异步拉起窗口并加载扩展列表。</summary>
    internal static async Task ShowForAsync(WebConsolePage page)
    {
        var window = new BrowserExtensionsWindow(page);
        window.Show();
        await window.LoadAsync();
    }

    /// <summary>取当前可用的 CoreWebView2（当前标签没有就用任意一个活着的标签）。</summary>
    private CoreWebView2? FindCore()
    {
        var core = _page.ActiveCore;
        if (core != null)
        {
            return core;
        }

        // 活动标签可能尚未创建 WebView2；退而求其次找一个已经就绪的标签。
        // FirstReadyCore 由页面暴露，避免直接翻它的私有 _views。
        return _page.FirstReadyCore;
    }

    private async Task LoadAsync()
    {
        var core = FindCore();
        if (core == null)
        {
            EmptyHint.Text = "内置浏览器尚未就绪。请先打开一个网页标签，再来管理扩展。";
            EmptyHint.Visibility = Visibility.Visible;
            ExtensionList.ItemsSource = null;
            return;
        }

        try
        {
            var extensions = await core.Profile.GetBrowserExtensionsAsync();
            _items = extensions.Select(e => new BrowserExtensionItem(e)).ToList();
            ExtensionList.ItemsSource = _items;
            EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (System.Exception ex)
        {
            EmptyHint.Text = $"读取扩展列表失败：{ex.Message}";
            EmptyHint.Visibility = Visibility.Visible;
            ExtensionList.ItemsSource = null;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BrowserExtensionItem item })
        {
            return;
        }

        try
        {
            await item.Extension.EnableAsync(!item.IsEnabled);
            await LoadAsync();
        }
        catch (System.Exception ex)
        {
            Dialogs.Error($"{(item.IsEnabled ? "禁用" : "启用")}扩展失败：{ex.Message}");
        }
    }

    private async void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BrowserExtensionItem item })
        {
            return;
        }

        var confirmed = await Dialogs.ConfirmAsync(
            "删除扩展",
            $"确定要删除扩展「{item.Name}」吗？该操作不可撤销。",
            "删除",
            danger: true);
        if (!confirmed)
        {
            return;
        }

        try
        {
            await item.Extension.RemoveAsync();
            await LoadAsync();
        }
        catch (System.Exception ex)
        {
            Dialogs.Error($"删除扩展失败：{ex.Message}");
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择扩展目录（含 manifest.json）",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var folder = dialog.FolderName;
        if (!File.Exists(Path.Combine(folder, "manifest.json")))
        {
            Dialogs.Error("所选目录里没有 manifest.json，不是有效的浏览器扩展。", "无法安装");
            return;
        }

        var core = FindCore();
        if (core == null)
        {
            Dialogs.Error("内置浏览器尚未就绪，请先打开一个网页标签。");
            return;
        }

        try
        {
            await core.Profile.AddBrowserExtensionAsync(folder);
            await LoadAsync();
        }
        catch (System.Exception ex)
        {
            Dialogs.Error($"安装扩展失败：{ex.Message}\n\n常见原因：插件不兼容、目录名以下划线开头、或扩展格式不被支持。");
        }
    }
}
