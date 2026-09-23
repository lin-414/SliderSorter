using System.Threading;
using System.Windows;
using System.Windows.Controls;
using BSGroupGenerator.Core;
using BSGroupGenerator.Wpf.ViewModels;
using BSGroupGenerator.Wpf.Views;
using Xunit;
using Xunit.Abstractions;

namespace BSGroupGenerator.Wpf.Tests;

/// <summary>左侧列表右键菜单的**运行时**接线（补反射对照测不到的那一层）。
///
/// XamlCommandBindingTests 只对照"NodeVM 上有没有 ExpandAllCommand 这个属性"；这里把真正
/// 的 TreeViewItem 造出来，验证容易静默失效的两处：
///   1) ContextMenu 是资源里的共享实例、不在逻辑树上，DataContext 靠 PlacementTarget 显式接上
///      ——接不上时三项都变灰、点了没反应，编译期毫无提示；
///   2) 菜单标题走 {DynamicResource L.*}（多语言热切换），键写错时标题是空的。
///
/// 用真实的 TreeView 容器（ItemContainerStyle = NodeTreeItemStyle），因此也钉住了
/// "菜单只挂在主窗口用的那套样式上"。</summary>
[Collection(WpfStaCollection.Name)]
public class NodeTreeContextMenuTests(ITestOutputHelper output)
{
    [Fact]
    public void ContextMenuItems_TargetTheRightClickedNode_WithLocalizedHeaders()
    {
        Exception? captured = null;
        var evidence = new List<string>();

        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                // pack URI 必须带程序集名（Controls.xaml / Lang.*.xaml 都是 Resource）。
                // 语言字典要在取菜单之前合并：标题是 DynamicResource，取值时去 Application 资源里找。
                WpfHost.AddAppDictionaries(app);
                WpfHost.AddDictionary(app, "Strings/Lang.zh.xaml");

                var m1 = NewMod("M1");
                var m2 = NewMod("M2");
                var roots = new System.Collections.ObjectModel.ObservableCollection<NodeVM> { m1, m2 };
                foreach (var root in roots)
                    root.Roots = roots; // 与 MainViewModel.RebuildTree 的接线一致

                var tree = new TreeView
                {
                    ItemsSource = roots,
                    ItemContainerStyle = (Style)app.Resources["NodeTreeItemStyle"],
                };
                var window = new Window { Content = tree, Width = 400, Height = 300, ShowActivated = false };
                window.Show();
                tree.UpdateLayout();

                var item = tree.ItemContainerGenerator.ContainerFromIndex(1) as TreeViewItem;
                Assert.NotNull(item);
                var menu = item!.ContextMenu;
                Assert.NotNull(menu);

                // 真实右键时 WPF 会设 PlacementTarget；这里手动设，等价于"右键点在第二行上"
                menu!.PlacementTarget = item;
                menu.IsOpen = true;
                Pump(menu.Dispatcher);

                var items = menu.Items.OfType<MenuItem>().ToList();
                evidence.Add($"菜单项={items.Count}："
                             + string.Join("、", items.Select(i => $"{i.Header ?? "(空)"}→{i.Command?.GetType().Name ?? "无命令"}")));

                Assert.Equal(3, items.Count);
                Assert.Equal(["全部展开", "全部折叠", "折叠其他"], items.Select(i => i.Header as string));
                // 命令必须落在**被右键的那个节点**上（共享实例最容易在这里接错行）
                Assert.Same(m2.ExpandAllCommand, items[0].Command);
                Assert.Same(m2.CollapseAllCommand, items[1].Command);
                Assert.Same(m2.CollapseOthersCommand, items[2].Command);

                // 真正点一下「全部展开」，确认作用到的是整棵树而不是别的行
                m1.IsExpanded = false;
                items[0].Command!.Execute(null);
                evidence.Add($"[全部展开后] M1.IsExpanded={m1.IsExpanded}，M2.IsExpanded={m2.IsExpanded}");
                Assert.True(m1.IsExpanded);
                Assert.True(m2.IsExpanded);

                m1.CollapseOthersCommand.Execute(null);
                Assert.True(m1.IsExpanded);
                Assert.False(m2.IsExpanded);

                // 右键选中那一行：走视图侧的 TreeSelection（真树开了虚拟化，节点上的
                // IsSelected 双向绑定会被容器回收写回 false，所以不绑 VM）。
                // 这里用的还是模板内部真实的元素（行里的 TextBlock），验证 ContainerFromElement
                // 能一路找到 TreeViewItem——这是该实现唯一容易出错的地方。
                var row1Text = FirstText(item!);
                var selected = TreeSelection.SelectRow(row1Text, tree);
                evidence.Add($"[选中] 点第 2 行文字 → 返回={selected}"
                             + $" 行1.IsSelected={item!.IsSelected} 行0.IsSelected={(tree.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem)?.IsSelected}");
                Assert.True(selected);
                Assert.True(item.IsSelected);
                Assert.False((tree.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem)!.IsSelected);

                // 点在树之外的元素（不属于任何一行）时不该乱选中
                Assert.False(TreeSelection.SelectRow(tree, tree));

                menu.IsOpen = false;
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

    private static ModNodeVM NewMod(string owner) =>
        new($"{owner}　(1)", owner,
            [new OutfitEntry { Name = owner + "_A", OwnerLabel = owner, SourceFile = "t.xml" }],
            _ => false, false);

    /// <summary>行里第一个 TextBlock——模拟"用户右键点在行的文字上"（模板内部的元素）。</summary>
    private static DependencyObject? FirstText(DependencyObject root)
    {
        if (root is TextBlock)
            return root;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            if (FirstText(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        return null;
    }

    /// <summary>把绑定求值推一轮：ContextMenu 的 DataContext 是绑到 PlacementTarget 上的，
    /// 挂接排在 Dispatcher 的 DataBind 优先级上（与 MainWindowCommandTests 里同一个理由）。</summary>
    private static void Pump(System.Windows.Threading.Dispatcher dispatcher) =>
        dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
}
