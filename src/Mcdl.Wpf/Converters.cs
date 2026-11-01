using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Mcdl.App;

/// <summary>文本框为空时显示占位提示（WPF 的 TextBox 没有 Watermark）。</summary>
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
