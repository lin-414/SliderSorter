using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BSGroupGenerator.Wpf.ViewModels;
using BSGroupGenerator.Wpf.Views;
using Xunit;
using Xunit.Abstractions;

namespace BSGroupGenerator.Wpf.Tests;

/// <summary>节点树的键盘操作：空格勾选、左右展开/折叠、上下移动行。
///
/// <para>
/// 这一层为什么必须有用例兜住：树里的勾选框是**模板内的 CheckBox**，而 <c>TreeView</c> 默认把
/// 空格当成导航键消费掉 —— 不接 <see cref="TreeKeyboard"/> 时，"选中一行 → 按空格"什么都不会发生，
/// 编译期、色板门禁、布局探针、绑定对照**全都不会红**。纯键盘用户能选中却勾不上，
/// 而勾选恰恰是这个工具的主循环。这是最典型的"只有运行时按键才暴露"的静默失效。
/// </para>
/// <para>
/// 按键走真实的 <c>Keyboard.KeyDownEvent</c> 冒泡（在树上 <c>RaiseEvent</c>），
/// 这样隧道/冒泡两阶段的先后顺序、以及 <c>Handled</c> 有没有真的阻断默认行为，
/// 都是按真机的方式在验。直接调私有方法等于只测自己写的那几行。
/// </para>
/// </summary>
[Collection(WpfStaCollection.Name)]
public class TreeKeyboardTests(ITestOutputHelper output)
{
    [Fact]
    public void SpaceTogglesTheFocusedRow_AndArrowsNavigate()
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

                // 三个根：前两个是模组（各带一个可勾选的服装子节点），第三个是分隔符。
                // 分隔符会折叠起来，用来验证方向键只走可见行。
                var m1 = NewMod("M1");
                var m2 = NewMod("M2");
                var sep = new SeparatorNodeVM("S1");
                var roots = new System.Collections.ObjectModel.ObservableCollection<NodeVM> { m1, m2, sep };
                foreach (var root in roots)
                    root.Roots = roots;

                var tree = new TreeView
                {
                    ItemsSource = roots,
                    ItemContainerStyle = (Style)app.Resources["NodeTreeItemStyle"],
                    // 真实窗口的树都声明了 ItemTemplate（行内容）与 ItemsPanel（虚拟化面板）。
                    // 光设 ItemsSource 时子层容器不会生成，容器遍历法会看不到任何子行——
                    // 那是宿主搭得不全，不是被测代码的问题。这里用三棵勾选树共用的那份行模板。
                    ItemTemplate = (DataTemplate)app.Resources["NodeRowTemplate"],
                    ItemsPanel = new ItemsPanelTemplate(
                        new FrameworkElementFactory(typeof(VirtualizingStackPanel))),
                };
                TreeKeyboard.Attach(tree);

                var window = new Window
                {
                    Content = tree, Width = 400, Height = 400,
                    ShowActivated = true,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000, Top = -32000, Opacity = 0,
                };
                window.Show();
                tree.UpdateLayout();
                Pump(tree.Dispatcher);

                m1.IsExpanded = true;
                m2.IsExpanded = true;
                tree.UpdateLayout();
                Pump(tree.Dispatcher);

                var row0 = Row(tree, 0);
                var row1 = Row(tree, 1);
                var row2 = Row(tree, 2);
                Assert.NotNull(row0);
                Assert.NotNull(row1);
                Assert.NotNull(row2);

                // ① 选中第一行 → 按空格 → 该节点勾选态翻转，并向下级联到它的服装子节点
                row0!.IsSelected = true;
                row0.Focus();
                Pump(tree.Dispatcher);
                var m1Before = m1.IsChecked;
                Send(tree, Key.Space);
                evidence.Add($"[空格] M1.IsChecked: {m1Before} → {m1.IsChecked}"
                             + $"，服装子节点={string.Join(",", m1.Children.Where(c => !c.IsPlaceholder).Select(c => c.IsChecked))}");
                Assert.NotEqual(m1Before, m1.IsChecked);
                Assert.Equal(m1.IsChecked, m1.Children.First(c => !c.IsPlaceholder).IsChecked);

                // 再按一次应当翻回来（不是单向的"全选"）
                Send(tree, Key.Space);
                evidence.Add($"[空格×2] M1.IsChecked 回到 {m1.IsChecked}");
                Assert.Equal(m1Before, m1.IsChecked);

                // ② 占位行不是可勾选条目：空格不该动它（它的复选框本来就是收起的）
                var placeholder = new NodeVM(NodeKind.Outfit) { IsPlaceholder = true };
                var rootWithPlaceholder = new System.Collections.ObjectModel.ObservableCollection<NodeVM> { placeholder };
                tree.ItemsSource = rootWithPlaceholder;
                foreach (var r in rootWithPlaceholder)
                    r.Roots = rootWithPlaceholder;
                tree.UpdateLayout();
                Pump(tree.Dispatcher);
                Row(tree, 0)!.IsSelected = true;
                var placeholderBefore = placeholder.IsChecked;
                Send(tree, Key.Space);
                evidence.Add($"[空格·占位行] IsChecked: {placeholderBefore} → {placeholder.IsChecked}（应不变）");
                Assert.Equal(placeholderBefore, placeholder.IsChecked);

                // 复原成三根，继续验方向键
                tree.ItemsSource = roots;
                tree.UpdateLayout();
                Pump(tree.Dispatcher);
                m1.IsExpanded = true;
                m2.IsExpanded = true;
                sep.IsExpanded = false;
                tree.UpdateLayout();
                Pump(tree.Dispatcher);

                // ③ 右键折叠：展开的行上按 → 不动，按 ← 折叠自己
                Row(tree, 0)!.IsSelected = true;
                Send(tree, Key.Left);
                evidence.Add($"[←] M1.IsExpanded={m1.IsExpanded}（展开态应先折叠自己）");
                Assert.False(m1.IsExpanded);

                // ④ 已折叠的行上按 → 展开（并物化子节点）
                Send(tree, Key.Right);
                evidence.Add($"[→] M1.IsExpanded={m1.IsExpanded}，子节点数={m1.Children.Count}");
                Assert.True(m1.IsExpanded);

                // ⑤ 已展开的行上按 → 进到第一子行（选中态移到子行）
                tree.UpdateLayout();
                Pump(tree.Dispatcher);
                Send(tree, Key.Right);
                tree.UpdateLayout();
                Pump(tree.Dispatcher);
                var allRows = Descendants(tree).ToList();
                var selectedRows = allRows.Where(r => r.IsSelected).ToList();
                evidence.Add($"[→→] 可见行={allRows.Count}，选中行={selectedRows.Count}"
                             + $"，选中={string.Join("、", selectedRows.Select(Describe))}");
                Assert.Single(selectedRows);
                Assert.NotSame(Row(tree, 0), selectedRows[0]);
                Assert.Equal(NodeKind.Outfit, ((NodeVM)selectedRows[0].DataContext).Kind);

                window.Close();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "用例线程超时未结束");

        foreach (var line in evidence)
            output.WriteLine(line);
        Assert.Null(captured);
    }

    /// <summary>把按键送到树上。走真实的 Keyboard 事件，隧道阶段先进我们的处理器。</summary>
    private static void Send(TreeView tree, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(tree), 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        tree.RaiseEvent(args);

        if (!args.Handled)
        {
            // 我们的处理器没接手时，仍然把冒泡阶段也走一遍，模拟真机（这样"没处理"会真的落到默认行为上）
            var bubble = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(tree), 0, key)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
            };
            tree.RaiseEvent(bubble);
        }
        Pump(tree.Dispatcher);
    }

    private static TreeViewItem? Row(TreeView tree, int index) =>
        tree.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;

    /// <summary>树上所有**已生成容器**的行（含折叠子树的容器）。用来断言"选中态落在哪一行"。</summary>
    private static IEnumerable<TreeViewItem> Descendants(TreeView tree)
    {
        foreach (var item in tree.Items)
            if (tree.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem row)
                foreach (var one in Walk(row))
                    yield return one;
    }

    private static IEnumerable<TreeViewItem> Walk(TreeViewItem row)
    {
        yield return row;
        foreach (var child in row.Items)
            if (row.ItemContainerGenerator.ContainerFromItem(child) is TreeViewItem childRow)
                foreach (var one in Walk(childRow))
                    yield return one;
    }

    private static string Describe(TreeViewItem? row) =>
        row?.DataContext is NodeVM node ? $"{node.Text}({node.Kind})" : row?.DataContext?.GetType().Name ?? "(无)";

    private static ModNodeVM NewMod(string owner) =>
        new($"{owner}　(1)", owner,
            [new BSGroupGenerator.Core.OutfitEntry { Name = owner + "_A", OwnerLabel = owner, SourceFile = "t.xml" }],
            _ => false, false);

    private static void Pump(System.Windows.Threading.Dispatcher dispatcher) =>
        dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.ContextIdle);
}
