using ServerOpsHelper.Models;
using ServerOpsHelper.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ServerOpsHelper.Views;

/// <summary>
/// 扩展命令的参数输入窗口。
/// <para>
/// 参数数量与类型由扩展清单决定，所以控件是代码生成的，不做固定表单。
/// 点「执行」时先用 <see cref="ExtensionService.Substitute"/> 走一遍替换 ——
/// 参数缺失/类型不对/含换行都能在这里就报出来，不用等 SSH 连上才发现。
/// </para>
/// </summary>
public partial class ExtensionRunDialog : Window
{
    private readonly ExtensionCommand _command;
    private readonly Dictionary<string, TextBox> _boxes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>用户填好的参数（键为参数名）</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ExtensionRunDialog(ExtensionPack pack, ExtensionCommand command)
    {
        InitializeComponent();

        _command = command;

        Title = $"执行：{command.Name}";
        HeaderTextBlock.Text = $"{pack.Name} · {command.Name}";
        HintTextBlock.Text = string.IsNullOrWhiteSpace(command.Description)
            ? (command.WriteAccess ? "这条命令会改动服务器上的内容。" : "只读命令，不会改动服务器。")
            : command.Description;

        BuildFields();

        Loaded += (_, _) =>
        {
            FieldsHost.Children.OfType<TextBox>().FirstOrDefault()?.Focus();
        };
    }

    private void BuildFields()
    {
        foreach (var parameter in _command.Parameters)
        {
            var label = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(parameter.Description)
                    ? parameter.Name
                    : $"{parameter.Name} — {parameter.Description}",
                Margin = new Thickness(0, 8, 0, 4),
                TextWrapping = TextWrapping.Wrap
            };
            label.SetResourceReference(StyleProperty, "CaptionTextBlockStyle");

            var box = new TextBox
            {
                Text = parameter.Default ?? string.Empty
            };

            _boxes[parameter.Name] = box;
            FieldsHost.Children.Add(label);
            FieldsHost.Children.Add(box);
        }
    }

    private void RunButton_Click(object sender, RoutedEventArgs e)
    {
        Values.Clear();
        foreach (var (name, box) in _boxes)
        {
            Values[name] = box.Text.Trim();
        }

        try
        {
            // 只为了校验，执行时页面那边会再替换一次（用同一份逻辑，结果一致）
            _ = ExtensionService.Substitute(_command, Values);
        }
        catch (ExtensionException ex)
        {
            Dialogs.Error(ex.Message, "参数有问题");
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
