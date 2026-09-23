using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SliderSorter.Wpf.Services;
using Xunit;

namespace SliderSorter.Wpf.Tests;

/// <summary>滚轮扫过下拉框不许改选中项，也不许把页面滚轮一起带走。
/// <para>
/// WPF 的 ComboBox 在**自己持有键盘焦点**时对滚轮逐项换选中值，而刚点过一项之后它正是有焦点的——
/// 于是"选完语言顺手往下滚页面"变成又换一种语言。2026-09-23 用户报的是语言这一格，而设置页
/// 那一列下拉框选的都是立刻落盘、还会连带重扫/重探的值（实例、Profile、BodySlide 目录、
/// 输出模式、主题、语言），手路过就把用户配置换了。
/// </para>
/// 反方向也要钉：没有焦点时 WPF 本就不动选中项、把滚轮让给页面，IgnoreWheel 若一律掐掉，
/// 设置页就会滚到下拉框那一格卡住。两条用例合起来才是"只在会出事的那种状态下拦"。
/// 挂 <see cref="WpfStaCollection"/>：样式与模板要真窗口。
/// </summary>
[Collection(WpfStaCollection.Name)]
public class ComboBoxWheelTests
{
    [Fact]
    public void WheelOverFocusedComboBoxDoesNotChangeSelection()
    {
        var (selection, _) = WheelOver(focusFirst: true);

        Assert.Equal(2, selection);
    }

    [Fact]
    public void WheelOverUnfocusedComboBoxStillScrollsThePage()
    {
        var (selection, offset) = WheelOver(focusFirst: false);

        Assert.Equal(2, selection);
        Assert.True(offset > 0, $"页面没接着滚（VerticalOffset={offset}）——拦滚轮把页面的那份也吞了");
    }

    /// <summary>在一个可滚页面上对下拉框滚一格，返回滚完后的选中项与页面偏移。</summary>
    private static (int Selection, double Offset) WheelOver(bool focusFirst)
    {
        ComboBox? combo = null;
        ScrollViewer? scroller = null;
        return WpfHost.WithWindow(
            () =>
            {
                // 控件必须在宿主线程上造（WpfHost.WithWindow 用工厂传内容正是为此）
                combo = new ComboBox();
                for (var i = 0; i < 5; i++)
                    combo.Items.Add($"第 {i} 项");
                combo.SelectedIndex = 2;
                var stack = new StackPanel { Children = { combo } };
                // 撑出可滚高度：不滚得动，就分不出"事件被吞了"和"本来就没得滚"
                for (var i = 0; i < 12; i++)
                    stack.Children.Add(new Border { Height = 120 });
                scroller = new ScrollViewer
                {
                    Content = stack,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                };
                return scroller;
            },
            _ =>
            {
                Assert.True(IgnoreWheel.GetEnabled(combo!),
                    "隐式 ComboBox 样式没打开 IgnoreWheel.Enabled，滚轮仍会改选中项");
                if (focusFirst)
                {
                    combo!.Focus();
                    Keyboard.Focus(combo);
                }

                // 复刻输入系统的一对二：先向下隧道，未被处理才接着抛冒泡的那个
                // （换选中项挂在冒泡那一路，所以拦要拦在隧道这一路）。
                var preview = Wheel(UIElement.PreviewMouseWheelEvent);
                combo!.RaiseEvent(preview);
                if (!preview.Handled)
                    combo.RaiseEvent(Wheel(UIElement.MouseWheelEvent));
                WpfHost.Pump();

                return (combo.SelectedIndex, scroller!.VerticalOffset);
            });
    }

    private static MouseWheelEventArgs Wheel(RoutedEvent routed) =>
        new(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = routed };
}
