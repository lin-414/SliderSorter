using System.Windows;
using System.Windows.Controls;

namespace SliderSorter.Wpf.Views;

/// <summary>把右键点到的那一行选中。
///
/// 选中是纯粹的**视图状态**（ViewModel 不关心选中的是哪一行），所以不走节点的双向绑定：
/// 真树开了虚拟化，容器回收/重建时双向绑定会把 false 写回来，实测在真机上"选中"不生效
/// （几百个节点的小树里却成立）。改成在右键弹出的那一刻，直接把命中的那一个 TreeViewItem
/// 置为选中——和左键点选的机制完全一样。</summary>
public static class TreeSelection
{
    /// <summary>选中 <paramref name="source"/> 所在的行。点在空白处（不属于任何行）时返回 false，
    /// 此时也不会有上下文菜单。</summary>
    public static bool SelectRow(DependencyObject? source, ItemsControl tree)
    {
        var row = RowOf(source, tree);
        if (row is null)
            return false;
        row.IsSelected = true;
        return true;
    }

    /// <summary>元素所属的 TreeViewItem。传模板内部的元素（行里的文字/复选框）也能一路找上去。</summary>
    public static TreeViewItem? RowOf(DependencyObject? source, ItemsControl tree) =>
        source is null ? null : tree.ContainerFromElement(source) as TreeViewItem;
}
