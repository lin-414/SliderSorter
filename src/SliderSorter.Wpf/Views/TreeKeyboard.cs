using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SliderSorter.Wpf.ViewModels;

namespace SliderSorter.Wpf.Views;

/// <summary>节点树的键盘操作：空格切换勾选、左右展开/折叠、上下移动聚焦行。
///
/// <para>
/// 为什么要自己接这一套：树里的勾选框是**模板内的 <c>CheckBox</c>**，而 <c>TreeView</c> 默认的
/// 键盘处理会把空格/方向键当成"导航"消费掉 —— 结果是纯键盘用户能选中一行、却**永远勾不上它**，
/// 只能改用鼠标去点那 13px 的方框。这是本工具唯一一条"只有鼠标能做"的关键操作，
/// 而"勾选一批服装"恰恰是它的主循环。
/// </para>
/// <para>
/// 为什么放在 <c>PreviewKeyDown</c>（隧道阶段）而不是 <c>KeyDown</c>：等冒泡到 TreeView 时，
/// TreeViewItem 往往已经把空格翻译成"切换展开"了。隧道阶段先拿到，处理掉就 <c>Handled = true</c>，
/// 阻断后续的默认行为。
/// </para>
/// <para>
/// 为什么不用 <c>TreeViewItem.IsSelected</c> 作为"当前行"的唯一依据：<c>TreeView</c> 开了虚拟化，
/// 容器会回收重建，选中态是唯一跨回收保留的东西，所以以它为准；移动焦点时再取回容器
/// （<see cref="ItemsControl.ContainerFromItem"/>），拿不到就跳过——那是不可见的行，本来也不该被聚焦。
/// </para>
/// <para>
/// 三棵树（主界面 / 「查看组」/「新装模组」）共用本类：它们的行结构都是
/// 「<c>NodeRowCheckBox</c> + <c>NodeRowText</c>」的 <see cref="NodeVM"/>，
/// 各自再写一遍必然漂开——本次会话里已经因为"同一段模板抄三处"踩过一次。
/// </para>
public static class TreeKeyboard
{
    /// <summary>给树接上键盘操作。幂等：同一棵树重复调用不会重复挂钩子。</summary>
    public static void Attach(TreeView tree)
    {
        tree.PreviewKeyDown -= OnPreviewKeyDown;
        tree.PreviewKeyDown += OnPreviewKeyDown;
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TreeView tree)
            return;

        // 修饰键组合一律放行：Ctrl+A（全选）之类留给业务，别在这里吞掉
        if (Keyboard.Modifiers is not (ModifierKeys.None))
            return;

        var row = CurrentRow(tree);

        switch (e.Key)
        {
            case Key.Space:
                if (row?.DataContext is NodeVM node)
                    Toggle(node);
                e.Handled = true;
                break;

            case Key.Left:
                // 语义与 Windows 资源管理器一致：展开时先折叠自己，已折叠才退到父级
                if (row is null)
                    return;
                if (row.IsExpanded)
                    row.IsExpanded = false;                 // 折叠（VM 与容器展开态是绑定的）
                else if (RowOf(tree, ParentNode(row)) is { } parent)
                    FocusRow(tree, parent);
                e.Handled = true;
                break;

            case Key.Right:
                if (row is null)
                    return;
                if (!row.IsExpanded)
                    row.IsExpanded = true;                  // 展开顺带物化子节点（ModNodeVM 懒加载）
                else if (FirstChildRow(row) is { } child)
                    FocusRow(tree, child);
                e.Handled = true;
                break;

            case Key.Up:
            case Key.Down:
                // TreeView 自带上下移动，但它只走**可见行**且会在展开态里跳格。
                // 覆写成"按所有可见行的线性顺序移动"，与资源管理器一致，也保证
                // "移到某行 → 空格勾上"这条链路不会因为折叠态而卡住。
                if (row is null)
                    return;
                e.Handled = MoveRow(tree, row, e.Key == Key.Down ? 1 : -1);
                break;
        }
    }

    /// <summary>空格切换勾选。占位行不是一个可勾选的条目，跳过（它的复选框本来就是收起的）。</summary>
    private static void Toggle(NodeVM node)
    {
        if (node.IsPlaceholder)
            return;
        // 半选（null）在用户按下空格时应变成"全选"——与点复选框的观感一致
        node.IsChecked = node.IsChecked != true;
    }

    /// <summary>行的父行：从**数据**沿 Parent 链取，再换成容器。
    ///
    /// <para>
    /// 为什么不走 <c>TreeViewItem.Parent</c>（视觉父）：树的 ItemTemplate 只写在
    /// <see cref="TreeView.ItemTemplate"/> 上，子层 <see cref="TreeViewItem"/> 的
    /// <c>Items.Count</c> 实测恒为 0（模板没下沉到容器），所以既取不到子行、
    /// 也不能靠容器父子关系反推层次。数据上的 <c>NodeVM.Parent</c> 是唯一可靠的层次来源。
    /// </para></summary>
    private static NodeVM? ParentNode(TreeViewItem row) => (row.DataContext as NodeVM)?.Parent;

    /// <summary>当前有键盘焦点的行；没有聚焦行时退回选中行（鼠标点过之后按空格也该生效）。</summary>
    private static TreeViewItem? CurrentRow(TreeView tree)
    {
        if (Keyboard.FocusedElement is DependencyObject focused &&
            tree.ContainerFromElement(focused) is TreeViewItem fromFocus)
            return fromFocus;

        return SelectedRow(tree);
    }

    private static TreeViewItem? SelectedRow(TreeView tree)
    {
        foreach (var item in AllRows(tree))
            if (item.IsSelected)
                return item;
        return null;
    }

    /// <summary>树里的**全部**行容器，按「根 → 各自子树」的可见顺序。
    ///
    /// <para>
    /// 关键：用 <see cref="ItemsControl.ItemContainerGenerator"/> 一层层往下找，
    /// 而**不是**遍历 <see cref="TreeViewItem.Items"/>——后者的 Items.Count 恒为 0（原因见
    /// <see cref="ParentNode"/>）。所以每一层都从该层控件的生成器取容器。
    /// </para>
    /// <para>
    /// 折叠的子树其容器根本没生成，自然不在结果里，"只走可见行"是结构保证而非过滤条件。
    /// </para></summary>
    private static IEnumerable<TreeViewItem> AllRows(TreeView tree)
    {
        foreach (var root in tree.Items)
            if (tree.ItemContainerGenerator.ContainerFromItem(root) is TreeViewItem row)
                foreach (var one in Subtree(row))
                    yield return one;
    }

    /// <summary>一行及其所有**已物化**的后代。每层都重新解析 item→container：
    /// 容器是延迟生成的，展开动作之后必须重新取一次，缓存住就会漏掉新出现的子行。</summary>
    private static IEnumerable<TreeViewItem> Subtree(TreeViewItem row)
    {
        yield return row;
        var node = row.DataContext as NodeVM;
        if (node is null || !node.IsExpanded)
            yield break;

        foreach (var child in node.Children)
            if (row.ItemContainerGenerator.ContainerFromItem(child) is TreeViewItem childRow)
                foreach (var one in Subtree(childRow))
                    yield return one;
    }

    /// <summary>在可见行序列里前后移动。返回是否真的移动了（到头了就返回 false，
    /// 让事件继续冒泡给别的处理器，而不是把按键无声吃掉）。</summary>
    private static bool MoveRow(TreeView tree, TreeViewItem from, int delta)
    {
        var rows = AllRows(tree).ToList();
        var index = rows.IndexOf(from);
        if (index < 0)
            return false;

        var target = index + delta;
        if (target < 0 || target >= rows.Count)
            return false;

        FocusRow(tree, rows[target]);
        return true;
    }

    /// <summary>焦点落到某行：选中 + 滚动进视野，然后**把键盘焦点交回树**。
    ///
    /// <para>
    /// 为什么焦点交回树而不是留在行上：<see cref="TreeViewItem"/> 自己有一组方向键处理器，
    /// 焦点落在它上面时，后续的 Arrow 会被它按"展开/选中"的语义先消费掉，
    /// 我们挂在树上的 <c>PreviewKeyDown</c>（隧道）虽然仍会先收到，但"选中哪一行"的真相
    /// 会被它的默认行为改写，表现为方向键跳格。树才是唯一挂着本类的那一层，路径最短、最可控。
    /// </para>
    /// <para>
    /// 代价：焦点全在树上时 <see cref="Keyboard.FocusedElement"/> 认不出"当前行"，
    /// 所以 <see cref="CurrentRow"/> 在拿不到聚焦行时**退回选中行**——选中态才是真正被依赖的状态。
    /// </para></summary>
    private static void FocusRow(TreeView tree, TreeViewItem row)
    {
        row.IsSelected = true;
        row.BringIntoView();
        tree.Focus();
    }

    /// <summary>一行的第一个子行容器（已展开且已物化时才有）。
    /// 从 <c>NodeVM.Children</c> 取数据、再换容器；不能走 <c>row.Items</c>（恒为空，原因见
    /// <see cref="ParentNode"/>）。占位子节点没有可勾选项，但它仍然是一行，照常可以落上去。</summary>
    private static TreeViewItem? FirstChildRow(TreeViewItem row)
    {
        if (row.DataContext is not NodeVM { Children.Count: > 0 } node)
            return null;
        return row.ItemContainerGenerator.ContainerFromItem(node.Children[0]) as TreeViewItem;
    }

    /// <summary>数据节点 → 它的行容器。当前行在根层时是树自己的生成器，否则在父行的生成器上。</summary>
    private static TreeViewItem? RowOf(TreeView tree, NodeVM? node)
    {
        if (node is null)
            return null;
        var parent = node.Parent;
        return parent is null
            ? tree.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem
            : parent.Children.Contains(node)
                ? (RowOf(tree, parent)?.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem)
                : null;
    }
}
