// DeskLink.Panel —— XAML 值转换器
//
// 约定（本项目 WPF 一致做法）：转换器与它服务的调色板**都定义在控件自身的 Resources**，
// 不用 Application 级资源——没有 Application 实例时（单测、设计器、嵌入宿主）静态资源会解析失败。
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DeskLink.Panel.ViewModels;

namespace DeskLink.Panel;

/// <summary>BannerLevel → 背景画刷。</summary>
public sealed class BannerBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is BannerLevel level ? LevelPalette.Bg(level) : LevelPalette.Bg(BannerLevel.Info);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>BannerLevel → 前景画刷。</summary>
public sealed class BannerForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is BannerLevel level ? LevelPalette.Fg(level) : LevelPalette.Fg(BannerLevel.Info);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool → Visibility（true=Visible）。用 <c>ConverterParameter=invert</c> 取反。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>非空字符串 → Visible（空则 Collapsed）。</summary>
public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>四种状态级别的固定配色。</summary>
internal static class LevelPalette
{
    public static Brush Bg(BannerLevel level) => level switch
    {
        BannerLevel.Ok => new SolidColorBrush(Color.FromRgb(0xEC, 0xFD, 0xF5)),
        BannerLevel.Warn => new SolidColorBrush(Color.FromRgb(0xFF, 0xFB, 0xEB)),
        BannerLevel.Error => new SolidColorBrush(Color.FromRgb(0xFE, 0xF2, 0xF2)),
        _ => new SolidColorBrush(Color.FromRgb(0xEF, 0xF6, 0xFF)),
    };

    public static Brush Fg(BannerLevel level) => level switch
    {
        BannerLevel.Ok => new SolidColorBrush(Color.FromRgb(0x04, 0x78, 0x57)),
        BannerLevel.Warn => new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)),
        BannerLevel.Error => new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C)),
        _ => new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8)),
    };
}
