using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SliderSorter.Wpf.Services;

/// <summary>让下拉框对滚轮免疫：光标停在它上面滚动时不改它的选中项。
///
/// 为什么给 ComboBox 挂上它（见 Controls.xaml 的隐式 ComboBox 样式）：ComboBox 在**自己持有
/// 键盘焦点**时会对滚轮逐项换选中值，而刚点过一项之后它正是有焦点的——"选完语言顺手往下滚页面"
/// 就变成又换一种语言。设置页那一列下拉框选的都是立刻落盘、还会连带重扫/重探的值
/// （实例、Profile、BodySlide 目录、输出模式、主题、语言），手路过就把用户配置换了。
///
/// 没有焦点时不拦：那时 WPF 本来就不动选中项，而是把滚轮让给页面（实测如此），
/// 拦下来只会让页面在光标经过下拉框时滚不动。</summary>
public static class IgnoreWheel
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(IgnoreWheel), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el)
            return;
        if ((bool)e.NewValue)
            el.PreviewMouseWheel += OnPreviewMouseWheel;
        else
            el.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    /// <summary>掐在 Preview（向下隧道）这一路：输入系统只在预览事件未被处理时才接着抛冒泡的
    /// MouseWheel，而换选中项挂在冒泡那一路。</summary>
    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not Control { IsKeyboardFocusWithin: true })
            return;
        e.Handled = true;
    }
}
