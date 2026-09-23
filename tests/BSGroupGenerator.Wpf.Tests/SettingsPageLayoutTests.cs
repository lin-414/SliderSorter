using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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
                // 只数**卡片**：SectionsPanel 里除卡片外还挂着引导条（ContentControl）等非卡片元素，
                // 曾用 panel.Children.OfType<Border>() 一把抓，加进引导条就得多改一处断言。
                return (panel.ActualWidth,
                        panel.Children.OfType<Border>()
                            .Where(card => card.Style == (Style)panel.FindResource("SettingCard"))
                            .Select(card => card.ActualWidth)
                            .ToArray());
            },
            ProbeWidth, ProbeHeight);

        // 五节就是五张卡片：工作环境 / 输出设置 / 外观 / 维护 / 帮助与关于。
        // 数错说明有人把某节拆成了两张卡、或塞了别的卡片级 Border 进来。
        // 加了新节就改这个数——它是"节与卡一一对应"这条不变式的锚点。
        Assert.Equal(5, cardWidths.Length);
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

    /// <summary>折叠条必须是 ToggleButton 且双向绑定：控件状态与 VM 状态互为镜像。
    ///
    /// 两个方向都要测，因为它们是两条互不覆盖的失败路径：
    /// ① 控件 → VM（新写法独有）。原先是普通 Button + Click 处理函数手动翻转布尔值，
    ///    控件自己不持有状态；换成 ToggleButton 后靠 <c>IsChecked="{Binding ..., Mode=TwoWay}"</c>。
    ///    漏掉 <c>Mode=TwoWay</c>（ToggleButton.IsChecked 默认是 TwoWay，但显式写出来更稳）
    ///    或绑错属性，表现是"点了展开、松手弹回去"——而 VM 侧那条测试（F1 方向）照样绿。
    /// ② 箭头方向随状态转。模板里的 IsChecked 触发器若丢了，箭头永远朝右，
    ///    用户看不出面板是开是关；这条在布局、绑定、色板门禁里都不会红，只能在这儿量。
    /// </summary>
    [Fact]
    public void DisclosureToggleDrivesTheViewModelBothWays()
    {
        MainViewModel? vm = null;
        var result = WpfHost.WithWindow(
            () =>
            {
                vm = new MainViewModel();
                return new SettingsPage { DataContext = vm };
            },
            host =>
            {
                Arrange(host);
                var manual = Named<ToggleButton>(host, "ManualToggle");
                var arrow = FindArrow(manual);

                // 初始：VM 是 false，控件必须是未勾选、箭头朝右（0°）
                Assert.False(manual.IsChecked);
                Assert.Equal(0.0, ArrowAngle(arrow), precision: 3);

                // ① 控件 → VM：模拟用户点击打开
                manual.IsChecked = true;
                Arrange(host);
                var afterOpenVm = vm!.IsManualExpanded;
                var afterOpenAngle = ArrowAngle(arrow);

                // ② 再点一次收起
                manual.IsChecked = false;
                Arrange(host);
                return (afterOpenVm, afterOpenAngle, AfterCloseVm: vm!.IsManualExpanded,
                        AfterCloseAngle: ArrowAngle(arrow));
            },
            1440, 900);

        Assert.True(result.afterOpenVm, "点开折叠条没有回写到 VM：面板不会展开");
        Assert.Equal(90.0, result.afterOpenAngle, precision: 3);
        Assert.False(result.AfterCloseVm, "再点一次没有收起：VM 还是展开态");
        Assert.Equal(0.0, result.AfterCloseAngle, precision: 3);
    }

    /// <summary>折叠条模板里的箭头 Path。按名字找而不是"第 0 个 Path"：
    /// 模板内元素藏在 Template 里，FindName 对模板命名域不一定生效，用可视化树搜更直白。</summary>
    private static System.Windows.Shapes.Path FindArrow(DependencyObject root)
    {
        foreach (var child in Descendants(root))
            if (child is System.Windows.Shapes.Path { Name: "arrow" } path)
                return path;
        throw new InvalidOperationException("折叠条模板里找不到名为 arrow 的箭头 Path");
    }

    private static double ArrowAngle(System.Windows.Shapes.Path arrow) =>
        arrow.RenderTransform is RotateTransform rotate ? rotate.Angle : double.NaN;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
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
