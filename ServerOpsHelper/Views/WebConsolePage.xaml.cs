using Microsoft.Web.WebView2.Core;
using ServerOpsHelper.Models;
using ServerOpsHelper.Services;
using ServerOpsHelper.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ServerOpsHelper.Views;

/// <summary>
/// 内置浏览器：通过 WebView2 打开管理后台，支持注入 Token / Cookie 实现免密登录。
/// <para>
/// 界面按普通浏览器组织：顶部地址栏，下面一行**标签页**（打开过的页面各占一个标签，
/// 标签多了自动变窄、再多了横向滚动），右侧「打开…」展开最近访问过的页面 / 常用链接 / 历史记录。
/// </para>
/// <para>
/// 与「常用链接」「访问记录」「密码箱」三处联动：
/// 每次导航完成都会记一笔访问历史；页面里手动登录时会被捕获下来，询问是否保存进密码箱。
/// </para>
/// <para>
/// **虽然类名里带 Page，这里其实是 <see cref="UserControl"/>**：
/// 它是常驻在主窗口布局里的唯一实例（切页只切可见性，从不销毁），
/// 而 WPF 不允许 <c>Page</c> 作为非 Window / Frame 的子元素（会抛「Page 只能具有 Window 或 Frame 父级」）。
/// </para>
/// </summary>
public partial class WebConsolePage : UserControl
{
    /// <summary>
    /// 往页面登录框填值的脚本片段（在页面上下文中执行，返回命中的输入框数量）。
    /// <para>
    /// 第三个参数是「网页视觉辅助」开关：为 true 时给被填的输入框加高亮闪烁，
    /// 样式注入一次（幂等），约 2.3 秒后自动摘掉，不残留任何类名。
    /// </para>
    /// </summary>
    private const string AutofillFunction = @"(function (username, password, aid) {
    var filled = [];
    function fill(selectors, value) {
        if (!value) { return 0; }
        for (var i = 0; i < selectors.length; i++) {
            var el = document.querySelector(selectors[i]);
            if (el) {
                el.focus();
                el.value = value;
                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
                filled.push(el);
                return 1;
            }
        }
        return 0;
    }
    var u = fill(['#username', '#user', 'input[name=username]', 'input[name=user]', 'input[name=account]', 'input[type=text][placeholder*=账号]', 'input[type=text][placeholder*=用户]', '.login-username'], username);
    var p = fill(['#password', 'input[name=password]', 'input[type=password]', '.login-password', 'input[placeholder*=密码]'], password);

    if (aid && filled.length) {
        var st = document.getElementById('__opsFillAidStyle');
        if (!st) {
            st = document.createElement('style');
            st.id = '__opsFillAidStyle';
            st.textContent = '@keyframes opsFillFlash{0%{box-shadow:0 0 0 0 rgba(0,120,212,.55)}70%{box-shadow:0 0 0 7px rgba(0,120,212,0)}100%{box-shadow:0 0 0 0 rgba(0,120,212,0)}}'
                + '.ops-fill-aid{box-shadow:0 0 0 3px rgba(0,120,212,.45)!important;background-color:#eaf5ff!important;'
                + 'transition:box-shadow .3s,background-color .3s;animation:opsFillFlash 1s ease-out 2;}';
            document.head.appendChild(st);
        }
        for (var k = 0; k < filled.length; k++) {
            var el = filled[k];
            el.classList.remove('ops-fill-aid');
            void el.offsetWidth;
            el.classList.add('ops-fill-aid');
            (function (el) {
                setTimeout(function () { el.classList.remove('ops-fill-aid'); }, 2300);
            })(el);
        }
    }
    return u + p;
})";

    /// <summary>
    /// 登录信息捕获脚本：每个新文档创建时注入。
    /// <para>
    /// 只在「看起来要提交」的动作上取一次值（表单提交 / 回车 / 点登录按钮），
    /// 避免在页面上随便点一下就被打断。取到密码框有值才回传，
    /// 由宿主决定是否弹窗，页面侧不做任何判断。
    /// </para>
    /// </summary>
    private const string CredentialCaptureScript = """
(function () {
    if (window.__opsCredentialHook) { return; }
    window.__opsCredentialHook = true;

    var USER_SELECTORS = ['#username', '#user', 'input[name=username]', 'input[name=user]',
        'input[name=account]', 'input[name=email]', 'input[type=email]', 'input[autocomplete=username]'];
    var PASS_SELECTORS = ['#password', 'input[name=password]', 'input[name=passwd]', 'input[type=password]'];
    var SUBMIT_WORDS = ['登录', '登陆', '登入', 'login', 'sign in', 'signin', 'submit', '提交', '确定', '确认'];

    function firstWithValue(scope, selectors) {
        if (!scope) { return null; }
        for (var i = 0; i < selectors.length; i++) {
            var list = scope.querySelectorAll(selectors[i]);
            for (var k = 0; k < list.length; k++) {
                if (list[k] && list[k].value) { return list[k]; }
            }
        }
        return null;
    }

    function report() {
        try {
            var pwdEl = firstWithValue(document, PASS_SELECTORS);
            if (!pwdEl || !pwdEl.value) { return; }

            var scope = pwdEl.form || document;
            var userEl = firstWithValue(scope, USER_SELECTORS) || firstWithValue(document, USER_SELECTORS);

            var payload = {
                type: 'credential',
                url: location.href,
                title: document.title || '',
                username: userEl ? (userEl.value || '') : '',
                password: pwdEl.value || ''
            };

            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(JSON.stringify(payload));
            }
        } catch (e) {
            // 页面脚本环境千奇百怪，捕获失败一律静默
        }
    }

    function looksLikeSubmit(node) {
        if (!node) { return false; }
        var tag = node.tagName;
        if (tag !== 'BUTTON' && tag !== 'INPUT') { return false; }

        var type = (node.getAttribute('type') || '').toLowerCase();
        if (type === 'submit') { return true; }

        var text = (node.innerText || node.value || node.textContent || '').trim().toLowerCase();
        if (!text || text.length > 20) { return false; }

        for (var i = 0; i < SUBMIT_WORDS.length; i++) {
            if (text.indexOf(SUBMIT_WORDS[i]) >= 0) { return true; }
        }
        return false;
    }

    document.addEventListener('submit', function () { report(); }, true);

    document.addEventListener('keydown', function (ev) {
        if (ev.key === 'Enter' || ev.keyCode === 13) { report(); }
    }, true);

    document.addEventListener('click', function (ev) {
        var node = ev.target;
        for (var depth = 0; node && node !== document.body && depth < 4; depth++) {
            if (looksLikeSubmit(node)) {
                setTimeout(report, 60);
                return;
            }
            node = node.parentElement;
        }
    }, true);
})();
""";

    private static readonly JsonSerializerOptions MessageJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 本次运行内「不再提示保存密码」的站点（不落盘，重开应用会重新提示）
    /// </summary>
    private static readonly HashSet<string> IgnoredCredentialOrigins = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _userDataFolder;

    /// <summary>
    /// WebView2 是否已经初始化（初始化推迟到本页第一次真正可见时）
    /// </summary>
    private bool _webViewInitialized;

    /// <summary>
    /// WebView2 浏览器进程是否已崩溃。
    /// <para>
    /// 崩溃后 <c>CoreWebView2</c> 的 getter 每次访问都会抛异常，
    /// 靠这个标志短路掉后续访问，并把提示说清楚（见 <see cref="TryGetCoreWebView2"/>）。
    /// </para>
    /// </summary>
    private bool _browserProcessFailed;

    /// <summary>
    /// WebView2 还没就绪时先记下要打开哪个标签，等就绪后补做导航
    /// </summary>
    private BrowserTab? _pendingTab;

    /// <summary>
    /// 自动填充每次导航只做一次，避免页面内跳转反复触发
    /// </summary>
    private bool _autoFillAttempted;

    /// <summary>
    /// 程序化调整缩放时抑制 ComboBox 的回调
    /// </summary>
    private bool _syncingZoom;

    /// <summary>
    /// 是否开启「检测到登录信息时提示保存到密码箱」
    /// </summary>
    private bool _captureEnabled = true;

    /// <summary>
    /// 已经问过的「站点 + 账号」，避免同一次登录被反复追问
    /// </summary>
    private readonly HashSet<string> _promptedCredentials = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 打开着的标签页（只表示本次运行里打开过的页面，不落盘）
    /// </summary>
    public ObservableCollection<BrowserTab> Tabs { get; } = new();

    /// <summary>
    /// 当前显示的标签
    /// </summary>
    private BrowserTab? ActiveTab => Tabs.FirstOrDefault(t => t.IsActive);

    /// <summary>
    /// 当前标签关联的常用链接（用于填充账号密码）
    /// </summary>
    private LinkItem? CurrentLink => ActiveTab?.Link;

    public WebConsolePage()
    {
        InitializeComponent();
        _userDataFolder = BuildUserDataFolder();
        WireUpUi();
    }

    // ───────────────────────── 对外入口 ─────────────────────────

    /// <summary>
    /// 打开一条常用链接（带凭据的还会自动填充登录信息）
    /// </summary>
    public void OpenLink(LinkItem link)
    {
        var url = link.EffectiveUrl;

        if (url.Length == 0)
        {
            StatusTextBlock.Text = $"「{link.Name}」没有可用的地址，请先在「常用链接」里补齐。";
            return;
        }

        OpenInTab(link, url);
    }

    /// <summary>
    /// 打开一个地址（没有关联链接，账号密码让「填充账号密码」按站点去密码箱里找）
    /// </summary>
    public void OpenUrl(string url)
    {
        var normalized = LinkItem.NormalizeUrl(url);
        if (normalized.Length == 0)
        {
            StatusTextBlock.Text = "没有可用的地址。";
            return;
        }

        OpenInTab(null, normalized);
    }

    /// <summary>
    /// 切到本页时调用：第一次可见才真正创建 WebView2，然后补做挂起的导航。
    /// <para>
    /// 本页在主窗口里是**常驻实例**（只切可见性，从不销毁），
    /// 所以 WebView2 一旦建起来就一直活着，切到别的功能页再切回来页面还在原处。
    /// </para>
    /// </summary>
    public async Task ActivateAsync()
    {
        // 先刷最近访问列表：它只读本地文件，很快；
        // 放到 WebView2 初始化之后的话，初始化一慢列表就跟着一直空白
        await LoadRecentAsync();

        if (!_webViewInitialized)
        {
            // WebView2 初始化偶发会卡住（运行时缺失、浏览器进程起不来），
            // 给个上限，超时就明说，不要让人对着空页面干等
            var initTask = InitializeWebView2Async();
            if (await Task.WhenAny(initTask, Task.Delay(TimeSpan.FromSeconds(10))) != initTask)
            {
                StatusTextBlock.Text = "内置浏览器初始化超时。请确认已安装 WebView2 Runtime，或点「打开」重试。";
                return;
            }

            await initTask;
        }

        if (_pendingTab is not { } pending)
        {
            return;
        }

        _pendingTab = null;
        NavigateTo(pending.Url);
    }

    /// <summary>
    /// WebView2 的用户数据目录（登录态、缓存）。
    /// <para>
    /// 刻意挂在 <see cref="ConfigService.ConfigDirectory"/> 下，而不是自己拼
    /// <c>%LOCALAPPDATA%\ServerOpsHelper</c>。原因有两个：
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>真实运行时路径不变</b>：没设 <c>SERVEROPSHELPER_CONFIG_DIR</c> 时
    /// ConfigDirectory 就是 <c>%LOCALAPPDATA%\ServerOpsHelper</c>，
    /// 所以老用户的登录态照旧，不会有任何迁移。
    /// </item>
    /// <item>
    /// <b>隔离要彻底</b>：探针 / 自动化会用 <c>SERVEROPSHELPER_CONFIG_DIR</c>
    /// 指向临时目录，若这里写死真实路径，则会与用户正在运行的实例**抢同一个
    /// WebView2 数据目录**，两边互相锁，表现为浏览器进程起不来、构造卡死
    /// （2026-09-25 探针就因此卡到超时）。
    /// </item>
    /// </list>
    /// </summary>
    private static string BuildUserDataFolder()
        => Path.Combine(ConfigService.Instance.ConfigDirectory, "WebView2Data");

    /// <summary>
    /// 网页视觉辅助：自动填充后是否在页面里高亮被填的输入框（设置里持久化）
    /// </summary>
    private bool _fillAidEnabled = true;

    /// <summary>
    /// 从设置回填开关时临时挂起 Toggled，避免把刚读出来的值又写回去
    /// </summary>
    private bool _loadingAidToggle;

    private void WireUpUi()
    {
        // 缩放选择框在 ☰ 下拉窗里，由 EnsureToolsDropDown 挂事件与初始值

        TabStrip.ItemsSource = Tabs;
        Tabs.CollectionChanged += (_, _) => RefreshTabWidths();

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var settings = await ConfigService.Instance.LoadSettingsAsync();

        // 把两个开关同步给访问记录服务与捕获逻辑；视觉辅助状态在下拉窗创建时回填
        VisitHistoryService.Instance.Configure(settings.RecordVisitHistory);
        _captureEnabled = settings.CaptureCredentials;
        _fillAidEnabled = settings.FillHighlightAid;

        // 主窗口被拖动 / 改大小时，贴着 ☰ 弹出的下拉窗位置就不对了，直接关掉
        var host = Window.GetWindow(this);
        if (host != null)
        {
            host.LocationChanged += (_, _) => NotifyToolsDropDownClosed();
            host.SizeChanged += (_, _) => NotifyToolsDropDownClosed();
        }

        if (UrlTextBox.Text.Length == 0)
        {
            UrlTextBox.Text = settings.DefaultAdminUrl;
        }

        // 本页是常驻实例，Loaded 时未必可见（开始时是折叠的），
        // 真正创建 WebView2 放到第一次切过来的时候，避免白占内存
        if (IsVisible)
        {
            await ActivateAsync();
        }
        else
        {
            await LoadRecentAsync();
        }
    }

    // ───────────────────────── 标签页 ─────────────────────────

    /// <summary>
    /// 打开一个新标签（同一个链接 / 同一地址已经开着，就直接切过去）
    /// </summary>
    private void OpenInTab(LinkItem? link, string url)
    {
        // 先按链接认：页内跳转会把标签地址改成深层地址（比如宝塔的 docker 页），
        // 这时再点同一个常用链接，按地址匹配就对不上了，会白白多开一个标签。
        var tab = link != null
            ? Tabs.FirstOrDefault(t => t.Link != null && t.Link.Id == link.Id)
            : null;

        tab ??= Tabs.FirstOrDefault(t => string.Equals(t.Url, url, StringComparison.OrdinalIgnoreCase));

        if (tab == null)
        {
            tab = new BrowserTab
            {
                Url = url,
                Link = link,
                Title = link?.Name ?? string.Empty
            };

            Tabs.Add(tab);
        }
        else if (link != null)
        {
            tab.Link = link;
        }

        ActivateTab(tab);
    }

    /// <summary>
    /// 切到某个标签：更新选中态、地址栏，并让 WebView2 导航过去
    /// </summary>
    private void ActivateTab(BrowserTab tab)
    {
        foreach (var item in Tabs)
        {
            item.IsActive = ReferenceEquals(item, tab);
        }

        UrlTextBox.Text = tab.Url;
        _autoFillAttempted = false;

        if (!_webViewInitialized)
        {
            // WebView2 还没建起来（本页第一次真正可见时才会建），先记下来
            _pendingTab = tab;
            StatusTextBlock.Text = $"正在准备内置浏览器，稍后打开 {tab.Url}";
            return;
        }

        // 已经停在这个地址上就别再导航一次（重复点当前标签会白刷一遍页面）
        if (IsAlreadyAt(tab.Url))
        {
            return;
        }

        NavigateTo(tab.Url);
    }

    /// <summary>
    /// 内置浏览器当前是不是已经在这个地址上
    /// </summary>
    private bool IsAlreadyAt(string url)
    {
        try
        {
            var current = TryGetCoreWebView2()?.Source;
            return !string.IsNullOrEmpty(current)
                   && string.Equals(current, url, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Tab_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: BrowserTab tab } && !tab.IsActive)
        {
            ActivateTab(tab);
        }
    }

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BrowserTab tab })
        {
            return;
        }

        // 关掉标签只是不再显示它，访问历史照样留着
        var index = Tabs.IndexOf(tab);
        var wasActive = tab.IsActive;
        Tabs.Remove(tab);

        if (!wasActive)
        {
            return;
        }

        if (Tabs.Count == 0)
        {
            UrlTextBox.Text = string.Empty;
            StatusTextBlock.Text = "所有标签都关掉了。用「打开…」或地址栏继续。";
            return;
        }

        // 切到右边那个；关的是最后一个就切左边的
        ActivateTab(Tabs[Math.Min(index, Tabs.Count - 1)]);
    }

    /// <summary>
    /// 标签宽度自适应：页面越多越窄，到下限为止（再多的靠横向滚动）。
    /// </summary>
    private void RefreshTabWidths()
    {
        var width = BrowserTab.ComputeWidth(TabStripHost.ActualWidth, Tabs.Count);

        foreach (var tab in Tabs)
        {
            tab.Width = width;
        }
    }

    private void TabStripHost_SizeChanged(object sender, SizeChangedEventArgs e)
        => RefreshTabWidths();

    // ───────────────────────── 导航 ─────────────────────────

    private void NavigateButton_Click(object sender, RoutedEventArgs e)
    {
        var url = LinkItem.NormalizeUrl(UrlTextBox.Text);
        if (url.Length == 0)
        {
            StatusTextBlock.Text = "请输入要打开的管理后台地址。";
            return;
        }

        // 和浏览器一致：地址栏回车是在**当前标签**里跳转；还没有标签就开一个
        if (ActiveTab is { } tab)
        {
            tab.Url = url;
            tab.Title = string.Empty;
            ActivateTab(tab);
            return;
        }

        OpenInTab(null, url);
    }

    /// <summary>
    /// 导航到指定地址（自动补齐协议头）
    /// </summary>
    private void NavigateTo(string? rawUrl)
    {
        var url = LinkItem.NormalizeUrl(rawUrl);
        if (url.Length == 0)
        {
            StatusTextBlock.Text = "请输入要打开的管理后台地址。";
            return;
        }

        if (TryGetCoreWebView2() == null)
        {
            StatusTextBlock.Text = "WebView2 尚未就绪，请稍候重试。";
            return;
        }

        try
        {
            UrlTextBox.Text = url;
            _autoFillAttempted = false;
            ConsoleWebView.Source = new Uri(url);
            StatusTextBlock.Text = $"正在打开 {url}";
        }
        catch (UriFormatException)
        {
            StatusTextBlock.Text = $"地址格式不正确：{url}";
        }
    }

    /// <summary>
    /// 「打开…」：展开 / 收起近期访问列表
    /// </summary>
    private async void OpenPanelToggle_Click(object sender, RoutedEventArgs e)
    {
        // 下拉窗里的「打开…」是菜单按钮（无选中态），按面板当前可见性翻转
        var show = RecentPanel.Visibility != Visibility.Visible;
        RecentPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (show)
        {
            await LoadRecentAsync();
        }
    }

    /// <summary>
    /// 从近期访问列表里挑一条：在新标签里打开
    /// </summary>
    private async void RecentItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RecentEntry entry })
        {
            return;
        }

        RecentPanel.Visibility = Visibility.Collapsed;

        OpenInTab(entry.Link, entry.Link?.EffectiveUrl ?? entry.Url);

        if (entry.Link != null)
        {
            await ConfigService.Instance.MarkLinkOpenedAsync(entry.Link);
            await LoadRecentAsync();
        }
    }

    /// <summary>
    /// 导航完成：刷新当前标签的标题、记录访问历史、刷新视口提示，
    /// 宝塔面板链接还会自动尝试填充账号密码
    /// </summary>
    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            return;
        }

        await ReportViewportAsync();
        await RecordCurrentVisitAsync();

        var link = CurrentLink;

        // 这里**不再要求** link.HasCredential：链接可能只有账号、密码在密码箱里，
        // 到底有没有东西可填交给 ResolveCredentialAsync 判定。
        // （原条件要求 HasCredential，看似等价，实则会把"链接只有账号"这种
        //   最常见的半残状态直接排除掉，宝塔自动填充因此从来不填密码。）
        if (_autoFillAttempted || link == null || link.Kind != LinkKind.BtPanel)
        {
            return;
        }

        _autoFillAttempted = true;

        // 给前端框架一点渲染登录框的时间
        await Task.Delay(600);
        await AutofillAsync(silent: false);
    }

    /// <summary>
    /// 把当前页面记进访问历史，同时把标题 / 地址同步到当前标签上
    /// </summary>
    private async Task RecordCurrentVisitAsync()
    {
        var core = TryGetCoreWebView2();
        if (core == null)
        {
            return;
        }

        string url;
        string title;
        try
        {
            url = core.Source;
            title = core.DocumentTitle ?? string.Empty;
        }
        catch (Exception)
        {
            // 页面正在销毁时读取源地址可能抛异常，静默跳过
            return;
        }

        // 标签跟着页面走：页内跳转后，标签上的标题与地址也要更新
        if (ActiveTab is { } tab)
        {
            ApplyTabLocation(tab, url, title);
        }

        var recorded = await VisitHistoryService.Instance.RecordAsync(url, title, CurrentLink);
        if (recorded != null)
        {
            await LoadRecentAsync();
        }
    }

    /// <summary>
    /// 把页面当前的地址 / 标题写回标签（页内路由变化也要跟得上）。
    /// <para>
    /// ⚠️ 这个方法是「宝塔切走再切回来会回到首页」那个 bug 的修复核心：
    /// WebView2 的 <c>NavigationCompleted</c> **只在整页加载时触发**，而宝塔这类 SPA
    /// 用 <c>history.pushState</c> 换路由（比如从首页进到 docker 页）时不会触发它，
    /// 于是标签里存的地址一直停在最初的入口地址 —— 切回来重新导航就回了首页。
    /// 所以除了 NavigationCompleted，还要靠 <c>SourceChanged</c> / <c>HistoryChanged</c>
    /// 把地址同步过来（见 SyncActiveTabLocation）。
    /// </para>
    /// </summary>
    /// <returns>标签地址是否真的发生了变化</returns>
    internal static bool ApplyTabLocation(BrowserTab tab, string? url, string? title)
    {
        var changed = false;

        if (!string.IsNullOrWhiteSpace(url)
            && !url.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(tab.Url, url, StringComparison.Ordinal))
        {
            tab.Url = url;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(title)
            && !string.Equals(tab.Title, title, StringComparison.Ordinal))
        {
            tab.Title = title;
        }

        return changed;
    }

    /// <summary>
    /// 页内路由变化（SPA）时把当前标签的地址 / 标题同步过来
    /// </summary>
    private void SyncActiveTabLocation()
    {
        var core = TryGetCoreWebView2();

        if (core == null || ActiveTab is not { } tab)
        {
            return;
        }

        try
        {
            if (ApplyTabLocation(tab, core.Source, core.DocumentTitle))
            {
                // 地址栏也跟着当前标签走，否则页内跳转后地址栏还停在入口地址
                UrlTextBox.Text = tab.Url;
                StatusTextBlock.Text = $"标签已更新为 {tab.DisplayTitle}（页内跳转）";
            }
        }
        catch (Exception)
        {
            // 页面正在销毁时读取 Source 可能抛异常，静默跳过
        }
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
        => SyncActiveTabLocation();

    private void OnHistoryChanged(object? sender, object e)
        => SyncActiveTabLocation();

    private void OnDocumentTitleChanged(object? sender, object e)
        => SyncActiveTabLocation();

    // ───────────────────────── 近期访问列表 ─────────────────────────

    /// <summary>
    /// 组装「近期访问」列表：本次会话打开的页面 → 最常用的收藏 → 最近的历史
    /// </summary>
    private async Task LoadRecentAsync()
    {
        var links = await ConfigService.Instance.LoadLinksAsync();
        var history = await ConfigService.Instance.LoadHistoryAsync();

        var entries = new List<RecentEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 本次打开应用后访问过的页面（最近在前）
        foreach (var visit in VisitHistoryService.Instance.SessionVisits.Take(8))
        {
            if (seen.Add(visit.Url))
            {
                entries.Add(RecentEntry.FromVisit(visit, session: true));
            }
        }

        // 2. 已保存的常用链接里最常用的几条
        var topLinks = links
            .Where(l => l.EffectiveUrl.Length > 0)
            .OrderByDescending(l => l.IsPinned)
            .ThenByDescending(l => l.OpenCount)
            .ThenByDescending(l => l.LastOpenedAt ?? DateTime.MinValue)
            .ThenBy(l => l.Name, StringComparer.CurrentCulture)
            .Take(6)
            .ToList();

        foreach (var link in topLinks)
        {
            if (seen.Add(link.EffectiveUrl))
            {
                entries.Add(RecentEntry.FromLink(link));
            }
        }

        // 3. 最近的历史记录补足
        foreach (var record in history)
        {
            if (entries.Count >= 18)
            {
                break;
            }

            if (seen.Add(record.Url))
            {
                entries.Add(RecentEntry.FromVisit(record, session: false));
            }
        }

        RecentList.ItemsSource = entries;
        RecentEmptyHint.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task InitializeWebView2Async()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
            await ConsoleWebView.EnsureCoreWebView2Async(environment);

            var core = TryGetCoreWebView2();

            if (core == null)
            {
                // 极罕见：Ensure 之后仍取不到（浏览器进程刚起来就退了）
                StatusTextBlock.Text = "内置浏览器初始化后不可用，请点「打开」重试。";
                return;
            }

            // 浏览器进程真正挂掉时（内存不足、目标页面自身崩溃、GPU 驱动异常都会导致）
            // CoreWebView2 的 getter 会直接抛异常而不是返回 null，单靠 == null 判断拦不住。
            // 订阅它，才能第一时间把状态说清楚，而不是等用户点哪里再莫名其妙报错。
            core.ProcessFailed += OnBrowserProcessFailed;

            // 捕获脚本要靠 WebMessage 把登录信息回传给宿主
            core.Settings.IsWebMessageEnabled = true;

            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationCompleted += OnNavigationCompleted;

            // SPA（宝塔这类）页内换路由不触发 NavigationCompleted，只触发这几个事件，
            // 不订阅的话标签地址会停在入口地址，切走再切回来就回到首页
            core.SourceChanged += OnSourceChanged;
            core.HistoryChanged += OnHistoryChanged;
            core.DocumentTitleChanged += OnDocumentTitleChanged;

            // 每次新文档创建时注入，页面内部跳转同样生效
            await core.AddScriptToExecuteOnDocumentCreatedAsync(CredentialCaptureScript);

            _webViewInitialized = true;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"WebView2 初始化失败：{ex.Message}（请确认已安装 WebView2 Runtime）";
        }
    }

    /// <summary>
    /// WebView2 浏览器进程崩溃。
    /// <para>
    /// 这在实际使用中是会发生的（内存紧张、访问到自身会崩的页面、GPU 驱动异常），
    /// 崩溃之后 <c>CoreWebView2</c> 的 getter 会抛 InvalidOperationException
    /// 而不是返回 null —— 如果不管，用户接下来点标签页、点「打开」都会直接报错，
    /// 在加上全局兜底之前甚至是整个应用退出。所以这里记下状态并说明白。
    /// </para>
    /// </summary>
    private void OnBrowserProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _browserProcessFailed = true;

        // 刻意不在这里把 _webViewInitialized 复位去"自动重连"：
        // 重新初始化会给同一个 core 再订阅一遍事件（WebMessageReceived 等），
        // 事件被触发两次反而更难查。宁可给一句诚实的提示。
        StatusTextBlock.Text =
            $"内置浏览器进程已结束（{e.ProcessFailedKind}），当前页面无法继续。请重启应用后再试。";
    }

    /// <summary>
    /// 安全地取 <c>CoreWebView2</c>：拿不到（含浏览器进程已崩溃）时返回 null。
    /// <para>
    /// ⚠️ 浏览器进程崩溃后，这个 getter **不会返回 null，而是直接抛异常**
    /// （<c>InvalidOperationException: The WebView control is no longer valid because
    /// the browser process crashed</c>）。代码里散落的 <c>CoreWebView2 == null</c>
    /// 判断对这种情况完全无效，异常会从事件处理器一路逃出去。
    /// 所以统一走这里取，崩了就当成"没有可用的 WebView2"，让调用方走既有的
    /// "尚未就绪" 分支，最差只是提示一句，不会崩。
    /// </para>
    /// </summary>
    private CoreWebView2? TryGetCoreWebView2()
    {
        // 已知崩溃就别再反复去碰它 —— 每次访问都会现抛一个异常，白付开销
        if (_browserProcessFailed)
        {
            return null;
        }

        try
        {
            return ConsoleWebView.CoreWebView2;
        }
        catch (Exception)
        {
            // 浏览器进程已崩溃
            _browserProcessFailed = true;
            return null;
        }
    }

    // ───────────────────────── 缩放 / 适应宽度 ─────────────────────────

    private async void ZoomComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingZoom || sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var text = item.Content?.ToString() ?? string.Empty;

        if (text == "适应宽度")
        {
            await FitWidthAsync();
            return;
        }

        if (text.EndsWith('%') && double.TryParse(text[..^1], out var percent))
        {
            ApplyZoom(percent / 100.0);
        }
    }

    private void ApplyZoom(double factor)
    {
        if (TryGetCoreWebView2() == null)
        {
            return;
        }

        ConsoleWebView.ZoomFactor = Math.Clamp(factor, 0.25, 5.0);
        StatusTextBlock.Text = $"缩放已设为 {ConsoleWebView.ZoomFactor * 100:F0}%";
    }

    private async void FitWidthButton_Click(object sender, RoutedEventArgs e)
    {
        var combo = sender as ComboBox ?? _toolsDropDown?.ZoomComboBox;

        if (combo != null)
        {
            _syncingZoom = true;
            combo.SelectedIndex = 0; // 适应宽度
            _syncingZoom = false;
        }

        await FitWidthAsync();
    }

    /// <summary>
    /// 按「页面内容宽度 / 当前视口宽度」重新计算缩放，让网页横向铺满可视区域。
    /// <para>
    /// 之所以需要它：高 DPI 下 WebView2 的视口可能与控件尺寸不一致（网页被放大到只能看到一角），
    /// 这时手动适配一次即可正常浏览。
    /// </para>
    /// </summary>
    private async Task FitWidthAsync()
    {
        if (TryGetCoreWebView2() == null)
        {
            StatusTextBlock.Text = "页面尚未就绪，请先打开地址。";
            return;
        }

        var metrics = await GetViewportMetricsAsync();
        if (metrics is not { } m || m.Width <= 0 || m.ScrollWidth <= 0)
        {
            StatusTextBlock.Text = "读不到页面尺寸，无法自动适配，可手动选择缩放比例。";
            return;
        }

        var current = ConsoleWebView.ZoomFactor;
        var target = Math.Clamp(current * m.Width / (double)m.ScrollWidth, 0.25, 2.0);
        ConsoleWebView.ZoomFactor = target;

        StatusTextBlock.Text = $"已按宽度自适应：{target * 100:F0}%（内容宽 {m.ScrollWidth}px / 视口 {m.Width}px）";
        await ReportViewportAsync();
    }

    /// <summary>
    /// 把当前缩放与「页面视口 / 控件尺寸」写进提示栏，便于判断是否发生了缩放异常
    /// </summary>
    private async Task ReportViewportAsync()
    {
        if (TryGetCoreWebView2() == null)
        {
            return;
        }

        var metrics = await GetViewportMetricsAsync();
        if (metrics is not { } m)
        {
            return;
        }

        StatusTextBlock.Text =
            $"缩放 {ConsoleWebView.ZoomFactor * 100:F0}%"
            + $" · 页面视口 {m.Width}×{m.Height} px"
            + $" · 控件 {ConsoleWebView.ActualWidth:F0}×{ConsoleWebView.ActualHeight:F0} DIP";
    }

    /// <summary>
    /// 读取页面视口（CSS px）与内容宽度
    /// </summary>
    private async Task<(int Width, int Height, int ScrollWidth)?> GetViewportMetricsAsync()
    {
        var core = TryGetCoreWebView2();
        if (core == null)
        {
            return null;
        }

        try
        {
            var raw = await core.ExecuteScriptAsync(
                "JSON.stringify({w: document.documentElement.clientWidth," +
                "h: document.documentElement.clientHeight," +
                "sw: document.documentElement.scrollWidth})");

            // ExecuteScriptAsync 返回的是「JSON 编码后的字符串」，需要解两层
            var inner = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrEmpty(inner))
            {
                return null;
            }

            using var document = JsonDocument.Parse(inner);
            var root = document.RootElement;
            return (root.GetProperty("w").GetInt32(),
                    root.GetProperty("h").GetInt32(),
                    root.GetProperty("sw").GetInt32());
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ───────────────────────── 账号密码填充 ─────────────────────────

    /// <summary>
    /// ☰ 工具下拉窗（懒创建；缩放框与视觉辅助开关都在它里面）
    /// </summary>
    private ToolsDropDownWindow? _toolsDropDown;

    /// <summary>
    /// 下拉窗刚因失焦关闭的时间点：防止「点 ☰ 关窗」的那次点击又被当成「点 ☰ 开窗」
    /// </summary>
    private DateTime _toolsDropDownClosedAt;

    /// <summary>
    /// ☰ 三条线：弹出 / 收起竖向工具下拉窗。
    /// 下拉是独立顶层窗口（WebView2 会盖住窗口内浮层），完全不挤压网页布局。
    /// </summary>
    private void ToolsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ToolsToggle.IsChecked != true)
        {
            CloseToolsDropDown();
            return;
        }

        // 刚因失焦关掉（比如用户点的就是这个 ☰），这次点击只负责关，别立刻又弹开
        if ((DateTime.Now - _toolsDropDownClosedAt).TotalMilliseconds < 250)
        {
            ToolsToggle.IsChecked = false;
            return;
        }

        EnsureToolsDropDown();
        _toolsDropDown!.Left = 0;
        _toolsDropDown.Top = 0;
        _toolsDropDown.Show();
        _toolsDropDown.UpdateLayout();

        // 锚点：☰ 按钮的右下角（先拿屏幕设备像素，再转回 DIP 定位）
        var anchorDevice = ToolsToggle.PointToScreen(new Point(0, ToolsToggle.ActualHeight));
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget;
        var anchor = transform != null
            ? transform.TransformFromDevice.Transform(anchorDevice)
            : anchorDevice;

        _toolsDropDown.Left = Math.Max(8, anchor.X + ToolsToggle.ActualWidth - _toolsDropDown.ActualWidth);
        _toolsDropDown.Top = anchor.Y + 4;
        _toolsDropDown.Activate();
    }

    internal void CloseToolsDropDown()
    {
        _toolsDropDown?.Hide();
        if (ToolsToggle.IsChecked == true)
        {
            ToolsToggle.IsChecked = false;
        }
    }

    /// <summary>
    /// 下拉窗自己要求关闭（失焦 / Esc / 菜单项已执行）
    /// </summary>
    internal void NotifyToolsDropDownClosed()
    {
        _toolsDropDownClosedAt = DateTime.Now;
        CloseToolsDropDown();
    }

    private void EnsureToolsDropDown()
    {
        if (_toolsDropDown != null)
        {
            return;
        }

        _toolsDropDown = new ToolsDropDownWindow(this);
        _toolsDropDown.ZoomComboBox.SelectionChanged += ZoomComboBox_SelectionChanged;
        _toolsDropDown.FillAidToggle.Checked += FillAidToggle_Toggled;
        _toolsDropDown.FillAidToggle.Unchecked += FillAidToggle_Toggled;

        // 兜底：窗口无论被谁、以何种方式真正关闭（代码 Close、用户 Alt+F4），
        // 都必须把缓存引用清掉 —— 已关闭的 Window 不能再 Show()，
        // 否则抛 InvalidOperationException 并让整个应用退出（2026-09-25 实际发生过）。
        // 清了引用，下次点 ☰ 会正常重建一个新窗口，最坏只是丢一次弹出位置。
        _toolsDropDown.Closed += OnToolsDropDownClosed;

        _syncingZoom = true;
        _toolsDropDown.ZoomComboBox.SelectedIndex = 3; // 100%
        _syncingZoom = false;

        _loadingAidToggle = true;
        _toolsDropDown.FillAidToggle.IsChecked = _fillAidEnabled;
        _loadingAidToggle = false;
    }

    /// <summary>
    /// 下拉窗被真正关闭时清掉缓存引用（见 EnsureToolsDropDown 的说明）
    /// </summary>
    private void OnToolsDropDownClosed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _toolsDropDown))
        {
            _toolsDropDown = null;
        }
    }

    // ── 供下拉窗调用的动作入口（菜单项点一下 = 执行一次对应按钮的逻辑）──

    internal void InvokeAutofill() => AutofillButton_Click(this, new RoutedEventArgs());

    // 这里刻意不传 _toolsDropDown 里的控件：OpenPanelToggle_Click 用不到 sender，
    // 而依赖 _toolsDropDown 非空会在窗口刚被关闭时变成空引用。
    internal void InvokeOpenRecent() => OpenPanelToggle_Click(this, new RoutedEventArgs());

    internal void InvokeBookmark() => BookmarkCurrentButton_Click(this, new RoutedEventArgs());

    internal void InvokeFitWidth() => FitWidthButton_Click(this, new RoutedEventArgs());

    /// <summary>
    /// 网页视觉辅助开关变化（来自 ☰ 下拉窗）：即时生效并写入设置持久化
    /// </summary>
    internal async void ApplyFillAidFromUi(bool enabled)
    {
        if (_loadingAidToggle)
        {
            return;
        }

        _fillAidEnabled = enabled;

        try
        {
            var settings = await ConfigService.Instance.LoadSettingsAsync();
            settings.FillHighlightAid = _fillAidEnabled;
            await ConfigService.Instance.SaveSettingsAsync(settings);

            StatusTextBlock.Text = _fillAidEnabled
                ? "视觉辅助已开启：自动填充后会在网页里高亮被填的输入框。"
                : "视觉辅助已关闭：自动填充后不再有高亮提示。";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"保存设置失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 视觉辅助开关的 UI 事件入口（下拉窗里的 CheckBox）
    /// </summary>
    private void FillAidToggle_Toggled(object sender, RoutedEventArgs e)
    {
        ApplyFillAidFromUi((sender as CheckBox)?.IsChecked == true);
    }

    private async void AutofillButton_Click(object sender, RoutedEventArgs e)
    {
        var credential = await ResolveCredentialAsync();

        if (credential is not { } c)
        {
            StatusTextBlock.Text = "当前页面没有关联的账号密码，密码箱里也没找到该站点的记录。可先用「收藏当前页」记下这条地址。";
            return;
        }

        await FillAsync(c.Username, c.Password, c.Source, silent: false);
    }

    private async Task AutofillAsync(bool silent)
    {
        var credential = await ResolveCredentialAsync();

        if (credential is not { } c)
        {
            return;
        }

        await FillAsync(c.Username, c.Password, c.Source, silent);
    }

    /// <summary>
    /// 算出当前页面该填的账号密码：以关联链接为主，**缺的字段用密码箱里同站点的记录补齐**。
    /// <para>
    /// ⚠️ 为什么必须"补齐"而不是"二选一"（2026-09-25 修的 bug）：
    /// 常用链接与密码箱是两处独立存储。实际数据里就存在这种情况 ——
    /// 链接是很早建的、只填了账号（密码留空），后来在页面里手动登录时，
    /// 密码被捕获脚本抓进了密码箱。原实现是"链接只要 HasCredential 就整条只用链接"，
    /// 于是密码恒为空串，填充脚本里 <c>if (!value) { return 0; }</c> 直接跳过密码框 ——
    /// 用户看到的现象就是"填充密码失败"，而密码箱里明明躺着密码。
    /// </para>
    /// </summary>
    /// <returns>可填的凭据与来源说明；两处都拿不到时返回 null</returns>
    private async Task<(string Username, string Password, string Source)?> ResolveCredentialAsync()
    {
        var link = CurrentLink;

        // 读 Source 在页面正在销毁时会抛 COM 异常。本方法在 FillAsync 之前执行，
        // 而它的调用方是 async void 事件处理器，异常漏出去就是整个应用退出，所以必须兜住。
        string? currentUrl = null;
        try
        {
            currentUrl = TryGetCoreWebView2()?.Source;
        }
        catch (Exception)
        {
            // 拿不到当前地址就当没有站点可匹配，只走链接那一路
        }

        var vaultItem = await FindVaultItemForSiteAsync(currentUrl);

        var (username, password, usedVault) = MergeCredential(
            link?.Username, link?.Password, vaultItem?.Username, vaultItem?.Password);

        if (string.IsNullOrWhiteSpace(username) && string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        string source;
        if (link != null && usedVault && vaultItem != null)
        {
            source = $"链接「{link.Name}」+ 密码箱「{vaultItem.Title}」";
        }
        else if (link != null)
        {
            source = $"链接「{link.Name}」";
        }
        else if (vaultItem != null)
        {
            source = $"密码箱「{vaultItem.Title}」";
        }
        else
        {
            source = "当前页面";
        }

        return (username, password, source);
    }

    /// <summary>
    /// 合并「链接」与「密码箱」两处凭据：**链接缺哪个字段，就用密码箱补哪个字段**。
    /// <para>
    /// 刻意抽成不碰 UI、不碰 IO 的纯函数，是为了能被探针单独验证。
    /// 这段"缺什么补什么"的规则正是 2026-09-25 那个"密码填不上"缺陷的核心：
    /// 错就错在原实现把两处当成互斥的二选一（链接有账号就整条只用链接）。
    /// </para>
    /// </summary>
    /// <returns>合并后的账号密码，以及是否真的用到了密码箱</returns>
    internal static (string Username, string Password, bool UsedVault) MergeCredential(
        string? linkUsername, string? linkPassword,
        string? vaultUsername, string? vaultPassword)
    {
        var username = linkUsername ?? string.Empty;
        var password = linkPassword ?? string.Empty;
        var usedVault = false;

        if (string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(vaultUsername))
        {
            username = vaultUsername;
            usedVault = true;
        }

        if (string.IsNullOrWhiteSpace(password) && !string.IsNullOrWhiteSpace(vaultPassword))
        {
            password = vaultPassword;
            usedVault = true;
        }

        return (username, password, usedVault);
    }

    /// <summary>
    /// 把账号密码填进页面登录框；「网页视觉辅助」开着时顺带高亮被填的输入框
    /// </summary>
    private async Task FillAsync(string? username, string? password, string sourceLabel, bool silent)
    {
        var core = TryGetCoreWebView2();
        if (core == null)
        {
            return;
        }

        var script = AutofillFunction
                     + "(" + JsonSerializer.Serialize(username ?? string.Empty)
                     + ", " + JsonSerializer.Serialize(password ?? string.Empty)
                     + ", " + (_fillAidEnabled ? "true" : "false") + ");";

        try
        {
            var result = await core.ExecuteScriptAsync(script);
            var filled = (result ?? "0").Trim('"');

            if (!silent || filled != "0")
            {
                StatusTextBlock.Text = filled == "0"
                    ? "没有找到可填充的账号 / 密码输入框，可改用「复制登录信息」手动粘贴。"
                    : $"已填充 {filled} 个输入框（来源：{sourceLabel}）"
                      + (_fillAidEnabled ? "，已在页面上高亮标出。" : "。");
            }
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"填充失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 按站点在密码箱里找匹配记录（地址同一站点即视为匹配）
    /// </summary>
    private static async Task<VaultItem?> FindVaultItemForSiteAsync(string? url)
    {
        var origin = UrlHelper.GetOrigin(url);
        if (origin.Length == 0)
        {
            return null;
        }

        var items = await ConfigService.Instance.LoadVaultAsync();

        return items.FirstOrDefault(i =>
            string.Equals(UrlHelper.GetOrigin(i.Url), origin, StringComparison.OrdinalIgnoreCase)
            && i.HasCredential);
    }

    // ───────────────────────── 登录信息捕获 ─────────────────────────

    /// <summary>
    /// 页面里手动登录后，捕获脚本会把账号密码回传到这里，由宿主决定是否入库
    /// </summary>
    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!_captureEnabled)
        {
            return;
        }

        // TryGetWebMessageAsString 会把消息当字符串取回，不是字符串的消息会抛异常
        string raw;
        try
        {
            raw = e.TryGetWebMessageAsString();
        }
        catch (Exception)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        CredentialMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<CredentialMessage>(raw, MessageJsonOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (message == null
            || !string.Equals(message.Type, "credential", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(message.Password))
        {
            return;
        }

        // 一律以 WebView2 报告的真实来源为准，不采信页面自己填的地址
        var source = (string.IsNullOrWhiteSpace(e.Source) ? message.Url : e.Source) ?? string.Empty;
        var origin = UrlHelper.GetOrigin(source);
        if (origin.Length == 0)
        {
            return;
        }

        var username = (message.Username ?? string.Empty).Trim();
        var key = $"{origin}|{username}";

        if (IgnoredCredentialOrigins.Contains(origin) || !_promptedCredentials.Add(key))
        {
            return;
        }

        // 密码箱里已经有这个站点的记录就不再打扰
        var existing = await FindVaultItemForSiteAsync(source);
        if (existing != null)
        {
            StatusTextBlock.Text = $"密码箱里已有「{existing.Title}」，未重复提示保存。";
            return;
        }

        var host = UrlHelper.GetHost(source);

        var draft = new VaultItem
        {
            Title = string.IsNullOrWhiteSpace(message.Title) ? host : message.Title.Trim(),
            Category = host,
            Username = username,
            Password = message.Password,
            Url = source
        };

        // 用独立窗口而不是 ContentDialog：WebView2 是原生子窗口，
        // 窗口内的浮层会被网页盖住，只有独立窗口能稳定压在网页之上
        var result = CredentialCaptureWindow.ShowCapture(
            Window.GetWindow(this),
            draft,
            host.Length > 0 ? host : origin);

        switch (result.Choice)
        {
            case CredentialCaptureChoice.Save:
                if (result.Item is { } saved)
                {
                    await ConfigService.Instance.SaveVaultItemAsync(saved);
                    StatusTextBlock.Text = $"已把「{saved.Title}」保存到密码箱，下次可用「填充账号密码」一键填入。";
                }
                break;

            case CredentialCaptureChoice.IgnoreSite:
                IgnoredCredentialOrigins.Add(origin);
                StatusTextBlock.Text = $"已忽略 {origin}，本次运行不再提示保存密码。";
                break;

            default:
                // 选「稍后」：撤掉去重标记，下次登录还能再问一次
                _promptedCredentials.Remove(key);
                break;
        }
    }

    /// <summary>
    /// 捕获脚本回传的消息体
    /// </summary>
    private sealed class CredentialMessage
    {
        public string? Type { get; set; }
        public string? Url { get; set; }
        public string? Title { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
    }

    // ───────────────────────── 收藏当前页 ─────────────────────────

    private async void BookmarkCurrentButton_Click(object sender, RoutedEventArgs e)
    {
        var core = TryGetCoreWebView2();
        var currentUrl = core?.Source ?? UrlTextBox.Text;

        if (string.IsNullOrWhiteSpace(currentUrl) || currentUrl == "about:blank")
        {
            StatusTextBlock.Text = "当前还没有打开任何页面，无法收藏。";
            return;
        }

        // 直接收藏，不弹编辑框：这里不弹对话框，是因为内置浏览器是原生子窗口，
        // 窗口内的浮层会被网页盖住；改名 / 补账号密码到「常用链接」页处理即可
        var draft = new LinkItem
        {
            Name = core?.DocumentTitle is { Length: > 0 } title && title != "about:blank"
                ? title
                : currentUrl,
            Url = currentUrl,
            Kind = LinkKind.WebPage
        };

        await ConfigService.Instance.SaveLinkAsync(draft);

        if (ActiveTab is { } tab)
        {
            tab.Link = draft;
        }

        await LoadRecentAsync();

        StatusTextBlock.Text = $"已把「{draft.Name}」加入常用链接。需要改名称或补账号密码，到「常用链接」页编辑即可。";
    }

    // ───────────────────────── Cookie 注入 ─────────────────────────

    /// <summary>
    /// 注入 Cookie（适用于基于 Session/Cookie 的免密登录）
    /// </summary>
    public async Task InjectCookieAsync(string name, string value, string domain)
    {
        var core = TryGetCoreWebView2();
        if (core == null)
        {
            return;
        }

        var cookie = core.CookieManager.CreateCookie(name, value, domain, "/");
        core.CookieManager.AddOrUpdateCookie(cookie);
        await Task.CompletedTask;
    }
}
