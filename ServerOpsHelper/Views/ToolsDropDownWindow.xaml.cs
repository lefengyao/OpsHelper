using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ServerOpsHelper.Views;

/// <summary>
/// ☰ 工具下拉窗：竖排菜单，贴着工具栏的 ☰ 按钮弹出，失焦即关。
/// <para>
/// 为什么是独立窗口：内置浏览器（WebView2）是原生子窗口，会把窗口内的 WPF 浮层全部盖住，
/// 所以菜单类的东西必须用独立顶层窗口（z 序必然压在子窗口之上）。
/// </para>
/// </summary>
public partial class ToolsDropDownWindow : Window
{
    private readonly WebConsolePage _page;

    public ToolsDropDownWindow(WebConsolePage page)
    {
        InitializeComponent();
        _page = page;

        // 点外面 / 主窗口被拖动 → 自动关（行为与浏览器菜单一致）
        Deactivated += (_, _) => _page.NotifyToolsDropDownClosed();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _page.NotifyToolsDropDownClosed();
            }
        };
    }

    // 缩放框 ZoomComboBox 与视觉辅助开关 FillAidToggle 由 XAML 的 x:Name 自动生成字段，
    // 控制台页直接访问（EnsureToolsDropDown 里挂事件与初始值）。

    /*
      菜单项一律用 _page.NotifyToolsDropDownClosed()（内部走 Hide），**不要用 Close()**。

      为什么（2026-09-25 修的真实崩溃）：
        控制台页把下拉窗缓存在 _toolsDropDown 字段里，只在字段为 null 时才新建。
        这里一旦 Close()，窗口就真的被销毁了，但字段仍指向这个"已关闭的窗口"；
        下次再点 ☰ 会走到 _toolsDropDown.Show() —— 已关闭的 Window 不允许再 Show，
        抛 InvalidOperationException。异常从事件处理器逃逸后没有人接，
        整个应用直接退出（事件日志里能看到 at WebConsolePage.ToolsToggle_Click -> Window.Show()）。
        用户视角就是"用完填充／收藏这些菜单，再点 ☰ 软件就没了"。

      用 Hide 则窗口只是隐藏、实例仍可复用，不产生"已关闭"状态；
      同时在 EnsureToolsDropDown 里订阅了 Closed 做兜底，
      万一将来有人又写了 Close()，也只会退化成"下次重建窗口"，不会再崩。
    */

    private void Fill_Click(object sender, RoutedEventArgs e)
    {
        _page.NotifyToolsDropDownClosed();
        _page.InvokeAutofill();
    }

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        _page.NotifyToolsDropDownClosed();
        _page.InvokeOpenRecent();
    }

    private void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        _page.NotifyToolsDropDownClosed();
        _page.InvokeBookmark();
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        _page.NotifyToolsDropDownClosed();
        _page.InvokeFitWidth();
    }

    private void Aid_Changed(object sender, RoutedEventArgs e)
    {
        _page.ApplyFillAidFromUi(FillAidToggle.IsChecked == true);
    }
}
