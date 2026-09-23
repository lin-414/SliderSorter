using System.Windows;
using System.Windows.Controls;
using SliderSorter.Wpf.ViewModels;
using SliderSorter.Wpf.Views.Pages;
using Xunit;

namespace SliderSorter.Wpf.Tests;

/// <summary>
/// 中间搬运栏（「加入组」/「移出组」）在两栏之间是居中的。
/// <para>
/// 钉住的是用户报的实机故障：两个按钮贴着右栏、左边空一大条。原因是搬运栏所在的那一列是
/// <c>Width="Auto"</c>——列宽恰好等于「按钮 + 外边距」，所以 <c>HorizontalAlignment="Center"</c>
/// 根本没有可偏移的余量，是个静默失效的装饰；当初为了避开 6px 拖拽条写的单边
/// <c>Margin="10,0,0,0"</c> 于是原封不动把整组按钮推到了右栏边缘。
/// </para>
/// <para>
/// 溢出探针（tools/layout-probe）看不见这类问题：它判的是"文字放不下"，而这里每个元素都放得下，
/// 只是整体偏了。所以只能量几何。
/// </para>
/// </summary>
[Collection(WpfStaCollection.Name)]
public class TransferBarLayoutTests
{
    /// <summary>宿主窗口实际排多宽由那台机器的 DPI 与屏幕决定（CI 上请求 1440 只到约 1030），
    /// 所以量之前显式按这个尺寸重排一遍，断言才只跟布局有关、不跟机器有关。</summary>
    private const double ProbeWidth = 1440;
    private const double ProbeHeight = 900;

    /// <summary>拖拽条的宽度，与 GroupGenerationPage.xaml 里 GridSplitter 的 Width 一致。
    /// 居中之所以不能靠"把留白删掉"来实现：那样按钮会盖住这条 6px 的命中区，两栏就拖不动了。</summary>
    private const double SplitterWidth = 6;

    [Theory]
    [InlineData("AddToGroupButton")]
    [InlineData("RemoveFromGroupButton")]
    public void TransferButtonIsCenteredInTheGutter(string buttonName)
    {
        using var scope = IsolatedUserState.Enter();

        var (leftGap, rightGap) = WpfHost.WithWindow(
            () => new GroupGenerationPage { DataContext = new MainViewModel() },
            page =>
            {
                Arrange(page);
                var button = Named<Button>(page, buttonName);
                // Button -> StackPanel -> 搬运栏那一列的 Grid
                var bar = Assert.IsType<Grid>(((FrameworkElement)button.Parent!).Parent!);
                var origin = button.TransformToVisual(bar).Transform(new Point(0, 0));
                return (origin.X, bar.ActualWidth - origin.X - button.ActualWidth);
            },
            ProbeWidth, ProbeHeight);

        Assert.True(leftGap >= SplitterWidth,
            $"左侧只留了 {leftGap:F1}，拖拽条（{SplitterWidth}）被按钮盖住了");
        Assert.True(rightGap >= 0, $"按钮溢出搬运栏：右侧 {rightGap:F1}");
        Assert.Equal(leftGap, rightGap, precision: 1);
    }

    /// <summary>排两遍再泵：绑定的 Content 要过一轮排版才有终宽。</summary>
    private static void Arrange(FrameworkElement host)
    {
        host.UpdateLayout();
        WpfHost.Pump();
        host.Measure(new Size(ProbeWidth, ProbeHeight));
        host.Arrange(new Rect(0, 0, ProbeWidth, ProbeHeight));
        host.UpdateLayout();
    }

    private static T Named<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        Assert.IsType<T>(root.FindName(name) ?? throw new InvalidOperationException($"分组生成页上找不到 {name}"));
}
