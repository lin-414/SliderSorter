using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SliderSorter.Core;
using SliderSorter.Wpf.ViewModels;
using SliderSorter.Wpf.Views.Pages;
using Xunit;

namespace SliderSorter.Wpf.Tests;

/// <summary>
/// 「输出归属」页的行渲染与勾选回写。
/// <para>
/// 这里钉住的是**模板绑定真的取到了值**：绑定路径写错时 WPF 只静默渲染成空文本——布局探针不喂数据上下文
/// 看不到，纯逻辑单测不看视觉树也漏过。实机截图里"候选服装名整列空白"就是这么来的。
/// </para>
/// 页面由 <see cref="WpfHost"/> 装进一个离屏窗口，再直接 <c>ShowRequest</c> 喂一份清单：
/// 壳那边这份清单来自 <see cref="MainViewModel.CurrentConflict"/>，但造一个真 VM 会连带触发扫描，
/// 而那些与本用例要验的东西无关。
/// <para>
/// 挂 <see cref="WpfStaCollection"/> 串行：窗口只能在 STA 线程构造，而 Application 是进程级单例。
/// </para>
/// </summary>
[Collection(WpfStaCollection.Name)]
public class OutputConflictPageTests
{
    private const string OutPath = @"meshes\actors\character\character assets\clothes\bikini";

    private static OutputConflictGroup Group(string path, params (string Name, string Owner, int Layer)[] candidates) => new()
    {
        OutputFilePath = path,
        KeySpellings = [path],
        GenWeights = true,
        Candidates = candidates.OrderBy(c => c.Layer)
            .Select(c => new ConflictCandidate(c.Name, c.Owner, c.Name + ".xml", c.Layer, true))
            .ToList(),
    };

    private static void ApplyCollapsed(HashSet<string>? collapsed, string name, bool isCollapsed)
    {
        if (collapsed is null)
            return;
        if (isCollapsed)
            collapsed.Add(name);
        else
            collapsed.Remove(name);
    }

    private static ConflictRequest Request(IReadOnlyList<OutputConflictGroup> groups,
        Action<IReadOnlyDictionary<string, string>>? save = null,
        IReadOnlyList<SliderGroup>? userGroups = null,
        HashSet<string>? collapsed = null) => new()
    {
        Groups = groups,
        Choices = new Dictionary<string, string>(StringComparer.Ordinal),
        IsGrouped = _ => false,
        // 默认"一个组都没建"：本页据此不做分类，其余用例看到的仍是平铺一层的列表
        UserGroups = () => userGroups ?? [],
        // 折叠状态照真实设置那样给一个可变集合：传 null 时"点了什么都不会发生"，
        // 与真实设置的默认值（空列表 = 全展开）同义
        IsGroupCollapsed = name => collapsed?.Contains(name) == true,
        SetGroupCollapsed = (name, isCollapsed) => ApplyCollapsed(collapsed, name, isCollapsed),
        // 「全部展开/折叠」走批量回写：测试里与逐条同一份语义（真实实现差的只是落盘次数）
        SetGroupsCollapsed = batch =>
        {
            foreach (var (name, isCollapsed) in batch)
                ApplyCollapsed(collapsed, name, isCollapsed);
        },
        SourceNifOf = _ => null,
        Save = save ?? (_ => { }),
        BuildSelectionPath = "",
        Export = () => (false, (string?)null),
        // 这些用例只验列表渲染与勾选回写，源网格一律解析不出来，也就用不上数据视图
        Assets = null,
    };

    /// <summary>建页 → 离屏 Show → 喂清单 → 泵一遍，然后把页交给用例检查。</summary>
    private static T WithPage<T>(ConflictRequest request, Func<OutputConflictPage, T> inspect) =>
        WpfHost.WithWindow(() => new OutputConflictPage(), host =>
        {
            var page = (OutputConflictPage)host;
            page.ShowRequest(request);
            WpfHost.Pump();
            page.UpdateLayout();
            return inspect(page);
        });

    private static T WithPage<T>(IReadOnlyList<OutputConflictGroup> groups, Func<OutputConflictPage, T> inspect) =>
        WithPage(Request(groups), inspect);

    /// <summary>x:Name 生成的字段是 internal，跨程序集取不到；走 FindName（页面自己是 XAML 名称域的根）。</summary>
    private static T Named<T>(FrameworkElement root, string name) where T : class =>
        Assert.IsType<T>(root.FindName(name));

    private static ListBox Box(FrameworkElement root, string name) => Named<ListBox>(root, name);

    private static List<string> TextsOf(DependencyObject root)
    {
        var texts = new List<string>();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock block && !string.IsNullOrEmpty(block.Text))
                texts.Add(block.Text);
            texts.AddRange(TextsOf(child));
        }
        return texts;
    }

    /// <summary>沿视觉树往上找指定类型的祖先（模板内部元素拿不到 x:Name，只能这样定位）。</summary>
    static UIElement? AncestorOf(DependencyObject node, Type type)
    {
        for (var parent = VisualTreeHelper.GetParent(node); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent.GetType() == type)
                return parent as UIElement;
        return null;
    }

    private static List<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                found.Add(match);
            found.AddRange(Descendants<T>(child));
        }
        return found;
    }

    [Fact]
    public void CandidateRowsRenderTheirOwnOutfitNames()
    {
        var groups = new[] { Group(OutPath, ("Alpha Suit", "ModA", 0), ("Beta Suit", "ModB", 1)) };

        var rows = WithPage(groups, page =>
            Descendants<ListBoxItem>(Box(page, "CandidateList")).Select(TextsOf).ToList());

        // 服装名必须真的落在它那一行里。绑定路径写错时 WPF 只会静默留白——
        // 布局探针不喂数据上下文、只看 Items 的用例也都发现不了，实机截图里的整列空白就是这么来的。
        Assert.Equal(3, rows.Count);
        Assert.Contains("Alpha Suit", rows[0]);
        Assert.Contains("Beta Suit", rows[1]);
        Assert.NotEmpty(rows[2]); // 末行"不指定"同样要有字，否则用户看不出那是个可选项
    }

    [Fact]
    public void ClickingAWinnerSavesThroughTheRequestAndMarksTheGroupResolved()
    {
        var saved = new List<IReadOnlyDictionary<string, string>>();
        var groups = new[] { Group(OutPath, ("Alpha Suit", "ModA", 0), ("Beta Suit", "ModB", 1)) };

        var outcome = WithPage(Request(groups, dict => saved.Add(dict)), page =>
        {
            var radios = Descendants<RadioButton>(Box(page, "CandidateList"));
            Assert.Equal(3, radios.Count); // 两个候选 + "不指定"
            Assert.True(radios[2].IsChecked); // 没选过时默认落在"不指定"上
            var before = string.Join("|", TextsOf(Box(page, "GroupList")));

            radios[1].IsChecked = true;
            radios[1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            WpfHost.Pump();
            page.UpdateLayout();

            return new
            {
                Clicks = saved.Count,
                Chosen = saved.Count == 0 ? null : saved[^1].Values.SingleOrDefault(),
                After = string.Join("|", TextsOf(Box(page, "GroupList"))),
                Before = before,
                Checked = Descendants<RadioButton>(Box(page, "CandidateList"))
                    .Select(r => r.IsChecked == true).ToList(),
            };
        });

        Assert.Equal(1, outcome.Clicks);
        Assert.Equal("Beta Suit", outcome.Chosen);
        // 措辞在 Lang.*.xaml 里（单测按本程序的既有约定不装载语言，取词回落成键名），
        // 所以这里只断言"状态那一行确实换了"，不断言换成了哪句话
        Assert.NotEqual(outcome.Before, outcome.After);
        // 重画后只有第二行选中：互斥靠整体重建保证，不依赖 GroupName 在模板里的隐式行为
        Assert.Equal(new[] { false, true, false }, outcome.Checked);
    }

    [Fact]
    public void RightClickingARowMakesItTheWinnerAndPreviewsIt()
    {
        var saved = new List<IReadOnlyDictionary<string, string>>();
        var groups = new[] { Group(OutPath, ("Alpha Suit", "ModA", 0), ("Beta Suit", "ModB", 1)) };
        var request = Request(groups, dict => saved.Add(dict));

        var outcome = WithPage(request, page =>
        {
            var radios = Descendants<RadioButton>(Box(page, "CandidateList"));
            Assert.Equal(3, radios.Count);
            // 右键一行 = 把这一行设为赢家。事件从行内元素冒泡到挂了处理器的行容器；
            // 直接在 ListBoxItem 上.raise 是到不了的（处理器在它的子元素上）。
            var rowGrid = AncestorOf(radios[1], typeof(Grid))!;
            rowGrid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
            {
                RoutedEvent = FrameworkElement.MouseRightButtonUpEvent,
            });
            WpfHost.Pump();
            page.UpdateLayout();
            var title = Named<TextBlock>(page, "PreviewTitle");
            var status = Named<TextBlock>(page, "PreviewStatus");
            return new
            {
                Chosen = saved.Count == 0 ? null : saved[^1].Values.SingleOrDefault(),
                Title = title.Text,
                Status = status.Text,
            };
        });

        Assert.Equal("Beta Suit", outcome.Chosen);
        Assert.Equal("Beta Suit", outcome.Title); // 预览跟着选中的行走
        // 这个 set 没有可解析的源网格（Request 里 SourceNifOf 返回 null），预览要能说清原因，不能空着
        Assert.NotEqual("", outcome.Status);
    }

    [Fact]
    public void OnlyCrossModFilterHidesSameModGroups()
    {
        var cross = Group(OutPath, ("Alpha", "ModA", 0), ("Beta", "ModB", 1));
        var sameMod = Group(OutPath + "2", ("V1", "ModA", 0), ("V2", "ModA", 0), ("V3", "ModA", 0));

        var counts = WithPage(new[] { cross, sameMod }, page =>
        {
            var toggle = Named<CheckBox>(page, "OnlyCrossCheck");
            var before = toggle.IsChecked == true;
            var filtered = Box(page, "GroupList").Items.Count;
            toggle.IsChecked = false;
            page.UpdateLayout();
            return (before, filtered, all: Box(page, "GroupList").Items.Count);
        });

        // 默认只看跨模组：同模组内部的三个变体那一组被藏起来；取消勾选后两组都在
        Assert.True(counts.before);
        Assert.Equal(1, counts.filtered);
        Assert.Equal(2, counts.all);
    }

    [Fact]
    public void AutoPickAppliesToVisibleGroupsOnly()
    {
        var cross = Group(OutPath, ("Alpha", "ModA", 0), ("Beta", "ModB", 1));
        var sameMod = Group(OutPath + "2", ("V1", "ModA", 0), ("V2", "ModA", 0));
        var saved = new List<IReadOnlyDictionary<string, string>>();

        var picked = WithPage(Request(new[] { cross, sameMod }, dict => saved.Add(dict)), page =>
        {
            var button = Named<Button>(page, "AutoButton");
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return saved[^1].ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        });

        // "只看跨模组"开着 → 批量只作用于可见的那一组；屏幕外的同模组组不该被顺手改掉
        Assert.Equal(new[] { OutPath }, picked.Keys);
        Assert.Equal("Alpha", picked[OutPath]);
    }

    [Fact]
    public void GroupRowsShowTheOutputFileNameInsteadOfTheWholePath()
    {
        // 取词器换成"真会拼出一条路径"的版本：测试宿主里没装载语言，CoreStrings 会回落成键名，
        // TargetDisplay 里就一个分隔符都没有，这一条也就测不到拆分对不对了。
        // 整个程序集关掉了并行（见 AssemblyInfo），所以用完还原就够。
        var previous = CoreStrings.Localizer;
        CoreStrings.Localizer = (_, args) => $"{string.Join(@"\", args)}_0.nif";
        try
        {
            var groups = new[] { Group(OutPath, ("Alpha Suit", "ModA", 0), ("Beta Suit", "ModB", 1)) };

            var texts = WithPage(groups, page => TextsOf(Box(page, "GroupList")));

            // 列表里只留文件名：整条路径人人共用一个目录前缀（…\actors\character\character assets\clothes\），
            // 折成 2–5 行也区分不了任何两行，行高却涨到 62–96px。拆错时表现是"列表里又出现一整条路径"——
            // 编译期、色板门禁、布局探针（探针只看溢出，不看折了几行）全都不会红。
            Assert.Contains(texts, t => t.Contains("bikini_0.nif", StringComparison.Ordinal));
            Assert.DoesNotContain(texts, t => t.Contains(@"actors\character", StringComparison.Ordinal));
        }
        finally
        {
            CoreStrings.Localizer = previous;
        }
    }

    [Fact]
    public void EmptyStateAndBatchActionsFollowThisPagesOwnRequest()
    {
        // 空状态的可见性原先挂在 VM 的 CurrentConflict 上，而本页的渲染只依赖 ShowRequest 喂进来的那份请求：
        // 两者不同步时，满列表上会飘着一句"还没有可显示的冲突"（实机截图里就是这么出现的）。
        var outcome = WpfHost.WithWindow(() => new OutputConflictPage(), host =>
        {
            var page = (OutputConflictPage)host;
            WpfHost.Pump();
            page.UpdateLayout();
            var before = (
                Empty: Named<TextBlock>(page, "EmptyStateLabel").Visibility,
                Auto: Named<Button>(page, "AutoButton").IsEnabled,
                Export: Named<Button>(page, "ExportButton").IsEnabled);

            page.ShowRequest(Request(new[] { Group(OutPath, ("Alpha", "ModA", 0), ("Beta", "ModB", 1)) }));
            WpfHost.Pump();
            page.UpdateLayout();
            var filled = (
                Empty: Named<TextBlock>(page, "EmptyStateLabel").Visibility,
                Auto: Named<Button>(page, "AutoButton").IsEnabled,
                Export: Named<Button>(page, "ExportButton").IsEnabled);

            // 扫完发现一组冲突都没有：清单是空的、不是没送来，空状态与按钮置灰同样要成立
            page.ShowRequest(Request(Array.Empty<OutputConflictGroup>()));
            WpfHost.Pump();
            page.UpdateLayout();
            return (before, filled,
                EmptyNoGroups: Named<TextBlock>(page, "EmptyStateLabel").Visibility,
                ExportNoGroups: Named<Button>(page, "ExportButton").IsEnabled);
        });

        // 没有 DataContext（= 还没被喂过清单）：说清为什么三栏是空的，批量与导出也按不动
        Assert.Equal(Visibility.Visible, outcome.before.Empty);
        Assert.False(outcome.before.Auto);
        Assert.False(outcome.before.Export);
        Assert.Equal(Visibility.Collapsed, outcome.filled.Empty);
        Assert.True(outcome.filled.Auto);
        Assert.True(outcome.filled.Export);
        Assert.Equal(Visibility.Visible, outcome.EmptyNoGroups);
        Assert.False(outcome.ExportNoGroups);
    }

    [Fact]
    public void LeavingTheTabReleasesTheViewportCapture()
    {
        // 切走时不收掉鼠标捕获，它会漏到别的标签页上（模态窗口时代由 OnClosed 兜，现在窗口一直活着）。
        // 这里把页装进一个带标签条的窗口，切走再切回，验捕获已释放且页面重新可见（取景补全在
        // OnIsVisibleChanged 里，量不到尺寸时不会崩）。
        var request = Request(new[] { Group(OutPath, ("Alpha", "ModA", 0), ("Beta", "ModB", 1)) });

        var outcome = WpfHost.WithWindow(() => new TabControl
        {
            Items =
            {
                new TabItem { Content = new OutputConflictPage() },
                new TabItem { Content = new TextBlock { Text = "别的页" } },
            },
        }, host =>
        {
            var tabs = (TabControl)host;
            var page = (OutputConflictPage)((TabItem)tabs.Items[0]!).Content;
            var viewport = (FrameworkElement)page.FindName("ViewportHost")!;
            page.ShowRequest(request);
            WpfHost.Pump();
            page.UpdateLayout();

            tabs.SelectedIndex = 1;
            WpfHost.Pump();
            var capturedWhileHidden = viewport.IsMouseCaptured;

            tabs.SelectedIndex = 0;
            WpfHost.Pump();
            page.UpdateLayout();
            return (capturedWhileHidden, visible: page.IsVisible);
        });

        Assert.False(outcome.capturedWhileHidden);
        Assert.True(outcome.visible);
    }

    // ── 按用户分组分类（左栏两级列表 + 「只看某组」）──
    // 分组头的文案与计数由页面事先拼在 ConflictGroupKey 上（模板的 DataContext 是 CollectionViewGroup，
    // 拿不到页面里的行集合）。所以这里钉的是"分组头真的渲染出来了、顺序对、没有多余的桶"，
    // 计数规则本身由 Core 侧的 CategorizeByUserGroup 单测钉住——测试宿主不装载语言，数字取不出来。

    private static SliderGroup UserGroup(string name, params string[] members) => new(name, members);

    [Fact]
    public void TheLeftColumnIsHeadedByTheUsersOwnGroups()
    {
        var bikini = Group(OutPath, ("Bikini Red", "ModA", 0), ("Bikini Blue", "ModB", 1));
        var armor = Group(OutPath + "2", ("Armor Steel", "ModC", 0), ("Armor Iron", "ModD", 1));
        var request = Request([bikini, armor],
            userGroups: [UserGroup("泳装", "Bikini Red", "Bikini Blue"), UserGroup("护甲", "Armor Steel")]);

        var texts = WithPage(request, page => TextsOf(Box(page, "GroupList")));

        // 分组头必须真的渲染出来：模板写的是 {Binding Name.Label}，而 Name 是 object（装的是
        // ConflictGroupKey）——路径写错时 WPF 只静默留白，列表照样有行、Items.Count 照样对，
        // 只有实机截图才看得出"分组名没了"
        Assert.Contains("泳装", texts);
        Assert.Contains("护甲", texts);
    }

    [Fact]
    public void ConflictsTouchingNoGroupShowUpUnderTheUngroupedHeader()
    {
        var inGroup = Group(OutPath, ("Bikini Red", "ModA", 0), ("Bikini Blue", "ModB", 1));
        var loose = Group(OutPath + "2", ("Loose One", "ModC", 0), ("Loose Two", "ModD", 1));
        var request = Request([inGroup, loose], userGroups: [UserGroup("泳装", "Bikini Red")]);

        var texts = WithPage(request, page => TextsOf(Box(page, "GroupList")));

        Assert.Contains("泳装", texts);
        // 「未入组」那一桶不能少：否则与任何分组无关的冲突会从界面上凭空消失。
        // 测试宿主里没装载语言，取词回落成键名，所以按键名断言（与既有约定一致）
        Assert.Contains("L.Conflict_Ungrouped", texts);
    }

    [Fact]
    public void WithoutUserGroupsTheListStaysFlatAndTheGroupSelectorIsHidden()
    {
        var groups = new[]
        {
            Group(OutPath, ("Alpha", "ModA", 0), ("Beta", "ModB", 1)),
            Group(OutPath + "2", ("Gamma", "ModC", 0), ("Delta", "ModD", 1)),
        };

        var outcome = WithPage(Request(groups), page => (
            Items: Box(page, "GroupList").Items.Count,
            UngroupedHeaders: TextsOf(Box(page, "GroupList")).Count(t => t == "L.Conflict_Ungrouped"),
            Combo: Named<ComboBox>(page, "GroupFilterBox").Visibility));

        // 没建组时不该冒出「未入组」这一个头：它不带任何信息，只是把整个列表下移一行；
        // 选项只剩"全部分组"一颗时下拉也收起（那时它与不筛等价）
        Assert.Equal(2, outcome.Items);
        Assert.Equal(0, outcome.UngroupedHeaders);
        Assert.Equal(Visibility.Collapsed, outcome.Combo);
    }

    [Fact]
    public void TheGroupSelectorNarrowsTheListToOneGroup()
    {
        var bikini = Group(OutPath, ("Bikini Red", "ModA", 0), ("Bikini Blue", "ModB", 1));
        var armor = Group(OutPath + "2", ("Armor Steel", "ModC", 0), ("Armor Iron", "ModD", 1));
        var request = Request([bikini, armor],
            userGroups: [UserGroup("泳装", "Bikini Red"), UserGroup("护甲", "Armor Steel")]);

        var outcome = WithPage(request, page =>
        {
            var combo = Named<ComboBox>(page, "GroupFilterBox");
            var options = combo.Items.Count;
            var all = Box(page, "GroupList").Items.Count;

            combo.SelectedIndex = 1; // 0 = 全部分组，1 = 泳装，2 = 护甲（选项按分组顺序）
            WpfHost.Pump();
            page.UpdateLayout();
            var bathing = Box(page, "GroupList").Items.Count;
            var bathingHeaders = TextsOf(Box(page, "GroupList"));

            combo.SelectedIndex = 2; // 护甲
            WpfHost.Pump();
            page.UpdateLayout();
            var armorHeaders = TextsOf(Box(page, "GroupList"));

            return (options, all, bathing, bathingHeaders, armorHeaders);
        });

        // 只列真的有冲突的分组 + 一颗「全部分组」：三十个组的用户不该在选项里翻三十行
        Assert.Equal(3, outcome.options);
        Assert.Equal(2, outcome.all);
        Assert.Equal(1, outcome.bathing);
        // 筛到某组后左栏只剩那个分组头 —— 上一轮的头必须消失，否则看着像筛错了
        Assert.Contains("泳装", outcome.bathingHeaders);
        Assert.DoesNotContain("护甲", outcome.bathingHeaders);
        Assert.Contains("护甲", outcome.armorHeaders);
        Assert.DoesNotContain("泳装", outcome.armorHeaders);
    }

    [Fact]
    public void AConflictSharedByTwoGroupsIsListedUnderBothButCountedOnce()
    {
        // 两个组各有一件衣服在争同一个文件：赢家全局只有一个，输掉的那个组的成员就建不出来，
        // 所以两个组都得看到它 —— 行因此重复，但"对当前列出的 N 组"必须按冲突去重
        var shared = Group(OutPath, ("Bikini Red", "ModA", 0), ("Armor Steel", "ModB", 1));
        var userGroups = new[] { UserGroup("泳装", "Bikini Red"), UserGroup("护甲", "Armor Steel") };
        var saved = new List<IReadOnlyDictionary<string, string>>();

        var outcome = WithPage(Request([shared], dict => saved.Add(dict), userGroups), page =>
        {
            var headers = TextsOf(Box(page, "GroupList"));
            var rows = Box(page, "GroupList").Items.Count;
            Named<Button>(page, "AutoButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return (rows, headers, Picked: saved[^1]);
        });

        Assert.Equal(2, outcome.rows); // 一条冲突，两个分组下各一行
        Assert.Contains("泳装", outcome.headers);
        Assert.Contains("护甲", outcome.headers);
        // 批量动作按冲突作用：AutoPick 对同一个输出路径重复应用虽然幂等，
        // 但作用范围若按行算，"全部按模组优先级"就会对同一条冲突写两遍
        Assert.Single(outcome.Picked);
    }

    [Fact]
    public void ComingBackToTheTabReclassifiesWhenTheGroupsChanged()
    {
        // 分组是在「分组生成」页改的，而本页只在 CurrentConflict 变化时重建：
        // 切回来时若不比一次分组指纹，看到的就是上一轮的分组头（下拉选项与「已在组内」徽标同理）
        var groups = new List<SliderGroup> { new("泳装", ["Bikini Red"]) };
        var request = Request([Group(OutPath, ("Bikini Red", "ModA", 0), ("Armor Steel", "ModB", 1))],
            userGroups: groups);

        var outcome = WpfHost.WithWindow(() => new TabControl
        {
            Items =
            {
                new TabItem { Content = new OutputConflictPage() },
                new TabItem { Content = new TextBlock { Text = "别的页" } },
            },
        }, host =>
        {
            var tabs = (TabControl)host;
            var page = (OutputConflictPage)((TabItem)tabs.Items[0]!).Content;
            page.ShowRequest(request);
            WpfHost.Pump();
            page.UpdateLayout();
            var before = TextsOf(Box(page, "GroupList")).Contains("护甲");

            tabs.SelectedIndex = 1;
            WpfHost.Pump();
            groups.Add(new SliderGroup("护甲", ["Armor Steel"])); // 在另一页建组并归组
            tabs.SelectedIndex = 0;
            WpfHost.Pump();
            page.UpdateLayout();
            return (before, after: TextsOf(Box(page, "GroupList")).Contains("护甲"));
        });

        Assert.False(outcome.before);
        Assert.True(outcome.after);
    }

    // ── 分组头的展开/折叠（2026-09-23）──
    //
    // 行标题在测试宿主里全都是同一个字符串：它来自 OutputConflictGroup.TargetDisplay，
    // 而 Core 没装取词器时 TargetDisplay 返回的是键名本身，于是每一行的标题都是
    // "L.Core_OutputConf_Weighted"。所以这里按"有几行"断言，不按"哪一行"断言。

    /// <summary>按分组名找到那个可折叠的分组头。分组头是 GroupItem 模板里的 ToggleButton，
    /// 没有 x:Name（模板内的元素不在页面的名称域里），只能按它内容里的组名认。</summary>
    private static ToggleButton GroupHeader(FrameworkElement page, string label) =>
        Descendants<ToggleButton>(Box(page, "GroupList")).First(t => TextsOf(t).Contains(label));

    /// <summary>左栏里**真正看得见**的冲突行数。
    /// <para>
    /// 不能只数文本：折叠用的是 <c>Visibility.Collapsed</c>，而已经生成过的模板项不会因此从
    /// 视觉树里消失——它只是不参与布局与渲染，于是数文本会把收起的行照样算进来。
    /// 反过来，首屏就处于收起态时那些模板项压根没生成过，数文本又恰好数不到，
    /// 看着像"折叠生效了"——两个方向都会骗过用例，所以一律以 <c>IsVisible</c> 为准。
    /// </para></summary>
    private static int RowCount(FrameworkElement page) =>
        Descendants<ListBoxItem>(Box(page, "GroupList")).Count(i => i.IsVisible);

    // ── 分组头的右键菜单（整列展开 / 折叠，2026-09-23）──
    //
    // 菜单是资源里的**共享实例**（ConflictGroupMenu），不在逻辑树上，锚点只能靠 PlacementTarget
    // 找回来；而三个动作又都是 Click 处理器（不是命令）。这两处都容易静默失效：接不上锚点时
    // 「折叠其他」什么都不做，标题键写错时菜单项是空的——编译期与布局探针都不会红。

    /// <summary>模拟"右键某个分组头 → 点菜单里的某一项"。
    /// PlacementTarget 由 WPF 在弹出前设成被右键的那个元素，这里手工设成同一个东西；
    /// 菜单项直接抛 Click——真实点击走的是同一条路由事件（MenuItem.Click 是冒泡事件，
    /// XAML 的 <c>Click=</c> 就挂在这上面）。</summary>
    private static void InvokeGroupMenu(FrameworkElement page, string groupLabel, string itemLabel)
    {
        var header = GroupHeader(page, groupLabel);
        var menu = header.ContextMenu
                   ?? throw new InvalidOperationException($"分组头「{groupLabel}」上没有右键菜单");
        menu.PlacementTarget = header;
        var item = menu.Items.OfType<MenuItem>().First(i => (i.Header as string) == itemLabel);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        WpfHost.Pump();
        page.UpdateLayout();
    }

    [Fact]
    public void TheGroupHeaderMenuCollapsesAndExpandsEveryGroup()
    {
        var bikini = Group(OutPath, ("Bikini Red", "ModA", 0), ("Bikini Blue", "ModB", 1));
        var armor = Group(OutPath + "2", ("Armor Steel", "ModC", 0), ("Armor Iron", "ModD", 1));
        var collapsed = new HashSet<string>(StringComparer.Ordinal);
        var request = Request([bikini, armor],
            userGroups: [UserGroup("泳装", "Bikini Red"), UserGroup("护甲", "Armor Steel")],
            collapsed: collapsed);

        var outcome = WithPage(request, page =>
        {
            var items = GroupHeader(page, "泳装").ContextMenu?.Items.OfType<MenuItem>()
                .Select(i => i.Header as string).ToList() ?? [];

            InvokeGroupMenu(page, "泳装", "全部折叠");
            var folded = (Rows: RowCount(page),
                Checked: new[] { "泳装", "护甲" }.Select(g => GroupHeader(page, g).IsChecked).ToList(),
                Saved: collapsed.Order(StringComparer.Ordinal).ToList());

            InvokeGroupMenu(page, "泳装", "全部展开");
            return (Items: items, Folded: folded, Expanded: RowCount(page),
                AfterExpand: collapsed.ToList());
        });

        Assert.Equal(["全部展开", "全部折叠", "折叠其他"], outcome.Items);
        // 两个分组各一行，全收起后一行都不该剩下（头本身还在，否则没法再展开）
        Assert.Equal(0, outcome.Folded.Rows);
        Assert.Equal(new bool?[] { false, false }, outcome.Folded.Checked);
        // 批量收起与逐个点折叠条落盘的是同一样东西（设置里存的是"收起的那些"）
        Assert.Equal(["护甲", "泳装"], outcome.Folded.Saved);
        Assert.Equal(2, outcome.Expanded);
        // 展开要把它从设置里摘掉，否则用户全展开一遍之后还留着一串陈旧的名字
        Assert.Empty(outcome.AfterExpand);
    }

    [Fact]
    public void CollapseOthersKeepsTheRightClickedGroupOpen()
    {
        var bikini = Group(OutPath, ("Bikini Red", "ModA", 0), ("Bikini Blue", "ModB", 1));
        var armor = Group(OutPath + "2", ("Armor Steel", "ModC", 0), ("Armor Iron", "ModD", 1));
        var request = Request([bikini, armor],
            userGroups: [UserGroup("泳装", "Bikini Red"), UserGroup("护甲", "Armor Steel")]);

        var outcome = WithPage(request, page =>
        {
            InvokeGroupMenu(page, "护甲", "折叠其他");
            return (Bikini: GroupHeader(page, "泳装").IsChecked,
                Armor: GroupHeader(page, "护甲").IsChecked,
                Rows: RowCount(page));
        });

        // 锚点取的是"被右键的那一组"：连它一起折了就看不见自己点的是谁
        Assert.False(outcome.Bikini);
        Assert.True(outcome.Armor);
        Assert.Equal(1, outcome.Rows);
    }

    [Fact]
    public void CollapsingAGroupHidesItsRowsAndExpandingBringsThemBack()
    {
        var bikini = Group(OutPath, ("Bikini Red", "ModA", 0), ("Bikini Blue", "ModB", 1));
        var armor = Group(OutPath + "2", ("Armor Steel", "ModC", 0), ("Armor Iron", "ModD", 1));
        var request = Request([bikini, armor],
            userGroups: [UserGroup("泳装", "Bikini Red"), UserGroup("护甲", "Armor Steel")]);

        var outcome = WithPage(request, page =>
        {
            var header = GroupHeader(page, "泳装");
            var before = RowCount(page);

            header.IsChecked = false; // = 点一下分组头
            WpfHost.Pump();
            page.UpdateLayout();
            var collapsed = RowCount(page);
            var headerStillThere = TextsOf(Box(page, "GroupList")).Contains("泳装");

            header.IsChecked = true;
            WpfHost.Pump();
            page.UpdateLayout();
            return (before, collapsed, headerStillThere, expanded: RowCount(page));
        });

        Assert.Equal(2, outcome.before);        // 两个组各一行
        Assert.Equal(1, outcome.collapsed);     // 收起「泳装」，只剩「护甲」那行
        Assert.True(outcome.headerStillThere);  // 头本身不能跟着消失，否则没法再展开
        Assert.Equal(2, outcome.expanded);
    }

    [Fact]
    public void AGroupCollapsedInTheSettingsStartsCollapsed()
    {
        var bikini = Group(OutPath, ("Bikini Red", "ModA", 0), ("Bikini Blue", "ModB", 1));
        var armor = Group(OutPath + "2", ("Armor Steel", "ModC", 0), ("Armor Iron", "ModD", 1));
        var request = Request([bikini, armor],
            userGroups: [UserGroup("泳装", "Bikini Red"), UserGroup("护甲", "Armor Steel")],
            collapsed: ["泳装"]);

        var (rows, headerChecked) = WithPage(request, page =>
            (RowCount(page), GroupHeader(page, "泳装").IsChecked));

        // 设置里记着"泳装是收起的"，一进来就该是收起的——这条就是"跨重启记住"的全部意义
        Assert.Equal(1, rows);
        Assert.False(headerChecked);
    }

    [Fact]
    public void TogglingAGroupHeaderWritesTheCollapsedStateToTheSettings()
    {
        var groups = new[] { Group(OutPath, ("Bikini Red", "ModA", 0), ("Bikini Blue", "ModB", 1)) };
        var collapsed = new HashSet<string>(StringComparer.Ordinal);
        var request = Request(groups, userGroups: [UserGroup("泳装", "Bikini Red")], collapsed: collapsed);

        var outcome = WithPage(request, page =>
        {
            var header = GroupHeader(page, "泳装");

            header.IsChecked = false;
            WpfHost.Pump();
            var afterCollapse = collapsed.ToList();

            header.IsChecked = true;
            WpfHost.Pump();
            return (afterCollapse, afterExpand: collapsed.ToList());
        });

        // 收起要落盘（设置里存的是"收起的那些"）；展开要把它摘掉——
        // 否则用户把组全展开一遍之后，设置里还留着一串陈旧的名字
        Assert.Equal(["泳装"], outcome.afterCollapse);
        Assert.Empty(outcome.afterExpand);
    }

    [Fact]
    public void FilteringToOneGroupExpandsItWithoutTouchingTheSettings()
    {
        // 「泳装」名下两处冲突（好让"展开"与"收起"的行数不同，否则这条用例两个数都是 1，
        // 收起与展开根本分不出来）、「护甲」名下一处
        var bikini = Group(OutPath, ("Bikini Red", "ModA", 0), ("Bikini Blue", "ModB", 1));
        var bikini2 = Group(OutPath + "2", ("Bikini Green", "ModE", 0), ("Loose One", "ModF", 1));
        var armor = Group(OutPath + "3", ("Armor Steel", "ModC", 0), ("Armor Iron", "ModD", 1));
        var collapsed = new HashSet<string>(StringComparer.Ordinal) { "泳装" };
        var request = Request([bikini, bikini2, armor],
            userGroups: [UserGroup("泳装", "Bikini Red", "Bikini Green"), UserGroup("护甲", "Armor Steel")],
            collapsed: collapsed);

        var outcome = WithPage(request, page =>
        {
            var before = RowCount(page);
            Named<ComboBox>(page, "GroupFilterBox").SelectedIndex = 1; // 0 = 全部分组，1 = 泳装
            WpfHost.Pump();
            page.UpdateLayout();
            return (before, filtered: RowCount(page), collapsed: collapsed.ToList());
        });

        Assert.Equal(1, outcome.before);   // 一进来：泳装收起 → 只剩护甲那行
        // 用户刚点名"只看泳装"，却因为上次收起过而只看到一条光杆标题，会以为这一组没冲突
        Assert.Equal(2, outcome.filtered);
        // 而这一次查看不该把他原先的收起意图抹掉
        Assert.Equal(["泳装"], outcome.collapsed);
    }

    [Fact]
    public void CollapsingAGroupDoesNotShrinkWhatTheBulkActionsActOn()
    {
        var bikini = Group(OutPath, ("Bikini Red", "ModA", 0), ("Bikini Blue", "ModB", 1));
        var armor = Group(OutPath + "2", ("Armor Steel", "ModC", 0), ("Armor Iron", "ModD", 1));
        var saved = new List<IReadOnlyDictionary<string, string>>();
        var request = Request([bikini, armor], save: d => saved.Add(d),
            userGroups: [UserGroup("泳装", "Bikini Red"), UserGroup("护甲", "Armor Steel")]);

        var outcome = WithPage(request, page =>
        {
            GroupHeader(page, "泳装").IsChecked = false;
            WpfHost.Pump();
            page.UpdateLayout();
            var rows = Box(page, "GroupList").Items.Count;
            Named<Button>(page, "AutoButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return (rows, Picked: saved[^1]);
        });

        // 收起是"我先不看它"，不是"我不管它"：批量动作仍作用于整份清单。
        // Items.Count（视图里的行数）也不跟着变——它变了就说明折叠被当成了过滤，
        // 那样"自动选择"会静默漏掉收起的那些组。
        Assert.Equal(2, outcome.rows);
        Assert.Equal(2, outcome.Picked.Count);
    }
}
