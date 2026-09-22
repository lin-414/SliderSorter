using System.Windows;
using System.Windows.Controls;
using BSGroupGenerator.Wpf.ViewModels;
using BSGroupGenerator.Wpf.Views.Pages;
using Xunit;

namespace BSGroupGenerator.Wpf.Tests;

/// <summary>
/// 设置页单列骨架的两条静默失效。
/// <para>
/// ① <b>标签轨没共享出去。</b>三节字段 Grid 的首列靠 <c>SharedSizeGroup="SettingsRail"</c> 对到同一条
/// 左轨上（boutique 用定宽 140，本项目不行：法语「Langue de l'interface」比中文宽得多，定宽会在 fr/ru
/// 把标签压窄）。漏写祖先的 <c>Grid.IsSharedSizeScope</c>、或者某处组名打错一个字，结果是每节各量各的
/// 标签宽——中文下四节"看着都对"，只是那条对齐的基准线没了。编译期、色板门禁、布局探针全都不会红。
/// </para>
/// <para>
/// ② <b>折叠面板不再收起。</b>说明正文 60+ 段、诊断报告要跑一遍扫描，两者的懒构建都挂在
/// <see cref="MainViewModel.IsManualExpanded"/> / <see cref="MainViewModel.IsDiagnosticsExpanded"/> 上
/// （壳的 F1 也走这条属性）。哪天有人把 Visibility 写死或换错转换器，收起态会照样占半屏，
/// 展开态却再也出不来。
/// </para>
/// <para>
/// ③ <b>卡片没铺满整页。</b>根 Grid 曾钉着 <c>MaxWidth="860"</c> 居中，宽窗口下四张卡片两侧各留一条
/// 空带。这类"没铺满"探针查不出来——它只判溢出，比可用宽度窄多少它不管；颜色、语言、绑定更是全都
/// 照常。所以这条只能在这儿量。
/// </para>
/// 断言只看附加属性与 Visibility，不看元素位置：宿主窗口是离屏的，没有稳定的客户区尺寸。
/// 标签轨的实宽是唯一的例外——Auto + 共享组的宽度只由内容决定，与给多少约束无关，所以量得准
/// （原来那个限宽断点不能这么测，正是因为它绑的是 star 列）。铺满那条量的也是 star 列，而宿主窗口
/// 实际排多宽由那台机器的 DPI 与屏幕决定（CI 上请求 1440 只到约 1030），所以量之前先显式按固定
/// 尺寸重排一遍再读——直接拿窗口给的真实宽度当基准，红的会是机器而不是布局。
/// 挂 <see cref="WpfStaCollection"/> 串行：窗口只能在 STA 线程构造，而 Application 是进程级单例。
/// </summary>
[Collection(WpfStaCollection.Name)]
public class SettingsPageLayoutTests
{
    private static readonly string[] FieldGridNames = ["EnvGrid", "OutputGrid", "AppearanceGrid"];

    [Fact]
    public void EveryFieldSectionSitsOnTheSharedLabelRail()
    {
        var (scopeShared, rails) = WpfHost.WithWindow(
            () => new SettingsPage { DataContext = new MainViewModel() }, MeasureRails, 1440, 900);

        Assert.True(scopeShared, "承载四节的 StackPanel 上没有 Grid.IsSharedSizeScope，共享组就成了各说各话");
        Assert.Equal(FieldGridNames.Length, rails.Length);
        Assert.All(rails, rail => Assert.Equal("SettingsRail", rail.Group));
        // 结构对了还要真量出同一条线：共享失败最典型的表现就是各自宽度互不相等
        Assert.All(rails, rail => Assert.True(rail.Width > 0, "标签轨没有实宽，说明这一节根本没参与排版"));
        Assert.Equal(rails.Min(rail => rail.Width), rails.Max(rail => rail.Width), precision: 1);
    }

    /// <summary>量宽度用的固定约束。宿主窗口 Show 出来只是为了让模板项与绑定真的挂上，
    /// 而窗口本身排多宽由那台机器的 DPI 与屏幕决定（CI 的 windows-latest 上请求 1440 只到约 1030），
    /// 所以量之前显式按这个尺寸重排一遍，断言才只跟布局有关、不跟机器有关。</summary>
    private const double ProbeWidth = 1440;
    private const double ProbeHeight = 900;

    [Fact]
    public void CardsSpanTheFullPageWidth()
    {
        var (panelWidth, cardWidths) = WpfHost.WithWindow(
            () => new SettingsPage { DataContext = new MainViewModel() },
            root =>
            {
                Arrange(root);
                root.Measure(new Size(ProbeWidth, ProbeHeight));
                root.Arrange(new Rect(0, 0, ProbeWidth, ProbeHeight));
                var panel = Named<StackPanel>(root, "SectionsPanel");
                return (panel.ActualWidth,
                        panel.Children.OfType<Border>().Select(card => card.ActualWidth).ToArray());
            },
            ProbeWidth, ProbeHeight);

        // 四节就是四张卡片：数错说明有人把某节拆成了两张卡、或塞了别的 Border 进来
        Assert.Equal(4, cardWidths.Length);
        Assert.All(cardWidths, width => Assert.Equal(panelWidth, width, precision: 1));
        // 10% 余量给页边距和垂直滚动条（滚动条折算成 DIP 会随 DPI 变），
        // 而把根 Grid 限回 MaxWidth="860" 居中会掉到这一尺寸的六成上下。
        Assert.True(panelWidth >= ProbeWidth * 0.9,
            $"卡片没铺满整页：按 {ProbeWidth:F0} 排时面板只有 {panelWidth:F0}");
    }

    [Fact]
    public void DisclosuresStartCollapsedAndFollowTheViewModel()
    {
        MainViewModel? vm = null;
        var results = WpfHost.WithWindow(
            () =>
            {
                vm = new MainViewModel();
                return new SettingsPage { DataContext = vm };
            },
            host =>
            {
                Arrange(host);
                var manual = Named<Border>(host, "ManualHost");
                var diagnostics = Named<Grid>(host, "DiagnosticsHost");
                Assert.Equal(Visibility.Collapsed, manual.Visibility);
                Assert.Equal(Visibility.Collapsed, diagnostics.Visibility);

                // F1 展开说明走的就是这条属性，所以它必须真的把面板排出来，而不是只在代码后台备好了正文
                vm!.IsManualExpanded = true;
                Arrange(host);
                return (Manual: manual.Visibility, Diagnostics: diagnostics.Visibility);
            },
            1440, 900);

        Assert.Equal(Visibility.Visible, results.Manual);
        Assert.Equal(Visibility.Collapsed, results.Diagnostics);
    }

    /// <summary>量三节字段 Grid 的首列：返回「祖先是否开了共享尺寸域」与各列的 (组名, 实宽)。
    /// 值必须在宿主线程上读完再交出：DependencyObject 有线程亲和，把控件本身带回 xUnit 线程读属性会炸。</summary>
    private static (bool ScopeShared, (string Group, double Width)[] Rails) MeasureRails(FrameworkElement root)
    {
        Arrange(root);
        var scope = Grid.GetIsSharedSizeScope(Named<StackPanel>(root, "SectionsPanel"));
        var rails = FieldGridNames
            .Select(name => Named<Grid>(root, name).ColumnDefinitions[0])
            .Select(col => (col.SharedSizeGroup, col.ActualWidth))
            .ToArray();
        return (scope, rails);
    }

    /// <summary>排两遍再泵：绑定与共享尺寸都要过一轮 Measure/Arrange 才有终值。</summary>
    private static void Arrange(FrameworkElement host)
    {
        host.UpdateLayout();
        WpfHost.Pump();
        host.UpdateLayout();
    }

    /// <summary>x:Name 生成的字段是 internal，跨程序集取不到；走 FindName（页面自己是 XAML 名称域的根）。</summary>
    private static T Named<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        Assert.IsType<T>(root.FindName(name) ?? throw new InvalidOperationException($"设置页上找不到 {name}"));
}
