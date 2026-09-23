using SliderSorter.Core;
using SliderSorter.Wpf.ViewModels;
using Xunit;

namespace SliderSorter.Wpf.Tests;

/// <summary>树节点 VM 的勾选级联与懒加载物化（无头、无 Dispatcher 依赖）。</summary>
public class NodeVMTests
{
    private static ModNodeVM BuildMod(string owner, List<string> outfits, bool allChecked = true)
    {
        var mod = new ModNodeVM($"{owner}　({outfits.Count})", owner, [], _ => false, false)
        {
            Text = $"{owner}　({outfits.Count})",
        };
        foreach (var name in outfits)
            mod.Children.Add(new OutfitNodeVM(name, hasConflict: false, isMember: false, isChecked: false)
            {
                Parent = mod,
            });
        mod.IsChecked = allChecked; // 触发级联，与用户勾选一致
        return mod;
    }

    [Fact]
    public void CheckMod_CascadesToOutfitChildren()
    {
        var mod = BuildMod("M1", ["A", "B"]);
        Assert.All(mod.Children.OfType<OutfitNodeVM>(), c => Assert.True(c.IsChecked));

        mod.IsChecked = false;
        Assert.All(mod.Children.OfType<OutfitNodeVM>(), c => Assert.False(c.IsChecked));

        mod.IsChecked = true;
        Assert.All(mod.Children.OfType<OutfitNodeVM>(), c => Assert.True(c.IsChecked));
    }

    [Fact]
    public void CheckSeparator_CascadesToModsAndOutfits()
    {
        var sep = new SeparatorNodeVM("IVY");
        var m1 = BuildMod("M1", ["A"]);
        var m2 = BuildMod("M2", ["B", "C"]);
        foreach (var mod in new[] { m1, m2 })
        {
            mod.Parent = sep;
            sep.Children.Add(mod);
        }

        sep.IsChecked = true;
        Assert.All(new[] { m1, m2 }, m => Assert.True(m.IsChecked));
        Assert.All(new[] { m1, m2 }.SelectMany(m => m.Children.OfType<OutfitNodeVM>()), c => Assert.True(c.IsChecked));

        sep.IsChecked = false;
        Assert.All(new[] { m1, m2 }.SelectMany(m => m.Children.OfType<OutfitNodeVM>()), c => Assert.False(c.IsChecked));
    }

    [Fact]
    public void LazyExpand_MaterializesWithParentCheckedState()
    {
        // 懒加载：构造时未物化（含占位子节点），首次展开才创建服装行并继承勾选状态
        var isMemberCalled = new List<string>();
        var outfits = new List<OutfitEntry>
        {
            new() { Name = "A", OwnerLabel = "M1", HasConflict = false, SourceFile = "test.xml" },
            new() { Name = "B", OwnerLabel = "M1", HasConflict = false, SourceFile = "test.xml" },
        };
        var mod = new ModNodeVM("M1　(2)", "M1", outfits, name => { isMemberCalled.Add(name); return name == "A"; }, false);
        Assert.Single(mod.Children); // 占位

        mod.IsChecked = true; // 折叠状态下勾选（子项尚未物化）
        Assert.False(mod.IsExpanded);

        mod.IsExpanded = true; // 展开 → 物化
        var materialized = mod.Children.OfType<OutfitNodeVM>().ToList();
        Assert.Equal(2, materialized.Count);
        Assert.All(materialized, o => Assert.True(o.IsChecked));
        Assert.Contains("A", isMemberCalled);
        // 组内标记不再是文本前缀「✔ 」：改用树项模板里独立的勾选标记，
        // 文本必须保持干净（否则按名字过滤、复制、对齐都会被那个前缀污染）。
        Assert.Equal("A", materialized.First(o => o.OutfitName == "A").Text);
        Assert.True(materialized.First(o => o.OutfitName == "A").IsMember);
    }

    [Fact]
    public void CollectChecked_FromMixedSelection_MatchesWinFormsBehavior()
    {
        // 分隔符勾选 → 模组勾选 → 收集其下全部服装（ApplyChecked 的收集语义）
        var sep = new SeparatorNodeVM("S");
        var mod = BuildMod("M1", ["A", "B"]);
        mod.Parent = sep;
        sep.Children.Add(mod);
        sep.IsChecked = true;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in sep.WalkSelfAndDescendants())
        {
            switch (node)
            {
                case OutfitNodeVM outfit when node.IsChecked == true:
                    names.Add(outfit.OutfitName);
                    break;
                case ModNodeVM m when node.IsChecked == true:
                    foreach (var o in m.Outfits)
                        names.Add(o.Name);
                    break;
            }
        }
        Assert.Equal(["A", "B"], names.OrderBy(n => n));
    }

    [Fact]
    public void CheckAllMods_SeparatorAggregatesToChecked()
    {
        // 用户场景：手动勾选分隔符下全部模组 → 分隔符应显示勾选
        var sep = new SeparatorNodeVM("S");
        var m1 = BuildMod("M1", ["A"], allChecked: false);
        var m2 = BuildMod("M2", ["B"], allChecked: false);
        foreach (var mod in new[] { m1, m2 })
        {
            mod.Parent = sep;
            sep.Children.Add(mod);
        }

        m1.IsChecked = true;
        Assert.Null(sep.IsChecked); // 只勾了一个 → 半选

        m2.IsChecked = true;
        Assert.True(sep.IsChecked); // 全勾 → 勾选
    }

    [Fact]
    public void UncheckAllChildren_SeparatorAggregatesToUnchecked()
    {
        // 用户场景：勾分隔符 → 逐个取消全部模组 → 分隔符应回到未勾选
        var sep = new SeparatorNodeVM("S");
        var m1 = BuildMod("M1", ["A"], allChecked: false);
        var m2 = BuildMod("M2", ["B"], allChecked: false);
        foreach (var mod in new[] { m1, m2 })
        {
            mod.Parent = sep;
            sep.Children.Add(mod);
        }

        sep.IsChecked = true;
        Assert.True(sep.IsChecked);

        m1.IsChecked = false;
        Assert.Null(sep.IsChecked); // 还剩一个勾着 → 半选

        m2.IsChecked = false;
        Assert.False(sep.IsChecked); // 全部取消 → 未勾选
    }

    [Fact]
    public void CheckOutfit_PropagatesUpThroughModToSeparator()
    {
        var sep = new SeparatorNodeVM("S");
        var mod = BuildMod("M1", ["A", "B"], allChecked: false);
        mod.Parent = sep;
        sep.Children.Add(mod);
        var outfitA = mod.Children.OfType<OutfitNodeVM>().First(o => o.OutfitName == "A");

        outfitA.IsChecked = true;
        Assert.Null(mod.IsChecked);     // 模组内部分勾 → 半选
        Assert.Null(sep.IsChecked);     // 分隔符尚未全勾 → 半选

        var outfitB = mod.Children.OfType<OutfitNodeVM>().First(o => o.OutfitName == "B");
        outfitB.IsChecked = true;
        Assert.True(mod.IsChecked);
        Assert.True(sep.IsChecked);     // 全部勾上 → 分隔符勾选
    }

    [Fact]
    public void LazyMod_Unmaterialized_ParticipatesInSeparatorCascade()
    {
        // 未物化模组（含占位）在分隔符级联时也要拿到状态，展开后物化出同样状态
        var sep = new SeparatorNodeVM("S");
        var outfits = new List<OutfitEntry>
        {
            new() { Name = "A", OwnerLabel = "M1", HasConflict = false, SourceFile = "t.xml" },
        };
        var mod = new ModNodeVM("M1　(1)", "M1", outfits, _ => false, false) { Parent = sep };
        sep.Children.Add(mod);

        sep.IsChecked = true;
        Assert.True(mod.IsChecked);

        mod.IsExpanded = true;
        Assert.All(mod.Children.OfType<OutfitNodeVM>(), o => Assert.True(o.IsChecked));
    }
}
