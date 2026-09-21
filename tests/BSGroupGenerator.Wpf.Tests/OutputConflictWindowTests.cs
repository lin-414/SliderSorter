using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BSGroupGenerator.Core;
using BSGroupGenerator.Wpf.ViewModels;
using BSGroupGenerator.Wpf.Views;
using Xunit;

namespace BSGroupGenerator.Wpf.Tests;

/// <summary>
/// 「输出冲突」窗口的行渲染与勾选回写。
/// <para>
/// 这里钉住的是**模板绑定真的取到了值**：绑定路径写错时 WPF 只静默渲染成空文本——布局探针不喂数据上下文
/// 看不到，纯逻辑单测不看视觉树也漏过。实机截图里"候选服装名整列空白"就是这么来的。
/// </para>
/// 挂 <see cref="WpfStaCollection"/> 串行：窗口只能在 STA 线程构造，而 Application 是进程级单例。
/// </summary>
[Collection(WpfStaCollection.Name)]
public class OutputConflictWindowTests
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
    };

    /// <summary>整类共用一条常驻 STA 线程。
    ///
    /// 每个用例各起一条线程建窗口是不行的：WPF 的 <c>Application.Current</c> 是整个 AppDomain 共享的，
    /// 第一个用例的线程退出后，后来的用例拿到的是一份"宿主线程已经不存在"的 Application，
    /// 窗口虽然能 Show，模板项与绑定却不一定补得上——表现就是同一句断言单跑通过、连跑随机空。
    /// 线程常驻 + 全部经 Dispatcher 投递，才是确定的。</summary>
    private static readonly Thread UiThread = new(RunUi);
    private static readonly DispatcherDispatcherHolder Holder = new();

    private sealed class DispatcherDispatcherHolder
    {
        public Dispatcher? Dispatcher;
    }

    private static void RunUi()
    {
        var app = Application.Current ?? new Application();
        AddDictionary(app, "Themes/Controls.xaml");
        AddDictionary(app, "Strings/Lang.zh.xaml");
        Holder.Dispatcher = Dispatcher.CurrentDispatcher;
        Dispatcher.Run();
    }

    /// <summary>在 STA 线程上建窗口并真实排版一遍（模板项只有排版后才会生成，绑定才会求值）。</summary>
    private static T WithWindow<T>(ConflictRequest request, Func<OutputConflictWindow, T> inspect)
    {
        if (Holder.Dispatcher is null)
        {
            UiThread.SetApartmentState(ApartmentState.STA);
            UiThread.IsBackground = true;
            UiThread.Start();
            SpinWait.SpinUntil(() => Holder.Dispatcher is not null, TimeSpan.FromSeconds(10));
        }
        return Holder.Dispatcher!.Invoke(() =>
        {
            // 必须真的 Show()：模板项只有在窗口连上视觉树并排版后才会生成，
            // 而"绑定路径写错"只有在这一步才会暴露成空文本（不 Show 时连 ListBox 都找不到）。
            // 挪到屏幕外 + 全透明，测试期间不会有任何东西闪出来。
            var window = new OutputConflictWindow(request)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowActivated = false,
                Opacity = 0,
            };
            window.Show();
            // Show + UpdateLayout 之后还要推一次消息队列：绑定的首次挂接排在 DataBind 优先级上，
            // 模板项的生成排在更低的 ItemContainerGenerator/ContextIdle 上——不泵就会拿到
            // "列表有 3 项、里面一个容器都没有"的假象。
            window.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            try
            {
                return inspect(window);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static T WithWindow<T>(IReadOnlyList<OutputConflictGroup> groups, Func<OutputConflictWindow, T> inspect) =>
        WithWindow(Request(groups), inspect);

    /// <summary>用 pack URI 而不是文件路径：文件路径加载时 XAML 里的 <c>clr-namespace</c> 前缀
    /// 会拿调用方程序集（测试程序集）去解析，Controls.xaml 里的 <c>Setter Property="ui:Watermark.Text"</c>
    /// 当场解析失败。</summary>
    private static void AddDictionary(Application app, string relative)
    {
        var uri = new Uri($"pack://application:,,,/BSGroupGenerator;component/{relative}");
        if (app.Resources.MergedDictionaries.Any(d => d.Source == uri))
            return;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
    }

    /// <summary>x:Name 生成的字段是 internal，跨程序集取不到；走 FindName（窗口是 XAML 名称域的根）。</summary>
    private static T Named<T>(Window window, string name) where T : class =>
        Assert.IsType<T>(window.FindName(name));

    private static ListBox Box(Window window, string name) => Named<ListBox>(window, name);

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

        var rows = WithWindow(groups, window =>
            Descendants<ListBoxItem>(Box(window, "CandidateList")).Select(TextsOf).ToList());

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

        var outcome = WithWindow(Request(groups, dict => saved.Add(dict)), window =>
        {
            var radios = Descendants<RadioButton>(Box(window, "CandidateList"));
            Assert.Equal(3, radios.Count); // 两个候选 + "不指定"
            Assert.True(radios[2].IsChecked); // 没选过时默认落在"不指定"上
            var before = string.Join("|", TextsOf(Box(window, "GroupList")));

            radios[1].IsChecked = true;
            radios[1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.ContextIdle);
            window.UpdateLayout();

            return new
            {
                Clicks = saved.Count,
                Chosen = saved.Count == 0 ? null : saved[^1].Values.SingleOrDefault(),
                After = string.Join("|", TextsOf(Box(window, "GroupList"))),
                Before = before,
                Checked = Descendants<RadioButton>(Box(window, "CandidateList"))
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

        var outcome = WithWindow(request, window =>
        {
            var radios = Descendants<RadioButton>(Box(window, "CandidateList"));
            Assert.Equal(3, radios.Count);
            // 右键一行 = 把这一行设为赢家。事件从行内元素冒泡到挂了处理器的行容器；
            // 直接在 ListBoxItem 上.raise 是到不了的（处理器在它的子元素上）。
            var rowGrid = AncestorOf(radios[1], typeof(Grid))!;
            rowGrid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
            {
                RoutedEvent = FrameworkElement.MouseRightButtonUpEvent,
            });
            window.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            var title = (TextBlock)window.FindName("PreviewTitle")!;
            var status = (TextBlock)window.FindName("PreviewStatus")!;
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

        var counts = WithWindow(new[] { cross, sameMod }, window =>
        {
            var toggle = Named<CheckBox>(window, "OnlyCrossCheck");
            var before = toggle.IsChecked == true;
            var filtered = Box(window, "GroupList").Items.Count;
            toggle.IsChecked = false;
            window.UpdateLayout();
            return (before, filtered, all: Box(window, "GroupList").Items.Count);
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

        var picked = WithWindow(Request(new[] { cross, sameMod }, dict => saved.Add(dict)), window =>
        {
            var button = Named<Button>(window, "AutoButton");
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return saved[^1].ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        });

        // "只看跨模组"开着 → 批量只作用于可见的那一组；屏幕外的同模组组不该被顺手改掉
        Assert.Equal(new[] { OutPath }, picked.Keys);
        Assert.Equal("Alpha", picked[OutPath]);
    }
}
