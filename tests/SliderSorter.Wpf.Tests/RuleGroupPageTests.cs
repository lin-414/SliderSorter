using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SliderSorter.Core;
using SliderSorter.Wpf.ViewModels;
using SliderSorter.Wpf.Views.Pages;
using Xunit;

namespace SliderSorter.Wpf.Tests;

/// <summary>规则分组页：预设列表与规则编辑器合并后的那几条不变量。
///
/// 保留的一条来自一次真实故障：<c>_rows</c> 曾用元组 <c>(RulePreset Preset, string Title, string Detail)</c>
/// 承载行数据，XAML 的 {Binding Title} 于是全部解析失败——元组的元素名只存在于编译期，
/// 运行时是 ValueTuple 的 Item1/Item2；WPF 绑定按反射找名字，找不到就静默给空串。
/// 编译、既有测试全绿，只表现为「预设列表一片空白」。所以这里既对照属性，也真的求值一遍绑定。
///
/// 新增的几条守的是合并带来的新风险：表单与预设列表第一次同屏常驻，于是出现
/// 「选中一条会不会真填进表单」「改了字段有没有说未保存」「切走再回来会不会把没存的编辑抹掉」
/// 「同名保存是不是覆盖」——这些在窗口时代要么不存在、要么由窗口生命周期兜着。
/// <para>
/// 挂 <see cref="WpfStaCollection"/> 串行：要构造真实控件的用例共用一条常驻 STA 线程（见 <see cref="WpfHost"/>）。
/// </para></summary>
[Collection(WpfStaCollection.Name)]
public class RuleGroupPageTests
{
    private static RulePreset Preset(string name, string group, bool add = true, string outfit = "3BA") => new()
    {
        Name = name,
        GroupName = group,
        Add = add,
        OutfitInclude = outfit,
    };

    /// <summary>列表模板里的三条绑定必须都能在行类型上求值出非空文本。
    /// 求值要造真实 TextBlock，所以整段走 <see cref="WpfHost"/> 的常驻 STA 线程。</summary>
    [Fact]
    public void PresetRowsBindToRealProperties()
    {
        var paths = RowTemplateBindingPaths();
        Assert.NotEmpty(paths); // 兜底：解析器写崩时不能"零路径通过"
        Assert.Equal(["Name", "Summary", "Tooltip"],
            paths.Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray());

        var failures = WpfHost.WithWindow(() => new Decorator(), _ =>
        {
            var item = Assert.Single(RulePresetRow.Build([Preset("P1", "G1")]));
            var hits = new List<string>();

            // 对照组：探针本身必须能求值，否则下面无论绑什么都只能得到空串
            if (Evaluate("Length", "hello") != "5")
                hits.Add("绑定探针自身求值失败（下面的断言全都不作数）");

            // 遍历**全部**路径（含重复）：去重只是为了让上面的断言列表稳定，
            // 求值这一步要覆盖每一处真实绑定
            foreach (var path in paths)
            {
                if (item.GetType().GetProperty(path) is null)
                    hits.Add($"行类型 {item.GetType().Name} 上没有属性 {path}（XAML 绑了它）");
                else if (string.IsNullOrEmpty(Evaluate(path, item)))
                    hits.Add($"绑定 {path} 求值为空串（行会渲染成空白）");
            }

            return hits;
        });

        Assert.Empty(failures);
    }

    /// <summary>列表模板（ListBox.ItemTemplate 那一段）里的 {Binding 路径}。
    /// 刻意从 XAML 里读而不是硬写：属性改名后本用例仍会失败。
    /// 只取模板那一段：本页还有树模板与 Banner 绑定，它们的数据源不是行对象。</summary>
    private static List<string> RowTemplateBindingPaths()
    {
        var xaml = File.ReadAllText(Path.Combine(WpfProjectDir(), "Views", "Pages", "RuleGroupPage.xaml"));
        var start = xaml.IndexOf("<ListBox.ItemTemplate>", StringComparison.Ordinal);
        var end = xaml.IndexOf("</ListBox.ItemTemplate>", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "找不到预设列表的 ItemTemplate");
        return Regex.Matches(xaml[start..end], "\\{Binding\\s+([A-Za-z_][A-Za-z0-9_]*)")
            .Select(m => m.Groups[1].Value)
            .ToList();
    }

    private static string Evaluate(string path, object dataContext)
    {
        var probe = new TextBlock { DataContext = dataContext };
        BindingOperations.SetBinding(probe, TextBlock.TextProperty, new Binding(path));
        BindingOperations.GetBindingExpression(probe, TextBlock.TextProperty)!.UpdateTarget();
        return probe.Text;
    }

    private static string WpfProjectDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var probe = Path.Combine(dir.FullName, "src", "SliderSorter.Wpf", "Views", "MainWindow.xaml");
            if (File.Exists(probe))
                return Path.Combine(dir.FullName, "src", "SliderSorter.Wpf");
        }
        throw new InvalidOperationException(
            $"从 {AppContext.BaseDirectory} 向上找不到 src/SliderSorter.Wpf；本用例需要仓库源码在测试程序集附近。");
    }

    // ── 表单与列表的联动 ──

    private static void InvokeClick(RuleGroupPage page, string handler) =>
        typeof(RuleGroupPage)
            .GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(page, [null!, new RoutedEventArgs()]);

    private static void InvokeReload(RuleGroupPage page, bool keepForm) =>
        typeof(RuleGroupPage)
            .GetMethod("Reload", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(page, [keepForm]);

    private static T Field<T>(RuleGroupPage page, string name) => (T)page.FindName(name)!;

    /// <summary>选中一条预设必须把它的六个字段全灌进表单。
    /// 合并之前这是「编辑所选…」按钮拉起编辑器时的责任（那时有一个 <c>LoadPreset</c> 专门测它）；
    /// 现在没有按钮了，选中即编辑，这条不变量挪到了 SelectionChanged 上。</summary>
    [Fact]
    public void SelectingAPresetFillsTheForm()
    {
        using var scope = IsolatedUserState.Enter();
        MainViewModel? vm = null;
        var second = new RulePreset
        {
            Name = "B", GroupName = "G2", Add = false,
            ModInclude = "mod", OutfitInclude = "outfit", OutfitExclude = "skip", UnassignedOnly = false,
        };

        try
        {
            var form = WpfHost.WithWindow(() =>
            {
                vm = new MainViewModel();
                vm.Store.Load([new SliderGroup("G1"), new SliderGroup("G2")]);
                vm.Settings.RulePresets.AddRange([Preset("A", "G1"), second]);
                return new RuleGroupPage { DataContext = vm };
            }, host =>
            {
                var page = (RuleGroupPage)host;
                var list = Field<ListBox>(page, "PresetList");
                Assert.Equal(2, list.Items.Count);

                list.SelectedIndex = 1;
                WpfHost.Pump();
                return (
                    Name: Field<TextBox>(page, "NameBox").Text,
                    Group: (Field<ComboBox>(page, "GroupCombo").SelectedItem as SliderGroup)?.Name,
                    Direction: Field<ComboBox>(page, "DirectionCombo").SelectedIndex,
                    Mod: Field<TextBox>(page, "ModIncludeBox").Text,
                    Outfit: Field<TextBox>(page, "OutfitIncludeBox").Text,
                    Exclude: Field<TextBox>(page, "OutfitExcludeBox").Text,
                    Unassigned: Field<CheckBox>(page, "UnassignedCheck").IsChecked,
                    Dirty: Field<TextBlock>(page, "DirtyMark").Visibility);
            });

            Assert.Equal("B", form.Name);
            Assert.Equal("G2", form.Group);
            Assert.Equal(1, form.Direction); // 0 = 加入，1 = 移出
            Assert.Equal("mod", form.Mod);
            Assert.Equal("outfit", form.Outfit);
            Assert.Equal("skip", form.Exclude);
            Assert.False(form.Unassigned!.Value);
            // 刚载入就是"与已存的那条一致"，此时标未保存等于骗人
            Assert.Equal(Visibility.Collapsed, form.Dirty);
        }
        finally
        {
            vm?.Settings.RulePresets.Clear();
        }
    }

    /// <summary>改了任一字段必须当场说「未保存」。表单与列表同屏之后，
    /// "屏幕上这条"与"设置里那条"第一次会长期不一致，没有这个标记就没人知道要按保存。</summary>
    [Fact]
    public void EditingAFieldMarksTheFormUnsaved()
    {
        using var scope = IsolatedUserState.Enter();
        MainViewModel? vm = null;

        try
        {
            var result = WpfHost.WithWindow(() =>
            {
                vm = new MainViewModel();
                vm.Store.Load([new SliderGroup("G1")]);
                vm.Settings.RulePresets.Add(Preset("A", "G1"));
                return new RuleGroupPage { DataContext = vm };
            }, host =>
            {
                var page = (RuleGroupPage)host;
                var dirty = Field<TextBlock>(page, "DirtyMark");
                Field<ListBox>(page, "PresetList").SelectedIndex = 0;
                WpfHost.Pump();
                var afterLoad = dirty.Visibility;

                Field<TextBox>(page, "OutfitIncludeBox").Text = "改了这里";
                WpfHost.Pump();
                return (afterLoad, AfterEdit: dirty.Visibility);
            });

            Assert.Equal(Visibility.Collapsed, result.Item1);
            Assert.Equal(Visibility.Visible, result.AfterEdit);
        }
        finally
        {
            vm?.Settings.RulePresets.Clear();
        }
    }

    /// <summary>保存：同名覆盖、换名另存，且列表选中跟着刚存的那条走。
    /// 「另存」这条尤其要钉——原实现靠弹窗预填名字，改名与否在同一个输入框里；
    /// 现在名字就是表单第一行，一旦保存变成"总是新增"，用户的每次微调都会冒出一条重复预设。</summary>
    [Fact]
    public void SavingOverwritesSameNameAndKeepsOthers()
    {
        using var scope = IsolatedUserState.Enter();
        MainViewModel? vm = null;

        try
        {
            var result = WpfHost.WithWindow(() =>
            {
                vm = new MainViewModel();
                vm.Store.Load([new SliderGroup("G1")]);
                vm.Settings.RulePresets.Add(Preset("A", "G1"));
                return new RuleGroupPage { DataContext = vm };
            }, host =>
            {
                var page = (RuleGroupPage)host;
                var list = Field<ListBox>(page, "PresetList");
                list.SelectedIndex = 0;
                WpfHost.Pump();

                Field<TextBox>(page, "OutfitIncludeBox").Text = "4C";
                InvokeClick(page, "Save_Click");
                WpfHost.Pump();
                var afterOverwrite = vm!.Settings.RulePresets.ToList();

                // 换名保存 = 另存一条，原来那条必须还在
                Field<TextBox>(page, "NameBox").Text = "B";
                InvokeClick(page, "Save_Click");
                WpfHost.Pump();
                var afterSaveAs = vm.Settings.RulePresets.ToList();
                return (
                    Overwritten: afterOverwrite.Single().OutfitInclude,
                    Count: afterOverwrite.Count,
                    Names: afterSaveAs.Select(p => p.Name).ToArray(),
                    Selected: (list.SelectedItem as RulePresetRow)?.Preset.Name,
                    Dirty: Field<TextBlock>(page, "DirtyMark").Visibility);
            });

            Assert.Equal(1, result.Count);
            Assert.Equal("4C", result.Overwritten);
            Assert.Equal(["A", "B"], result.Names);
            Assert.Equal("B", result.Selected);
            Assert.Equal(Visibility.Collapsed, result.Dirty);
        }
        finally
        {
            vm?.Settings.RulePresets.Clear();
        }
    }

    /// <summary>名字空着不许存成预设：会写出一条没有身份的条目，列表里点不到也删不掉。</summary>
    [Fact]
    public void SavingWithoutANameWritesNothing()
    {
        using var scope = IsolatedUserState.Enter();
        MainViewModel? vm = null;

        try
        {
            var count = WpfHost.WithWindow(() =>
            {
                vm = new MainViewModel();
                vm.Store.Load([new SliderGroup("G1")]);
                return new RuleGroupPage { DataContext = vm };
            }, host =>
            {
                var page = (RuleGroupPage)host;
                Field<TextBox>(page, "NameBox").Text = "   ";
                InvokeClick(page, "Save_Click");
                return vm!.Settings.RulePresets.Count;
            });

            Assert.Equal(0, count);
        }
        finally
        {
            vm?.Settings.RulePresets.Clear();
        }
    }

    /// <summary>切走再回来不能抹掉没保存的编辑。
    /// 合并之前不存在这个坑（编辑器是独立窗口，切标签页动不到它），现在这一页会被 TabControl
    /// 移出可视树再放回来，而"回来时重读预设"是必要的（预设可能在别处被改、语言可能刚换过）——
    /// 所以重读必须只重读列表，不重灌表单。</summary>
    [Fact]
    public void ReloadingForTabSwitchPreservesUnsavedEdits()
    {
        using var scope = IsolatedUserState.Enter();
        MainViewModel? vm = null;

        try
        {
            var result = WpfHost.WithWindow(() =>
            {
                vm = new MainViewModel();
                vm.Store.Load([new SliderGroup("G1")]);
                vm.Settings.RulePresets.Add(Preset("A", "G1", outfit: "3BA"));
                return new RuleGroupPage { DataContext = vm };
            }, host =>
            {
                var page = (RuleGroupPage)host;
                Field<ListBox>(page, "PresetList").SelectedIndex = 0;
                WpfHost.Pump();
                Field<TextBox>(page, "OutfitIncludeBox").Text = "改到一半";

                InvokeReload(page, keepForm: true);
                WpfHost.Pump();
                return (
                    Outfit: Field<TextBox>(page, "OutfitIncludeBox").Text,
                    Selected: Field<ListBox>(page, "PresetList").SelectedIndex,
                    Dirty: Field<TextBlock>(page, "DirtyMark").Visibility);
            });

            Assert.Equal("改到一半", result.Outfit);
            Assert.Equal(0, result.Selected);
            Assert.Equal(Visibility.Visible, result.Dirty);
        }
        finally
        {
            vm?.Settings.RulePresets.Clear();
        }
    }

    /// <summary>空列表与列表本身互斥：有预设时那句「还没有预设」必须收起。
    /// 这类"两态叠在同一格"的写法最容易出满列表上飘着一句空状态（设置页踩过一次）。</summary>
    [Fact]
    public void EmptyHintHidesOnceThereIsAPreset()
    {
        using var scope = IsolatedUserState.Enter();
        MainViewModel? vm = null;

        try
        {
            var result = WpfHost.WithWindow(() =>
            {
                vm = new MainViewModel();
                vm.Store.Load([new SliderGroup("G1")]);
                return new RuleGroupPage { DataContext = vm };
            }, host =>
            {
                var page = (RuleGroupPage)host;
                var hint = Field<TextBlock>(page, "EmptyHint");
                var onEmpty = hint.Visibility;

                vm!.Settings.RulePresets.Add(Preset("A", "G1"));
                InvokeReload(page, keepForm: false);
                return (onEmpty, AfterAdd: hint.Visibility);
            });

            Assert.Equal(Visibility.Visible, result.Item1);
            Assert.Equal(Visibility.Collapsed, result.AfterAdd);
        }
        finally
        {
            vm?.Settings.RulePresets.Clear();
        }
    }
}
