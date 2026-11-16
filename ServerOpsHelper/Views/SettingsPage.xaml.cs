using ServerOpsHelper.Mcp;
using ServerOpsHelper.Models;
using ServerOpsHelper.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ModernWpf;

namespace ServerOpsHelper.Views;

/// <summary>
/// 系统设置页面：外观与地址、密码箱与访问记录、智能体接入（MCP）、账号安全
/// </summary>
public partial class SettingsPage : Page
{
    private readonly ConfigService _configService;

    /// <summary>当前令牌（界面上展示的就是它，重置后立即刷新）</summary>
    private string _mcpToken = string.Empty;

    public SettingsPage()
    {
        InitializeComponent();
        _configService = ConfigService.Instance;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var settings = await _configService.LoadSettingsAsync();

        DefaultUrlTextBox.Text = settings.DefaultAdminUrl;
        // 大小写不敏感：主题字符串可能来自手工编辑的配置或旧版本，"light" 也要认
        ThemeToggle.IsOn = !string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase);

        ConfigPathTextBox.Text = _configService.ConfigDirectory;

        LoadMcpSection(settings);
        LoadExtensionsSection();

        await RefreshSecuritySectionAsync();
    }

    /// <summary>
    /// 顶部页签切换：四个面板始终实例化，只切 Visibility。
    /// <para>
    /// 刻意不用 TabControl —— 它只实例化当前 Tab 的内容，未选中的页会从逻辑树上消失，
    /// 探针和 FindName 都会找不到控件。这里四页全在，切页只是显隐。
    /// </para>
    /// </summary>
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // XAML 解析期 NavGeneral 的 IsChecked="True" 会先于面板字段构造触发，
        // 那时这些字段还是 null。面板的初始显隐在 XAML 里已经写好了，直接跳过即可。
        if (PanelGeneral is null || PanelPrivacy is null || PanelMcp is null
            || PanelExtensions is null || PanelAccount is null)
        {
            return;
        }

        var tag = (sender as RadioButton)?.Tag as string;

        PanelGeneral.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
        PanelPrivacy.Visibility = tag == "Privacy" ? Visibility.Visible : Visibility.Collapsed;
        PanelMcp.Visibility = tag == "Mcp" ? Visibility.Visible : Visibility.Collapsed;
        PanelExtensions.Visibility = tag == "Extensions" ? Visibility.Visible : Visibility.Collapsed;
        PanelAccount.Visibility = tag == "Account" ? Visibility.Visible : Visibility.Collapsed;

        // 切到扩展页时重新扫一遍：用户很可能刚在资源管理器里改完 json 再切回来
        if (tag == "Extensions")
        {
            ExtensionService.Instance.Reload();
            LoadExtensionsSection();
        }

        // 底部那个按钮只保存「设置项」，改密码是本页独立动作，别让人误会
        if (SaveHintTextBlock is not null)
        {
            SaveHintTextBlock.Text = tag == "Account"
                ? "登录密码在下方直接修改，这里的按钮只保存其他设置"
                : "修改后点此保存（立即生效）";
        }
    }

    private async void ThemeToggle_Toggled(object sender, RoutedEventArgs e)
    {        if (!IsLoaded) return;

        var theme = ThemeToggle.IsOn ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ThemeManager.Current.ApplicationTheme = theme;

        var settings = await _configService.LoadSettingsAsync();
        settings.Theme = theme.ToString();
        await _configService.SaveSettingsAsync(settings);
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // MCP 端口先校验：写进配置的东西必须是能用的
        if (!int.TryParse(McpPortTextBox.Text.Trim(), out var mcpPort) || mcpPort < 1 || mcpPort > 65535)
        {
            Dialogs.Error("MCP 端口必须是 1 到 65535 之间的整数。");
            return;
        }

        var settings = await _configService.LoadSettingsAsync();
        settings.DefaultAdminUrl = DefaultUrlTextBox.Text.Trim();
        settings.Theme = ThemeToggle.IsOn ? "Dark" : "Light";
        settings.CaptureCredentials = CaptureCredentialToggle.IsOn;
        settings.RecordVisitHistory = RecordHistoryToggle.IsOn;
        settings.McpHttpEnabled = McpHttpToggle.IsOn;
        settings.McpHttpPort = mcpPort;
        settings.McpAllowWriteTools = McpAllowWriteToggle.IsOn;
        await _configService.SaveSettingsAsync(settings);

        // 立即生效，不必等下次打开 Web 控制台
        VisitHistoryService.Instance.Configure(settings.RecordVisitHistory);

        // MCP 服务同样立即按新设置启停（改端口 / 换开关都走这里）
        McpRuntime.Apply(settings);
        LoadMcpSection(settings);

        Dialogs.Info("设置已保存。");
    }

    // ───────────────────────── 智能体接入（MCP） ─────────────────────────

    private void LoadMcpSection(AppSettings settings)
    {
        McpAllowWriteToggle.IsOn = settings.McpAllowWriteTools;
        McpHttpToggle.IsOn = settings.McpHttpEnabled;
        McpPortTextBox.Text = settings.McpHttpPort.ToString();

        _mcpToken = McpRuntime.CurrentToken();
        McpTokenTextBox.Text = _mcpToken;

        RefreshMcpStatus(settings);
    }

    private void RefreshMcpStatus(AppSettings? settings = null)
    {
        settings ??= _configService.LoadSettings();

        if (!settings.McpHttpEnabled)
        {
            McpStatusTextBlock.ClearValue(TextBlock.ForegroundProperty);
            McpStatusTextBlock.Text = "本地 MCP 服务未启动（不影响 stdio 方式）。";
            return;
        }

        if (McpRuntime.IsHttpRunning)
        {
            McpStatusTextBlock.ClearValue(TextBlock.ForegroundProperty);
            McpStatusTextBlock.Text =
                $"已启动：{McpRuntime.HttpEndpoint}（本次运行已处理 {McpRuntime.HttpRequestCount} 次请求）";
            return;
        }

        McpStatusTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xD1, 0x34, 0x38));
        McpStatusTextBlock.Text = $"启动失败：{McpRuntime.HttpLastError ?? "未知原因"}";
    }

    private void CopyMcpToken_Click(object sender, RoutedEventArgs e)
    {
        if (ClipboardService.TryCopy(McpTokenTextBox.Text))
        {
            Dialogs.Info("访问令牌已复制。");
        }
        else
        {
            Dialogs.Error("复制失败：剪贴板被其它程序占用，请稍后再试。");
        }
    }

    private async void ResetMcpToken_Click(object sender, RoutedEventArgs e)
    {
        var settings = await _configService.LoadSettingsAsync();

        _mcpToken = McpRuntime.ResetToken(settings);
        McpTokenTextBox.Text = _mcpToken;
        RefreshMcpStatus(settings);

        Dialogs.Info("令牌已重置。已经接入的客户端要更新配置里的令牌，否则会被拒绝。", "令牌已重置");
    }

    private void CopyStdioConfig_Click(object sender, RoutedEventArgs e)
        => CopyConfig(McpRuntime.BuildStdioClientConfig(), "stdio 客户端配置");

    private void CopyHttpConfig_Click(object sender, RoutedEventArgs e)
    {
        var settings = _configService.LoadSettings();
        CopyConfig(McpRuntime.BuildHttpClientConfig(settings, McpTokenTextBox.Text), "HTTP 客户端配置");
    }

    private static void CopyConfig(string config, string what)
    {
        if (ClipboardService.TryCopy(config))
        {
            Dialogs.Info($"{what}已复制，粘到智能体的 MCP 配置文件里即可。");
        }
        else
        {
            Dialogs.Error("复制失败：剪贴板被其它程序占用，请稍后再试。");
        }
    }

    private void OpenAuditLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = McpAuditLog.LogDirectory;
            Directory.CreateDirectory(directory);

            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Dialogs.Error($"打不开审计日志目录：{ex.Message}");
        }
    }


    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        var configPath = ConfigPathTextBox.Text;
        if (Directory.Exists(configPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = configPath,
                UseShellExecute = true
            });
        }
    }

    // ───────────────────────── 扩展 ─────────────────────────

    private static readonly Brush OkBrush = FreezeBrush("#2e9e5b");

    private static readonly Brush MutedBrush = FreezeBrush("#888780");

    private static readonly Brush ErrorBrush = FreezeBrush("#c4553d");

    private static Brush FreezeBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 给用户抄的模板。刻意放一个只读命令（tail）+ 一个变更命令（reload nginx），
    /// 让人一看就知道 writeAccess 是干嘛的。
    /// </summary>
    private const string ExtensionSampleJson = """
    {
      "id": "deploy",
      "name": "部署与巡检",
      "version": "1.0.0",
      "description": "我自己的常用动作",
      "commands": [
        {
          "id": "restart_nginx",
          "name": "重载 Nginx",
          "description": "先测配置，通过后平滑重载",
          "command": "sudo nginx -t && sudo systemctl reload nginx",
          "writeAccess": true,
          "timeoutSeconds": 60
        },
        {
          "id": "tail_log",
          "name": "看日志",
          "description": "看指定日志的最后若干行",
          "command": "tail -n ${lines} ${path}",
          "writeAccess": false,
          "parameters": [
            {
              "name": "lines",
              "type": "int",
              "default": "100",
              "min": 1,
              "max": 5000,
              "description": "行数"
            },
            {
              "name": "path",
              "type": "path",
              "default": "/var/log/nginx/error.log",
              "description": "日志文件路径"
            }
          ]
        }
      ]
    }
    """;

    private void LoadExtensionsSection()
    {
        var service = ExtensionService.Instance;
        var summary = service.Current;

        ExtensionPathTextBox.Text = service.ExtensionsDirectory;

        var installed = summary.Packs
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(pack => new ExtensionListItem
            {
                Id = pack.Id,
                Initial = FirstChar(pack.Name),
                Title = pack.Name,
                VersionText = $"v{pack.Version}",
                StatusText = !pack.IsValid ? "加载失败" : (pack.Enabled ? "已启用" : "已停用"),
                StatusBrush = !pack.IsValid ? ErrorBrush : (pack.Enabled ? OkBrush : MutedBrush),
                Summary = BuildExtensionSummary(pack),
                ToggleText = pack.Enabled ? "停用" : "启用"
            })
            .ToList();

        InstalledExtensionsList.ItemsSource = installed;
        NoExtensionTextBlock.Visibility = installed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var installedIds = new HashSet<string>(
            summary.Packs.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);

        CatalogExtensionsList.ItemsSource = ExtensionCatalog.All
            .Select(item => new CatalogListItem
            {
                Id = item.Id,
                Initial = FirstChar(item.Name),
                Title = item.Name,
                VersionText = $"v{item.Version}",
                Description = item.Description,
                ActionText = installedIds.Contains(item.Id) ? "已安装" : "安装",
                CanInstall = !installedIds.Contains(item.Id)
            })
            .ToList();

        ExtensionHintTextBlock.Text = summary.Errors.Count > 0
            ? $"有 {summary.Errors.Count} 处问题：{string.Join("；", summary.Errors)}"
            : $"可用 {summary.ActivePackCount} 个扩展、{summary.ActiveCommandCount} 个命令。" +
              "安装/卸载后立即生效；智能体那边要重新读取工具清单（或重连）才能看到变化。";

        if (string.IsNullOrEmpty(ExtensionSampleTextBox.Text))
        {
            ExtensionSampleTextBox.Text = ExtensionSampleJson;
        }
    }

    private static string FirstChar(string text)
        => string.IsNullOrWhiteSpace(text) ? "?" : text.Trim()[..1].ToUpperInvariant();

    private static string BuildExtensionSummary(ExtensionPack pack)
    {
        if (!pack.IsValid)
        {
            return $"没能加载：{pack.LoadError}";
        }

        var agentCount = pack.Commands.Count(c => c.ExposeToAgent);
        var parts = new List<string> { $"{pack.Commands.Count} 个命令（{agentCount} 个给智能体）" };

        if (!string.IsNullOrWhiteSpace(pack.Author))
        {
            parts.Add(pack.Author);
        }

        if (!string.IsNullOrWhiteSpace(pack.Description))
        {
            parts.Add(pack.Description);
        }

        return string.Join(" · ", parts);
    }

    private void InstallExtensionPackage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择扩展包",
            Filter = "扩展包 (*.zip;*.json)|*.zip;*.json|压缩包 (*.zip)|*.zip|清单文件 (*.json)|*.json",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        var result = ExtensionService.Instance.InstallFromPath(dialog.FileName);
        LoadExtensionsSection();

        if (result.Ok)
        {
            Dialogs.Info(result.Message, "安装成功");
        }
        else
        {
            Dialogs.Error(result.Message, "安装失败");
        }
    }

    private void InstallBuiltInExtension_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CatalogListItem item)
        {
            return;
        }

        var result = ExtensionService.Instance.InstallBuiltIn(item.Id);
        LoadExtensionsSection();

        if (result.Ok)
        {
            Dialogs.Info(result.Message, "安装成功");
        }
        else
        {
            Dialogs.Error(result.Message, "安装失败");
        }
    }

    private void ToggleExtension_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ExtensionListItem item)
        {
            return;
        }

        var pack = ExtensionService.Instance.Packs.FirstOrDefault(
            p => string.Equals(p.Id, item.Id, StringComparison.OrdinalIgnoreCase));

        if (pack == null)
        {
            return;
        }

        var result = ExtensionService.Instance.SetEnabled(item.Id, !pack.Enabled);
        LoadExtensionsSection();

        if (!result.Ok)
        {
            Dialogs.Error(result.Message);
        }
    }

    private async void UninstallExtension_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ExtensionListItem item)
        {
            return;
        }

        var ok = await Dialogs.ConfirmAsync(
            "卸载扩展",
            $"确定卸载「{item.Title}」？它的命令会立即从服务器页和智能体工具清单里消失。",
            "卸载",
            danger: true);

        if (!ok)
        {
            return;
        }

        var result = ExtensionService.Instance.Uninstall(item.Id);
        LoadExtensionsSection();

        if (result.Ok)
        {
            Dialogs.Info(result.Message, "已卸载");
        }
        else
        {
            Dialogs.Error(result.Message, "卸载失败");
        }
    }

    private void OpenExtensionFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ExtensionListItem item)
        {
            return;
        }

        var pack = ExtensionService.Instance.Packs.FirstOrDefault(
            p => string.Equals(p.Id, item.Id, StringComparison.OrdinalIgnoreCase));

        var manifest = pack?.SourcePath;

        try
        {
            if (!string.IsNullOrEmpty(manifest) && File.Exists(manifest))
            {
                // 单文件扩展也定位到文件本身
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{manifest}\"",
                    UseShellExecute = true
                });

                return;
            }

            OpenExtensionsFolder_ClickCore();
        }
        catch (Exception ex)
        {
            Dialogs.Error($"打开失败：{ex.Message}");
        }
    }

    private void OpenExtensionsFolder_Click(object sender, RoutedEventArgs e) => OpenExtensionsFolder_ClickCore();

    private void OpenExtensionsFolder_ClickCore()
    {
        try
        {
            var directory = ExtensionService.Instance.ExtensionsDirectory;

            // 目录不存在时先建出来，否则资源管理器会自己决定打开"文档"
            Directory.CreateDirectory(directory);

            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Dialogs.Error($"打开扩展目录失败：{ex.Message}");
        }
    }

    private void ReloadExtensions_Click(object sender, RoutedEventArgs e)
    {
        var summary = ExtensionService.Instance.Reload();
        LoadExtensionsSection();

        if (summary.Errors.Count > 0)
        {
            Dialogs.Error($"扩展已重新加载，但有 {summary.Errors.Count} 处问题：\n\n" +
                          string.Join("\n", summary.Errors));
            return;
        }

        Dialogs.Info($"扩展已重新加载：{summary.ActivePackCount} 个扩展、" +
                     $"{summary.ActiveCommandCount} 个命令可用。");
    }

    // ───────────────────────── 账号安全 ─────────────────────────

    /// <summary>
    /// 刷新账号信息与「仍在使用默认密码」告警
    /// </summary>
    private async Task RefreshSecuritySectionAsync()
    {
        var username = AuthService.Instance.CurrentUser;

        CurrentUserTextBlock.Text = string.IsNullOrEmpty(username)
            ? "当前会话没有登录账号信息。"
            : $"当前账号：{username}";

        var isDefault = await AuthService.Instance.IsUsingDefaultPasswordAsync(username);
        DefaultPasswordHintTextBlock.Visibility = isDefault ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PasswordField_Changed(object sender, RoutedEventArgs e) => UpdatePasswordValidation();

    /// <summary>
    /// 返回第一条不通过的校验信息；三个框都空时返回 null（表示尚未开始填写）
    /// </summary>
    private string? GetPasswordValidationError()
    {
        var current = CurrentPasswordBox.Password;
        var next = NewPasswordBox.Password;
        var confirm = ConfirmPasswordBox.Password;

        if (current.Length == 0 && next.Length == 0 && confirm.Length == 0)
        {
            return null;
        }

        if (current.Length == 0)
        {
            return "请输入当前密码。";
        }

        if (next.Length < AuthService.MinPasswordLength)
        {
            return $"新密码至少 {AuthService.MinPasswordLength} 位。";
        }

        if (!next.Any(char.IsLetter) || !next.Any(char.IsDigit))
        {
            return "新密码需同时包含字母和数字。";
        }

        if (next != confirm)
        {
            return "两次输入的新密码不一致。";
        }

        if (next == current)
        {
            return "新密码不能与当前密码相同。";
        }

        return null;
    }

    private bool HasAnyPasswordInput()
        => CurrentPasswordBox.Password.Length > 0
           || NewPasswordBox.Password.Length > 0
           || ConfirmPasswordBox.Password.Length > 0;

    private void UpdatePasswordValidation()
    {
        var error = GetPasswordValidationError();

        // 完全没填时不刷红字，避免刚进页面就一片警告
        var message = HasAnyPasswordInput() ? error ?? string.Empty : string.Empty;

        PasswordHintTextBlock.Text = message;
        PasswordHintTextBlock.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        ChangePasswordButton.IsEnabled = HasAnyPasswordInput() && error == null;
    }

    private async void ChangePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        var error = GetPasswordValidationError();
        if (error != null)
        {
            PasswordHintTextBlock.Text = error;
            PasswordHintTextBlock.Visibility = Visibility.Visible;
            return;
        }

        var username = AuthService.Instance.CurrentUser;
        if (string.IsNullOrEmpty(username))
        {
            Dialogs.Error("当前会话没有登录账号信息，请重新登录后再试。");
            return;
        }

        var changed = await AuthService.Instance.ChangePasswordAsync(
            username, CurrentPasswordBox.Password, NewPasswordBox.Password);

        if (!changed)
        {
            PasswordHintTextBlock.Text = "当前密码不正确，修改失败。";
            PasswordHintTextBlock.Visibility = Visibility.Visible;
            return;
        }

        CurrentPasswordBox.Password = string.Empty;
        NewPasswordBox.Password = string.Empty;
        ConfirmPasswordBox.Password = string.Empty;
        UpdatePasswordValidation();

        await RefreshSecuritySectionAsync();

        Dialogs.Info("登录密码已修改，下次登录请使用新密码。", "修改成功");
    }
}

/// <summary>「已安装」卡片的数据行（DataTemplate 绑定用）</summary>
public sealed class ExtensionListItem
{
    public string Id { get; init; } = string.Empty;

    public string Initial { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string VersionText { get; init; } = string.Empty;

    public string StatusText { get; init; } = string.Empty;

    public Brush StatusBrush { get; init; } = Brushes.Gray;

    public string Summary { get; init; } = string.Empty;

    public string ToggleText { get; init; } = string.Empty;
}

/// <summary>「推荐扩展」卡片的数据行</summary>
public sealed class CatalogListItem
{
    public string Id { get; init; } = string.Empty;

    public string Initial { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string VersionText { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string ActionText { get; init; } = "安装";

    public bool CanInstall { get; init; } = true;
}
