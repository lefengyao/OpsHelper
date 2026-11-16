using ModernWpf;
using ServerOpsHelper.Mcp;
using ServerOpsHelper.Services;
using ServerOpsHelper.Views;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ServerOpsHelper;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 无界面模式（被 MCP 客户端以 <c>--mcp-stdio</c> 拉起的子进程）。
    /// <para>
    /// 这个模式下绝不能弹任何 MessageBox：MCP 客户端把进程挂在后台，
    /// 弹窗会一直挂在那儿等一个永远没人点的"确定"，表现为"智能体卡住不响应"。
    /// </para>
    /// </summary>
    private bool _headless;

    /// <summary>
    /// UI 线程未处理异常的弹窗次数上限。
    /// <para>
    /// 有些异常会在每次布局 / 重绘时重复触发，不设上限会被弹窗淹没，
    /// 反而让人没法继续操作去保存现场。
    /// </para>
    /// </summary>
    private int _crashDialogCount;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常兜底必须**最先**挂上：挂晚了，之前抛出的异常仍然没人接，
        // 应用会直接消失，用户连一句报错都看不到。
        InstallGlobalExceptionHandlers();

        // 扩展清单要在两个模式之前加载：stdio 模式下智能体的 tools/list 里也得有扩展工具。
        // Reload 内部已经把解析错误收进结果里，不会抛；这里再兜一层是防目录级 IO 意外。
        try
        {
            ExtensionService.Instance.Reload();
        }
        catch (Exception)
        {
            // 扩展坏了不能影响启动
        }

        // 智能体通过 stdio 拉起本程序时，只跑 MCP 循环，不登录、不开窗口
        if (IsStdioMode(e.Args))
        {
            StartStdioMode();
            return;
        }

        ApplySavedTheme();

        // 访问记录开关在启动时就生效，避免 Web 控制台还没打开就漏记
        var startupSettings = ConfigService.Instance.LoadSettings();
        VisitHistoryService.Instance.Configure(startupSettings.RecordVisitHistory);

        // 登录期间只有登录窗这一个窗口。ShutdownMode 默认是 OnLastWindowClose，
        // 登录成功时 DialogResult=true → Close() 会让 WPF 认为「最后一个窗口关了」而触发 Shutdown()，
        // 等这里回来建主窗口时关闭流程已经排队，表现为「登录成功后界面一闪就没」。
        // 因此登录阶段必须改成显式关闭，等主窗口出来后再交回「主窗口关闭即退出」。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var loginWindow = new LoginWindow();
        if (loginWindow.ShowDialog() != true)
        {
            Shutdown();
            return;
        }

        try
        {
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            // 主界面构造失败时不要再无声退出，把原因摆到台面上
            MessageBox.Show(
                $"主界面加载失败：{ex.GetType().Name}\n\n{ex.Message}",
                "启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // 登录窗已关闭，之后就是标准的「关掉主窗口即退出」
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // 智能体接入（HTTP 传输）：按设置启停。端口被占之类的失败只记在 LastError 里，
        // 由设置页展示 —— 一个可选的接入功能不该影响主程序能不能用。
        McpRuntime.Apply(startupSettings);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        McpRuntime.StopHttp();

        // FRP 只在这次会话真的打开过那个页面时才收尾：
        // 不停的话 frpc/frps 会变成没人管的孤儿进程，下次打开界面也看不到它、更停不掉。
        Frp.FrpService.ShutdownIfCreated();

        base.OnExit(e);
    }

    // ───────────────────────── MCP stdio 模式 ─────────────────────────

    private static bool IsStdioMode(string[] args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--mcp-stdio", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 进入无界面模式：不建窗口，只把 Dispatcher 留着让进程活着，
    /// MCP 循环跑完（客户端关掉 stdin）再退出。
    /// </summary>
    private void StartStdioMode()
    {
        _headless = true;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _ = RunStdioModeAsync();
    }

    private async Task RunStdioModeAsync()
    {
        var exitCode = 0;

        try
        {
            exitCode = await McpStdioHost.RunAsync();
        }
        catch (Exception ex)
        {
            WriteCrashLog("MCP stdio 模式异常退出", ex);
            exitCode = 1;
        }
        finally
        {
            // Shutdown 只能在 Dispatcher 线程上调；这里可能已经不在 UI 上下文中
            try
            {
                Dispatcher.Invoke(() => Shutdown(exitCode));
            }
            catch (Exception)
            {
                // 进程反正要退，兜底失败也无所谓
            }
        }
    }

    // ───────────────────────── 全局异常兜底 ─────────────────────────

    /// <summary>
    /// 崩溃日志目录（数据目录下的 logs 子目录，不碰任何既有数据文件）
    /// </summary>
    private static string CrashLogDirectory
        => Path.Combine(ConfigService.Instance.ConfigDirectory, "logs");

    /// <summary>
    /// 给三个可能"漏网"的异常源都挂上兜底。
    /// <para>
    /// 为什么必须有这一层（2026-09-25 修的）：应用此前**没有任何**全局异常处理，
    /// 于是任何一个从事件处理器（尤其是 <c>async void</c>）、绑定回调里逃逸的异常，
    /// 都会让进程直接退出 —— 用户看到的就是「软件自己没了」，既没有提示也没有日志，
    /// 完全无从排查。有了这层，最差也是弹一句话 + 落一份日志。
    /// </para>
    /// </summary>
    private void InstallGlobalExceptionHandlers()
    {
        // 1) UI 线程未处理异常 —— 最常见，能拦下
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 2) 非 UI 线程的致命异常 —— 拦不住（进程仍会退出），但至少留下证据
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // 3) 被丢弃的 Task 异常 —— 不处理会在 GC 时升级成进程级崩溃
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>
    /// UI 线程未处理异常：拦下来 + 记日志 + 告知用户。
    /// <para>
    /// 这里**必须**把 <c>Handled</c> 置为 true。不置的话 WPF 会直接让进程退出，
    /// 也就回到了"点一下软件就没了"的老样子。
    /// </para>
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logPath = WriteCrashLog("UI 线程未处理异常", e.Exception);
        e.Handled = true;

        // 无界面模式下弹窗没人点，只会把 MCP 客户端一起卡死 —— 只留日志
        if (_headless)
        {
            return;
        }

        if (_crashDialogCount >= 3)
        {
            // 已经在连环报错，别再一遍遍弹窗打断用户
            return;
        }

        _crashDialogCount++;

        var more = _crashDialogCount >= 3
            ? "\n\n（同类提示已达上限，后续不再弹出，异常仍会继续写入日志。）"
            : string.Empty;

        MessageBox.Show(
            $"界面操作出了点问题，已经拦下来，程序会继续运行。\n\n"
            + $"类型：{e.Exception.GetType().Name}\n"
            + $"信息：{e.Exception.Message}\n\n"
            + $"详情已记录到：\n{logPath}\n\n"
            + $"如果同样的操作再做一次还是报错，把这个日志文件发出来即可定位。{more}",
            "操作出错（已拦下）",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    /// <summary>
    /// 非 UI 线程的致命异常。这里已经无法阻止进程退出，只能留下现场。
    /// </summary>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        WriteCrashLog(
            $"非 UI 线程致命异常（IsTerminating={e.IsTerminating}）",
            e.ExceptionObject as Exception);
    }

    /// <summary>
    /// 被丢弃的 Task 异常。标为已观察，避免它在 GC 时升级成进程级崩溃。
    /// </summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog("未观察的 Task 异常", e.Exception);
        e.SetObserved();
    }

    /// <summary>
    /// 把异常追加写入崩溃日志，返回日志文件路径。
    /// <para>
    /// 整个方法不允许抛异常 —— 它是在异常处理器里跑的，
    /// 在这里再炸一次就没有任何兜底了，所以最外层直接吞掉一切。
    /// </para>
    /// </summary>
    private static string WriteCrashLog(string category, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(CrashLogDirectory);

            var path = Path.Combine(CrashLogDirectory, $"crash-{DateTime.Now:yyyyMMdd}.log");

            var sb = new StringBuilder();
            sb.AppendLine(new string('-', 70));
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {category}");
            sb.AppendLine($"程序版本：{typeof(App).Assembly.GetName().Version}");
            sb.AppendLine($"系统：{Environment.OSVersion} / .NET {Environment.Version}");
            sb.AppendLine(exception?.ToString() ?? "(没有异常对象)");
            sb.AppendLine();

            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }
        catch (Exception)
        {
            return "(日志写入失败)";
        }
    }

    /// <summary>
    /// 恢复上次保存的主题。
    /// <para>
    /// 这里刻意使用同步读取：OnStartup 运行在 UI 线程上且已安装 Dispatcher 同步上下文，
    /// 阻塞等待异步文件读取会死锁。
    /// </para>
    /// </summary>
    private static void ApplySavedTheme()
    {
        var settings = ConfigService.Instance.LoadSettings();

        ThemeManager.Current.ApplicationTheme =
            string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase)
                ? ApplicationTheme.Light
                : ApplicationTheme.Dark;
    }
}
