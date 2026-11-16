using ServerOpsHelper.Models;
using System;
using System.Globalization;
using System.Windows.Data;

namespace ServerOpsHelper.Converters;

/// <summary>
/// 将 ServerOsType 转换为 Segoe MDL2 Assets 图标字符
/// </summary>
public class OsTypeToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ServerOsType osType)
        {
            return osType switch
            {
                ServerOsType.Windows => "", // WindowsLogo
                ServerOsType.Linux => "",   // CommandPrompt（暂用命令提示符图标）
                _ => ""                    // Globe
            };
        }

        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
