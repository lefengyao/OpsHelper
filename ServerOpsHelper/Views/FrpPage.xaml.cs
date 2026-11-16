using ServerOpsHelper.Frp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ServerOpsHelper.Views;

/// <summary>
/// FRP 穿透页：把 FrpManager（原本是 ASP.NET Core + Vue 的独立应用）搬成 OpsHelper 里的原生页签。
/// <para>
/// 共同点与差异：
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <b>配置完全共用</b>：读写 `%APPDATA%\FrpManager` 的同一批 profiles / state.json，
///       在 FrpManager 里配好的服务器、规则、frp 目录，这里直接能用。
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>去掉了 HTTP 层</b>：原实现是「Vue → REST → 后端」，这里 UI 直接调同一套 C# 类，
///       少一层进程和一个端口。协议语义（启动/停止/日志序号/档案切换）保持一致。
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>保存策略更保守</b>：原版是 debounce 自动保存；这里规则改动即时落盘，
///       服务器表单点「保存配置」才写，启动前会替用户再落一次盘（见 <see cref="StartStop_Click"/>）。
///     </description>
///   </item>
/// </list>
/// </summary>
public partial class FrpPage : Page
{
    private readonly FrpService _frp = FrpService.Instance;

    private readonly ObservableCollection<ProxyRule> _rules = new();

    private readonly ObservableCollection<LogRow> _logs = new();

    private readonly DispatcherTimer _uptimeTimer;

    private FrpcConfig _frpc = new();

    private FrpsConfig _frps = new();

    private string _mode = "frpc";

    private string _profile = FrpEnvironment.DefaultProfileName;

    /// <summary>正在编辑的规则在 <see cref="_rules"/> 里的下标；-1 表示"新增"</summary>
    private int _editingIndex = -1;

    /// <summary>加载期屏蔽 SelectionChanged / Toggled 的递归</summary>
    private bool _loading;

    private long _logSeq;

    private bool _installing;

    // 日志区是固定深色底（像终端），配色不跟主题走，避免浅色主题下白字白底
    private static readonly Brush LogNormal = Freeze("#d8dee9");
    private static readonly Brush LogError = Freeze("#ff8a80");
    private static readonly Brush LogSystem = Freeze("#82b1ff");

    public FrpPage()
    {
        InitializeComponent();

        RulesGrid.ItemsSource = _rules;
        FrpLogListBox.ItemsSource = _logs;

        FillStaticCombos();

        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += (_, _) => RefreshRuntime();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ───────────────────────── 生命周期 ─────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _frp.EnsureInitialized();

        _frp.State.LogChanged += OnLogChanged;
        _uptimeTimer.Start();

        // 进来时先把已有日志铺一遍（进程可能在别的页面跑过）
        AppendLogsFromState(0);

        RefreshProfiles(_frp.Environment.ActiveProfile);
        RefreshEnvironment();
        RefreshStatus();
        RefreshSummary();

        // 版本信息后台查一次（离线/被墙会失败，不能让页面卡住或弹错）
        _ = CheckVersionSilentlyAsync();
    }

    private async Task CheckVersionSilentlyAsync()
    {
        try
        {
            var info = await _frp.Installer.GetVersionInfoAsync();

            if (info.Stable is { Length: > 0 } stable)
            {
                VerStableTextBlock.Text = $"v{stable}";
            }

            VerLatestTextBlock.Text = info.Latest is { Length: > 0 } latest
                ? $"v{latest}"
                : (string.IsNullOrEmpty(info.LatestError) ? "—" : $"查询失败：{info.LatestError}");

            if (info.Current is { Length: > 0 } current)
            {
                VerCurrentTextBlock.Text = current;
            }
        }
        catch (Exception)
        {
            // 检查更新是锦上添花，失败就保持"—"
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _frp.State.LogChanged -= OnLogChanged;
        _uptimeTimer.Stop();
    }

    private void FillStaticCombos()
    {
        FrpModeComboBox.ItemsSource = new[] { "frpc", "frps" };

        RuleTypeComboBox.ItemsSource = new[] { "tcp", "udp", "http", "https", "tcpmux" };

        var protocols = new[]
        {
            new ProtocolOption("tcp", "TCP（默认，最稳）"),
            new ProtocolOption("kcp", "KCP（UDP，弱网友好）"),
            new ProtocolOption("quic", "QUIC（UDP，低延迟）"),
            new ProtocolOption("websocket", "WebSocket（可穿 CDN）"),
            new ProtocolOption("wss", "WSS（WebSocket + TLS）"),
        };

        SrvProtocolComboBox.ItemsSource = protocols;
        SrvProtocolComboBox.DisplayMemberPath = nameof(ProtocolOption.Label);
        SrvProtocolComboBox.SelectedValuePath = nameof(ProtocolOption.Value);

        SrvServerProtocolComboBox.ItemsSource = protocols;
        SrvServerProtocolComboBox.DisplayMemberPath = nameof(ProtocolOption.Label);
        SrvServerProtocolComboBox.SelectedValuePath = nameof(ProtocolOption.Value);
    }

    // ───────────────────────── 配置档案 ─────────────────────────

    private void RefreshProfiles(string? select)
    {
        _loading = true;

        try
        {
            var profiles = _frp.Environment.ListProfiles();

            FrpProfileComboBox.ItemsSource = profiles;

            var target = !string.IsNullOrWhiteSpace(select) && profiles.Contains(select)
                ? select
                : profiles.FirstOrDefault() ?? FrpEnvironment.DefaultProfileName;

            FrpProfileComboBox.SelectedItem = target;
            _profile = target;
        }
        finally
        {
            _loading = false;
        }

        LoadConfigForProfile();
    }

    private void LoadConfigForProfile()
    {
        _frpc = _frp.LoadFrpc(_profile);
        _frps = _frp.LoadFrps(_profile);

        _loading = true;
        try
        {
            FrpModeComboBox.SelectedItem = _mode;
        }
        finally
        {
            _loading = false;
        }

        LoadRules();
        LoadServerForm();
        RefreshSummary();
        RefreshStatus();
    }

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var name = FrpProfileComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(name) || name == _profile)
        {
            return;
        }

        if (!_frp.Environment.SetActiveProfile(name, out var message))
        {
            Dialogs.Error(message);
            return;
        }

        _profile = name;
        LoadConfigForProfile();
        RefreshEnvironment();
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptWindow.Prompt(Window.GetWindow(this), "新配置名（会从当前档案复制一份）");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (!_frp.Environment.CreateProfile(name, _profile, out var message))
        {
            Dialogs.Error(message);
            return;
        }

        _frp.Environment.SetActiveProfile(name.Trim(), out _);
        RefreshProfiles(name.Trim());
        RefreshEnvironment();
    }

    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptWindow.Prompt(Window.GetWindow(this), "重命名为", _profile);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == _profile)
        {
            return;
        }

        if (!_frp.Environment.RenameProfile(_profile, name, out var message))
        {
            Dialogs.Error(message);
            return;
        }

        RefreshProfiles(name.Trim());
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_frp.Environment.ListProfiles().Count <= 1)
        {
            Dialogs.Error("至少要保留一个配置档案。");
            return;
        }

        var ok = await Dialogs.ConfirmAsync(
            "删除配置档案",
            $"确定删除「{_profile}」？里面的 frpc.toml / frps.toml 会一起删掉，无法恢复。",
            "删除",
            danger: true);

        if (!ok)
        {
            return;
        }

        if (!_frp.Environment.DeleteProfile(_profile, out var message))
        {
            Dialogs.Error(message);
            return;
        }

        RefreshProfiles(_frp.Environment.ActiveProfile);
        RefreshEnvironment();
    }

    // ───────────────────────── 顶部工具条 / 状态 ─────────────────────────

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _mode = FrpModeComboBox.SelectedItem as string ?? "frpc";
        LoadServerForm();
        RefreshStatus();
        RefreshSummary();
    }

    private void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_frp.State.IsRunning)
        {
            _frp.Process.Stop();
            RefreshStatus();
            RefreshSummary();
            return;
        }

        // 启动前替用户落一次盘：否则"改了表单没点保存"会起成旧配置，很难自己发现
        if (SrvTextModeCheck.IsChecked != true && !SaveCurrentForm(silent: true))
        {
            return;
        }

        _frp.Process.Start(_mode);

        RefreshStatus();
        RefreshSummary();
        RefreshEnvironment();
    }

    private void RefreshStatus()
    {
        var state = _frp.State;
        var running = state.IsRunning;

        FrpStatusTextBlock.Text = running ? $"运行中 · {state.CurrentMode}" : "已停止";
        FrpStatusTextBlock.Foreground = running
            ? Freeze("#2e9e5b")
            : (Brush)(TryFindResource("SystemControlForegroundBaseMediumBrush") ?? Brushes.Gray);

        FrpStartStopButton.Content = running ? $"停止 {state.CurrentMode}" : $"启动 {_mode}";

        FrpRunningHintTextBlock.Text = running && state.CurrentMode != _mode
            ? $"正在运行的是 {state.CurrentMode}，与当前选择不同 —— 切换后需要重新启动"
            : string.Empty;

        OvModeTextBlock.Text = _mode;
        OvPidTextBlock.Text = running && state.ProcessId is { } pid ? $"PID {pid}（{state.CurrentMode}）" : "未运行";
        OvConfigPathTextBlock.Text = _frp.ConfigPath(_mode);
        OvConfigPathTextBlock.ToolTip = OvConfigPathTextBlock.Text;

        RefreshRuntime();
    }

    private void RefreshRuntime()
    {
        if (!_frp.State.IsRunning || _frp.State.StartedAt is not { } started)
        {
            OvUptimeTextBlock.Text = "—";
            return;
        }

        var span = DateTime.Now - started;

        OvUptimeTextBlock.Text = span.TotalHours >= 1
            ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分 {span.Seconds} 秒"
            : $"{span.Minutes} 分 {span.Seconds} 秒";
    }

    private void RefreshSummary()
    {
        OvProfileTextBlock.Text = _profile;

        OvTargetTextBlock.Text = _mode == "frpc"
            ? $"{_frpc.ServerAddr}:{_frpc.ServerPort}"
            : $"{_frps.BindAddr}:{_frps.BindPort}";

        var proxies = _frpc.Proxies.Count;
        OvRuleCountTextBlock.Text = _mode == "frpc" ? $"{proxies} 条" : "—（frps 不配置转发规则）";

        var warn = _mode == "frpc" && proxies == 0
            ? "当前档案还没有转发规则，启动后不会有任何端口/域名映射。"
            : string.Empty;

        OvWarnTextBlock.Text = warn;
        OvWarnTextBlock.Visibility = warn.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshEnvironment()
    {
        var env = _frp.Environment;
        var ok = env.Validate(out var message);

        FrpDirTextBox.Text = env.FrpDirectory;

        FrpDirHintTextBlock.Text = ok
            ? $"可用：{env.FrpDirectory}"
            : $"不可用：{message}。点下面「安装 / 切到稳定版」可自动下载 frp（约 20MB，需联网），也可以自己选目录。";

        FrpDirHintTextBlock.Foreground = ok
            ? Freeze("#2e9e5b")
            : Freeze("#c4553d");

        VerCurrentTextBlock.Text = _frp.Installer.GetLocalVersion() ?? "未检测到";
    }

    private void BrowseFrpDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择 frpc.exe / frps.exe 所在目录",
            Multiselect = false
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            FrpDirTextBox.Text = dialog.FolderName;
        }
    }

    private void AutoLocateFrpDir_Click(object sender, RoutedEventArgs e)
    {
        if (_frp.Environment.TryLocateAutomatically())
        {
            RefreshEnvironment();
            Dialogs.Info($"已定位到：{_frp.Environment.FrpDirectory}");
            return;
        }

        Dialogs.Error("没有在程序目录或 PATH 里找到 frpc/frps，请手动指定。");
    }

    private void ApplyFrpDir_Click(object sender, RoutedEventArgs e)
    {
        var path = FrpDirTextBox.Text.Trim();

        _frp.Environment.FrpDirectory = path;

        if (!_frp.Environment.Validate(out var message))
        {
            Dialogs.Error(message);
            RefreshEnvironment();
            return;
        }

        RefreshEnvironment();
        Dialogs.Info("FRP 目录已更新。");
    }

    // ───────────────────────── 概览：版本 / 安装 ─────────────────────────

    private async void CheckVersion_Click(object sender, RoutedEventArgs e)
    {
        VerLatestTextBlock.Text = "检查中…";

        try
        {
            var info = await _frp.Installer.GetVersionInfoAsync();

            VerCurrentTextBlock.Text = info.Current ?? "未检测到";
            VerStableTextBlock.Text = info.Stable is { Length: > 0 } s ? $"v{s}" : "—";
            VerLatestTextBlock.Text = info.Latest is { Length: > 0 } l
                ? $"v{l}"
                : $"查询失败：{info.LatestError ?? "未知原因"}";
        }
        catch (Exception ex)
        {
            VerLatestTextBlock.Text = $"查询失败：{ex.Message}";
        }
    }

    private async void InstallFrp_Click(object sender, RoutedEventArgs e)
    {
        if (_installing)
        {
            return;
        }

        var wantLatest = (sender as FrameworkElement)?.Tag as string == "latest";
        string? version = null;

        if (wantLatest)
        {
            // 最新版号要现查：界面上的"最新版本"可能还没查过，或者已经过期
            try
            {
                var info = await _frp.Installer.GetVersionInfoAsync();

                version = info.Latest;

                if (string.IsNullOrWhiteSpace(version))
                {
                    Dialogs.Error($"没查到最新版本号：{info.LatestError ?? "请先点「检查更新」"}");
                    return;
                }
            }
            catch (Exception ex)
            {
                Dialogs.Error($"查询最新版本失败：{ex.Message}");
                return;
            }
        }

        var target = version == null ? "推荐稳定版" : $"最新版 v{version}";

        var ok = await Dialogs.ConfirmAsync(
            "安装 / 更新 frp",
            $"会从 GitHub 下载 frp 官方发布包（{target}），解压到当前 FRP 目录并覆盖 frpc/frps。\n" +
            "正在运行的 frp 进程会先自动停止。是否继续？",
            "下载并安装");

        if (!ok)
        {
            return;
        }

        // 与原版一致：运行中先停，而不是让用户自己去点停止
        if (_frp.State.IsRunning)
        {
            _frp.Process.Stop();
            RefreshStatus();
        }

        _installing = true;
        VerHintTextBlock.Text = "正在下载并安装，请稍候…";

        try
        {
            var result = await _frp.Installer.InstallAsync(version);

            VerHintTextBlock.Text = result.Message ?? (result.Ok ? "安装完成。" : "安装失败。");

            if (result.Ok)
            {
                RefreshEnvironment();
            }
        }
        catch (Exception ex)
        {
            VerHintTextBlock.Text = $"安装失败：{ex.Message}";
        }
        finally
        {
            _installing = false;
        }
    }

    // ───────────────────────── 转发规则 ─────────────────────────

    private void LoadRules()
    {
        _rules.Clear();

        foreach (var rule in _frpc.Proxies)
        {
            _rules.Add(rule.Clone());
        }

        _editingIndex = -1;
        ClearRuleForm();
    }

    private void RulesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || RulesGrid.SelectedIndex < 0 || RulesGrid.SelectedIndex >= _rules.Count)
        {
            return;
        }

        FillRuleForm(RulesGrid.SelectedIndex);
    }

    private void FillRuleForm(int index)
    {
        var rule = _rules[index];
        _editingIndex = index;

        RuleEditorTitleTextBlock.Text = $"规则：{rule.Name}";

        RuleNameTextBox.Text = rule.Name;
        RuleTypeComboBox.SelectedItem = rule.Type;
        RuleLocalIpTextBox.Text = rule.LocalIP;
        RuleLocalPortTextBox.Text = rule.LocalPort.ToString(CultureInfo.InvariantCulture);
        RuleRemotePortTextBox.Text = rule.RemotePort.ToString(CultureInfo.InvariantCulture);
        RuleDomainsTextBox.Text = rule.CustomDomainsText;

        UpdateRuleTypeVisibility(rule.Type);

        RuleHintTextBlock.Text = "改完点「应用并保存」，立即写入当前档案的 frpc.toml。";
    }

    private void ClearRuleForm()
    {
        _editingIndex = -1;

        RuleEditorTitleTextBlock.Text = "规则详情";
        RuleNameTextBox.Text = string.Empty;
        RuleTypeComboBox.SelectedItem = "tcp";
        RuleLocalIpTextBox.Text = "127.0.0.1";
        RuleLocalPortTextBox.Text = "8080";
        RuleRemotePortTextBox.Text = "8080";
        RuleDomainsTextBox.Text = string.Empty;

        UpdateRuleTypeVisibility("tcp");

        RuleHintTextBlock.Text = "左边选一条规则来改，或点「＋ 添加」新建。";
    }

    private void UpdateRuleTypeVisibility(string? type)
    {
        var portBased = type is "tcp" or "udp";

        RuleRemotePortPanel.Visibility = portBased ? Visibility.Visible : Visibility.Collapsed;
        RuleDomainsPanel.Visibility = portBased ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RuleType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || RuleRemotePortPanel == null)
        {
            return;
        }

        UpdateRuleTypeVisibility(RuleTypeComboBox.SelectedItem as string);
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        RulesGrid.SelectedItem = null;
        ClearRuleForm();
        RuleEditorTitleTextBlock.Text = "新增规则";
        RuleNameTextBox.Focus();
    }

    private void CancelRule_Click(object sender, RoutedEventArgs e)
    {
        RulesGrid.SelectedItem = null;
        ClearRuleForm();
    }

    private void ApplyRule_Click(object sender, RoutedEventArgs e)
    {
        var name = RuleNameTextBox.Text.Trim();
        if (name.Length == 0)
        {
            Dialogs.Error("规则名称不能为空。");
            return;
        }

        var type = RuleTypeComboBox.SelectedItem as string ?? "tcp";

        if (!int.TryParse(RuleLocalPortTextBox.Text.Trim(), out var localPort)
            || localPort is < 1 or > 65535)
        {
            Dialogs.Error("本地端口必须是 1 ~ 65535 的整数。");
            return;
        }

        var rule = _editingIndex >= 0 && _editingIndex < _rules.Count
            ? _rules[_editingIndex]
            : new ProxyRule();

        rule.Name = name;
        rule.Type = type;
        rule.LocalIP = RuleLocalIpTextBox.Text.Trim();
        rule.LocalPort = localPort;

        if (rule.IsPortBased)
        {
            if (!int.TryParse(RuleRemotePortTextBox.Text.Trim(), out var remotePort)
                || remotePort is < 1 or > 65535)
            {
                Dialogs.Error("远程端口必须是 1 ~ 65535 的整数。");
                return;
            }

            rule.RemotePort = remotePort;
        }
        else
        {
            var domains = RuleDomainsTextBox.Text
                .Split(new[] { ',', '，', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(d => d.Trim())
                .Where(d => d.Length > 0)
                .ToList();

            if (domains.Count == 0)
            {
                Dialogs.Error("http / https / tcpmux 类型必须至少填一个域名。");
                return;
            }

            rule.CustomDomains = domains;
        }

        if (_editingIndex < 0)
        {
            _rules.Add(rule);
        }

        if (!SaveRulesToDisk())
        {
            return;
        }

        RulesGrid.Items.Refresh();

        var index = _rules.IndexOf(rule);
        if (index >= 0)
        {
            RulesGrid.SelectedIndex = index;
            FillRuleForm(index);
        }

        RefreshSummary();
    }

    private async void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedIndex < 0 || RulesGrid.SelectedIndex >= _rules.Count)
        {
            Dialogs.Info("先在列表里选中一条规则。");
            return;
        }

        var rule = _rules[RulesGrid.SelectedIndex];

        var ok = await Dialogs.ConfirmAsync(
            "删除转发规则",
            $"确定删除「{rule.Name}」？保存后当前档案的 frpc.toml 会同步更新。",
            "删除",
            danger: true);

        if (!ok)
        {
            return;
        }

        _rules.Remove(rule);

        if (!SaveRulesToDisk())
        {
            return;
        }

        RulesGrid.SelectedIndex = -1;
        ClearRuleForm();
        RefreshSummary();
    }

    private void ReloadRules_Click(object sender, RoutedEventArgs e)
    {
        _frpc = _frp.LoadFrpc(_profile);
        LoadRules();
        RefreshSummary();
    }

    private bool SaveRulesToDisk()
    {
        _frpc.Proxies = _rules.Select(r => r.Clone()).ToList();

        try
        {
            _frp.SaveFrpc(_profile, _frpc);
            return true;
        }
        catch (Exception ex)
        {
            Dialogs.Error($"保存规则失败：{ex.Message}");
            return false;
        }
    }

    // ───────────────────────── 服务器配置 ─────────────────────────

    private void LoadServerForm()
    {
        var isFrpc = _mode == "frpc";

        FrpcFormPanel.Visibility = isFrpc ? Visibility.Visible : Visibility.Collapsed;
        FrpsFormPanel.Visibility = isFrpc ? Visibility.Collapsed : Visibility.Visible;

        SrvTitleTextBlock.Text = isFrpc ? "服务器连接（frpc 客户端）" : "监听与面板（frps 服务端）";
        SrvPathTextBlock.Text = $"配置文件：{_frp.ConfigPath(_mode)}";

        SrvAddrTextBox.Text = _frpc.ServerAddr;
        SrvPortTextBox.Text = _frpc.ServerPort.ToString(CultureInfo.InvariantCulture);
        SrvTokenBox.SetPassword(_frpc.Token, false);
        SrvProtocolComboBox.SelectedValue = _frpc.Protocol;

        SrvBindAddrTextBox.Text = _frps.BindAddr;
        SrvBindPortTextBox.Text = _frps.BindPort.ToString(CultureInfo.InvariantCulture);
        SrvServerTokenBox.SetPassword(_frps.Token, false);
        SrvServerProtocolComboBox.SelectedValue = _frps.Protocol;

        SrvWebAddrTextBox.Text = _frps.WebAddr;
        SrvWebPortTextBox.Text = _frps.WebPort.ToString(CultureInfo.InvariantCulture);
        SrvWebUserTextBox.Text = _frps.WebUser;
        SrvWebPasswordBox.SetPassword(_frps.WebPassword, false);
    }

    private void ReloadServer_Click(object sender, RoutedEventArgs e)
    {
        _frpc = _frp.LoadFrpc(_profile);
        _frps = _frp.LoadFrps(_profile);

        LoadServerForm();
        LoadRules();
        RefreshSummary();

        if (SrvTextModeCheck.IsChecked == true)
        {
            SrvTextEditor.Text = _frp.ReadConfigText(_mode, _profile, out _);
        }
    }

    private void SaveServer_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveCurrentForm(silent: false))
        {
            return;
        }

        Dialogs.Info("配置已保存。改动要停止再启动后生效。");
        RefreshSummary();
    }

    private void SaveServerText_Click(object sender, RoutedEventArgs e)
    {
        var text = SrvTextEditor.Text;

        // 保存前先用同一套解析器过一遍：语法坏了要当场说，而不是等 frp 起不来
        try
        {
            if (_mode == "frpc")
            {
                _frpc = FrpToml.ParseFrpc(text);
            }
            else
            {
                _frps = FrpToml.ParseFrps(text);
            }
        }
        catch (Exception ex)
        {
            Dialogs.Error($"TOML 解析失败：{ex.Message}");
            return;
        }

        try
        {
            _frp.SaveConfigText(_mode, _profile, text);
        }
        catch (Exception ex)
        {
            Dialogs.Error($"保存失败：{ex.Message}");
            return;
        }

        LoadRules();
        RefreshSummary();
        Dialogs.Info("TOML 已保存。改动要停止再启动后生效。");
    }

    private void SrvTextMode_Changed(object sender, RoutedEventArgs e)
    {
        if (SrvTextPanel == null || SrvFormPanel == null)
        {
            return;
        }

        var textMode = SrvTextModeCheck.IsChecked == true;

        SrvFormPanel.Visibility = textMode ? Visibility.Collapsed : Visibility.Visible;
        SrvTextPanel.Visibility = textMode ? Visibility.Visible : Visibility.Collapsed;

        if (textMode)
        {
            SrvTextEditor.Text = _frp.ReadConfigText(_mode, _profile, out _);
            return;
        }

        // 切回表单：用文本内容重新解析，避免表单里还是旧值
        var raw = SrvTextEditor.Text;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (_mode == "frpc")
            {
                _frpc = FrpToml.ParseFrpc(raw);
            }
            else
            {
                _frps = FrpToml.ParseFrps(raw);
            }
        }

        LoadServerForm();
        LoadRules();
        RefreshSummary();
    }

    /// <summary>
    /// 把表单写回内存对象并落盘。启动前会以 silent 方式调一次。
    /// </summary>
    private bool SaveCurrentForm(bool silent)
    {
        if (SrvTextModeCheck.IsChecked == true)
        {
            // 文本模式下"表单"就是那个多行框，启动前不替用户动它
            return true;
        }

        if (_mode == "frpc")
        {
            if (!int.TryParse(SrvPortTextBox.Text.Trim(), out var port) || port is < 1 or > 65535)
            {
                if (!silent)
                {
                    Dialogs.Error("服务器端口必须是 1 ~ 65535 的整数。");
                }

                return false;
            }

            _frpc.ServerAddr = SrvAddrTextBox.Text.Trim();
            _frpc.ServerPort = port;
            _frpc.Token = SrvTokenBox.Password;
            _frpc.Protocol = SrvProtocolComboBox.SelectedValue as string ?? "tcp";

            // 规则页可能刚改过，不能拿旧的 Proxies 覆盖回去
            _frpc.Proxies = _rules.Select(r => r.Clone()).ToList();

            try
            {
                _frp.SaveFrpc(_profile, _frpc);
                return true;
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    Dialogs.Error($"保存失败：{ex.Message}");
                }

                return false;
            }
        }

        if (!int.TryParse(SrvBindPortTextBox.Text.Trim(), out var bindPort) || bindPort is < 1 or > 65535)
        {
            if (!silent)
            {
                Dialogs.Error("监听端口必须是 1 ~ 65535 的整数。");
            }

            return false;
        }

        if (!int.TryParse(SrvWebPortTextBox.Text.Trim(), out var webPort) || webPort is < 0 or > 65535)
        {
            if (!silent)
            {
                Dialogs.Error("面板端口必须是 0 ~ 65535 的整数。");
            }

            return false;
        }

        _frps.BindAddr = SrvBindAddrTextBox.Text.Trim();
        _frps.BindPort = bindPort;
        _frps.Token = SrvServerTokenBox.Password;
        _frps.Protocol = SrvServerProtocolComboBox.SelectedValue as string ?? "tcp";
        _frps.WebAddr = SrvWebAddrTextBox.Text.Trim();
        _frps.WebPort = webPort;
        _frps.WebUser = SrvWebUserTextBox.Text.Trim();
        _frps.WebPassword = SrvWebPasswordBox.Password;

        try
        {
            _frp.SaveFrps(_profile, _frps);
            return true;
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                Dialogs.Error($"保存失败：{ex.Message}");
            }

            return false;
        }
    }

    // ───────────────────────── 日志 ─────────────────────────

    private void OnLogChanged()
    {
        Dispatcher.BeginInvoke(new Action(() => AppendLogsFromState(_logSeq)));
    }

    private void AppendLogsFromState(long since)
    {
        var entries = _frp.State.GetLogs(since, 1000);

        foreach (var entry in entries)
        {
            _logs.Add(new LogRow(
                $"{entry.Time:HH:mm:ss}  {entry.Text}",
                entry.Level switch
                {
                    "err" => LogError,
                    "sys" => LogSystem,
                    _ => LogNormal
                }));

            _logSeq = entry.Seq + 1;
        }

        while (_logs.Count > 1000)
        {
            _logs.RemoveAt(0);
        }

        LogCountTextBlock.Text = $"{_logs.Count} 条";

        if (FrpLogAutoScrollCheck.IsChecked == true && _logs.Count > 0)
        {
            FrpLogListBox.ScrollIntoView(_logs[^1]);
        }
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        _frp.State.ClearLogs();

        _logs.Clear();
        _logSeq = 0;
        LogCountTextBlock.Text = "0 条";
    }

    // ───────────────────────── 杂项 ─────────────────────────

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // XAML 解析期 NavFrpOverview 的 IsChecked="True" 会先于其它面板字段构造触发
        if (PanelFrpOverview is null || PanelFrpRules is null
            || PanelFrpServer is null || PanelFrpLogs is null)
        {
            return;
        }

        var tag = (sender as RadioButton)?.Tag as string;

        PanelFrpOverview.Visibility = tag == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        PanelFrpRules.Visibility = tag == "Rules" ? Visibility.Visible : Visibility.Collapsed;
        PanelFrpServer.Visibility = tag == "Server" ? Visibility.Visible : Visibility.Collapsed;
        PanelFrpLogs.Visibility = tag == "Logs" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "Logs" && FrpLogAutoScrollCheck.IsChecked == true && _logs.Count > 0)
        {
            FrpLogListBox.ScrollIntoView(_logs[^1]);
        }
    }

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>协议下拉项</summary>
    private sealed record ProtocolOption(string Value, string Label);
}

/// <summary>日志行（绑定用；颜色跟着级别走）</summary>
public sealed class LogRow
{
    public LogRow(string display, Brush foreground)
    {
        Display = display;
        Foreground = foreground;
    }

    public string Display { get; }

    public Brush Foreground { get; }
}
