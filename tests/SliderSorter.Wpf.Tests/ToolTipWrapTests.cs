using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace SliderSorter.Wpf.Tests;

/// <summary>
/// 2026-09-25 用户截图的回归：悬浮长句提示显示不全。字符串内容自动生成的 TextBlock 默认不换行，
/// 隐式样式里的 MaxWidth=560 只限得住 ToolTip 的边框、限不住字，溢出部分被屏幕边直接裁掉。
/// 修复是在 ToolTip 模板的 ContentPresenter 资源作用域里给 <c>sys:String</c> 挂一份带换行的
/// 数据模板——字符串内容恰好由模板接管（TextWrapping 附加属性不继承，presenter 自己也没有
/// 可设的口径），非字符串内容不经过它。
/// <para>
/// ToolTip 不允许有逻辑/视觉父级（挂进窗口当场抛），而父级不存在的元素不会自动吃隐式样式，
/// 所以这里显式把隐式样式套上去再量——运行时 ToolTipService 弹出的正是同一份样式。
/// </para>
/// 用「只看跨模组冲突」实际挂的那条长提示量：宽度必须被 MaxWidth 拦住，生成的 TextBlock
/// 必须是 Wrap 且真的折了行。
/// </summary>
[Collection(WpfStaCollection.Name)]
public class ToolTipWrapTests
{
    /// <summary>OutputConflictPage「只看跨模组冲突」实际用的长文案（Lang.zh 套上真实计数）。</summary>
    private const string LongTip =
        "跨模组冲突 581 组，同一模组内部共用 873 组。同一件衣服的几百个配色共用一个输出文件是模组的常态，勾上就先不看它们。";

    [Fact]
    public void LongStringToolTipWrapsInsideMaxWidth()
    {
        // 依赖属性只能在创建它的 STA 线程上读，全部在回调里取成标量再带出来
        var (width, wrapping, textHeight, fontSize) = WpfHost.WithWindow(
            () => new Grid(),
            _ =>
            {
                var tip = new ToolTip { Content = LongTip };
                tip.Style = (Style)Application.Current.TryFindResource(typeof(ToolTip));
                tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var tb = FindTextBlock(tip);
                Assert.NotNull(tb);
                return (tip.DesiredSize.Width, tb!.TextWrapping, tb.DesiredSize.Height, tb.FontSize);
            });

        Assert.True(width <= 561, $"ToolTip 量出宽 {width:0.#}，MaxWidth=560 没拦住，字在往外溢");
        Assert.Equal(TextWrapping.Wrap, wrapping);
        // 单行 13px 字高约 17；超过约两行线高才算真的折了行（阈值故意含糊，别绑死行距参数）
        Assert.True(textHeight > fontSize * 2.2,
            $"文本块高 {textHeight:0.#}，还是单行——提示没换行");
    }

    private static TextBlock? FindTextBlock(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock tb) return tb;
            var found = FindTextBlock(child);
            if (found is not null) return found;
        }

        return null;
    }
}
