using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LXMusicAccessible.App.Converters;

/// <summary>
/// 布尔值到可见性转换器
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            // 如果参数为 "Invert"，则反转结果
            if (parameter?.ToString() == "Invert")
            {
                return boolValue ? Visibility.Collapsed : Visibility.Visible;
            }
            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility visibility)
        {
            // 如果参数为 "Invert"，则反转结果
            if (parameter?.ToString() == "Invert")
            {
                return visibility != Visibility.Visible;
            }
            return visibility == Visibility.Visible;
        }
        return false;
    }
}

/// <summary>
/// 反向布尔值到可见性转换器
/// </summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return boolValue ? Visibility.Collapsed : Visibility.Visible;
        }
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility visibility)
        {
            return visibility != Visibility.Visible;
        }
        return false;
    }
}

/// <summary>
/// 整数到可见性转换器（大于0时显示）
/// </summary>
public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int intValue)
        {
            return intValue > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 搜索趋势图标转换器
/// </summary>
public class TrendIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int trendValue)
        {
            return trendValue switch
            {
                > 0 => "↑",
                < 0 => "↓",
                _ => "→"
            };
        }
        return "→";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
