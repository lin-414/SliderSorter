using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BSGroupGenerator.Core;
using BSGroupGenerator.Wpf.Views;
using Xunit;

namespace BSGroupGenerator.Wpf.Tests;

/// <summary>规则预设窗口：列表行的文本必须真的渲染出来。
///
/// 起因是一次真实故障：<c>_rows</c> 用元组 <c>(RulePreset Preset, string Title, string Detail)</c> 承载行数据，
/// XAML 的 <c>{Binding Title}</c> / <c>{Binding Detail}</c> 于是全部解析失败。元组的元素名只存在于编译期，
/// 运行时是 <c>ValueTuple</c> 的 <c>Item1/Item2/Item3</c>；WPF 绑定按反射找 <c>Title</c> 找不到就静默给空串
/// ——编译、既有测试全绿，只表现为"预设列表一片空白"，用户以为预设压根没存上。
/// 因此这里既对照属性（结构），也真的求值一遍绑定（运行期），后者才是元组能蒙混过关的那一层。</summary>
[Collection(WpfStaCollection.Name)]
public class RulePresetsWindowTests
{
    [Fact]
    public void PresetRowsBindToRealProperties()
    {
        Exception? captured = null;
        var failures = new List<string>();

        // WPF 窗口只能在 STA 线程上构造（见 HelpWindowTests 对同一套样板代码的说明）。
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                if (app.Resources.MergedDictionaries.Count == 0)
                {
                    // pack URI 必须带程序集名，否则按**入口程序集**（测试宿主）解析而找不到
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/BSGroupGenerator;component/Themes/Controls.xaml"),
                    });
                }

                var presets = new List<RulePreset>
                {
                    new() { Name = "P1", GroupName = "G1", OutfitInclude = "3BA" },
                };
                var window = new RulePresetsWindow(presets, _ => true);
                var list = (ListBox)window.FindName("List")!;
                var item = Assert.Single(list.ItemsSource!.Cast<object>());

                var paths = ListTemplateBindingPaths();
                // 兜底：解析器写崩时不能"零路径通过"
                Assert.Equal(["Detail", "Title"], paths.OrderBy(p => p, StringComparer.Ordinal));

                // 对照组：探针本身必须能求值，否则下面无论绑什么都只能得到空串
                Assert.Equal("5", Evaluate("Length", "hello"));

                foreach (var path in paths)
                {
                    if (item.GetType().GetProperty(path) is null)
                        failures.Add($"列表项类型 {item.GetType().Name} 上没有属性 {path}（XAML 绑了它）");

                    // 真的求值一次：路径写错/绑到元组上时，这里拿到的是空串而不是报错
                    var text = Evaluate(path, item);
                    if (string.IsNullOrEmpty(text))
                        failures.Add($"绑定 {path} 求值为空串（行会渲染成空白）");
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
        var xaml = File.ReadAllText(Path.Combine(WpfProjectDir(), "Views", "RulePresetsWindow.xaml"));

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
                var app = Application.Current ?? new Application();
                if (app.Resources.MergedDictionaries.Count == 0)
                {
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/BSGroupGenerator;component/Themes/Controls.xaml"),
                    });
                }

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

                var window = new RuleGroupWindow(groups, "G1", [],
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

    /// <summary>「编辑所选…」必须把**选中的那条**交给宿主：传错条目、或压根没传，都会让编辑变成"改了别的预设"。
    /// 按钮与双击行共用一个处理函数，这里直接调它（真实点击由宿主窗口在真机上验）。
    ///
    /// 只测"已选中"这一支：未选中那一支要弹提示框，而测试线程里没有消息泵，ShowDialog 会一直等下去
    /// （本用例第一版就是这么把测试挂住的）。"编辑器就绪才关掉预设窗口"也只在真机上验——
    /// 在测试里关窗会牵动 Application 的关闭策略（那是应用级状态，跨线程碰它会直接把测试宿主搞崩）。</summary>
    [Fact]
    public void EditSelectedHandsTheSelectedPresetToTheEditor()
    {
        Exception? captured = null;
        var handed = new List<RulePreset?>();
        var failures = new List<string>();

        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                if (app.Resources.MergedDictionaries.Count == 0)
                {
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/BSGroupGenerator;component/Themes/Controls.xaml"),
                    });
                }

                var first = new RulePreset { Name = "A", GroupName = "G1" };
                var second = new RulePreset { Name = "B", GroupName = "G2" };
                // 编辑器"没就绪"（返回 false）：这条支路不关窗，测试里也就不碰 Window.Close
                var window = new RulePresetsWindow([first, second], preset =>
                {
                    handed.Add(preset);
                    return false;
                });
                var list = (ListBox)window.FindName("List")!;
                list.SelectedIndex = 1;

                typeof(RulePresetsWindow).GetMethod("Edit_Click",
                    BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(window, [null!, new RoutedEventArgs()]);

                if (handed.Count != 1 || !ReferenceEquals(handed[0], second))
                    failures.Add($"编辑所选送出的是 {handed.LastOrDefault()?.Name ?? "<无>"}，预期 B");
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.Null(captured);
        Assert.Empty(failures);
    }

    /// <summary>列表模板里 <c>{Binding}</c> 的路径（本文件的模板只有 Title/Detail 两个）。
    /// 刻意从 XAML 里读而不是硬写：路径改名后本用例仍会失败。</summary>
    private static List<string> ListTemplateBindingPaths()
    {
        var xaml = File.ReadAllText(Path.Combine(WpfProjectDir(), "Views", "RulePresetsWindow.xaml"));
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
