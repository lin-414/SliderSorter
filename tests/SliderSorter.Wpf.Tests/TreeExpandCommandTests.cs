using System.Collections.ObjectModel;
using SliderSorter.Core;
using SliderSorter.Wpf.ViewModels;
using Xunit;

namespace SliderSorter.Wpf.Tests;

/// <summary>左侧列表右键菜单的三个命令：全部展开 / 全部折叠 / 折叠其他。
///
/// 命令落在节点上（菜单项的 DataContext 就是被右键的那一行），树级操作经 <c>Roots</c> 拿到整棵树——
/// 所以这里把 Roots 按 MainViewModel.RebuildTree 的方式接上，否则"全部展开"只会展开自己这棵子树。
/// 无头、无 Dispatcher、无 Application。</summary>
public class TreeExpandCommandTests
{
    private static ModNodeVM Mod(string owner, params string[] outfits) =>
        new($"{owner}　({outfits.Length})", owner,
            outfits.Select(o => new OutfitEntry { Name = o, OwnerLabel = owner, SourceFile = "t.xml" }).ToList(),
            _ => false, false);

    /// <summary>三个平铺的模组根节点（Roots 接在根上，与 RebuildTree 一致）。</summary>
    private static (ObservableCollection<NodeVM> Roots, ModNodeVM M1, ModNodeVM M2, ModNodeVM M3) Flat()
    {
        ModNodeVM m1 = Mod("M1", "A1", "A2"), m2 = Mod("M2", "B1", "B2"), m3 = Mod("M3", "C1", "C2");
        var roots = new ObservableCollection<NodeVM> { m1, m2, m3 };
        foreach (var root in roots)
            root.Roots = roots;
        return (roots, m1, m2, m3);
    }

    [Fact]
    public void ExpandAll_ExpandsEveryRoot_AndMaterializesTheirChildren()
    {
        var (_, m1, m2, m3) = Flat();
        Assert.All(new NodeVM[] { m1, m2, m3 }, n => Assert.Single(n.Children)); // 展开前只有占位

        m2.ExpandAllCommand.Execute(null);

        Assert.All(new NodeVM[] { m1, m2, m3 }, n => Assert.True(n.IsExpanded));
        // 展开顺带物化：占位行被真实服装行替换（懒加载的模组节点只在首次展开时建子节点）
        Assert.All(new NodeVM[] { m1, m2, m3 }, n => Assert.Equal(2, n.Children.Count));
        Assert.All(new NodeVM[] { m1, m2, m3 }.SelectMany(n => n.Children), c => Assert.NotNull(c.Parent));
    }

    [Fact]
    public void CollapseAll_CollapsesEveryRoot()
    {
        var (_, m1, m2, m3) = Flat();
        m1.ExpandAllCommand.Execute(null);
        Assert.All(new NodeVM[] { m1, m2, m3 }, n => Assert.True(n.IsExpanded));

        m3.CollapseAllCommand.Execute(null);

        Assert.All(new NodeVM[] { m1, m2, m3 }, n => Assert.False(n.IsExpanded));
    }

    [Fact]
    public void CollapseOthers_OnFlatTree_KeepsOnlyTheClickedRoot()
    {
        var (_, m1, m2, m3) = Flat();
        m1.ExpandAllCommand.Execute(null);

        m3.CollapseOthersCommand.Execute(null);

        Assert.False(m1.IsExpanded);
        Assert.False(m2.IsExpanded);
        Assert.True(m3.IsExpanded);
    }

    /// <summary>嵌套场景：分隔符 → M1/M2。折叠祖先会把当前节点自己也藏起来，所以「自己 → 根」
    /// 这条链必须整条保留展开，只有旁系被折叠。</summary>
    [Fact]
    public void CollapseOthers_KeepsClickedNodeAndItsAncestorsExpanded()
    {
        var sep = new SeparatorNodeVM("IVY");
        var m1 = Mod("M1", "A1");
        var m2 = Mod("M2", "B1");
        var roots = new ObservableCollection<NodeVM> { sep };
        sep.Roots = roots;
        m1.Parent = sep;
        m2.Parent = sep;
        sep.Children.Add(m1);
        sep.Children.Add(m2);

        sep.ExpandAllCommand.Execute(null);
        Assert.True(sep.IsExpanded && m1.IsExpanded && m2.IsExpanded);

        m2.CollapseOthersCommand.Execute(null);

        Assert.True(sep.IsExpanded);   // 祖先保留：折了它就看不见当前节点
        Assert.True(m2.IsExpanded);    // 当前节点保持展开
        Assert.False(m1.IsExpanded);   // 旁系折叠
    }

    /// <summary>右键点在一个**折叠着**的模组上：它自己要"保持展开"（用户要看的就是它），
    /// 其余照常折叠。这一行本来可能一个子节点都没物化过。</summary>
    [Fact]
    public void CollapseOthers_OnCollapsedNode_ExpandsItAndCollapsesTheRest()
    {
        var (_, m1, m2, m3) = Flat();
        m2.IsExpanded = true;

        m1.CollapseOthersCommand.Execute(null);

        Assert.True(m1.IsExpanded);    // 当前节点保持展开
        Assert.Equal(2, m1.Children.Count); // 顺带物化出服装行
        Assert.False(m2.IsExpanded);
        Assert.False(m3.IsExpanded);
    }
}
