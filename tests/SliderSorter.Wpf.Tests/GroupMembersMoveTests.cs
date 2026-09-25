using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SliderSorter.Core;
using SliderSorter.Wpf.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace SliderSorter.Wpf.Tests;

/// <summary>成员窗口「移动到…」的**运行时**接线：按钮弹出的目标菜单必须列全其他组、排除当前组，
/// 一个组都没有时要给置灰说明而不是空菜单。移动本身的数据语义在 <c>GroupStore.MoveMembers</c>
/// 的纯逻辑用例里（SliderSorter.Tests），这里只盯菜单这层——它是用户唯一能摸到的入口，
/// 列错组/漏了组的数组过滤写错时，编译期毫无提示。</summary>
[Collection(WpfStaCollection.Name)]
public class GroupMembersMoveTests(ITestOutputHelper output)
{
    [Fact]
    public void MoveMenu_ListsOtherGroups_ExcludingCurrent_AndOpensFromTheButton()
    {
        Exception? captured = null;
        var evidence = new List<string>();

        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                WpfHost.AddAppDictionaries(app);
                WpfHost.AddDictionary(app, "Strings/Lang.zh.xaml");

                var store = new GroupStore();
                store.Load(new List<SliderGroup>
                {
                    new("A-Decorations", new[] { "X", "Y" }),
                    new("B-Armor", new[] { "Z" }),
                    new("C-Weapons"),
                });

                var window = NewMembersWindow(store);
                window.Show();
                window.UpdateLayout();

                // 勾上 X：按钮点击路径里先收勾选再弹菜单，一个都没勾会弹提示框挡住测试。
                // 行是 VM 不是可视元素，从 ItemsSource 顺着 Children 找。
                var tree = (TreeView)typeof(Views.GroupMembersWindow)
                    .GetField("Tree", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(window)!;
                var outfit = tree.ItemsSource!.Cast<NodeVM>()
                    .SelectMany(n => n.WalkSelfAndDescendants())
                    .OfType<OutfitNodeVM>()
                    .First(n => n.OutfitName == "X");
                outfit.IsChecked = true;

                // 点真的按钮（而不是直接调 BuildMoveMenu）：x:Name 锚点、Placement、IsOpen 这一串
                // 只有真点才走得到。菜单挂到按钮上朝上开——按钮贴窗口底边，方向写反会被裁掉。
                var moveButton = (Button)typeof(Views.GroupMembersWindow)
                    .GetField("MoveButton", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(window)!;
                moveButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump(window);

                Assert.True(moveButton.ContextMenu is null); // 菜单是临时 new 的，不挂按钮资源上
                evidence.Add($"[窗口] 标题={window.Title}");

                var menu = (ContextMenu)typeof(Views.GroupMembersWindow)
                    .GetField("_moveMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(window)!;
                Assert.NotNull(menu);
                Assert.True(menu.IsOpen, "点「移动到…」后菜单没有开起来");
                var items = menu!.Items.OfType<MenuItem>().ToList();
                evidence.Add($"[菜单] {items.Count} 项：{string.Join("、", items.Select(i => (i.Header as TextBlock)?.Text))}");
                Assert.Equal(2, items.Count);
                Assert.Equal(["B-Armor", "C-Weapons"],
                    items.Select(i => Assert.IsType<TextBlock>(i.Header).Text)); // 当前组 A 不出现，顺序随 Store

                menu.IsOpen = false;

                // 目标清单是弹菜单时从 Store 现查的：窗口开着时组表加了新组，下一次点击要能看到
                store.NewGroup("D-Later");
                var rebuilt = (ContextMenu)typeof(Views.GroupMembersWindow)
                    .GetMethod("BuildMoveMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [new List<string> { "X" }])!;
                var headers = rebuilt.Items.OfType<MenuItem>()
                    .Select(i => Assert.IsType<TextBlock>(i.Header).Text).ToList();
                evidence.Add($"[重建] {headers.Count} 项：{string.Join("、", headers)}");
                Assert.Equal(["B-Armor", "C-Weapons", "D-Later"], headers);

                window.Close();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        foreach (var line in evidence)
            output.WriteLine(line);
        Assert.Null(captured);
    }

    /// <summary>一个其他组都没有时：给一条置灰的说明（用户至少知道按钮没坏），而不是空菜单。</summary>
    [Fact]
    public void MoveMenu_ShowsDisabledHint_WhenNoOtherGroupsExist()
    {
        Exception? captured = null;
        var evidence = new List<string>();

        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                WpfHost.AddAppDictionaries(app);
                WpfHost.AddDictionary(app, "Strings/Lang.zh.xaml");

                var store = new GroupStore();
                store.Load(new List<SliderGroup> { new("唯一组", new[] { "X" }) });

                var window = NewMembersWindow(store);
                var menu = (ContextMenu)typeof(Views.GroupMembersWindow)
                    .GetMethod("BuildMoveMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [new List<string> { "X" }])!;

                var items = menu.Items.OfType<MenuItem>().ToList();
                evidence.Add($"[菜单] {items.Count} 项：{string.Join("、", items.Select(i => i.Header))}");
                var hint = Assert.Single(items);
                Assert.False(hint.IsEnabled);
                // 本程序集的约定是宿主不装 L10n 快照（见 LanguageSwitchTests）：Tr 回落成键名，
                // 断言按键名写——钉住"菜单用了这个键"，不钉某一种语言的措辞
                Assert.Equal("L.Members_MoveNoTarget", hint.Header);
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        foreach (var line in evidence)
            output.WriteLine(line);
        Assert.Null(captured);
    }

    /// <summary>造一个挂在 store 上的成员窗口（用 Store 的第一组当"当前组"）。结构里放一个
    /// 分隔符 + 一个模组两件服装，成员是 X、Y——树里就这两行，够收勾选也够看清重建后的行数。</summary>
    private static Views.GroupMembersWindow NewMembersWindow(GroupStore store)
    {
        var groupA = store.Groups[0];
        var structure = new List<(string? Separator, string Owner, List<string> Outfits)>
        {
            ("分隔符", "模组一", ["X", "Y", "Z"]), // Z 不在组里，不该出现在树里
        };
        return new Views.GroupMembersWindow(groupA, store, structure,
            beforeChange: () => { }, onChanged: () => { });
    }

    /// <summary>当前开着的 ContextMenu 经窗口的 <c>_moveMenu</c> 字段拿（见 GroupMembersWindow）。</summary>

    private static void Pump(Window window) =>
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var n in Descendants(child))
                yield return n;
        }
    }
}
