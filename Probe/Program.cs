using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows.Threading;
using ServerOpsHelper;
using ServerOpsHelper.Frp;
using ServerOpsHelper.Mcp;
using ServerOpsHelper.Models;
using ServerOpsHelper.Services;
using ServerOpsHelper.ViewModels;
using ServerOpsHelper.Views;

namespace Probe;

/// <summary>
/// OpsHelper 验证探针。参见 Probe.csproj 顶部的说明。
/// <para>
/// 输出同时写到 %TEMP%\probe_result.log（UTF-8），避免控制台代码页把中文糊掉。
/// </para>
/// </summary>
internal static class Program
{
    private static readonly List<string> Lines = new();
    private static int _failures;

    private static string IsolatedConfigDir => Path.Combine(Path.GetTempPath(), "opshelper-probe-config");

    [STAThread]
    private static int Main(string[] args)
    {
        // 日志清空重来：Log() 是追加写（为了超时也能看到进度），不清会混进上一轮的内容
        try
        {
            File.WriteAllText(ProbeLogPath, string.Empty);
        }
        catch (Exception)
        {
            // 清不掉就让后面追加，不影响探针本身
        }

        // ── 铁律：探针只许在隔离的数据目录里跑 ──
        var real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ServerOpsHelper");

        if (Directory.Exists(IsolatedConfigDir))
        {
            Directory.Delete(IsolatedConfigDir, true);
        }

        Directory.CreateDirectory(IsolatedConfigDir);
        Environment.SetEnvironmentVariable("SERVEROPSHELPER_CONFIG_DIR", IsolatedConfigDir);

        // 只渲染界面截图、不做检查：改完 UI 自己先看一眼用
        if (args.Contains("--shot-settings"))
        {
            return RenderSettingsShots();
        }

        if (args.Contains("--shot-detail"))
        {
            return RenderServerDetailShot();
        }

        if (args.Contains("--shot-frp"))
        {
            return RenderFrpShots();
        }

        if (string.Equals(
                Path.GetFullPath(IsolatedConfigDir),
                Path.GetFullPath(real),
                StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("FAIL 探针拒绝在真实数据目录里运行");
            return 2;
        }

        Log($"[probe] 配置目录已隔离到 {IsolatedConfigDir}");
        Log($"[probe] 用户真实目录（只读都不写）：{real}");

        try
        {
            SeedConfig();
            RunXamlLoadChecks();
            RunVaultEditRoundTripChecks();
            RunCredentialMergeChecks();
            RunRealVaultReadOnlyDiagnosis();
            RunFrpChecks();
            // 扩展检查会临时造扩展再删干净，必须排在 MCP 之前（MCP 断言"只有 10 个工具"）
            RunExtensionChecks();
            RunExtensionInstallChecks();
            // MCP 检查必须在 RunConsoleChecks 之前：后者结尾会 InvokeShutdown 掉 Dispatcher，
            // 之后再也跑不了消息循环（设置页的 async Loaded 需要它）
            RunMcpChecks();
            RunConsoleChecks();
        }
        catch (Exception ex)
        {
            Fail("探针整体崩了", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(IsolatedConfigDir))
                {
                    Directory.Delete(IsolatedConfigDir, true);
                }

                Log("[probe] 隔离目录已清理");
            }
            catch (Exception ex)
            {
                Log($"[warn] 隔离目录清理失败：{ex.Message}");
            }

            // 结论先落进日志，再把日志写盘 —— 否则日志里看不到最终结论行
            Log(_failures == 0 ? "=== PROBE OK ===" : $"=== {_failures} PROBE FAILED ===");

            var path = Path.Combine(Path.GetTempPath(), "probe_result.log");
            File.WriteAllText(path, string.Join(Environment.NewLine, Lines), Encoding.UTF8);
            Console.WriteLine($"结果已写入 {path}");
        }

        Console.WriteLine(_failures == 0 ? "=== PROBE OK ===" : $"=== {_failures} PROBE FAILED ===");
        return _failures == 0 ? 0 : 1;
    }

    // ───────────────────────── 基础设施 ─────────────────────────

    /// <summary>探针日志的落盘路径。</summary>
    private static readonly string ProbeLogPath =
        Path.Combine(Path.GetTempPath(), "probe_result.log");

    /// <summary>
    /// 记一条日志：内存留一份（最后完整重写文件），同时**立即追加落盘**。
    /// <para>
    /// 为什么要边跑边写：2026-10-01 探针跑超时被强杀，而日志只在最后写盘，
    /// 结果一点线索都没留下（不知道卡在哪一条）。追加写之后，超时也能看出进度到哪。
    /// </para>
    /// </summary>
    private static void Log(string text)
    {
        Lines.Add(text);

        try
        {
            File.AppendAllText(ProbeLogPath, text + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception)
        {
            // 写日志失败不能反过来把探针弄挂
        }
    }

    private static void Check(string name, Func<bool> condition, string detail)
    {
        if (condition())
        {
            Log($"OK   {name}");
        }
        else
        {
            _failures++;
            Log($"FAIL {name} -> {detail}");
        }
    }

    private static void Fail(string name, string detail)
    {
        _failures++;
        Log($"FAIL {name} -> {detail}");
    }

    private static T? Field<T>(object target, string name) where T : class
        => target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?.GetValue(target) as T;

    private static void Call(object target, string method, params object?[] args)
        => target.GetType()
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .Invoke(target, args);

    private static void SeedConfig()
    {
        var links = new[]
        {
            new { Id = Guid.NewGuid(), Name = "宝塔", Url = "https://bt.example.com/entry", Kind = 1 },
            new { Id = Guid.NewGuid(), Name = "联维云", Url = "https://cloud.example.com/login", Kind = 0 },
        };

        var history = Enumerable.Range(0, 3).Select(i => new
        {
            Id = Guid.NewGuid(),
            Url = $"https://hist{i}.example.com/",
            Title = $"历史页 {i}",
            LinkId = (Guid?)null,
            LinkName = "",
            FirstVisitedAt = DateTime.Now.AddHours(-3 - i),
            LastVisitedAt = DateTime.Now.AddHours(-1 - i),
            VisitCount = i + 1,
        });

        File.WriteAllText(
            Path.Combine(IsolatedConfigDir, "links.json"),
            System.Text.Json.JsonSerializer.Serialize(links),
            Encoding.UTF8);

        File.WriteAllText(
            Path.Combine(IsolatedConfigDir, "history.json"),
            System.Text.Json.JsonSerializer.Serialize(history),
            Encoding.UTF8);

        File.WriteAllText(
            Path.Combine(IsolatedConfigDir, "settings.json"),
            """{"DefaultAdminUrl":"https://www.example.com","Theme":"Light","RecordVisitHistory":true,"CaptureCredentials":true,"FillHighlightAid":true}""",
            Encoding.UTF8);
    }

    // ───────────────────────── 一、XAML 能不能加载 ─────────────────────────

    private static void RunXamlLoadChecks()
    {
        var app = new App();
        app.InitializeComponent();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var targets = new (string Name, Func<object> Create)[]
        {
            ("LoginWindow", () => new LoginWindow()),
            ("MainWindow", () => new MainWindow()),
            ("ServerListPage", () => new ServerListPage()),
            ("ServerDetailPage", () => new ServerDetailPage(new ServerInfo())),
            ("BookmarksPage", () => new BookmarksPage()),
            ("HistoryPage", () => new HistoryPage()),
            ("VaultPage", () => new VaultPage()),
            ("SettingsPage", () => new SettingsPage()),
            ("WebConsolePage", () => new WebConsolePage()),
            // 二期的 SFTP 文件面板：虽然嵌在 ServerDetailPage 里会跟着构造，
            // 但它是独立功能区（自己的 XAML 与资源引用），单独登一条便于定位问题
            ("SftpFilePanel", () => new SftpFilePanel()),
            ("ServerMonitorPanel", () => new ServerMonitorPanel()),
            ("SecretBox", () => new ServerOpsHelper.Controls.SecretBox()),
            ("CredentialCaptureWindow", () => Activator.CreateInstance(
                typeof(CredentialCaptureWindow), nonPublic: true)!),
            ("TextPromptWindow", () => Activator.CreateInstance(
                typeof(TextPromptWindow), nonPublic: true)!),
            ("LinkEditDialog", () => Activator.CreateInstance(
                typeof(LinkEditDialog), nonPublic: true)!),
            ("VaultEditDialog", () => Activator.CreateInstance(
                typeof(VaultEditDialog), nonPublic: true)!),
            ("ServerEditDialog", () => Activator.CreateInstance(
                typeof(ServerEditDialog), nonPublic: true)!),
            // 扩展相关的两个窗口：参数输入是多控件动态生成的，最容易在解析期出错
            ("ExtensionRunDialog", () => new ExtensionRunDialog(
                new ExtensionPack { Id = "probe", Name = "探针" },
                new ExtensionCommand { Id = "x", Name = "x", Command = "echo 1" })),
            ("ExtensionResultWindow", () => new ExtensionResultWindow("探针", new RemoteCommandResult())),
            // FRP 页：面板多、控件多，XAML 里少个 name 都会在解析期炸
            ("FrpPage", () => new FrpPage()),
        };

        foreach (var (name, create) in targets)
        {
            try
            {
                create();
                Check($"XAML 能加载：{name}", () => true, "");
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                Fail($"XAML 能加载：{name}", $"{inner.GetType().Name}: {inner.Message}");
            }
        }

        // ☰ 下拉窗需要宿主页
        try
        {
            var host = new WebConsolePage();
            _ = new ToolsDropDownWindow(host);
            Check("XAML 能加载：ToolsDropDownWindow", () => true, "");
        }
        catch (Exception ex)
        {
            Fail("XAML 能加载：ToolsDropDownWindow", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ───────────────────────── 二、界面行为（跑真实消息循环）─────────────────────────

    private static void RunConsoleChecks()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;

        WebConsolePage? page = null;
        Window? window = null;
        DispatcherTimer? timer = null;

        try
        {
            page = new WebConsolePage();
            window = new Window
            {
                Width = 1120,
                Height = 700,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -30000,
                Top = -30000,
                Content = page,
            };

            window.Show();
            window.UpdateLayout();
        }
        catch (Exception ex)
        {
            Fail("控制台页无法放进窗口", $"{ex.GetType().Name}: {ex.Message}");
            return;
        }

        var host = window;
        var console = page;

        timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(1800),
            DispatcherPriority.Background,
            (_, _) =>
            {
                timer?.Stop();

                try
                {
                    RunTabChecks(console, host);
                    RunToolsDropDownChecks(console, host);
                    RunToolsDropDownReopenChecks(console, host);
                    RunSpaRouteChecks(console);
                    RunMultiWebViewChecks(console);
                }
                catch (Exception ex)
                {
                    // TargetInvocationException（反射包装）的 Message 完全无用，
                    // 必须挖到最内层，并把堆栈打出来，否则只知道"抛异常"却不知道在哪
                    var inner = ex;
                    while (inner is TargetInvocationException && inner.InnerException != null)
                    {
                        inner = inner.InnerException;
                    }

                    Fail("界面体检过程抛异常", $"{inner.GetType().Name}: {inner.Message}");
                    Log("       堆栈：" + (inner.StackTrace ?? "(无)").Replace("\n", "\n       "));
                }
                finally
                {
                    host.Close();
                    dispatcher.InvokeShutdown();
                }
            },
            dispatcher);

        timer.Start();
        Dispatcher.Run();
    }

    private static void RunTabChecks(WebConsolePage page, Window window)
    {
        var tabStrip = Field<ItemsControl>(page, "TabStrip");
        var urlBox = Field<TextBox>(page, "UrlTextBox");

        Check("标签栏已绑定到 Tabs", () => ReferenceEquals(tabStrip?.ItemsSource, page.Tabs), "ItemsSource 不是 Tabs");
        Check("起始没有标签", () => page.Tabs.Count == 0, $"实际 {page.Tabs.Count} 个");

        page.OpenUrl("https://site1.example.com");
        page.OpenUrl("https://site2.example.com");
        page.OpenUrl("https://site3.example.com");

        Check("打开 3 个地址 → 3 个标签", () => page.Tabs.Count == 3, $"实际 {page.Tabs.Count} 个");
        Check("最后一个打开的标签是当前标签",
            () => page.Tabs.Count == 3 && page.Tabs[2].IsActive && !page.Tabs[0].IsActive,
            "选中态不对");
        Check("地址栏跟着当前标签走", () => urlBox?.Text == "https://site3.example.com", $"地址栏 = {urlBox?.Text}");

        page.OpenUrl("https://site1.example.com");
        Check("重复打开同一地址只复用标签", () => page.Tabs.Count == 3, $"实际 {page.Tabs.Count} 个");
        Check("复用后切到了那个标签", () => page.Tabs[0].IsActive, "没有切到已有标签");

        var widthAt3 = page.Tabs[0].Width;

        for (var i = 4; i <= 15; i++)
        {
            page.OpenUrl($"https://site{i}.example.com");
        }

        window.UpdateLayout();

        var widthAt15 = page.Tabs[0].Width;
        Log($"       标签宽度：3 个时 {widthAt3:F0}，15 个时 {widthAt15:F0}");

        Check("标签变多后宽度自动压缩", () => widthAt15 < widthAt3, $"没有变窄（{widthAt3:F0} → {widthAt15:F0}）");
        Check("压缩后不低于可读下限",
            () => page.Tabs.All(t => t.Width >= BrowserTab.MinWidth - 0.01),
            $"有标签窄于 {BrowserTab.MinWidth}");
        Check("标签数量正确（15 个）", () => page.Tabs.Count == 15, $"实际 {page.Tabs.Count} 个");

        var activeIndex = page.Tabs.ToList().FindIndex(t => t.IsActive);
        var neighbor = page.Tabs[Math.Min(activeIndex + 1, page.Tabs.Count - 1)];

        Call(page, "TabClose_Click", new Button { Tag = page.Tabs[activeIndex] }, new RoutedEventArgs());
        window.UpdateLayout();

        Check("关闭当前标签后数量减一", () => page.Tabs.Count == 14, $"实际 {page.Tabs.Count} 个");
        Check("关闭当前标签后自动切到邻近标签", () => neighbor.IsActive, "没有切换当前标签");
        Check("关掉后只剩一个当前标签",
            () => page.Tabs.Count(t => t.IsActive) == 1,
            $"有 {page.Tabs.Count(t => t.IsActive)} 个处于选中态");
    }

    private static void RunToolsDropDownChecks(WebConsolePage page, Window window)
    {
        // WebView2 已改为「每标签一个」，运行时动态加进 WebViewHost 容器；
        // 布局断言针对这个容器（它和旧的单个 ConsoleWebView 占同一格、同样的定位）
        var webView = Field<FrameworkElement>(page, "WebViewHost");
        var statusBlock = Field<FrameworkElement>(page, "StatusTextBlock");
        var tabStripHost = Field<FrameworkElement>(page, "TabStripHost");
        var toolsToggle = Field<System.Windows.Controls.Primitives.ToggleButton>(page, "ToolsToggle");
        var refreshButton = Field<Button>(page, "RefreshButton");

        Check("工具栏有独立的「刷新」按钮", () => refreshButton != null, "找不到 RefreshButton");

        double Top(FrameworkElement e) => e.TransformToAncestor(page).Transform(new Point(0, 0)).Y;

        Log($"[layout] tab={Top(tabStripHost!):F0} webTop={Top(webView!):F0}"
            + $" statusTop={Top(statusBlock!):F0} webHeight={webView!.ActualHeight:F0}");

        Check("网页占据剩余空间（没有被挤成 0 高）", () => webView!.ActualHeight > 200,
            $"网页高度只有 {webView!.ActualHeight:F0}");
        Check("标签栏在网页上方", () => Top(tabStripHost!) < Top(webView!), "顺序不对");
        Check("提示栏在网页下方", () => Top(statusBlock!) > Top(webView!), "顺序不对");

        var heightBefore = webView!.ActualHeight;

        Check("☰ 默认没有弹出下拉窗",
            () => toolsToggle?.IsChecked == false
                  && (Field<Window>(page, "_toolsDropDown")?.IsVisible ?? false) == false,
            "下拉窗一开始就开着");

        // 先勾上 ☰（ToggleButton 的 Click 由用户点出来，探针里手动置位再调处理器）
        toolsToggle!.IsChecked = true;
        Call(page, "ToolsToggle_Click", toolsToggle, new RoutedEventArgs());
        window.UpdateLayout();

        var dropDown = Field<Window>(page, "_toolsDropDown");

        if (dropDown == null)
        {
            Fail("点 ☰ 后弹出竖向下拉窗", "下拉窗根本没被创建");
            return;
        }

        var heightDuring = webView.ActualHeight;

        Log($"       下拉窗 {dropDown.ActualWidth:F0}×{dropDown.ActualHeight:F0}，网页高度 {heightBefore:F0} → {heightDuring:F0}");

        Check("点 ☰ 后弹出竖向下拉窗", () => dropDown.IsVisible, "下拉窗没弹出来");
        Check("下拉窗是竖的（高比宽大）",
            () => dropDown.ActualHeight > 0 && dropDown.ActualHeight > dropDown.ActualWidth,
            $"实际 {dropDown.ActualWidth:F0}×{dropDown.ActualHeight:F0}");
        Check("下拉窗不挤压网页（高度一字不变）",
            () => Math.Abs(heightDuring - heightBefore) < 2,
            $"网页高度被挤了（{heightBefore:F0} → {heightDuring:F0}）");
        Check("下拉窗弹在工具栏下方",
            () => dropDown.Top - window.Top > 60,
            $"下拉 Top={dropDown.Top:F0}，主窗 Top={window.Top:F0}");

        var aidToggle = Field<CheckBox>(dropDown, "FillAidToggle");
        Check("视觉辅助开关在下拉里且默认开启", () => aidToggle?.IsChecked == true, $"IsChecked = {aidToggle?.IsChecked}");

        // 「注入 Token」只对 JWT + localStorage 的后台有用，老板确认业务用不上，已从菜单移除。
        // 留一条断言防止哪天又被加回来（它内部会弹模态框，探针点了会卡死）。
        var menuItems = Descendants<Button>(dropDown)
            .Select(b => b.Content as string ?? string.Empty)
            .Where(c => c.Length > 0)
            .ToList();

        Check("菜单里已没有「注入 Token」（功能已移除）",
            () => menuItems.All(c => !c.Contains("Token", StringComparison.Ordinal)),
            string.Join(" / ", menuItems));

        var autofillScript = typeof(WebConsolePage)
            .GetField("AutofillFunction", BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null) as string;

        Check("填充脚本带高亮逻辑",
            () => autofillScript != null && autofillScript.Contains("ops-fill-aid") && autofillScript.Contains("aid"),
            "脚本里没有 ops-fill-aid 高亮逻辑");

        toolsToggle.IsChecked = false;
        Call(page, "ToolsToggle_Click", toolsToggle, new RoutedEventArgs());
        window.UpdateLayout();

        Check("再点 ☰ 后下拉窗收起", () => !dropDown.IsVisible, "下拉窗没收起");
    }

    /// <summary>递归取可视树里所有 T（<see cref="FindChild{T}"/> 只按名字找第一个）</summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// 回归：点过下拉窗里的菜单项之后，再点 ☰ 还能不能开。
    /// <para>
    /// ⚠️ 2026-09-25 的真实崩溃就出在这条路径上：菜单项原来是 <c>Close()</c>（真关闭窗口），
    /// 而控制台页把窗口缓存在 <c>_toolsDropDown</c> 字段里、只在字段为 null 时新建，
    /// 于是第二次点 ☰ 会对一个"已关闭的 Window"调 <c>Show()</c>，抛
    /// InvalidOperationException 并让整个应用退出（Windows 事件日志里能看到
    /// <c>at WebConsolePage.ToolsToggle_Click</c>）。
    /// </para>
    /// <para>
    /// 上面的 <see cref="RunToolsDropDownChecks"/> 只走了"开 → 关"，
    /// **从没点过菜单项**，所以这条必崩路径一直没被发现 —— 这个检查就是补那个洞。
    /// </para>
    /// </summary>
    private static void RunToolsDropDownReopenChecks(WebConsolePage page, Window window)
    {
        var toolsToggle = Field<System.Windows.Controls.Primitives.ToggleButton>(page, "ToolsToggle");

        if (toolsToggle == null)
        {
            Fail("☰ 按钮存在", "找不到 ToolsToggle");
            return;
        }

        // 菜单项走的是同一条关闭路径，逐个点一遍，避免只修了「填充」那一个。
        // ⚠️ 不能往这个列表里加会弹模态框的项：探针没人点「确定」，一点就永久挂住。
        // （「注入 Token」就是这样卡死过一次，2026-11-06 已连同菜单项一起删掉。）
        foreach (var (method, label) in new[]
                 {
                     ("Fill_Click", "填充账号密码"),
                     ("Recent_Click", "打开…"),
                     ("Bookmark_Click", "收藏当前页"),
                     ("Fit_Click", "适应宽度"),
                 })
        {
            var r = TryReopenAfterMenu(page, window, toolsToggle, method);

            Check($"点过「{label}」后再点 ☰ 不抛异常",
                () => r.Exception == null,
                r.Exception is null ? "" : $"{r.Exception.GetType().Name}: {r.Exception.Message}");

            Check($"点过「{label}」后再点 ☰ 能正常弹出",
                () => r.IsVisible,
                "下拉窗没弹出来（引用可能仍指向已关闭的窗口）");
        }

        // ── 兜底验证：就算窗口被硬 Close() 掉（极端情况），也不该崩，而是自动重建 ──
        var current = Field<Window>(page, "_toolsDropDown");
        current?.Close();

        Check("窗口被 Close 后缓存引用被清掉（兜底生效）",
            () => Field<Window>(page, "_toolsDropDown") == null,
            "字段仍指向已关闭的窗口 —— 下次 Show() 必然抛异常");

        var rebuilt = TryReopenAfterMenu(page, window, toolsToggle, null);
        Check("窗口被 Close 后仍能重新打开（自动重建）",
            () => rebuilt.Exception == null && rebuilt.IsVisible,
            rebuilt.Exception is null
                ? "下拉窗没弹出来"
                : $"{rebuilt.Exception.GetType().Name}: {rebuilt.Exception.Message}");
    }

    /// <summary>
    /// 开 ☰ →（可选）点一个菜单项 → 再开 ☰，返回第二次打开的结果。
    /// </summary>
    private static (Exception? Exception, bool IsVisible) TryReopenAfterMenu(
        WebConsolePage page, Window window,
        System.Windows.Controls.Primitives.ToggleButton toolsToggle, string? menuMethod)
    {
        Exception? error = null;

        try
        {
            // 每轮都从"下拉窗是关着的"这个干净状态开始（更贴近真实操作）
            toolsToggle.IsChecked = false;
            Call(page, "ToolsToggle_Click", toolsToggle, new RoutedEventArgs());
            window.UpdateLayout();
            ResetDropDownDebounce(page);

            // 第一次打开
            toolsToggle.IsChecked = true;
            Call(page, "ToolsToggle_Click", toolsToggle, new RoutedEventArgs());
            window.UpdateLayout();

            var dropDown = Field<Window>(page, "_toolsDropDown");
            if (dropDown == null)
            {
                return (new InvalidOperationException("第一次点 ☰ 就没能创建下拉窗"), false);
            }

            // 点菜单项（内部会关闭下拉窗）
            if (menuMethod != null)
            {
                Call(dropDown, menuMethod, dropDown, new RoutedEventArgs());
                window.UpdateLayout();
            }

            // 绕过 250ms 防抖：那是给真人连点用的，探针两次点击之间没有真实间隔
            ResetDropDownDebounce(page);

            // 再开一次 —— 修复前这里必抛 InvalidOperationException
            toolsToggle.IsChecked = true;
            Call(page, "ToolsToggle_Click", toolsToggle, new RoutedEventArgs());
            window.UpdateLayout();
        }
        catch (Exception ex)
        {
            error = ex;
        }

        return (error, Field<Window>(page, "_toolsDropDown")?.IsVisible ?? false);
    }

    /// <summary>
    /// 把「下拉窗刚关闭的时间点」拨到 5 秒前，绕过那段 250ms 防抖。
    /// </summary>
    private static void ResetDropDownDebounce(WebConsolePage page)
        => page.GetType()
            .GetField("_toolsDropDownClosedAt", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(page, DateTime.Now.AddSeconds(-5));

    /// <summary>
    /// 密码箱编辑对话框的密码往返：带密码进去 → BuildItem 出来，密码不能丢。
    /// <para>
    /// SecretBox 内部同时有 PasswordBox 与明文 TextBox 两个控件互相同步，
    /// 只要有一处方向反了、或 XAML 初始化时 TextChanged 把 <c>_password</c> 覆盖成空，
    /// 编辑一次就会把密码清空，而用户毫无察觉（保存后密码就变空了）——
    /// 这正是"密码箱好像有问题"最值得怀疑的落点，必须实测而不是只看代码。
    /// </para>
    /// </summary>
    private static void RunVaultEditRoundTripChecks()
    {
        var dialogType = typeof(VaultEditDialog);
        var setup = dialogType.GetMethod("Setup", BindingFlags.Instance | BindingFlags.NonPublic);
        var build = dialogType.GetMethod("BuildItem", BindingFlags.Instance | BindingFlags.NonPublic);

        if (setup == null || build == null)
        {
            Fail("VaultEditDialog 有 Setup / BuildItem", "方法被改名或删了");
            return;
        }

        VaultItem? RoundTrip(VaultItem source, out string error)
        {
            try
            {
                var dialog = Activator.CreateInstance(dialogType, nonPublic: true)!;
                setup.Invoke(dialog, new object?[] { source, false });
                error = "";
                return build.Invoke(dialog, null) as VaultItem;
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
                error = $"{inner.GetType().Name}: {inner.Message}";
                return null;
            }
        }

        // BuildItem 是就地改写同一个实例，所以期望值必须先抄下来再比
        var source = new VaultItem
        {
            Title = "往返测试",
            Category = "分类",
            Username = "tester@example.com",
            Password = "P@ssw0rd-中文-123",
            Url = "https://example.com/login",
            Notes = "备注",
        };
        var expectedPassword = source.Password;
        var expectedUser = source.Username;

        var back = RoundTrip(source, out var error);

        if (back == null)
        {
            Fail("密码箱编辑对话框可往返", error);
            return;
        }

        Check("编辑对话框往返后密码不丢",
            () => back.Password == expectedPassword,
            $"原 {expectedPassword.Length} 字符 → 出来 {back.Password.Length} 字符");
        Check("编辑对话框往返后账号不丢",
            () => back.Username == expectedUser, $"实际「{back.Username}」");
        Check("编辑对话框往返后其他字段不丢",
            () => back.Title == "往返测试"
                  && back.Url == "https://example.com/login"
                  && back.Notes == "备注"
                  && back.Category == "分类",
            "有字段被改动");

        // 空密码要原样保持为空（不能凭空变出内容）
        var emptySrc = new VaultItem { Title = "空密码", Username = "u", Password = "" };
        var emptyBack = RoundTrip(emptySrc, out var error2);
        if (emptyBack == null)
        {
            Fail("密码箱编辑对话框可往返（空密码）", error2);
        }
        else
        {
            Check("空密码往返后仍为空", () => emptyBack.Password.Length == 0,
                $"变成了 {emptyBack.Password.Length} 字符");
        }

        // 引号、反斜杠、换行等特殊字符不能被破坏
        var specialSrc = new VaultItem { Title = "特殊字符", Password = "a\"b\\c'd\ne\tf{}<>&" };
        var expectSpecial = specialSrc.Password;
        var specialBack = RoundTrip(specialSrc, out var error3);
        if (specialBack == null)
        {
            Fail("密码箱编辑对话框可往返（特殊字符）", error3);
        }
        else
        {
            Check("特殊字符密码往返后逐字一致",
                () => specialBack.Password == expectSpecial,
                $"原 {expectSpecial.Length} → 出 {specialBack.Password.Length} 字符");
        }
    }

    /// <summary>
    /// 只读诊断：当前 Windows 账户能否解开真实密码箱里的密文。
    /// <para>
    /// 刻意**只读、只报成败与条数，绝不打印明文**。DPAPI 是绑定 Windows 账户的，
    /// 换账户 / 换机器 / 重装系统后旧密文就解不开了，而产品的 Decrypt 遇到失败会
    /// 原样返回带前缀的密文，界面上看起来就是一长串乱码 ——
    /// 这是"密码箱好像坏了"最常见的成因，值得单独确认一次。
    /// </para>
    /// </summary>
    private static void RunRealVaultReadOnlyDiagnosis()
    {
        var realVault = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ServerOpsHelper", "vault.json");

        if (!File.Exists(realVault))
        {
            Log($"[只读诊断] 真实密码箱不存在（{realVault}），跳过");
            return;
        }

        List<VaultItem> items;
        try
        {
            var json = File.ReadAllText(realVault);
            items = System.Text.Json.JsonSerializer.Deserialize<List<VaultItem>>(json) ?? new();
        }
        catch (Exception ex)
        {
            Fail("[只读诊断] 真实密码箱能解析", $"{ex.GetType().Name}: {ex.Message}");
            return;
        }

        var cred = new CredentialService();
        var encrypted = items.Count(i => cred.IsEncrypted(i.Password));
        var emptyCount = items.Count(i => string.IsNullOrEmpty(i.Password));
        var plainCount = items.Count - encrypted - emptyCount;

        Log($"[只读诊断] 真实密码箱 {items.Count} 条：密文 {encrypted} / 明文 {plainCount} / 空密码 {emptyCount}");

        var broken = 0;
        foreach (var item in items.Where(i => cred.IsEncrypted(i.Password)))
        {
            // Decrypt 解密失败时会原样返回带前缀的密文，据此判断这段密文解不开
            var decrypted = cred.Decrypt(item.Password);
            if (cred.IsEncrypted(decrypted))
            {
                broken++;
                Log($"[只读诊断]   「{item.Title}」解密失败（当前账户解不开这段密文）");
            }
        }

        Check("[只读诊断] 密码箱密文都能被当前账户解开",
            () => broken == 0,
            $"{broken} 条解不开 —— 换过 Windows 账户 / 机器后 DPAPI 密文即失效");

        if (emptyCount > 0)
        {
            Log($"[只读诊断] 注意：有 {emptyCount} 条记录**本身就没存密码**（不是解密问题）");
        }
    }

    /// <summary>
    /// 验证「链接缺的字段用密码箱补齐」这条合并规则。
    /// <para>
    /// 这是"填充密码失败"的根因所在：链接只有账号、密码在密码箱里时，
    /// 原实现整条只用链接，密码恒为空串，填充脚本会直接跳过密码框。
    /// </para>
    /// </summary>
    private static void RunCredentialMergeChecks()
    {
        var method = typeof(WebConsolePage).GetMethod(
            "MergeCredential", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        if (method == null)
        {
            Fail("能找到 MergeCredential", "方法不存在（被改名或删了？）");
            return;
        }

        (string Username, string Password, bool UsedVault) Merge(
            string? lu, string? lp, string? vu, string? vp)
            => ((string, string, bool))method.Invoke(null, new object?[] { lu, lp, vu, vp })!;

        // 核心场景：链接只有账号，密码在密码箱里 —— 这正是老板机器上的真实数据
        var mixed = Merge("mmnjzior", "", "mmnjzior", "VaultPwd!");
        Check("链接缺密码时用密码箱补齐", () => mixed.Password == "VaultPwd!",
            $"密码变成了 {mixed.Password.Length} 个字符");
        Check("补齐时账号保持链接里的值", () => mixed.Username == "mmnjzior", $"实际 {mixed.Username}");
        Check("补齐时标记用到了密码箱", () => mixed.UsedVault, "UsedVault 应为 true");

        // 反向：链接有密码、账号在密码箱里
        var reverse = Merge("", "LinkPwd!", "VaultUser", "VaultPwd!");
        Check("链接缺账号时用密码箱补齐", () => reverse.Username == "VaultUser", $"实际 {reverse.Username}");
        Check("反向补齐时密码保持链接里的值", () => reverse.Password == "LinkPwd!", "密码被覆盖了");

        // 链接完整时不许被密码箱覆盖
        var full = Merge("LinkUser", "LinkPwd!", "VaultUser", "VaultPwd!");
        Check("链接完整时不覆盖为密码箱的值",
            () => full.Username == "LinkUser" && full.Password == "LinkPwd!",
            $"变成了 {full.Username}/{full.Password.Length}");
        Check("链接完整时不标记用到密码箱", () => !full.UsedVault, "UsedVault 应为 false");

        // 只有密码箱
        var vaultOnly = Merge(null, null, "VaultUser", "VaultPwd!");
        Check("链接为空时完全用密码箱",
            () => vaultOnly.Username == "VaultUser" && vaultOnly.Password == "VaultPwd!",
            "没有正确回退到密码箱");

        // 两处都没有 → 全空（调用方据此提示"找不到记录"）
        var none = Merge(null, null, null, null);
        Check("两处都没有凭据时返回空",
            () => none.Username.Length == 0 && none.Password.Length == 0,
            "不该有值");

        // 空白字符要当成"没填"，否则会把一串空格填进输入框
        var blanks = Merge("   ", "  ", "VaultUser", "VaultPwd!");
        Check("空白字符视为未填写并用密码箱补齐",
            () => blanks.Username == "VaultUser" && blanks.Password == "VaultPwd!",
            "空白没有被当成未填写");
    }

    /// <summary>
    /// 页内路由（SPA）跟随：宝塔切走再切回来不能回到首页 —— 这是本轮修的 bug
    /// </summary>
    private static void RunSpaRouteChecks(WebConsolePage page)
    {
        var applyLocation = typeof(WebConsolePage).GetMethod(
            "ApplyTabLocation", BindingFlags.Static | BindingFlags.NonPublic)!;

        bool Apply(BrowserTab tab, string? url, string? title)
            => (bool)applyLocation.Invoke(null, new object?[] { tab, url, title })!;

        var spaTab = new BrowserTab { Url = "https://bt.example.com/entry", Title = "宝塔" };

        Check("页内路由变化会更新标签地址（回到首页那个 bug 的修复点）",
            () => Apply(spaTab, "https://bt.example.com/entry/docker/container", "Docker · 宝塔")
                  && spaTab.Url.EndsWith("/docker/container", StringComparison.Ordinal),
            $"标签地址 = {spaTab.Url}");

        Check("标签标题也跟着页内路由更新", () => spaTab.Title == "Docker · 宝塔", $"标题 = {spaTab.Title}");

        Check("about:blank 不污染标签地址",
            () => !Apply(spaTab, "about:blank", null)
                  && spaTab.Url.EndsWith("/docker/container", StringComparison.Ordinal),
            $"标签地址被写成了 {spaTab.Url}");

        Check("地址没变时不重复改写", () => !Apply(spaTab, spaTab.Url, null), "同样的地址也返回了 changed");

        Check("空标题不会把已有标题抹掉",
            () => { Apply(spaTab, null, "   "); return spaTab.Title == "Docker · 宝塔"; },
            $"标题 = {spaTab.Title}");

        var openInTab = typeof(WebConsolePage).GetMethod(
            "OpenInTab", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var btLink = new LinkItem
        {
            Id = Guid.NewGuid(),
            Name = "宝塔",
            Url = "https://bt.example.com/entry",
            Kind = LinkKind.BtPanel,
        };

        var before = page.Tabs.Count;

        openInTab.Invoke(page, new object?[] { btLink, "https://bt.example.com/entry/docker" });
        var afterFirst = page.Tabs.Count;

        openInTab.Invoke(page, new object?[] { btLink, "https://bt.example.com/entry" });
        var afterSecond = page.Tabs.Count;

        Check("点同一个常用链接只开一个标签（哪怕它已跳到深层地址）",
            () => afterFirst == before + 1 && afterSecond == afterFirst,
            $"标签数 {before} → {afterFirst} → {afterSecond}");

        Check("该链接只对应一个标签",
            () => page.Tabs.Count(t => t.Link?.Id == btLink.Id) == 1,
            $"有 {page.Tabs.Count(t => t.Link?.Id == btLink.Id)} 个");
    }

    /// <summary>
    /// 多标签架构回归：每个标签必须是独立的 WebView2（独立 Core / 独立崩溃状态），
    /// 而不是所有标签共用一个控件 —— 这正是"多页面串成一个页面 + 一个白屏带崩全部"的根因修复点。
    /// </summary>
    private static void RunMultiWebViewChecks(WebConsolePage page)
    {
        var pageType = typeof(WebConsolePage);

        // 承载容器存在（各标签的 WebView2 动态加进来）
        Check("存在每标签 WebView2 的承载容器 WebViewHost",
            () => Field<FrameworkElement>(page, "WebViewHost") != null,
            "找不到 WebViewHost");

        // TabWebView 包装类型有独立的 Core / ProcessFailed / AutoFillAttempted 字段
        var tabViewType = pageType.GetNestedType("TabWebView", BindingFlags.NonPublic);
        Check("存在每标签独立的 TabWebView 包装类型", () => tabViewType != null, "找不到 TabWebView");

        if (tabViewType != null)
        {
            var names = tabViewType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .Concat(tabViewType.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name))
                .ToList();

            Check("TabWebView 各自持有独立 Core", () => names.Contains("Core"), "没有 Core 属性");
            Check("TabWebView 各自记录崩溃状态 ProcessFailed",
                () => names.Contains("ProcessFailed"), "没有 ProcessFailed 属性");
            Check("TabWebView 各自记录自动填充状态 AutoFillAttempted",
                () => names.Contains("AutoFillAttempted"), "没有 AutoFillAttempted 属性");
        }

        // 关键方法都在（切可见性 / 确保 view / 释放 view / 刷新），防止哪天被误删回退成共享单实例
        foreach (var method in new[] { "ShowOnlyView", "EnsureViewAsync", "ReleaseView", "ActivateTabAsync" })
        {
            Check($"多标签关键方法存在：{method}",
                () => pageType.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic) != null,
                $"找不到 {method}");
        }

        // 刷新按钮的处理器存在
        Check("刷新按钮处理器 RefreshButton_Click 存在",
            () => pageType.GetMethod("RefreshButton_Click", BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "找不到 RefreshButton_Click");

        // 每开一个标签，packages 里就多一个 view（用反射读私有 _views 字典；环境未初始化时不会真建 WebView2，
        // 但这里只验结构存在，不依赖真实 WebView2 运行时）
        var viewsField = pageType.GetField("_views", BindingFlags.Instance | BindingFlags.NonPublic);
        Check("存在标签→WebView2 映射 _views",
            () => viewsField != null
                  && typeof(System.Collections.IDictionary).IsAssignableFrom(viewsField.FieldType),
            "_views 不存在或不是字典");
    }

    // ───────────────────────── FRP 配置（TOML 往返）─────────────────────────

    /// <summary>
    /// 只验纯逻辑：TOML 解析 / 序列化往返。**不碰** <c>FrpEnvironment</c> ——
    /// 它写死 %APPDATA%\FrpManager，那是老板的真实数据目录，探针只许读不许写。
    /// </summary>
    private static void RunFrpChecks()
    {
        Log("── FRP 配置解析 ──");

        const string frpcSample = """
        serverAddr = "103.236.71.248"
        serverPort = 10064

        auth.method = "token"
        auth.token = "abc123def456"

        [[proxies]]
        name = "本地网关"
        type = "tcp"
        localIP = "127.0.0.1"
        localPort = 8791
        remotePort = 6000
        """;

        var frpc = FrpToml.ParseFrpc(frpcSample);

        Check("FRP：解析 serverAddr / serverPort",
            () => frpc.ServerAddr == "103.236.71.248" && frpc.ServerPort == 10064,
            $"{frpc.ServerAddr}:{frpc.ServerPort}");
        Check("FRP：解析 auth.token", () => frpc.Token == "abc123def456", frpc.Token);
        Check("FRP：解析代理规则", () => frpc.Proxies.Count == 1 && frpc.Proxies[0].LocalPort == 8791,
            $"{frpc.Proxies.Count} 条");
        Check("FRP：中文规则名不乱码", () => frpc.Proxies[0].Name == "本地网关", frpc.Proxies[0].Name);

        var roundTrip = FrpToml.ParseFrpc(FrpToml.SerializeFrpc(frpc));
        Check("FRP：序列化后能再解析（往返不丢字段）",
            () => roundTrip.ServerAddr == frpc.ServerAddr
                  && roundTrip.ServerPort == frpc.ServerPort
                  && roundTrip.Token == frpc.Token
                  && roundTrip.Proxies.Count == 1
                  && roundTrip.Proxies[0].RemotePort == 6000,
            FrpToml.SerializeFrpc(frpc));

        // 域名型规则：不能写 remotePort（frp 会拒绝这种组合）
        var httpCfg = new FrpcConfig
        {
            ServerAddr = "a.example.com",
            ServerPort = 7000,
            Proxies =
            {
                new ProxyRule
                {
                    Name = "web",
                    Type = "http",
                    LocalIP = "127.0.0.1",
                    LocalPort = 8080,
                    CustomDomains = { "a.com", "b.com" }
                }
            }
        };

        var httpText = FrpToml.SerializeFrpc(httpCfg);
        Check("FRP：域名型规则写 customDomains 且不写 remotePort",
            () => httpText.Contains("customDomains = [\"a.com\", \"b.com\"]", StringComparison.Ordinal)
                  && !httpText.Contains("remotePort", StringComparison.Ordinal),
            httpText);

        var httpBack = FrpToml.ParseFrpc(httpText);
        Check("FRP：域名型规则往返后域名不丢",
            () => httpBack.Proxies[0].CustomDomains.SequenceEqual(new[] { "a.com", "b.com" }),
            string.Join(",", httpBack.Proxies[0].CustomDomains));

        // Token 里带引号 / 反斜杠不能把 TOML 写坏
        var tricky = new FrpcConfig { ServerAddr = "1.2.3.4", ServerPort = 7000, Token = "a\"b\\c" };
        var trickyBack = FrpToml.ParseFrpc(FrpToml.SerializeFrpc(tricky));
        Check("FRP：Token 里的引号与反斜杠被正确转义",
            () => trickyBack.Token == "a\"b\\c", trickyBack.Token);

        // 语法坏掉时降级解析不能把配置判成空 —— 否则界面一保存就把用户内容清空
        var broken = FrpToml.ParseFrpc("serverAddr = \"5.6.7.8\"\nserverPort = 7000\n[[proxies]\nname = \"x\"");
        Check("FRP：TOML 语法错误时降级提取关键字段",
            () => broken.ServerAddr == "5.6.7.8" && broken.ServerPort == 7000,
            $"{broken.ServerAddr}:{broken.ServerPort}");

        // frps
        var frps = FrpToml.ParseFrps("""
        bindAddr = "0.0.0.0"
        bindPort = 7000

        auth.method = "token"
        auth.token = "srv-token"

        webServer.addr = "0.0.0.0"
        webServer.port = 7500
        webServer.user = "admin"
        webServer.password = "pass123"
        """);

        Check("FRP：解析 frps 监听与面板",
            () => frps.BindPort == 7000 && frps.Token == "srv-token"
                  && frps.WebPort == 7500 && frps.WebUser == "admin" && frps.WebPassword == "pass123",
            $"{frps.BindAddr}:{frps.BindPort} web={frps.WebPort}");

        var frpsBack = FrpToml.ParseFrps(FrpToml.SerializeFrps(frps));
        Check("FRP：frps 往返不丢字段",
            () => frpsBack.BindPort == 7000 && frpsBack.WebPassword == "pass123" && frpsBack.Token == "srv-token",
            FrpToml.SerializeFrps(frps));
    }

    // ───────────────────────── 扩展安装 / 卸载 ─────────────────────────

    /// <summary>
    /// 验证"插件管理"那套动作：安装内置、重复安装被拒、停用/启用、zip 安装、zip slip 拒绝、
    /// 单文件安装、卸载。全程只在隔离目录里写，跑完清空。
    /// </summary>
    private static void RunExtensionInstallChecks()
    {
        Log("── 扩展安装 / 卸载 ──");

        var service = ExtensionService.Instance;
        var root = service.ExtensionsDirectory;

        var install = service.InstallBuiltIn("nginx-tools");
        Check("安装内置扩展成功", () => install.Ok, install.Message);

        var pack = service.Packs.FirstOrDefault(p => p.Id == "nginx-tools");
        Check("内置扩展命令数正确", () => pack?.Commands.Count == 4, $"{pack?.Commands.Count}");
        Check("内置扩展出现在 MCP 工具清单",
            () => McpToolCatalog.All.Any(t => t.Name == "ext_nginx-tools_reload"),
            "没有 reload 工具");

        var again = service.InstallBuiltIn("nginx-tools");
        Check("重复安装被拒绝", () => !again.Ok, again.Message);

        var disable = service.SetEnabled("nginx-tools", false);
        Check("停用后工具从清单消失",
            () => disable.Ok && McpToolCatalog.All.All(
                t => !t.Name.StartsWith("ext_nginx-tools_", StringComparison.Ordinal)),
            "停用后仍在清单里");

        service.SetEnabled("nginx-tools", true);
        Check("重新启用后工具回来",
            () => McpToolCatalog.All.Any(t => t.Name == "ext_nginx-tools_reload"),
            "没回来");

        // ── zip 安装 ──
        var srcDir = Path.Combine(Path.GetTempPath(), "probe-ext-src-" + Guid.NewGuid().ToString("N"));
        var zipPath = Path.Combine(Path.GetTempPath(), "probe-ext-" + Guid.NewGuid().ToString("N") + ".zip");

        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "extension.json"), """
        {
          "id": "zip-pack",
          "name": "压缩包扩展",
          "commands": [
            { "id": "hi", "name": "打个招呼", "command": "echo hi", "writeAccess": false }
          ]
        }
        """, new UTF8Encoding(false));

        ZipFile.CreateFromDirectory(srcDir, zipPath);

        var zipInstall = service.InstallFromPath(zipPath);
        Check("从 zip 安装成功", () => zipInstall.Ok, zipInstall.Message);
        Check("zip 安装后目录结构正确",
            () => File.Exists(Path.Combine(root, "zip-pack", "extension.json")),
            "没找到 extensions/zip-pack/extension.json");

        // ── zip slip：带 ../ 的包必须被拒 ──
        var evilZip = Path.Combine(Path.GetTempPath(), "probe-evil-" + Guid.NewGuid().ToString("N") + ".zip");

        using (var fs = File.Create(evilZip))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../evil.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("should never land outside");
        }

        var evil = service.InstallFromPath(evilZip);
        Check("zip 路径穿越被拒绝", () => !evil.Ok && evil.Message.Contains("非法路径", StringComparison.Ordinal), evil.Message);

        // ── 单文件 json 安装 ──
        var singleJson = Path.Combine(Path.GetTempPath(), "probe-single-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(singleJson, """
        {
          "id": "single-pack",
          "name": "单文件扩展",
          "commands": [
            { "id": "date", "name": "看时间", "command": "date", "writeAccess": false }
          ]
        }
        """, new UTF8Encoding(false));

        var singleInstall = service.InstallFromPath(singleJson);
        Check("从单个 json 安装成功", () => singleInstall.Ok, singleInstall.Message);
        Check("单文件安装在 extensions 根目录",
            () => File.Exists(Path.Combine(root, "single-pack.json")),
            "没找到 extensions/single-pack.json");

        // ── 卸载 ──
        var uninstall = service.Uninstall("nginx-tools");
        Check("卸载内置扩展成功",
            () => uninstall.Ok && !Directory.Exists(Path.Combine(root, "nginx-tools")),
            uninstall.Message);

        service.Uninstall("zip-pack");
        service.Uninstall("single-pack");

        Check("清理后回到内置 10 个工具",
            () => McpToolCatalog.All.Count == 10,
            $"{McpToolCatalog.All.Count}");

        // 临时文件清掉（zip / src / json）
        foreach (var path in new[] { zipPath, evilZip, singleJson })
        {
            try { File.Delete(path); } catch { }
        }

        try
        {
            if (Directory.Exists(srcDir))
            {
                Directory.Delete(srcDir, true);
            }
        }
        catch
        {
            // 删不掉不影响结论
        }
    }

    // ───────────────────────── 扩展（清单式插件）─────────────────────────

    /// <summary>
    /// 验证扩展的加载、MCP 合并、参数转义。
    /// <para>
    /// 全程只在隔离目录里造文件，跑完把测试扩展删干净 —— 后面的 MCP 断言依赖
    /// 「工具清单只有内置 10 个」这个前提，漏删会让那一条假失败。
    /// </para>
    /// </summary>
    private static void RunExtensionChecks()
    {
        Log("── 扩展（清单式插件）──");

        var service = ExtensionService.Instance;
        var root = service.ExtensionsDirectory;

        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex)
        {
            Fail("扩展：无法创建扩展目录", ex.Message);
            return;
        }

        // ── 用例 1：一个正常扩展（一条只读命令 + 一条变更命令）──
        var goodDir = Path.Combine(root, "probe-good");
        Directory.CreateDirectory(goodDir);
        File.WriteAllText(Path.Combine(goodDir, "extension.json"), """
        {
          "id": "probe-pack",
          "name": "探针扩展",
          "version": "9.9.9",
          "commands": [
            { "id": "echo_hello", "name": "回显", "description": "回显一段文字",
              "command": "echo ${text}", "writeAccess": false,
              "parameters": [ { "name": "text", "type": "string", "default": "hello" } ] },
            { "id": "rm_file", "name": "删除文件", "command": "rm -f ${path}", "writeAccess": true,
              "parameters": [ { "name": "path", "type": "path", "default": "/tmp/x" } ] }
          ]
        }
        """, new UTF8Encoding(false));

        var summary = service.Reload();
        var pack = summary.Packs.FirstOrDefault(p => p.Id == "probe-pack");

        Check("扩展：正常清单能加载", () => pack != null && pack.IsValid,
            pack?.LoadError ?? "没找到 probe-pack");
        Check("扩展：命令数正确", () => pack?.Commands.Count == 2,
            $"{pack?.Commands.Count}");

        var allNames = McpToolCatalog.All.Select(t => t.Name).ToArray();
        Check("扩展：命令合并进 MCP 工具清单",
            () => allNames.Contains("ext_probe-pack_echo_hello"),
            string.Join(",", allNames));

        Check("扩展：只读命令不需要写权限",
            () => McpToolCatalog.Find("ext_probe-pack_echo_hello")?.RequiresWriteAccess == false,
            "RequiresWriteAccess 不是 false");

        Check("扩展：变更命令需要写权限",
            () => McpToolCatalog.Find("ext_probe-pack_rm_file")?.RequiresWriteAccess == true,
            "RequiresWriteAccess 不是 true");

        var echoSpec = McpToolCatalog.Find("ext_probe-pack_echo_hello");
        var props = echoSpec?.InputSchema["properties"] as JsonObject;

        Check("扩展：schema 含 server 与自定义参数",
            () => props != null && props.ContainsKey("server") && props.ContainsKey("text"),
            props?.ToJsonString() ?? "(没有 schema)");

        // ── 用例 2：参数替换必须做 shell 引用（这条最要命）──
        var echo = pack?.Commands.FirstOrDefault(c => c.Id == "echo_hello");
        var quoted = string.Empty;

        try
        {
            quoted = ExtensionService.Substitute(echo!, new Dictionary<string, string>
            {
                ["text"] = "a'b; rm -rf /"
            });
        }
        catch (Exception ex)
        {
            quoted = "异常：" + ex.Message;
        }

        Check("扩展：参数替换做 shell 引用（单引号被中和）",
            () => quoted == "echo 'a'\\''b; rm -rf /'",
            quoted);

        var defaulted = string.Empty;
        try
        {
            defaulted = ExtensionService.Substitute(
                pack!.Commands.First(c => c.Id == "rm_file"),
                new Dictionary<string, string>());
        }
        catch (Exception ex)
        {
            defaulted = "异常：" + ex.Message;
        }

        Check("扩展：没传参数时用默认值", () => defaulted == "rm -f '/tmp/x'", defaulted);

        // ── 用例 3：几类必须被拒的输入 ──
        var noDefault = new ExtensionCommand
        {
            Id = "x",
            Name = "x",
            Command = "echo ${v}",
            Parameters = { new ExtensionParameter { Name = "v", Type = "string" } }
        };

        Check("扩展：缺必填参数被拒",
            () => ThrowsExtension(() => ExtensionService.Substitute(noDefault, new Dictionary<string, string>())),
            "没有抛异常");

        Check("扩展：参数里的换行被拒",
            () => ThrowsExtension(() => ExtensionService.Substitute(noDefault,
                new Dictionary<string, string> { ["v"] = "a\nreboot" })),
            "没有抛异常");

        var intCommand = new ExtensionCommand
        {
            Id = "y",
            Name = "y",
            Command = "tail -n ${n} /var/log/syslog",
            Parameters = { new ExtensionParameter { Name = "n", Type = "int", Min = 1, Max = 10, Default = "5" } }
        };

        Check("扩展：整数越界被拒",
            () => ThrowsExtension(() => ExtensionService.Substitute(intCommand,
                new Dictionary<string, string> { ["n"] = "99" })),
            "没有抛异常");

        // ── 用例 4：坏清单只影响自己，并且给出原因 ──
        var badDir = Path.Combine(root, "probe-bad");
        Directory.CreateDirectory(badDir);
        File.WriteAllText(Path.Combine(badDir, "extension.json"), """
        { "id": "Bad Id", "name": "坏扩展", "commands": [ { "id": "x", "name": "x", "command": "echo 1" } ] }
        """, new UTF8Encoding(false));

        var badSummary = service.Reload();
        var badPack = badSummary.Packs.FirstOrDefault(p =>
            p.SourcePath.Contains("probe-bad", StringComparison.OrdinalIgnoreCase));

        Check("扩展：非法 id 被拒绝", () => badPack != null && !badPack.IsValid,
            "坏扩展竟然通过了");
        Check("扩展：拒绝原因可读（提到 id 不合法）",
            () => badPack?.LoadError.Contains("不合法") == true,
            badPack?.LoadError ?? "(没有 LoadError)");
        Check("扩展：坏扩展不影响好的",
            () => badSummary.Packs.Any(p => p.Id == "probe-pack" && p.IsValid),
            "好扩展也没了");

        // ── 用例 5：enabled=false 不暴露给智能体 ──
        var offDir = Path.Combine(root, "probe-off");
        Directory.CreateDirectory(offDir);
        File.WriteAllText(Path.Combine(offDir, "extension.json"), """
        {
          "id": "probe-off", "name": "停用扩展", "enabled": false,
          "commands": [ { "id": "noop", "name": "noop", "command": "echo 1", "writeAccess": false } ]
        }
        """, new UTF8Encoding(false));

        service.Reload();
        Check("扩展：enabled=false 不出现在工具清单",
            () => McpToolCatalog.All.All(t => t.Name != "ext_probe-off_noop"),
            "停用的扩展仍然暴露了工具");

        // ── 用例 6：服务器页的下拉框跟着扩展走 ──
        CheckServerPageActions(2, Visibility.Visible, "扩展：服务器页下拉框出现并填入 2 条命令");

        // ── 用例 7：界面「执行」走的那条链路（不是 MCP 那条）──
        // 127.0.0.1:1 连接必被拒，用来证明失败会被包装成 RemoteCommandResult，
        // 而不是把异常抛给 UI（抛了就是未处理异常弹窗）。
        var deadServer = new ServerInfo
        {
            Name = "probe-dead",
            Host = "127.0.0.1",
            Port = 1,
            Username = "root",
            OsType = ServerOsType.Linux
        };

        // 放在 Task.Run 里等：这里跑在 UI 线程上，直接 GetResult() 会阻塞 Dispatcher，
        // 被测方法内部的 await 继续排队等 UI 线程就成死锁（sync-over-async 的经典坑）
        var uiRun = Task.Run(() => ExtensionService.Instance.ExecuteAsync(
                deadServer, echo!, new Dictionary<string, string> { ["text"] = "hi" }))
            .GetAwaiter()
            .GetResult();

        Check("扩展：界面执行链路把连接失败包装成结果（不抛异常）",
            () => !uiRun.Success && uiRun.Error.Length > 0,
            $"Success={uiRun.Success}, Error={uiRun.Error}");

        // ── 收尾：清掉全部测试扩展 ──
        foreach (var dir in new[] { goodDir, badDir, offDir })
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch (Exception ex)
            {
                Fail("扩展：清理测试扩展失败", $"{dir}：{ex.Message}");
            }
        }

        service.Reload();
        Check("扩展：清理后回到内置 10 个工具",
            () => McpToolCatalog.All.Count == 10,
            $"{McpToolCatalog.All.Count}");

        CheckServerPageActions(0, Visibility.Collapsed, "扩展：没有扩展时服务器页下拉框隐藏");
    }

    /// <summary>
    /// 服务器详情页的「扩展动作」区：有扩展要显示并填好条目，没有要收起来。
    /// <para>
    /// 直接反射调 private 的 <c>LoadExtensionActions</c>：探针里不 Show 页面，
    /// Loaded 不会触发，正常路径走不到那里。
    /// </para>
    /// </summary>
    private static void CheckServerPageActions(int expectedCount, Visibility expected, string name)
    {
        try
        {
            var page = new ServerDetailPage(new ServerInfo { Name = "probe", Host = "127.0.0.1" });

            typeof(ServerDetailPage)
                .GetMethod("LoadExtensionActions", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(page, null);

            var panel = page.FindName("ExtensionActionPanel") as FrameworkElement;
            var combo = page.FindName("ExtensionCommandComboBox") as ComboBox;
            var count = (combo?.ItemsSource as ICollection)?.Count ?? -1;

            Check(name,
                () => panel != null && panel.Visibility == expected && count == expectedCount,
                $"panel={panel?.Visibility}, count={count}, 期望 count={expectedCount}, visibility={expected}");
        }
        catch (Exception ex)
        {
            Fail(name, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool ThrowsExtension(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ExtensionException)
        {
            return true;
        }
    }

    // ───────────────────────── 六、MCP 智能体接入 ─────────────────────────

    /// <summary>探针用的假密码：只要它在任何工具输出 / 审计日志里出现，就是泄漏。</summary>
    private const string ProbeSecret = "PROBE-SECRET-9f3a";

    /// <summary>
    /// 验证「智能体通过 MCP 管理服务器」这条链路。
    /// <para>
    /// 这里不连真实服务器（探针绝不能碰生产环境）：假服务器指向 127.0.0.1 的关闭端口，
    /// 连接必然失败 —— 我们要验的本来也不是 SSH 能不能通，而是
    /// <b>协议对不对、权限闸门有没有生效、密码会不会漏出去</b>。
    /// </para>
    /// </summary>
    private static void RunMcpChecks()
    {
        Log("── 六、MCP 智能体接入 ──");

        SeedMcpServers();
        SetMcpAllowWrite(false);

        var core = new McpServerCore();

        // ── 协议协商 ──
        var init = Rpc(core, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "probe", ["version"] = "1.0" }
        });

        var initResult = init?["result"];
        Check("initialize 回显支持的最新协议版本",
            () => initResult?["protocolVersion"]?.GetValue<string>() == "2025-06-18",
            initResult?["protocolVersion"]?.GetValue<string>() ?? "(null)");

        Check("initialize 声明 tools 能力",
            () => initResult?["capabilities"]?["tools"] != null,
            "缺少 capabilities.tools");

        Check("serverInfo 名称与版本齐全",
            () => initResult?["serverInfo"]?["name"]?.GetValue<string>() == "ServerOpsHelper"
                  && !string.IsNullOrWhiteSpace(initResult?["serverInfo"]?["version"]?.GetValue<string>()),
            initResult?["serverInfo"]?.ToJsonString() ?? "(null)");

        Check("initialize 返回给模型的使用说明",
            () => !string.IsNullOrWhiteSpace(initResult?["instructions"]?.GetValue<string>()),
            "instructions 为空");

        var oldClient = Rpc(core, "initialize", new JsonObject { ["protocolVersion"] = "2024-11-05" });
        Check("客户端要老版本时回老版本（协商而不是硬顶）",
            () => oldClient?["result"]?["protocolVersion"]?.GetValue<string>() == "2024-11-05",
            oldClient?["result"]?["protocolVersion"]?.GetValue<string>() ?? "(null)");

        var unknownVersion = Rpc(core, "initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" });
        Check("不认识的版本回落到最新版",
            () => unknownVersion?["result"]?["protocolVersion"]?.GetValue<string>() == McpServerCore.LatestProtocolVersion,
            unknownVersion?["result"]?["protocolVersion"]?.GetValue<string>() ?? "(null)");

        // ── 通知 ──
        var notification = Rpc(core, "notifications/initialized", null, notification: true);
        Check("通知不产生响应", () => notification == null, "返回了内容");

        // ── 工具清单 ──
        var tools = Rpc(core, "tools/list", null)?["result"]?["tools"] as JsonArray;
        var toolNames = tools?
            .Select(t => t?["name"]?.GetValue<string>() ?? string.Empty)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray() ?? Array.Empty<string>();

        Check("工具清单共 10 个", () => toolNames.Length == 10, string.Join(",", toolNames));
        Check("工具名与预期一致",
            () => toolNames.SequenceEqual(new[]
            {
                "download_file",
                "exec_command",
                "exec_command_on_servers",
                "get_metrics",
                "get_server",
                "list_files",
                "list_servers",
                "read_file",
                "upload_file",
                "write_file"
            }),
            string.Join(",", toolNames));

        Check("每个工具都带 inputSchema",
            () => tools != null && tools.All(t => t?["inputSchema"]?["type"]?.GetValue<string>() == "object"),
            "有工具缺 inputSchema");

        Check("只读工具被标注 readOnlyHint",
            () => tools!.First(t => t?["name"]?.GetValue<string>() == "list_servers")?["annotations"]?["readOnlyHint"]?.GetValue<bool>() == true,
            "list_servers 没有 readOnlyHint");

        // ── 只读工具 ──
        var listServers = CallTool(core, "list_servers", new JsonObject());
        var listText = ToolText(listServers);
        Check("list_servers 未授权即可用", () => listServers["result"]?["isError"]?.GetValue<bool>() == false, listText);

        var listPayload = JsonNode.Parse(listText) as JsonObject;
        Check("list_servers 返回两台服务器",
            () => listPayload?["count"]?.GetValue<int>() == 2,
            listPayload?["count"]?.ToJsonString() ?? "(null)");

        Check("list_servers 不含密码",
            () => !listText.Contains(ProbeSecret, StringComparison.Ordinal)
                  && !listText.Contains("\"password\"", StringComparison.OrdinalIgnoreCase),
            "密码出现在工具输出里！");

        var byTag = CallTool(core, "list_servers", new JsonObject { ["tag"] = "db" });
        var byTagPayload = JsonNode.Parse(ToolText(byTag)) as JsonObject;
        Check("按标签过滤生效",
            () => byTagPayload?["count"]?.GetValue<int>() == 1
                  && byTagPayload?["servers"]?[0]?["name"]?.GetValue<string>() == "probe-db",
            ToolText(byTag));

        var byName = CallTool(core, "get_server", new JsonObject { ["server"] = "probe-web" });
        var byNameText = ToolText(byName);
        Check("get_server 能按名称解析",
            () => (JsonNode.Parse(byNameText) as JsonObject)?["host"]?.GetValue<string>() == "127.0.0.1",
            byNameText);

        Check("get_server 不含密码",
            () => !byNameText.Contains(ProbeSecret, StringComparison.Ordinal)
                  && !byNameText.Contains("\"password\"", StringComparison.OrdinalIgnoreCase),
            byNameText);

        var byId = CallTool(core, "get_server",
            new JsonObject { ["server"] = "11111111-1111-1111-1111-111111111111" });
        Check("get_server 能按 GUID 解析",
            () => ToolText(byId).Contains("probe-web", StringComparison.Ordinal),
            ToolText(byId));

        var ambiguous = CallTool(core, "get_server", new JsonObject { ["server"] = "probe" });
        Check("模糊名匹配到多台时要求改用 id",
            () => ambiguous["result"]?["isError"]?.GetValue<bool>() == true
                  && ToolText(ambiguous).Contains("多台", StringComparison.Ordinal),
            ToolText(ambiguous));

        var missing = CallTool(core, "get_server", new JsonObject { ["server"] = "根本没有这台" });
        Check("找不到服务器时给出可选列表",
            () => missing["result"]?["isError"]?.GetValue<bool>() == true
                  && ToolText(missing).Contains("probe-web", StringComparison.Ordinal),
            ToolText(missing));

        // ── 权限闸门（默认关闭）──
        var execDenied = CallTool(core, "exec_command",
            new JsonObject { ["server"] = "probe-web", ["command"] = "id" });
        var execDeniedText = ToolText(execDenied);
        Check("默认拒绝执行命令",
            () => execDenied["result"]?["isError"]?.GetValue<bool>() == true,
            execDeniedText);
        Check("拒绝时告诉用户去哪里打开",
            () => execDeniedText.Contains("变更", StringComparison.Ordinal)
                  && execDeniedText.Contains("设置", StringComparison.Ordinal),
            execDeniedText);

        var writeDenied = CallTool(core, "write_file",
            new JsonObject { ["server"] = "probe-web", ["path"] = "/tmp/x", ["content"] = "x" });
        Check("默认拒绝写文件", () => writeDenied["result"]?["isError"]?.GetValue<bool>() == true, ToolText(writeDenied));

        var uploadDenied = CallTool(core, "upload_file",
            new JsonObject { ["server"] = "probe-web", ["localPath"] = "C:\\nope.txt", ["remotePath"] = "/tmp/x" });
        Check("默认拒绝上传文件", () => uploadDenied["result"]?["isError"]?.GetValue<bool>() == true, ToolText(uploadDenied));

        var downloadDenied = CallTool(core, "download_file",
            new JsonObject { ["server"] = "probe-web", ["remotePath"] = "/tmp/x", ["localPath"] = "x" });
        Check("默认拒绝下载文件", () => downloadDenied["result"]?["isError"]?.GetValue<bool>() == true, ToolText(downloadDenied));

        // 分页与追加的参数要在 schema 里声明，否则模型根本不知道能这么用
        var readSchema = tools!.First(t => t?["name"]?.GetValue<string>() == "read_file")?["inputSchema"];
        // offset 的 schema 是 Long（上限 long.MaxValue），这里必须按 long 读 ——
        // 用 GetValue<int>() 会抛 "Int64 不能转成 Int32"（v1.10.0 加分页时漏掉的）
        Check("read_file 声明了 offset 与 1 MiB 页上限",
            () => readSchema?["properties"]?["offset"]?["minimum"]?.GetValue<long>() == 0
                  && readSchema?["properties"]?["maxBytes"]?["maximum"]?.GetValue<int>() == 1_048_576,
            readSchema?.ToJsonString() ?? "(null)");

        var writeSchema = tools!.First(t => t?["name"]?.GetValue<string>() == "write_file")?["inputSchema"];
        Check("write_file 声明了 overwrite/append",
            () => writeSchema?["properties"]?["mode"]?["enum"]?.AsArray().Count == 2,
            writeSchema?.ToJsonString() ?? "(null)");

        var batchDenied = CallTool(core, "exec_command_on_servers",
            new JsonObject { ["tag"] = "prod", ["command"] = "uptime" });
        Check("默认拒绝批量执行", () => batchDenied["result"]?["isError"]?.GetValue<bool>() == true, ToolText(batchDenied));

        // ── 授权之后：闸门放行，失败原因变成"连不上"而不是"没权限" ──
        SetMcpAllowWrite(true);

        var uploadAllowed = CallTool(core, "upload_file",
            new JsonObject
            {
                ["server"] = "probe-web",
                ["localPath"] = "C:\\probe-does-not-exist.txt",
                ["remotePath"] = "/tmp/x"
            });
        var uploadAllowedText = ToolText(uploadAllowed);
        Check("授权后上传先做本机路径校验（不再被权限拦截）",
            () => !uploadAllowedText.Contains("已被禁用", StringComparison.Ordinal)
                  && uploadAllowedText.Contains("找不到", StringComparison.Ordinal),
            uploadAllowedText);

        var execAllowed = CallTool(core, "exec_command",
            new JsonObject { ["server"] = "probe-web", ["command"] = "id", ["timeoutSeconds"] = 5 });
        var execAllowedText = ToolText(execAllowed);
        Check("授权后不再被权限拦截",
            () => !execAllowedText.Contains("已被禁用", StringComparison.Ordinal),
            execAllowedText);
        Check("连不上时把原因如实返回",
            () => execAllowed["result"]?["isError"]?.GetValue<bool>() == true
                  && execAllowedText.Contains("失败", StringComparison.Ordinal),
            execAllowedText);

        var batchAllowed = CallTool(core, "exec_command_on_servers",
            new JsonObject { ["servers"] = new JsonArray("probe-web", "probe-db"), ["command"] = "uptime", ["timeoutSeconds"] = 5 });
        var batchText = ToolText(batchAllowed);
        var batchPayload = JsonNode.Parse(batchText) as JsonObject;
        Check("批量执行按服务器逐个给结果",
            () => batchPayload?["total"]?.GetValue<int>() == 2
                  && batchPayload?["failed"]?.GetValue<int>() == 2,
            batchText);

        Check("批量结果 key 带主机名便于对号",
            () => batchText.Contains("probe-web (127.0.0.1)", StringComparison.Ordinal),
            batchText);

        // ── 协议错误 ──
        var unknownTool = Rpc(core, "tools/call", new JsonObject { ["name"] = "rm_rf", ["arguments"] = new JsonObject() });
        Check("未知工具返回 -32602",
            () => unknownTool?["error"]?["code"]?.GetValue<int>() == -32602,
            unknownTool?.ToJsonString() ?? "(null)");

        var unknownMethod = Rpc(core, "server/nuke", null);
        Check("未知方法返回 -32601",
            () => unknownMethod?["error"]?["code"]?.GetValue<int>() == -32601,
            unknownMethod?.ToJsonString() ?? "(null)");

        var pong = Rpc(core, "ping", null);
        Check("ping 返回空对象", () => pong?["result"] is JsonObject, pong?.ToJsonString() ?? "(null)");

        // ── 令牌 ──
        var token1 = McpTokenStore.EnsureToken();
        var token2 = McpTokenStore.EnsureToken();
        Check("令牌为 64 位十六进制", () => token1.Length == 64 && token1.All(Uri.IsHexDigit), token1);
        Check("重复读取令牌不变", () => token1 == token2, "两次读取不一致");

        var stored = File.Exists(McpTokenStore.TokenFilePath)
            ? File.ReadAllText(McpTokenStore.TokenFilePath)
            : string.Empty;
        Check("令牌落盘是密文（不是明文）",
            () => stored.Length > 0 && !stored.Contains(token1, StringComparison.Ordinal),
            "mcp-token.txt 里能直接读到明文令牌");

        // ── 审计日志 ──
        var auditPath = McpAuditLog.CurrentFilePath;
        var audit = File.Exists(auditPath) ? File.ReadAllText(auditPath) : string.Empty;
        Check("写入了审计日志", () => audit.Length > 0, auditPath);
        Check("审计记录了工具名与目标",
            () => audit.Contains("list_servers", StringComparison.Ordinal)
                  && audit.Contains("probe-web", StringComparison.Ordinal),
            "审计内容缺少关键字段");
        Check("审计日志不含密码",
            () => !audit.Contains(ProbeSecret, StringComparison.Ordinal),
            "审计日志泄漏密码！");

        // ── HTTP 传输 ──
        RunMcpHttpChecks();

        // ── 客户端配置生成 ──
        var stdioConfig = McpRuntime.BuildStdioClientConfig();
        Check("stdio 客户端配置指向当前 exe 与 --mcp-stdio",
            () => stdioConfig.Contains("ServerOpsHelper", StringComparison.OrdinalIgnoreCase)
                  && stdioConfig.Contains("--mcp-stdio", StringComparison.Ordinal),
            stdioConfig);

        var httpConfig = McpRuntime.BuildHttpClientConfig(
            new AppSettings { McpHttpPort = 9260 }, token1);
        Check("HTTP 客户端配置带端点与令牌",
            () => httpConfig.Contains("http://127.0.0.1:9260/mcp", StringComparison.Ordinal)
                  && httpConfig.Contains(token1, StringComparison.Ordinal),
            httpConfig);

        // ── 按设置启停（设置页保存走的就是这条路径）──
        var applyPort = FindFreePort();
        McpRuntime.Apply(new AppSettings { McpHttpEnabled = true, McpHttpPort = applyPort });
        Check("按设置打开后服务真的在跑",
            () => McpRuntime.IsHttpRunning && McpRuntime.HttpPort == applyPort,
            McpRuntime.HttpLastError ?? "未启动");

        McpRuntime.Apply(new AppSettings { McpHttpEnabled = false, McpHttpPort = applyPort });
        Check("按设置关闭后服务停下", () => !McpRuntime.IsHttpRunning, "仍在运行");

        // ── 设置页真的能把这一块渲染出来 ──
        RunSettingsMcpChecks();

        // ── 顶部页签分页（四页全实例化，只切 Visibility）──
        RunSettingsNavChecks();

        // ── read_file 的二进制识别 ──
        // 不识别的话，模型读一张 PNG 会拿到一屏 UTF-8 乱码，白烧上下文。
        var looksBinary = typeof(ServerOpsHelper.Mcp.McpToolExecutor).GetMethod(
            "LooksBinary", BindingFlags.Static | BindingFlags.NonPublic);

        if (looksBinary is null)
        {
            Fail("二进制文件识别", "找不到 LooksBinary 方法");
        }
        else
        {
            bool IsBinary(byte[] bytes) => (bool)looksBinary.Invoke(null, new object[] { bytes })!;

            Check("二进制识别：普通文本不算",
                () => !IsBinary(Encoding.UTF8.GetBytes("hello\nworld\t! 2026-10-17")),
                "");

            Check("二进制识别：UTF-8 中文不算",
                () => !IsBinary(Encoding.UTF8.GetBytes("服务器运维助手 / 密码箱与访问记录")),
                "");

            Check("二进制识别：含 NUL 的判为二进制",
                () => IsBinary(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00 }),
                "");

            Check("二进制识别：大量控制字符判为二进制",
                () => IsBinary(Enumerable.Repeat((byte)0x01, 200).ToArray()),
                "");
        }

        // ── read_file 分页不能把汉字劈成两半 ──
        var trimUtf8 = typeof(ServerOpsHelper.Mcp.McpToolExecutor).GetMethod(
            "TrimIncompleteUtf8Length", BindingFlags.Static | BindingFlags.NonPublic);

        if (trimUtf8 is null)
        {
            Fail("UTF-8 分页边界", "找不到 TrimIncompleteUtf8Length 方法");
        }
        else
        {
            int Trim(byte[] bytes, bool endOfFile) => (int)trimUtf8.Invoke(null, new object[] { bytes, endOfFile })!;

            var chinese = Encoding.UTF8.GetBytes("中");

            Check("UTF-8 分页：完整汉字不截",
                () => Trim(chinese, false) == 3,
                Trim(chinese, false).ToString());

            Check("UTF-8 分页：残缺汉字退回字符边界",
                () => Trim(chinese[..2], false) == 0,
                Trim(chinese[..2], false).ToString());

            Check("UTF-8 分页：ASCII 不受影响",
                () => Trim(Encoding.UTF8.GetBytes("abc"), false) == 3,
                "");

            Check("UTF-8 分页：文件真的结束时不回退",
                () => Trim(chinese[..2], true) == 2,
                "");

            var mixed = Encoding.UTF8.GetBytes("a中");
            Check("UTF-8 分页：混合文本只切掉残缺部分",
                () => Trim(mixed[..2], false) == 1,
                Trim(mixed[..2], false).ToString());
        }
    }

    /// <summary>
    /// 设置页的 MCP 区块：把填充逻辑真跑一遍，验证令牌 / 开关 / 状态文字都填上了
    /// （XAML 改错控件名这类问题只有运行期才炸）。
    /// <para>
    /// 这里直接反射调 <c>LoadMcpSection</c>，不走 <c>Loaded</c> + 消息循环：
    /// 探针进程里 Dispatcher 由后面的 <see cref="RunConsoleChecks"/> 独占（它 <c>Dispatcher.Run()</c>
    /// 之后 <c>InvokeShutdown()</c>），在前面再 PushFrame 会把整个探针挂住。
    /// 填充逻辑本身没有阻塞调用（全是 await 文件 IO），所以同步调用等价。
    /// </para>
    /// </summary>
    private static void RunSettingsMcpChecks()
    {
        try
        {
            var page = new SettingsPage();
            var settings = ConfigService.Instance.LoadSettings();

            var load = typeof(SettingsPage).GetMethod(
                "LoadMcpSection", BindingFlags.Instance | BindingFlags.NonPublic);

            if (load == null)
            {
                Fail("设置页 MCP 区块可用", "找不到 LoadMcpSection 方法（被改名了？）");
                return;
            }

            load.Invoke(page, new object?[] { settings });
            page.UpdateLayout();

            var tokenBox = FindChild<TextBox>(page, "McpTokenTextBox");
            var portBox = FindChild<TextBox>(page, "McpPortTextBox");
            var status = FindChild<TextBlock>(page, "McpStatusTextBlock");
            var writeToggle = FindChild<ModernWpf.Controls.ToggleSwitch>(page, "McpAllowWriteToggle");
            var httpToggle = FindChild<ModernWpf.Controls.ToggleSwitch>(page, "McpHttpToggle");

            Check("设置页有 MCP 令牌输入框",
                () => tokenBox != null && tokenBox.Text.Length == 64,
                tokenBox?.Text.Length.ToString() ?? "找不到控件");

            Check("设置页回填了已保存的端口",
                () => portBox?.Text == "9260",
                portBox?.Text ?? "找不到控件");

            Check("设置页回填了变更操作开关",
                () => writeToggle?.IsOn == true,
                writeToggle?.IsOn.ToString() ?? "找不到控件");

            Check("设置页回填了 HTTP 开关",
                () => httpToggle != null && httpToggle.IsOn == false,
                httpToggle?.IsOn.ToString() ?? "找不到控件");

            Check("设置页显示了服务状态",
                () => !string.IsNullOrWhiteSpace(status?.Text) && status!.Text.Contains("未启动", StringComparison.Ordinal),
                status?.Text ?? "找不到控件");
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: not null } tie ? tie.InnerException! : ex;
            Fail("设置页 MCP 区块可用", $"{inner.GetType().Name}: {inner.Message}");
        }
    }

    /// <summary>
    /// 设置页的顶部页签：验证 4 个页签都在、默认落在「通用」、点选能真切到对应面板。
    /// <para>
    /// 为什么必须测：这些面板是**靠 Visibility 切换**的（四个都实例化），
    /// 如果哪天有人图省事换成 TabControl，未选中的页会被卸载、控件从逻辑树上消失，
    /// 看上去界面照常，但所有 FindName / 自动化都会失效。这条断言就是拦这个的。
    /// </para>
    /// </summary>
    private static void RunSettingsNavChecks()
    {
        try
        {
            var page = new SettingsPage();

            var navGeneral = FindChild<RadioButton>(page, "NavGeneral");
            var navPrivacy = FindChild<RadioButton>(page, "NavPrivacy");
            var navMcp = FindChild<RadioButton>(page, "NavMcp");
            var navAccount = FindChild<RadioButton>(page, "NavAccount");

            Check("设置页有 4 个顶部页签",
                () => navGeneral != null && navPrivacy != null && navMcp != null && navAccount != null,
                $"通用={navGeneral != null} 隐私={navPrivacy != null} 智能体={navMcp != null} 账号={navAccount != null}");

            var panelGeneral = FindChild<ScrollViewer>(page, "PanelGeneral");
            var panelPrivacy = FindChild<ScrollViewer>(page, "PanelPrivacy");
            var panelMcp = FindChild<ScrollViewer>(page, "PanelMcp");
            var panelAccount = FindChild<ScrollViewer>(page, "PanelAccount");

            Check("设置页有 4 个内容面板",
                () => panelGeneral != null && panelPrivacy != null && panelMcp != null && panelAccount != null,
                $"通用={panelGeneral != null} 隐私={panelPrivacy != null} 智能体={panelMcp != null} 账号={panelAccount != null}");

            if (navGeneral is null || navPrivacy is null || navMcp is null || navAccount is null
                || panelGeneral is null || panelPrivacy is null || panelMcp is null || panelAccount is null)
            {
                return;
            }

            // ① 默认落在「通用」，其余三页折叠
            Check("默认显示「通用」页、其余三页折叠",
                () => panelGeneral.Visibility == Visibility.Visible
                      && panelPrivacy.Visibility == Visibility.Collapsed
                      && panelMcp.Visibility == Visibility.Collapsed
                      && panelAccount.Visibility == Visibility.Collapsed,
                $"通用={panelGeneral.Visibility} 隐私={panelPrivacy.Visibility} 智能体={panelMcp.Visibility} 账号={panelAccount.Visibility}");

            // ② 点「隐私与记录」
            navPrivacy.IsChecked = true;
            Check("点「隐私与记录」切到对应面板",
                () => panelPrivacy.Visibility == Visibility.Visible
                      && panelGeneral.Visibility == Visibility.Collapsed,
                $"隐私={panelPrivacy.Visibility} 通用={panelGeneral.Visibility}");

            // ③ 点「智能体接入」——面板折叠时里面的控件仍必须在逻辑树上
            navMcp.IsChecked = true;
            var tokenInMcp = FindChild<TextBox>(page, "McpTokenTextBox");
            Check("切到「智能体接入」后令牌控件可见且可查找",
                () => panelMcp.Visibility == Visibility.Visible && tokenInMcp != null,
                $"面板={panelMcp.Visibility} 令牌框={(tokenInMcp != null ? "找到" : "丢失")}");

            // ④ 点「账号安全」：底部提示文案要跟着变，避免人以为保存按钮会存密码
            navAccount.IsChecked = true;
            var hint = FindChild<TextBlock>(page, "SaveHintTextBlock");
            Check("切到「账号安全」后底部提示说明保存按钮只存其他设置",
                () => panelAccount.Visibility == Visibility.Visible
                      && hint is not null
                      && (hint.Text ?? string.Empty).Contains("只保存其他设置", StringComparison.Ordinal),
                $"面板={panelAccount.Visibility} 提示={hint?.Text ?? "找不到"}");

            // ⑤ 切回「通用」：来回切换不能有面板漏显或同时显
            navGeneral.IsChecked = true;
            Check("切回「通用」后只有通用页显示",
                () => panelGeneral.Visibility == Visibility.Visible
                      && panelPrivacy.Visibility == Visibility.Collapsed
                      && panelMcp.Visibility == Visibility.Collapsed
                      && panelAccount.Visibility == Visibility.Collapsed,
                $"通用={panelGeneral.Visibility} 隐私={panelPrivacy.Visibility} 智能体={panelMcp.Visibility} 账号={panelAccount.Visibility}");
        }
        catch (Exception ex)
        {
            Fail("设置页顶部页签可用", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 把「系统设置」四个页签各离屏渲染成一张 PNG（改完 UI 自己先看一眼）。
    /// <para>
    /// 不开窗口、不登录、不碰真实数据（探针环境已指向隔离目录）。
    /// 用 RenderTargetBitmap 直接渲染已经 Measure/Arrange 过的页面 ——
    /// 不用 Show() 的原因：探针进程里 Show 窗口会撞 ModernWpf 的 WindowChrome
    /// 延迟资源引用失效（堆栈落在 BindingExpression.Deactivate），与页面本身无关。
    /// </para>
    /// </summary>
    /// <summary>
    /// 把 FRP 页的四个页签离屏渲染成 PNG（不 Show、不启动进程）。
    /// <para>
    /// 会**只读**加载一次真实配置（%APPDATA%\FrpManager）—— 这正是要看的效果：
    /// 老板现有档案能不能正确显示出来。全程不写任何文件。
    /// </para>
    /// </summary>
    private static int RenderFrpShots()
    {
        try
        {
            SeedConfig();

            if (Application.Current is null)
            {
                var app = new App();
                app.InitializeComponent();
            }

            Application.Current!.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ModernWpf.ThemeManager.Current.ApplicationTheme = ModernWpf.ApplicationTheme.Light;

            const int width = 1120;
            const int height = 1040;

            var page = new FrpPage
            {
                Width = width,
                Height = height,
                Background = (Application.Current?.TryFindResource("ApplicationPageBackgroundThemeBrush") as Brush)
                             ?? Brushes.White
            };

            // 不 Show 就不会触发 Loaded，这里手动跑一次初始化
            // （要指定参数类型：Page 上还有同名的 OnLoaded 重载，不指定会 AmbiguousMatch）
            typeof(FrpPage)
                .GetMethod(
                    "OnLoaded",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    types: new[] { typeof(object), typeof(RoutedEventArgs) },
                    modifiers: null)
                ?.Invoke(page, new object?[] { page, new RoutedEventArgs() });

            // 日志页要有内容才看得出配色（内存状态，不落盘）
            ServerOpsHelper.Frp.FrpService.Instance.State.AppendLog("[系统] 已启动 frpc (PID 12345)", "sys");
            ServerOpsHelper.Frp.FrpService.Instance.State.AppendLog("2026/11/06 22:30:01 [I] [service.go:282] login to server success", "ok");
            ServerOpsHelper.Frp.FrpService.Instance.State.AppendLog("[错误] 示例：连接被拒绝", "err");

            // 没有消息循环，LogChanged 的 Dispatcher 回调不会自己跑，这里手动刷一次
            typeof(FrpPage)
                .GetMethod("AppendLogsFromState", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(page, new object?[] { 0L });

            page.Measure(new Size(width, height));
            page.Arrange(new Rect(0, 0, width, height));
            page.UpdateLayout();

            var outDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shots");
            Directory.CreateDirectory(outDir);

            var tabs = new (string Nav, string File)[]
            {
                ("NavFrpOverview", "frp-1-overview.png"),
                ("NavFrpRules", "frp-2-rules.png"),
                ("NavFrpServer", "frp-3-server.png"),
                ("NavFrpLogs", "frp-4-logs.png"),
            };

            foreach (var (navName, fileName) in tabs)
            {
                if (FindChild<RadioButton>(page, navName) is { } nav)
                {
                    nav.IsChecked = true;
                    page.UpdateLayout();
                }

                var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(page);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));

                var path = Path.Combine(outDir, fileName);
                using (var fs = File.Create(path))
                {
                    encoder.Save(fs);
                }

                Console.WriteLine($"已渲染 {fileName}");
            }

            Console.WriteLine($"输出目录：{outDir}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"渲染失败：{ex}");
            return 1;
        }
    }

    /// <summary>
    /// 把服务器详情页离屏渲染成 PNG（不 Show、不登录、不连任何机器）。
    /// <para>
    /// 专门用来看「扩展动作」那一行有没有把标题行挤坏：WebView2 与 SFTP 面板没初始化时是空框，
    /// 但标题 / 主机信息 / 动作区的布局是真实的。
    /// </para>
    /// </summary>
    private static int RenderServerDetailShot()
    {
        try
        {
            SeedConfig();

            if (Application.Current is null)
            {
                var app = new App();
                app.InitializeComponent();
            }

            Application.Current!.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ModernWpf.ThemeManager.Current.ApplicationTheme = ModernWpf.ApplicationTheme.Light;

            // 造一个演示扩展，否则「扩展动作」区是折叠的，截了也看不出效果
            var demoDir = Path.Combine(ExtensionService.Instance.ExtensionsDirectory, "demo");
            Directory.CreateDirectory(demoDir);
            File.WriteAllText(Path.Combine(demoDir, "extension.json"), """
            {
              "id": "demo", "name": "部署与巡检", "version": "1.0.0",
              "description": "我自己的常用动作",
              "commands": [
                { "id": "restart_nginx", "name": "重载 Nginx", "command": "sudo nginx -t", "writeAccess": true },
                { "id": "tail_log", "name": "看日志", "command": "tail -n 100 /var/log/nginx/error.log", "writeAccess": false }
              ]
            }
            """, new UTF8Encoding(false));

            ExtensionService.Instance.Reload();

            var outDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shots");
            Directory.CreateDirectory(outDir);

            const int width = 1100;
            const int height = 720;

            var page = new ServerDetailPage(new ServerInfo
            {
                Name = "web-01",
                Host = "10.0.0.1",
                Port = 22,
                Username = "root",
                OsType = ServerOsType.Linux,
                Description = "生产 Web 节点"
            })
            {
                Width = width,
                Height = height,
                Background = (Application.Current?.TryFindResource("ApplicationPageBackgroundThemeBrush") as Brush)
                             ?? Brushes.White
            };

            typeof(ServerDetailPage)
                .GetMethod("LoadExtensionActions", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(page, null);

            page.Measure(new Size(width, height));
            page.Arrange(new Rect(0, 0, width, height));
            page.UpdateLayout();

            var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(page);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));

            var path = Path.Combine(outDir, "server-detail.png");
            using (var fs = File.Create(path))
            {
                encoder.Save(fs);
            }

            Console.WriteLine($"已渲染 server-detail.png → {path}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"渲染失败：{ex}");
            return 1;
        }
    }

    private static int RenderSettingsShots()
    {
        try
        {
            // 设置页里的 StaticResource（HeaderTextBlockStyle 等）来自 App.xaml，
            // 必须先把 App 实例化并加载资源字典，否则 XamlReader 直接抛。
            SeedConfig();
            if (Application.Current is null)
            {
                var app = new App();
                app.InitializeComponent();
            }

            Application.Current!.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // 渲染用浅色主题：老板实际用的是 Light，深色底下看不出灰字和分隔线
            ModernWpf.ThemeManager.Current.ApplicationTheme = ModernWpf.ApplicationTheme.Light;

            var outDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shots");
            Directory.CreateDirectory(outDir);

            const int width = 980;
            const int height = 760;

            var page = new SettingsPage
            {
                Width = width,
                Height = height,
                Background = (Application.Current?.TryFindResource("ApplicationPageBackgroundThemeBrush") as Brush)
                             ?? Brushes.White
            };

            page.Measure(new Size(width, height));
            page.Arrange(new Rect(0, 0, width, height));
            page.UpdateLayout();

            // 把 MCP 那一块按真实配置填一遍（端口 / 令牌 / 状态文字），否则截出来是空框
            typeof(SettingsPage)
                .GetMethod("LoadMcpSection", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(page, new object[] { ConfigService.Instance.LoadSettings() });

            page.UpdateLayout();

            // 扩展页要有内容才看得出布局：在隔离目录里临时造一个演示扩展
            try
            {
                var demoDir = Path.Combine(ExtensionService.Instance.ExtensionsDirectory, "demo");
                Directory.CreateDirectory(demoDir);
                File.WriteAllText(Path.Combine(demoDir, "extension.json"), """
                {
                  "id": "demo", "name": "部署与巡检", "version": "1.0.0",
                  "description": "我自己的常用动作", "author": "示例",
                  "commands": [
                    { "id": "restart_nginx", "name": "重载 Nginx", "command": "sudo nginx -t", "writeAccess": true },
                    { "id": "tail_log", "name": "看日志", "command": "tail -n 100 /var/log/nginx/error.log", "writeAccess": false }
                  ]
                }
                """, new UTF8Encoding(false));

                ExtensionService.Instance.Reload();

                typeof(SettingsPage)
                    .GetMethod("LoadExtensionsSection", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(page, null);

                page.UpdateLayout();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"演示扩展写入失败：{ex.Message}");
            }

            var tabs = new (string Nav, string File)[]
            {
                ("NavGeneral", "settings-1-general.png"),
                ("NavPrivacy", "settings-2-privacy.png"),
                ("NavMcp", "settings-3-mcp.png"),
                ("NavExtensions", "settings-4-extensions.png"),
                ("NavAccount", "settings-5-account.png"),
            };

            foreach (var (navName, fileName) in tabs)
            {
                if (FindChild<RadioButton>(page, navName) is { } nav)
                {
                    nav.IsChecked = true;
                    page.UpdateLayout();
                }

                var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(page);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));

                var path = Path.Combine(outDir, fileName);
                using (var fs = File.Create(path))
                {
                    encoder.Save(fs);
                }

                Console.WriteLine($"已渲染 {fileName}");
            }

            Console.WriteLine($"输出目录：{outDir}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"渲染失败：{ex}");
            return 1;
        }
    }

    /// <summary>
    /// 按名字在**逻辑树**里找控件。
    /// <para>
    /// 不能用可视化树：Page 没有 Show 过时没走模板化，VisualTreeHelper 的子树是空的，
    /// 而 XAML 里声明的面板与控件在逻辑树上一定存在。
    /// </para>
    /// </summary>
    private static T? FindChild<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T match && match.Name == name)
        {
            return match;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node)
            {
                continue;
            }

            var found = FindChild<T>(node, name);
            if (found != null)
            {
                return found;
            }
        }

        // 兜底：逻辑树里没有的（例如控件模板内部）再从可视化树找一遍。
        // 必须先判 Visual：逻辑树会把 RowDefinition 这类非 Visual 对象也吐出来，
        // 直接调 VisualTreeHelper 会抛"不是 Visual 或 Visual3D"。
        if (root is not (Visual or System.Windows.Media.Media3D.Visual3D))
        {
            return null;
        }

        var visualCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < visualCount; i++)
        {
            var found = FindChild<T>(VisualTreeHelper.GetChild(root, i), name);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static void RunMcpHttpChecks()
    {
        var port = FindFreePort();
        var token = "probe-" + Guid.NewGuid().ToString("N");
        var host = new McpHttpHost();

        try
        {
            host.Start(port, token);

            Check("HTTP 服务只监听 127.0.0.1",
                () => host.IsRunning && host.Port == port,
                host.LastError ?? "未启动");

            var body = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "initialize",
                ["params"] = new JsonObject { ["protocolVersion"] = "2025-06-18" }
            }.ToJsonString();

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

            var noAuth = PostMcp(http, port, "/mcp", body, null, null);
            Check("HTTP 无令牌返回 401",
                () => noAuth.StatusCode == HttpStatusCode.Unauthorized,
                noAuth.StatusCode.ToString());

            var badAuth = PostMcp(http, port, "/mcp", body, "wrong-token", null);
            Check("HTTP 错误令牌返回 401",
                () => badAuth.StatusCode == HttpStatusCode.Unauthorized,
                badAuth.StatusCode.ToString());

            var ok = PostMcp(http, port, "/mcp", body, token, null);
            var okText = ok.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check("HTTP 正确令牌返回 200",
                () => ok.StatusCode == HttpStatusCode.OK,
                ok.StatusCode.ToString());
            Check("HTTP 返回 initialize 结果",
                () => okText.Contains("protocolVersion", StringComparison.Ordinal)
                      && okText.Contains("ServerOpsHelper", StringComparison.Ordinal),
                Truncate(okText, 200));

            var sse = PostMcp(http, port, "/mcp", body, token, "text/event-stream");
            var sseText = sse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check("客户端只要 SSE 时按事件流返回",
                () => sse.Content.Headers.ContentType?.MediaType == "text/event-stream"
                      && sseText.StartsWith("event: message", StringComparison.Ordinal),
                Truncate(sseText, 160));

            var notFound = PostMcp(http, port, "/admin", body, token, null);
            Check("非 /mcp 路径返回 404",
                () => notFound.StatusCode == HttpStatusCode.NotFound,
                notFound.StatusCode.ToString());

            var getRequest = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/mcp");
            getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var getResponse = http.SendAsync(getRequest).GetAwaiter().GetResult();
            Check("GET /mcp 返回 405（不实现 SSE 长连接）",
                () => getResponse.StatusCode == HttpStatusCode.MethodNotAllowed,
                getResponse.StatusCode.ToString());

            var malformed = PostMcp(http, port, "/mcp", "{this is not json", token, null);
            var malformedText = malformed.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check("坏 JSON 回 -32700",
                () => malformedText.Contains("-32700", StringComparison.Ordinal),
                Truncate(malformedText, 200));

            // 超大请求体：只发头（Content-Length 超限），服务端必须在读 body 前就拒绝
            var oversized = RawHttp(
                port,
                "POST /mcp HTTP/1.1\r\n" +
                $"Host: 127.0.0.1:{port}\r\n" +
                $"Authorization: Bearer {token}\r\n" +
                "Content-Type: application/json\r\n" +
                $"Content-Length: {8 * 1024 * 1024 + 1}\r\n" +
                "\r\n");
            Check("请求体超过 8 MiB 时回 413",
                () => oversized.StartsWith("HTTP/1.1 413", StringComparison.Ordinal),
                Truncate(oversized, 200));
            Check("413 提示改用 upload_file",
                () => oversized.Contains("upload_file", StringComparison.Ordinal),
                Truncate(oversized, 300));
        }
        finally
        {
            host.Stop();
        }

        Check("Stop 后服务标记为已停止", () => !host.IsRunning, "仍在运行");

        var stillListening = true;
        try
        {
            using var probe = new TcpClient();
            probe.Connect(IPAddress.Loopback, port);
        }
        catch (SocketException)
        {
            stillListening = false;
        }

        Check("Stop 后端口已释放", () => !stillListening, "端口仍可连接");
    }

    // ───────────────────────── MCP 辅助 ─────────────────────────

    private static JsonObject? Rpc(
        McpServerCore core,
        string method,
        JsonObject? parameters,
        int id = 1,
        bool notification = false)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };

        if (!notification)
        {
            request["id"] = id;
        }

        if (parameters != null)
        {
            request["params"] = parameters;
        }

        return core.HandleAsync(request, "probe", CancellationToken.None).GetAwaiter().GetResult();
    }

    private static JsonObject CallTool(McpServerCore core, string name, JsonObject arguments)
        => Rpc(core, "tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments }, 10)!;

    private static string ToolText(JsonObject response)
        => response["result"]?["content"] is JsonArray { Count: > 0 } content
            ? content[0]?["text"]?.GetValue<string>() ?? string.Empty
            : string.Empty;

    private static HttpResponseMessage PostMcp(
        HttpClient http,
        int port,
        string path,
        string body,
        string? token,
        string? accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}{path}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (!string.IsNullOrEmpty(accept))
        {
            request.Headers.TryAddWithoutValidation("Accept", accept);
        }

        return http.SendAsync(request).GetAwaiter().GetResult();
    }

    /// <summary>发原始 HTTP 文本并读回整个响应（用于构造 HttpClient 不肯发的畸形请求）。</summary>
    private static string RawHttp(int port, string requestText)
    {
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        client.ReceiveTimeout = 5000;

        using var stream = client.GetStream();
        var bytes = Encoding.UTF8.GetBytes(requestText);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();

        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void SeedMcpServers()
    {
        var servers = new[]
        {
            new ServerInfo
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Name = "probe-web",
                Host = "127.0.0.1",
                Port = 1,
                Username = "root",
                Password = ProbeSecret,
                OsType = ServerOsType.Linux,
                Tags = "prod,web",
                Description = "探针用假服务器"
            },
            new ServerInfo
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Name = "probe-db",
                Host = "127.0.0.1",
                Port = 2,
                Username = "dba",
                Password = string.Empty,
                OsType = ServerOsType.Linux,
                Tags = "prod,db",
                Description = "探针用假数据库"
            }
        };

        File.WriteAllText(
            Path.Combine(IsolatedConfigDir, "servers.json"),
            System.Text.Json.JsonSerializer.Serialize(servers),
            Encoding.UTF8);
    }

    private static void SetMcpAllowWrite(bool allowWrite)
    {
        File.WriteAllText(
            Path.Combine(IsolatedConfigDir, "settings.json"),
            System.Text.Json.JsonSerializer.Serialize(new AppSettings
            {
                Theme = "Light",
                McpAllowWriteTools = allowWrite
            }),
            Encoding.UTF8);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
