using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ServerOpsHelper.Converters;

/// <summary>
/// 反向的布尔到可见性转换器：true → Collapsed，false → Visible。
/// 用于"进行中才显示进度条"这类场景。
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && v != Visibility.Visible;
}
