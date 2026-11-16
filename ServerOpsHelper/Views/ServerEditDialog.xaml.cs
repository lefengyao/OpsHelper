using ServerOpsHelper.Models;
using System.Windows;
using System.Windows.Controls;
using ModernWpf.Controls;

namespace ServerOpsHelper.Views;

/// <summary>
/// 服务器编辑对话框，支持新增与编辑两种模式
/// </summary>
public partial class ServerEditDialog : ContentDialog
{
    private ServerInfo _server = new();

    private ServerEditDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 弹出对话框编辑服务器
    /// </summary>
    /// <param name="server">传入则编辑，传 null 则新增</param>
    /// <returns>用户确认后返回 ServerInfo，取消返回 null</returns>
    public static async Task<ServerInfo?> ShowEditAsync(ServerInfo? server)
    {
        var dialog = new ServerEditDialog();
        dialog.Setup(server ?? new ServerInfo(), server != null);

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        return dialog.BuildServer();
    }

    /// <summary>
    /// 填充表单
    /// </summary>
    private void Setup(ServerInfo server, bool isEdit)
    {
        _server = server;

        Title = isEdit ? "编辑服务器" : "新增服务器";
        PrimaryButtonText = isEdit ? "保存" : "添加";
        CloseButtonText = "取消";

        NameTextBox.Text = server.Name;
        HostTextBox.Text = server.Host;
        PortTextBox.Text = server.Port.ToString();
        UsernameTextBox.Text = server.Username;
        PasswordBox.Password = server.Password;
        TagsTextBox.Text = server.Tags;
        DescriptionTextBox.Text = server.Description;

        var index = server.OsType == ServerOsType.Windows ? 1 : 0;
        if (OsTypeComboBox.Items.Count > index)
        {
            OsTypeComboBox.SelectedIndex = index;
        }
    }

    /// <summary>
    /// 收集表单数据生成 ServerInfo
    /// </summary>
    private ServerInfo BuildServer()
    {
        _server.Name = NameTextBox.Text.Trim();
        _server.Host = HostTextBox.Text.Trim();
        _server.Username = UsernameTextBox.Text.Trim();
        _server.Password = PasswordBox.Password;
        _server.Tags = TagsTextBox.Text.Trim();
        _server.Description = DescriptionTextBox.Text.Trim();
        _server.OsType = OsTypeComboBox.SelectedIndex == 1 ? ServerOsType.Windows : ServerOsType.Linux;

        if (int.TryParse(PortTextBox.Text.Trim(), out var port) && port > 0)
        {
            _server.Port = port;
        }

        return _server;
    }
}
