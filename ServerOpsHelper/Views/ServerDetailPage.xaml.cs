using Microsoft.Web.WebView2.Core;
using ServerOpsHelper.Models;
using ServerOpsHelper.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace ServerOpsHelper.Views;

/// <summary>
/// 服务器详情页 = 真 SSH 终端。
/// <para>
/// 2026-10-01 改造：原来是「输入框 + 执行命令」的单命令执行器（每条命令新建一次
/// SSH 连接、纯文本输出），对齐不了 FinalShell。现在是 WebView2 里跑 xterm.js 的
/// 真交互终端：PTY 会话（SSH.NET ShellStream）、颜色、vim/htop、Tab 补全、
/// 上下键历史、Ctrl+C。数据流：
/// </para>
/// <para>
/// xterm.js 键入 → postMessage → <see cref="RemoteTerminalSession.Write"/> → 远端；
/// 远端输出 → <see cref="RemoteTerminalSession.OutputReceived"/>（后台线程）→
/// Dispatcher → postMessage → xterm.js 渲染。
/// </para>
/// </summary>
public partial class ServerDetailPage : Page
{
    /// <summary>WebView2 的虚拟主机名，映射到程序目录下的 TerminalAssets。</summary>
    private const string TerminalHost = "terminal.appassets.local";

    private static readonly JsonSerializerOptions MessageJsonOptions = new()
    {
        // 前端发来的字段是小驼峰（type/cols/rows），这里关闭大小写敏感
        PropertyNameCaseInsensitive = true,
    };

    private readonly ServerInfo _server;
    private readonly RemoteTerminalSession _session = new();
    private bool _sessionStarted;
    private bool _pageClosed;

    public ServerDetailPage(ServerInfo server)
    {
        InitializeComponent();
        _server = server;

        Loaded += OnLoaded;
        // Frame 导航离开本页时触发 Unloaded —— 断开 SSH，别让连接挂着
        Unloaded += (_, _) => CloseSession();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TitleTextBlock.Text = _server.Name;
        HostTextBlock.Text = $"地址：{_server.Host}:{_server.Port}（{_server.Username}）";
        OsTypeTextBlock.Text = $"系统：{_server.OsType}";
        DescriptionTextBlock.Text = string.IsNullOrWhiteSpace(_server.Description)
            ? "暂无备注"
            : $"备注：{_server.Description}";

        _ = InitTerminalAsync();
        // 文件面板与监控各用自己的连接（与终端通道独立），某一个连不上不影响其他两个
        _ = FilePanel.InitializeAsync(_server);
        _ = MonitorPanel.InitializeAsync(_server);

        LoadExtensionActions();
    }

    // ───────────────────────── 扩展动作 ─────────────────────────

    /// <summary>下拉框里的一项（把一个扩展命令摊平成可绑定的对象）</summary>
    private sealed class ExtensionActionItem
    {
        public required ExtensionPack Pack { get; init; }

        public required ExtensionCommand Command { get; init; }

        public string DisplayName => Command.Name;
    }

    /// <summary>
    /// 把用户扩展里的命令填进下拉框。没有扩展时整块隐藏，页面上不多出空控件。
    /// </summary>
    private void LoadExtensionActions()
    {
        var items = ExtensionService.Instance.AllCommands()
            .Select(pair => new ExtensionActionItem { Pack = pair.Pack, Command = pair.Command })
            .OrderBy(item => item.Pack.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Command.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ExtensionCommandComboBox.ItemsSource = items;
        ExtensionActionPanel.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 默认选中第一条：只有一个扩展动作时（很常见）连点都不用点
        if (items.Count > 0)
        {
            ExtensionCommandComboBox.SelectedIndex = 0;
        }
    }

    private async void RunExtensionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExtensionCommandComboBox.SelectedItem is not ExtensionActionItem item)
        {
            Dialogs.Info("请先在下拉框里选择一个扩展动作。");
            return;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (item.Command.Parameters.Count > 0)
        {
            var dialog = new ExtensionRunDialog(item.Pack, item.Command)
            {
                Owner = Window.GetWindow(this)
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            foreach (var (key, value) in dialog.Values)
            {
                values[key] = value;
            }
        }

        RunExtensionButton.IsEnabled = false;
        RunExtensionButton.Content = "执行中…";

        try
        {
            var result = await ExtensionService.Instance.ExecuteAsync(_server, item.Command, values);

            new ExtensionResultWindow($"{item.Pack.Name} · {item.Command.Name}", result)
            {
                Owner = Window.GetWindow(this)
            }.ShowDialog();
        }
        catch (ExtensionException ex)
        {
            Dialogs.Error(ex.Message, "参数有问题");
        }
        catch (Exception ex)
        {
            Dialogs.Error($"执行失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            RunExtensionButton.IsEnabled = true;
            RunExtensionButton.Content = "执行";
        }
    }

    /// <summary>
    /// 初始化 WebView2 并加载终端页（xterm.js 走虚拟主机映射，离线可用）。
    /// </summary>
    private async Task InitTerminalAsync()
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "TerminalAssets");
        if (!File.Exists(Path.Combine(assets, "terminal.html")))
        {
            ShowHint($"终端资源缺失（{assets}）。请重新安装程序，或检查安装包是否完整。");
            return;
        }

        try
        {
            await TerminalWebView.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            ShowHint($"终端组件初始化失败：{ex.Message}\n\n请确认系统已安装 WebView2 Runtime。");
            return;
        }

        var core = TerminalWebView.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(
            TerminalHost, assets, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnTerminalMessage;
        core.Navigate($"https://{TerminalHost}/terminal.html");
    }

    /// <summary>
    /// 终端页 ↔ 会话 的消息桥。ready 由 xterm 初始化完成后发出（带初始行列数），
    /// 宿主据此发起 SSH 连接；之后 input/resize 实时转发。
    /// </summary>
    private void OnTerminalMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        TerminalMessage? msg;
        try
        {
            msg = JsonSerializer.Deserialize<TerminalMessage>(e.WebMessageAsJson, MessageJsonOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (string.IsNullOrEmpty(msg?.Type))
        {
            return;
        }

        switch (msg.Type)
        {
            case "ready":
                StartSessionOnce((uint)Math.Max(2, msg.Cols), (uint)Math.Max(2, msg.Rows));
                break;

            case "input" when msg.Data != null:
                _session.Write(msg.Data);
                break;

            case "resize" when msg.Cols > 0 && msg.Rows > 0:
                _session.Resize((uint)msg.Cols, (uint)msg.Rows);
                break;
        }
    }

    private void StartSessionOnce(uint columns, uint rows)
    {
        if (_sessionStarted)
        {
            return;
        }
        _sessionStarted = true;

        // 三个事件都来自 SSH.NET 后台线程 —— 统一调度回 UI 线程再发 WebView2
        _session.OutputReceived += text => PostToTerminal(new { type = "output", data = text });
        _session.StatusChanged += text => PostToTerminal(new { type = "status", data = text });
        _session.Disconnected += text =>
        {
            PostToTerminal(new { type = "status", data = text });
            PostToTerminal(new
            {
                type = "output",
                data = "\r\n\x1b[38;5;245m[ 会话已结束。重新进入本页即可重新连接 ]\x1b[0m\r\n",
            });
        };

        _ = _session.ConnectAsync(_server, columns, rows);
    }

    /// <summary>
    /// 把消息发给 xterm.js。匿名对象的小写属性名即 JSON 字段名（前端按 type/data 取值）。
    /// 页面关闭后 WebView2 已释放，所有发送都静默吞掉。
    /// </summary>
    private void PostToTerminal(object message)
    {
        if (_pageClosed)
        {
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_pageClosed)
                {
                    return;
                }
                try
                {
                    TerminalWebView.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(message));
                }
                catch (Exception)
                {
                    // 导航离开/关闭窗口后 WebView 已不可用，忽略
                }
            });
        }
        catch (Exception)
        {
            // Dispatcher 已停（窗口关闭），忽略
        }
    }

    private void ShowHint(string text)
    {
        TerminalHintTextBlock.Text = text;
        TerminalHintTextBlock.Visibility = Visibility.Visible;
    }

    private void CloseSession()
    {
        _pageClosed = true;
        _session.Dispose();
        FilePanel.Close();
        MonitorPanel.Close();
    }
}

/// <summary>终端页 ↔ 宿主 的消息（前端小驼峰，解析时忽略大小写）。</summary>
internal sealed class TerminalMessage
{
    public string? Type { get; set; }
    public string? Data { get; set; }
    public int Cols { get; set; }
    public int Rows { get; set; }
}
