using SliderSorter.Core;
using SliderSorter.Wpf.ViewModels;
using Xunit;

namespace SliderSorter.Wpf.Tests;

/// <summary>重建树时展开/折叠状态的保留（回归用例，对应「加入分组后左侧列表被全部展开」）。
///
/// 重建后的新节点默认全折叠，因此断言分两半：展开过的必须还是展开，没展开的（含用户
/// **手动折叠**过的）必须保持折叠——只断言前者的话，把"把全部节点都展开"当成实现对也能过。
///
/// 与 MainViewModel 无关：节点树上没有磁盘、没有 Dispatcher、没有 Application。</summary>
public class TreeExpandStateTests
{
    /// <summary>假的模组节点，带真实可物化的服装行（展开时会建出子节点）。</summary>
    private static ModNodeVM Mod(string owner, params string[] outfits) =>
        new($"{owner}　({outfits.Length})", owner,
            outfits.Select(o => new OutfitEntry { Name = o, OwnerLabel = owner, SourceFile = "t.xml" }).ToList(),
            _ => false, false);

    private static List<NodeVM> FlatTree() =>
    [
        Mod("M1", "A1", "A2"),
        Mod("M2", "B1", "B2"),
        Mod("M3", "C1", "C2"),
    ];

    [Fact]
    public void OnlyTheExpandedRoot_IsRestored()
    {
        // 用户只展开了中间那个模组
        var old = FlatTree();
        old[1].IsExpanded = true;

        var keys = TreeExpandState.Capture(old);
        var rebuilt = FlatTree();
        TreeExpandState.Restore(rebuilt, keys);

        Assert.True(rebuilt[1].IsExpanded);
        Assert.False(rebuilt[0].IsExpanded);   // ← 修复前这里是 true：根节点共用一个键
        Assert.False(rebuilt[2].IsExpanded);   // ← 同上
    }

    [Fact]
    public void ManuallyCollapsedRoot_StaysCollapsed()
    {
        // 用户先后展开两个模组，又把前一个手动折回去了
        var old = FlatTree();
        old[0].IsExpanded = true;
        old[1].IsExpanded = true;
        old[0].IsExpanded = false;

        var keys = TreeExpandState.Capture(old);
        var rebuilt = FlatTree();
        TreeExpandState.Restore(rebuilt, keys);

        Assert.False(rebuilt[0].IsExpanded);   // ← 修复前这里也变成 true（"手动折叠"被无视）
        Assert.True(rebuilt[1].IsExpanded);
        Assert.False(rebuilt[2].IsExpanded);
    }

    [Fact]
    public void NestedNodes_KeepTheirOwnKeys()
    {
        // 分隔符 → 两个模组：只有 S2 的模组是展开的
        static List<NodeVM> Tree()
        {
            var sep = new SeparatorNodeVM("IVY");
            var m1 = Mod("M1", "A1");
            var m2 = Mod("M2", "B1");
            m1.Parent = sep;
            m2.Parent = sep;
            sep.Children.Add(m1);
            sep.Children.Add(m2);
            return [sep];
        }

        var old = Tree();
        var sepOld = (SeparatorNodeVM)old[0];
        sepOld.Children[1].IsExpanded = true;

        var keys = TreeExpandState.Capture(old);
        var rebuilt = Tree();
        TreeExpandState.Restore(rebuilt, keys);

        var sepNew = (SeparatorNodeVM)rebuilt[0];
        Assert.True(sepNew.Children[1].IsExpanded);
        Assert.False(sepNew.Children[0].IsExpanded);
        Assert.False(sepNew.IsExpanded);
    }

    [Fact]
    public void Capture_WithNothingExpanded_IsEmpty()
    {
        Assert.Empty(TreeExpandState.Capture(FlatTree()));
    }
}
