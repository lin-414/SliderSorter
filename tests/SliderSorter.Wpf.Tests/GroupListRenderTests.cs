using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SliderSorter.Core;
using SliderSorter.Wpf.ViewModels;
using SliderSorter.Wpf.Views.Pages;
using Xunit;

namespace SliderSorter.Wpf.Tests;

/// <summary>
/// 「分组生成」页右侧的组列表真的把组画出来了。
/// <para>
/// 钉住的是一个真实故障（用户报障：重新进入程序后分组整栏空白，双击空白却能弹出 A-3BA）：
/// ListBox 绑的是 <see cref="MainViewModel.GroupsView"/> —— 构造期一次性建立的过滤投影，
/// 且该属性**没有变更通知**。刷新时若把 <c>Groups</c> 换成新集合，投影就永远对着旧实例，
/// 列表整栏空白，而 <c>Store</c> 里明明有组。
/// </para>
/// <para>
/// 纯逻辑单测看不出来（<c>Groups</c> 本身是对的、数量也对），必须渲染出来数行数——
/// 与"绑定路径写错只静默留白"是同一类只能靠渲染发现的问题。
/// </para>
/// </summary>
[Collection(WpfStaCollection.Name)]
public class GroupListRenderTests
{
    private static readonly string[] Names = ["A-3BA", "A-CBBE", "A-UBE"];

    /// <summary>顺序反过来的一份，用来验证"列表顺序跟着 Store 走"。
    /// 刻意写成字面量而不是 <c>Names.Reverse()</c>：后者会绑到 <c>MemoryExtensions.Reverse(Span&lt;T&gt;)</c>
    /// （返回 void）而不是 LINQ 的 <c>Enumerable.Reverse</c>——数组有到 Span 的隐式转换，这是一个真实的坑。</summary>
    private static readonly string[] Reversed = ["A-UBE", "A-CBBE", "A-3BA"];

    /// <summary>刷新（= 重新扫描载入上次生成的组）之后，列表里必须真有几行、每行是哪个组。</summary>
    [Fact]
    public void GroupRowsSurviveListRefresh()
    {
        using var scope = IsolatedUserState.Enter();

        var rows = WpfHost.WithWindow(BuildPage, host =>
        {
            var page = (GroupGenerationPage)host;
            var list = (ListBox)page.FindName("GroupList")!;
            // 行容器由模板生成，Pump 之后才拿得到
            WpfHost.Pump();
            list.UpdateLayout();
            return Descendants<ListBoxItem>(list).Select(TextsOf).ToList();
        });

        // 每行 = 组名 + 计数徽标（同一行的第二个 TextBlock，见 GroupGenerationPage.xaml 的模板）
        Assert.Equal(Names, rows.Select(r => r[0]).ToList());
        Assert.Equal(["1", "1", "1"], rows.Select(r => r[1]).ToList());
    }

    /// <summary><c>Groups</c> 是同一个实例、只就地更新——<c>GroupsView</c> 必须一直跟着它。
    /// 这是渲染用例的最小化版本：谁要是把刷新改回"换一个新集合"，这里立刻红。</summary>
    [Fact]
    public void GroupsViewTracksGroupsInPlace()
    {
        using var scope = IsolatedUserState.Enter();

        var result = WpfHost.WithWindow(() => new Grid(), _ =>
        {
            var vm = new MainViewModel();
            vm.Store.Load(Groups(Names));
            vm.RefreshGroupsList();
            var first = (Groups: vm.Groups.Count, View: vm.GroupsView.Cast<object>().Count());

            // 再刷新一次（对应"加入组/撤销"这类原地重排）：视图仍要跟上，且顺序跟着 Store 走
            vm.Store.Load(Groups(Reversed));
            vm.RefreshGroupsList();
            return (First: first,
                Second: (Groups: vm.Groups.Count, View: vm.GroupsView.Cast<object>().Count()),
                Order: vm.Groups.Select(g => g.Name).ToList());
        });

        Assert.Equal((3, 3), result.First);
        Assert.Equal((3, 3), result.Second);
        Assert.Equal(Reversed, result.Order);
    }

    /// <summary>原地重建列表不该把选中项弄丢：<c>Store.Current</c> 是哪个组，
    /// 刷新后列表就要高亮哪一行（否则 GroupInfo 说着"当前组：X"、列表却一行没选中）。</summary>
    [Fact]
    public void SelectionFollowsCurrentGroupAcrossRefresh()
    {
        using var scope = IsolatedUserState.Enter();

        var result = WpfHost.WithWindow(() =>
        {
            var vm = new MainViewModel();
            vm.Store.Load(Groups(Names));
            vm.RefreshGroupsList();
            vm.Store.SelectGroup(Names[2]);   // 用户点了第三个组
            vm.RefreshGroupsList();           // 随后来一次原地刷新
            return new GroupGenerationPage { DataContext = vm };
        }, host =>
        {
            var page = (GroupGenerationPage)host;
            var vm = (MainViewModel)page.DataContext;
            var list = (ListBox)page.FindName("GroupList")!;
            WpfHost.Pump();
            list.UpdateLayout();
            return (VmRow: vm.SelectedGroup?.Name, Current: vm.Store.Current?.Name,
                Selected: (list.SelectedItem as SliderSorter.Wpf.ViewModels.GroupItem)?.Name);
        });

        Assert.Equal(Names[2], result.VmRow);
        Assert.Equal(Names[2], result.Current);
        Assert.Equal(Names[2], result.Selected);
    }

    /// <summary>过滤组列表不许把「当前组」换人，点某一行选中的也必须是看得见的那一行。
    /// <para>
    /// 钉住的是真实故障：ListBox 绑的是过滤投影 <see cref="MainViewModel.GroupsView"/>，
    /// 而旧写法把 <c>SelectedIndex</c>（视图内下标）当成 <c>Store.Groups</c> 的下标用。
    /// 三组 A-3BA/A-CBBE/A-UBE，选中第三行后在过滤框打 <c>UBE</c>，视图只剩一行、下标变成 0，
    /// 于是 <c>Store.Current</c> 悄悄跳到 A-3BA —— 之后的重命名/删除/「添加勾选」全落在
    /// 用户没点过的那个组上，而用户以为自己在编辑 A-UBE。
    /// </para>
    /// </summary>
    [Fact]
    public void FilteredGroupListKeepsAndSelectsTheVisibleRow()
    {
        using var scope = IsolatedUserState.Enter();

        var result = WpfHost.WithWindow(() =>
        {
            var vm = new MainViewModel();
            vm.Store.Load(Groups(Names));
            vm.RefreshGroupsList();
            vm.Store.SelectGroup(Names[2]); // 用户点中第三行
            vm.RefreshGroupsList();
            return new GroupGenerationPage { DataContext = vm };
        }, host =>
        {
            var page = (GroupGenerationPage)host;
            var vm = (MainViewModel)page.DataContext;
            var list = (ListBox)page.FindName("GroupList")!;
            WpfHost.Pump();
            list.UpdateLayout();

            vm.GroupFilterText = "UBE";     // 只打字，不点任何一行：三组里只有 A-UBE 留着
            WpfHost.Pump();
            list.UpdateLayout();
            var visible = list.Items.Cast<object>().Cast<SliderSorter.Wpf.ViewModels.GroupItem>()
                .Select(g => g.Name).ToList();
            var currentAfterTyping = vm.Store.Current?.Name;

            // 在过滤结果里点那一行（视图内下标 0）
            list.SelectedItem = list.Items[0];
            WpfHost.Pump();
            return (Visible: visible, AfterTyping: currentAfterTyping,
                Chosen: vm.Store.Current?.Name, VmRow: vm.SelectedGroup?.Name);
        });

        Assert.Equal([Names[2]], result.Visible);
        Assert.Equal(Names[2], result.AfterTyping);  // 打字不该换当前组
        Assert.Equal(Names[2], result.Chosen);       // 点可见那一行选中的就是它
        Assert.Equal(Names[2], result.VmRow);
    }

    private static List<SliderGroup> Groups(IEnumerable<string> names) =>
        names.Select(n => new SliderGroup(n, [n + "-member"])).ToList();

    /// <summary>离屏页面 + 真 VM。VM 必须建在 UI 线程上：<c>GroupsView</c> 是投影，
    /// 它认下的是创建它的那个 Dispatcher，跨线程绑定会直接抛。</summary>
    private static GroupGenerationPage BuildPage()
    {
        var vm = new MainViewModel();
        vm.Store.Load(Groups(Names));
        vm.RefreshGroupsList();
        return new GroupGenerationPage { DataContext = vm };
    }

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
}
