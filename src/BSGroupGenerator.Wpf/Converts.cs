using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BSGroupGenerator.Wpf;

/// <summary>反相的布尔 → 可见性转换器：true 折叠、false 可见。
///
/// 为什么需要：WPF 只内置了 <see cref="BooleanToVisibilityConverter"/>（true→Visible），
/// 而「A 和 B 互斥显示」的场景（一个组都没有 / 被过滤成空的）必须要反相那一半。
/// 换别的写法都更差：加一个取反属性要污染 ViewModel；用 DataTrigger 要写两份
/// （true 显式设 Visible 还得留 Collapsed 分支），而这个转换器的语义一眼可读。
///
/// 非 bool 一律按 false 处理（转成 Visible）：绑定路径写错时宁可多显示一块提示，
/// 也不要静默折叠——静默折叠的表现是"界面上什么都没发生"，最难排查。</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility v && v != Visibility.Visible;
}
