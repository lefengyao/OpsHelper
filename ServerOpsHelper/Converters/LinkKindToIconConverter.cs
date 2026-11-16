using ServerOpsHelper.Models;
using System;
using System.Globalization;
using System.Windows.Data;

namespace ServerOpsHelper.Converters;

/// <summary>
/// 将 <see cref="LinkKind"/> 转换为 Segoe MDL2 Assets 图标字符。
/// <para>使用 Unicode 转义书写，避免特殊字符在不同编辑器 / 编码下被破坏。</para>
/// </summary>
public class LinkKindToIconConverter : IValueConverter
{
    /// <summary>Globe 图标</summary>
    private const string GlobeGlyph = "\uE774";

    /// <summary>Lock 图标</summary>
    private const string LockGlyph = "\uE72E";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is LinkKind kind && kind == LinkKind.BtPanel ? LockGlyph : GlobeGlyph;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
