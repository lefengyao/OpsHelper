using ServerOpsHelper.Models;

namespace ServerOpsHelper.Services;

/// <summary>
/// 外壳导航能力：由主窗口实现并注册，供各页面请求切换功能页或打开网页。
/// <para>
/// 之所以要这层间接：内置网页浏览器是**常驻**在主窗口里的唯一实例，
/// 页面不能再用 <c>NavigationService.Navigate(new WebConsolePage(...))</c> 去新建一个，
/// 否则每切一次都会重建 WebView2、丢掉正在看的页面。
/// </para>
/// </summary>
public interface IShellNavigator
{
    /// <summary>
    /// 切到内置网页浏览器并打开一条常用链接（带账号密码，可自动填充）
    /// </summary>
    void OpenWebConsole(LinkItem link);

    /// <summary>
    /// 切到内置网页浏览器并打开一个地址
    /// </summary>
    void OpenWebConsole(string url);

    /// <summary>
    /// 切换到指定功能页（Tag 与侧边栏项一致：Servers / Bookmarks / History / Vault / WebConsole / Settings）
    /// </summary>
    void NavigateTo(string tag);
}

/// <summary>
/// 外壳导航入口（静态转发给当前主窗口）
/// </summary>
public static class ShellNavigation
{
    /// <summary>
    /// 当前主窗口注册的导航实现；窗口关闭后置空
    /// </summary>
    public static IShellNavigator? Current { get; set; }

    /// <summary>
    /// 在内置浏览器里打开一条常用链接
    /// </summary>
    public static void OpenWebConsole(LinkItem link) => Current?.OpenWebConsole(link);

    /// <summary>
    /// 在内置浏览器里打开一个地址
    /// </summary>
    public static void OpenWebConsole(string url) => Current?.OpenWebConsole(url);

    /// <summary>
    /// 切换到指定功能页
    /// </summary>
    public static void NavigateTo(string tag) => Current?.NavigateTo(tag);
}
