using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BSGroupGenerator.Core;
using BSGroupGenerator.Wpf.ViewModels;
using BSGroupGenerator.Wpf.Views.Pages;
using Xunit;

namespace BSGroupGenerator.Wpf.Tests;

/// <summary>
/// 「输出冲突与选择」页的行渲染与勾选回写。
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
        GenWeights = true,
        Candidates = candidates.OrderBy(c => c.Layer)
            .Select(c => new ConflictCandidate(c.Name, c.Owner, c.Name + ".xml", c.Layer, true))
            .ToList(),
    };

    private static ConflictRequest Request(IReadOnlyList<OutputConflictGroup> groups,
        Action<IReadOnlyDictionary<string, string>>? save = null) => new()
    {
        Groups = groups,
        Choices = new Dictionary<string, string>(StringComparer.Ordinal),
        IsGrouped = _ => false,
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
}
