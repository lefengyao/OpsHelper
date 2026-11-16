using ModernWpf.Controls;
using ServerOpsHelper.Models;
using ServerOpsHelper.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ServerOpsHelper.Views;

/// <summary>
/// SFTP 文件面板（二期）：浏览远端目录 + 上传/下载/删除/重命名/新建文件夹。
/// <para>
/// 走独立的 SFTP 连接（与终端的 shell 通道分开），所以传大文件时终端照样能用。
/// 所有耗时操作都在后台线程，界面只通过状态行反馈，不用弹窗打断。
/// </para>
/// </summary>
public partial class SftpFilePanel : UserControl
{
    private readonly SftpService _sftp = new();
    private string _currentPath = "/";
    private bool _ready;
    private bool _busy;

    public SftpFilePanel()
    {
        InitializeComponent();
    }

    /// <summary>连接 SFTP 并列出根目录。页面加载后调用一次。</summary>
    public async Task InitializeAsync(ServerInfo server)
    {
        if (_ready)
        {
            return;
        }

        try
        {
            SetBusy("正在连接 SFTP …");
            await _sftp.ConnectAsync(server);
            _ready = true;
            await LoadAsync("/");
        }
        catch (Exception ex)
        {
            SetStatus($"SFTP 连接失败：{ex.Message}", error: true);
        }
    }

    /// <summary>页面离开时调用：断开 SFTP 连接。</summary>
    public void Close()
    {
        _sftp.Dispose();
        _ready = false;
    }

    // ───────────────────────── 目录浏览 ─────────────────────────

    private async Task LoadAsync(string path)
    {
        if (!_ready || _busy)
        {
            return;
        }

        SetBusy("正在读取目录 …");
        try
        {
            var list = await _sftp.ListAsync(path);

            _currentPath = SftpService.NormalizePath(path);
            PathTextBox.Text = _currentPath;
            FileListView.ItemsSource = list;

            var dirs = list.Count(e => e.IsDirectory);
            SetStatus($"{_currentPath} — 共 {list.Count} 项"
                      + $"（文件夹 {dirs} / 文件 {list.Count - dirs}）");
        }
        catch (Exception ex)
        {
            SetStatus($"读取目录失败：{ex.Message}", error: true);
        }
    }

    private async void GoUp_Click(object sender, RoutedEventArgs e)
    {
        await LoadAsync(SftpService.ParentOf(_currentPath));
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadAsync(_currentPath);
    }

    private async void PathTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await LoadAsync(PathTextBox.Text);
    }

    private async void FileListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileListView.SelectedItem is not SftpEntry entry)
        {
            return;
        }

        if (entry.IsDirectory)
        {
            await LoadAsync(entry.FullPath);
        }
        else
        {
            await DownloadEntryAsync(entry);
        }
    }

    // ───────────────────────── 文件操作 ─────────────────────────

    private async void DownloadItem_Click(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is SftpEntry entry)
        {
            await DownloadEntryAsync(entry);
        }
    }

    private async Task DownloadEntryAsync(SftpEntry entry)
    {
        if (!_ready || _busy)
        {
            return;
        }

        if (entry.IsDirectory)
        {
            SetStatus("下载暂只支持单个文件（整个文件夹请先在服务器上打包）。", error: true);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = entry.Name,
            Title = "保存到本地",
            Filter = "所有文件|*.*",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SetBusy($"正在下载 {entry.Name} …");
        try
        {
            await _sftp.DownloadAsync(entry.FullPath, dialog.FileName);
            SetStatus($"已下载到 {dialog.FileName}");
        }
        catch (Exception ex)
        {
            SetStatus($"下载失败：{ex.Message}", error: true);
        }
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _busy)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要上传的文件",
            Filter = "所有文件|*.*",
            Multiselect = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var files = dialog.FileNames;
        SetBusy($"正在上传 {files.Length} 个文件 …");

        var ok = 0;
        var failed = new System.Collections.Generic.List<string>();

        foreach (var local in files)
        {
            try
            {
                var remote = SftpService.NormalizePath(_currentPath + "/" + Path.GetFileName(local));
                await _sftp.UploadAsync(local, remote);
                ok++;
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(local)}（{ex.Message}）");
            }
        }

        if (failed.Count == 0)
        {
            SetStatus($"已上传 {ok} 个文件到 {_currentPath}");
        }
        else
        {
            SetStatus($"上传完成 {ok} 个，失败 {failed.Count} 个：{string.Join("；", failed)}", error: true);
        }

        await LoadAsync(_currentPath);
    }

    private async void RenameItem_Click(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is not SftpEntry entry || !_ready || _busy)
        {
            return;
        }

        var box = new TextBox { Text = entry.Name, MinWidth = 280 };
        var dialog = new ContentDialog
        {
            Title = "重命名",
            Content = box,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var newName = box.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName == entry.Name)
        {
            return;
        }

        if (newName.Contains('/') || newName.Contains('\\'))
        {
            SetStatus("名称里不能包含斜杠。", error: true);
            return;
        }

        SetBusy($"正在重命名为 {newName} …");
        try
        {
            var target = SftpService.NormalizePath(_currentPath + "/" + newName);
            if (await _sftp.ExistsAsync(target))
            {
                SetStatus($"已存在同名项：{newName}", error: true);
                return;
            }

            await _sftp.RenameAsync(entry.FullPath, target);
            SetStatus($"已重命名为 {newName}");
            await LoadAsync(_currentPath);
        }
        catch (Exception ex)
        {
            SetStatus($"重命名失败：{ex.Message}", error: true);
        }
    }

    private async void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is not SftpEntry entry || !_ready || _busy)
        {
            return;
        }

        // 删除不可撤销，必须二次确认；删文件夹要额外提醒是递归删除
        var message = entry.IsDirectory
            ? $"确定删除文件夹「{entry.Name}」吗？\n\n它会连同里面的**全部内容**一起递归删除，且无法恢复。"
            : $"确定删除文件「{entry.Name}」吗？此操作无法恢复。";

        var dialog = new ContentDialog
        {
            Title = "确认删除",
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetBusy($"正在删除 {entry.Name} …");
        try
        {
            if (entry.IsDirectory)
            {
                await _sftp.DeleteDirectoryAsync(entry.FullPath);
            }
            else
            {
                await _sftp.DeleteFileAsync(entry.FullPath);
            }

            SetStatus($"已删除 {entry.Name}");
            await LoadAsync(_currentPath);
        }
        catch (Exception ex)
        {
            SetStatus($"删除失败：{ex.Message}", error: true);
        }
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _busy)
        {
            return;
        }

        var box = new TextBox { Text = "新建文件夹", MinWidth = 280 };
        var dialog = new ContentDialog
        {
            Title = "新建文件夹",
            Content = box,
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var name = box.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (name.Contains('/') || name.Contains('\\'))
        {
            SetStatus("名称里不能包含斜杠。", error: true);
            return;
        }

        SetBusy($"正在创建 {name} …");
        try
        {
            var target = SftpService.NormalizePath(_currentPath + "/" + name);
            if (await _sftp.ExistsAsync(target))
            {
                SetStatus($"已存在同名项：{name}", error: true);
                return;
            }

            await _sftp.CreateDirectoryAsync(target);
            SetStatus($"已创建文件夹 {name}");
            await LoadAsync(_currentPath);
        }
        catch (Exception ex)
        {
            SetStatus($"创建失败：{ex.Message}", error: true);
        }
    }

    // ───────────────────────── 状态反馈 ─────────────────────────

    /// <summary>普通提示（灰）/ 错误提示（警示色）都走状态行 —— 不打断操作。</summary>
    private void SetStatus(string text, bool error = false)
    {
        _busy = false;
        StatusTextBlock.Text = text;
        StatusTextBlock.Foreground = error
            ? (Brush)FindResource("SystemControlErrorTextForegroundBrush")
            : (Brush)FindResource("SystemControlForegroundBaseMediumBrush");
    }

    private void SetBusy(string text)
    {
        _busy = true;
        StatusTextBlock.Text = text;
        StatusTextBlock.Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush");
    }
}
