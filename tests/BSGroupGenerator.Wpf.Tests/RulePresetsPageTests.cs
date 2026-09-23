using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BSGroupGenerator.Core;
using BSGroupGenerator.Wpf.ViewModels;
using BSGroupGenerator.Wpf.Views.Pages;
using Xunit;

namespace BSGroupGenerator.Wpf.Tests;

/// <summary>规则预设页：列表行的文本必须真的渲染出来，且「编辑所选」交出去的是选中的那条。
///
/// 前者起因是一次真实故障：<c>_rows</c> 用元组 <c>(RulePreset Preset, string Title, string Detail)</c> 承载行数据，
/// XAML 的 <c>{Binding Title}</c> / <c>{Binding Detail}</c> 于是全部解析失败。元组的元素名只存在于编译期，
/// 运行时是 <c>ValueTuple</c> 的 <c>Item1/Item2/Item3</c>；WPF 绑定按反射找 <c>Title</c> 找不到就静默给空串
/// ——编译、既有测试全绿，只表现为"预设列表一片空白"，用户以为预设压根没存上。
/// 因此这里既对照属性（结构），也真的求值一遍绑定（运行期），后者才是元组能蒙混过关的那一层。
/// <para>
/// 挂 <see cref="WpfStaCollection"/> 串行：要构造真实控件的用例共用一条常驻 STA 线程（见 <see cref="WpfHost"/>）。
/// </para></summary>
[Collection(WpfStaCollection.Name)]
public class RulePresetsPageTests
{
    private static RulePreset Preset(string name, string group) => new()
    {
        Name = name,
        GroupName = group,
        OutfitInclude = "3BA",
    };

    [Fact]
    public void PresetRowsBindToRealProperties()
    {
        Exception? captured = null;
        var failures = new List<string>();

        var thread = new Thread(() =>
        {
            try
            {
                var rows = RulePresetRow.Build([Preset("P1", "G1")]);
                var item = Assert.Single(rows);

                var paths = ListTemplateBindingPaths();
                // 兜底：解析器写崩时不能"零路径通过"。
                // 去重是必须的：同一个属性可以合法地出现在多个位置上——左列模板就用
                // Text + ToolTip 两处绑同一个 Title（长名称被省略号截断时，ToolTip 是唯一能看到全名的地方）。
                // 不去重的话，加一个 ToolTip 就会让这条断言红，而它要守的是"路径能在行类型上求值"，不是"每条只出现一次"。
                var distinct = paths.Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();
                Assert.Equal(["Title"], distinct);

                // 详情栏的绑定不走模板（是 code-behind 直接赋 Text 的），所以 Title/Detail
                // 两个属性都得真能在行类型上求值——Detail 现在只经 code-behind 使用，
                // 但一旦有人把它改成绑定、而属性被改名，这里要能拦住。
                Assert.NotNull(item.GetType().GetProperty("Detail"));
                Assert.NotNull(item.GetType().GetProperty("Title"));

                // 对照组：探针本身必须能求值，否则下面无论绑什么都只能得到空串
                Assert.Equal("5", Evaluate("Length", "hello"));

                // 遍历**全部**路径（含重复）而不是去重后的：去重只是为了让断言列表稳定，
                // 求值这一步要覆盖每一处真实绑定。
                foreach (var path in paths)
                {
                    if (item.GetType().GetProperty(path) is null)
                        failures.Add($"列表项类型 {item.GetType().Name} 上没有属性 {path}（XAML 绑了它）");

                    // 真的求值一次：路径写错/绑到元组上时，这里拿到的是空串而不是报错
                    var text = Evaluate(path, item);
                    if (string.IsNullOrEmpty(text))
                        failures.Add($"绑定 {path} 求值为空串（行会渲染成空白）");
                }

                // 详情栏由 code-behind 直接填 Text，不经过模板，所以要单独求值一遍：
                // 这两条属性是右栏的唯一数据源，空了右栏就是一块空白面板。
                foreach (var path in new[] { "Title", "Detail" })
                {
                    if (string.IsNullOrEmpty(Evaluate(path, item)))
                        failures.Add($"详情栏的 {path} 求值为空串（右栏会渲染成空白）");
                }
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(captured);
        Assert.Empty(failures);
    }

    /// <summary>规则归组窗口的「关闭」必须显式接 Click。
    ///
    /// 该窗口是 <c>Show()</c> 打开的**非模态**窗口，而 IsCancel 的"关掉自己"语义只在 <c>ShowDialog()</c>
    /// 的窗口上成立（WPF 的 DialogCancelCommand 对非模态窗口什么都不做）。只写 IsCancel 的后果是按钮
    /// 点下去毫无反应、只能靠标题栏的 × 关窗，用户会当成程序卡住。这里按 XAML 静态对照，逼作者保留 Click。</summary>
    [Fact]
    public void RuleEditorCloseButtonHasExplicitClickHandler()
    {
        var path = Path.Combine(WpfProjectDir(), "Views", "RuleGroupWindow.xaml");
        Assert.True(File.Exists(path), $"找不到 {path}");

        var buttons = Regex.Matches(File.ReadAllText(path), "<Button\\b[^>]*>", RegexOptions.Singleline)
            .Select(m => m.Value)
            .Where(tag => tag.Contains("L.Btn_Close", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(buttons); // 兜底：正则没匹配到就不是"零违规通过"
        Assert.All(buttons, tag => Assert.Contains("Click=", tag));
    }

    /// <summary>预设列表必须有能打开既有预设的入口：「编辑所选…」按钮 + 双击行。
    /// 此前这里只有新建/删除，列表里没有任何可编辑的路径，用户的原话是"无法编辑规则预设"。</summary>
    [Fact]
    public void PresetListExposesEditEntryPoints()
    {
        var xaml = File.ReadAllText(Path.Combine(WpfProjectDir(), "Views", "Pages", "RulePresetsPage.xaml"));

        var editButtons = Regex.Matches(xaml, "<Button\\b[^>]*>", RegexOptions.Singleline)
            .Select(m => m.Value)
            .Where(tag => tag.Contains("L.RulePresets_Edit", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(editButtons); // 兜底：正则没匹配到就不是"零违规通过"
        Assert.All(editButtons, tag => Assert.Contains("Click=", tag));
        Assert.Contains("MouseDoubleClick=\"List_DoubleClick\"", xaml);
    }

    /// <summary>「编辑所选」拉起的编辑器必须预填成那条预设：目标组、方向、三个关键字、仅未分配。
    /// 空表单等于让用户重填一遍，和"不能编辑"没区别。复用同一个编辑器窗口时也走 LoadPreset，一并钉住。</summary>
    [Fact]
    public void RuleEditorLoadsAnExistingPresetForEditing()
    {
        Exception? captured = null;
        var failures = new List<string>();

        var thread = new Thread(() =>
        {
            try
            {
                var groups = new List<SliderGroup> { new("G1"), new("G2") };
                var preset = new RulePreset
                {
                    Name = "P1",
                    GroupName = "G2",
                    Add = false,
                    ModInclude = "mod",
                    OutfitInclude = "outfit",
                    OutfitExclude = "skip",
                    UnassignedOnly = false,
                };

                var window = new BSGroupGenerator.Wpf.Views.RuleGroupWindow(groups, "G1", [],
                    (_, _, _, _) => [],
                    () => { }, onSavePreset: _ => { }, presetToEdit: preset);

                // 编辑器载入后再存一次，读回来应当是同一条内容（LoadPreset ↔ CapturePreset 成对）
                var loaded = window.CapturePreset("P1");
                foreach (var (field, expected, actual) in new (string, object, object)[]
                         {
                             ("GroupName", "G2", loaded.GroupName),
                             ("Add", false, loaded.Add),
                             ("ModInclude", "mod", loaded.ModInclude),
                             ("OutfitInclude", "outfit", loaded.OutfitInclude),
                             ("OutfitExclude", "skip", loaded.OutfitExclude),
                             ("UnassignedOnly", false, loaded.UnassignedOnly),
                         })
                {
                    if (!expected.Equals(actual))
                        failures.Add($"载入预设后 {field} 应为 {expected}，实际是 {actual}");
                }

                // 复用已打开的编辑器（宿主窗口还开着时再点编辑）走的是 LoadPreset
                window.LoadPreset(new RulePreset { Name = "P2", GroupName = "G1", Add = true, UnassignedOnly = true });
                var second = window.CapturePreset("P2");
                if (second.GroupName != "G1" || !second.Add || !second.UnassignedOnly || second.ModInclude.Length != 0)
                    failures.Add("LoadPreset 复用同一窗口时没有把字段全部换成新预设");
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(captured);
        Assert.Empty(failures);
    }

    /// <summary>「编辑所选…」必须把**选中的那条**交给编辑器：传错条目、或压根没传，都会让编辑变成"改了别的预设"。
    /// 按钮与双击行走的是同一个 <c>SelectedPreset()</c>，这里走按钮那条支路。
    ///
    /// 只测"已选中"这一支：未选中那一支要弹提示框，而测试线程里没有消息泵，ShowDialog 会一直等下去
    /// （本用例第一版就是这么把测试挂住的）。</summary>
    [Fact]
    public void EditSelectedHandsTheSelectedPresetToTheEditor()
    {
        // 页面读的是 AppSettings.Shared 里的预设清单，所以整个用例在隔离目录下跑，
        // 结束时把清单清空，不把这两条测试预设留给同一进程里的其他用例。
        using var scope = IsolatedUserState.Enter();
        var first = Preset("A", "G1");
        var second = Preset("B", "G2");
        var handed = new List<RulePreset?>();
        MainViewModel? vm = null;

        // VM 与页面都在常驻 UI 线程上造（工厂在 WpfHost 的线程里求值）：
        // 预设先塞进 VM 再建页面，页面构造期的那次 Reload 就能把两行摆好。
        try
        {
            var outcome = WpfHost.WithWindow(() =>
            {
                vm = new MainViewModel();
                vm.Settings.RulePresets.AddRange([first, second]);
                // 返回 false＝"编辑器没就绪"：这条支路不该有任何副作用，正好用来做纯记录
                vm.RuleEditorOpener = preset =>
                {
                    handed.Add(preset);
                    return false;
                };
                return new RulePresetsPage { DataContext = vm };
            }, host =>
            {
                var page = (RulePresetsPage)host;
                var list = (ListBox)page.FindName("List")!;
                Assert.Equal(2, list.Items.Count);
                list.SelectedIndex = 1;

                typeof(RulePresetsPage).GetMethod("Edit_Click",
                    BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(page, [null!, new RoutedEventArgs()]);
                return handed.Count;
            });

            Assert.Equal(1, outcome);
            Assert.Same(second, handed[0]);
        }
        finally
        {
            // 预设清单挂在进程级共享的 AppSettings 上，不清就会留给同一进程里的下一个用例
            vm?.Settings.RulePresets.Clear();
        }
    }

    /// <summary>分栏后右栏的两态必须互斥且都真的出现：选中一条显详情，未选中显引导。
    ///
    /// 这一类"两态互斥"最容易出的错是**两态同时可见**（先前设置页的空状态就踩过：
    /// 满列表上飘着一句"还没有可显示的冲突"）。这里两个元素在同一格 Grid 里叠放，
    /// 靠 Visibility 切换——漏切一个，就会出现引导语压在详情上的画面。
    /// 另外详情文本必须真的填进去：code-behind 赋值漏了或赋错字段，面板在、字是空的。
    /// </summary>
    [Fact]
    public void DetailPaneSwitchesBetweenSelectionAndHint()
    {
        using var scope = IsolatedUserState.Enter();
        MainViewModel? vm = null;

        try
        {
            var result = WpfHost.WithWindow(() =>
            {
                vm = new MainViewModel();
                vm.Settings.RulePresets.AddRange([Preset("P1", "G1"), Preset("P2", "G2")]);
                return new RulePresetsPage { DataContext = vm };
            }, host =>
            {
                var page = (RulePresetsPage)host;
                var list = (ListBox)page.FindName("List")!;
                var panel = (StackPanel)page.FindName("DetailPanel")!;
                var hint = (TextBlock)page.FindName("DetailHint")!;
                var title = (TextBlock)page.FindName("DetailTitle")!;
                var body = (TextBlock)page.FindName("DetailBody")!;

                // 未选中：引导可见、详情收起
                var (hintOnEmpty, panelOnEmpty) = (hint.Visibility, panel.Visibility);

                // 选中第二条：详情可见、引导收起，且文本非空
                list.SelectedIndex = 1;
                WpfHost.Pump();
                return (hintOnEmpty, panelOnEmpty,
                        HintOnSelect: hint.Visibility, PanelOnSelect: panel.Visibility,
                        TitleText: title.Text, BodyText: body.Text);
            });

            Assert.Equal(Visibility.Visible, result.hintOnEmpty);
            Assert.Equal(Visibility.Collapsed, result.panelOnEmpty);
            Assert.Equal(Visibility.Collapsed, result.HintOnSelect);
            Assert.Equal(Visibility.Visible, result.PanelOnSelect);
            Assert.False(string.IsNullOrWhiteSpace(result.TitleText), "右栏详情标题是空的");
            Assert.False(string.IsNullOrWhiteSpace(result.BodyText), "右栏详情正文是空的");
        }
        finally
        {
            vm?.Settings.RulePresets.Clear();
        }
    }

    /// <summary>列表模板里 <c>{Binding}</c> 的路径（本页的模板只有 Title/Detail 两个）。
    /// 刻意从 XAML 里读而不是硬写：路径改名后本用例仍会失败。</summary>
    private static List<string> ListTemplateBindingPaths()
    {
        var xaml = File.ReadAllText(Path.Combine(WpfProjectDir(), "Views", "Pages", "RulePresetsPage.xaml"));
        return Regex.Matches(xaml, "\\{Binding\\s+([A-Za-z_][A-Za-z0-9_]*)")
            .Select(m => m.Groups[1].Value)
            .ToList();
    }

    /// <summary>把一条绑定路径在给定数据源上求值一次，返回渲染出来的文本。
    /// 绑定解析失败时 WPF 不抛异常，只有空串——本用例要抓的正是这个静默失败。</summary>
    private static string Evaluate(string path, object dataContext)
    {
        var probe = new TextBlock { DataContext = dataContext };
        BindingOperations.SetBinding(probe, TextBlock.TextProperty, new Binding(path));
        BindingOperations.GetBindingExpression(probe, TextBlock.TextProperty)!.UpdateTarget();
        return probe.Text;
    }

    /// <summary>WPF 项目的源码目录。从测试程序集往上找仓库根，避免写死 bin 的层级深度。</summary>
    private static string WpfProjectDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var probe = Path.Combine(dir.FullName, "src", "BSGroupGenerator.Wpf", "Views", "MainWindow.xaml");
            if (File.Exists(probe))
                return Path.Combine(dir.FullName, "src", "BSGroupGenerator.Wpf");
        }
        throw new InvalidOperationException(
            $"从 {AppContext.BaseDirectory} 向上找不到 src/BSGroupGenerator.Wpf；本用例需要仓库源码在测试程序集附近。");
    }
}
