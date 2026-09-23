using SliderSorter.Wpf.Services;
using SliderSorter.Wpf.ViewModels;
using Xunit;

namespace SliderSorter.Wpf.Tests;

/// <summary>无 Application 上下文（单测、设计器）时取词不抛异常、回落到键名。
/// 服装节点文本在构造函数里取词，因此该路径必须有此保证。
/// 挂到 STA 集合上串行：本程序集里还有会构造真实 Application/窗口的用例，
/// 那类用例一旦先把语言装载进来，这里的"回落到键名"就不再成立（快照非 null = 有词可取）。</summary>
[Collection(WpfStaCollection.Name)]
public class L10nTests
{
    [Fact]
    public void TrWithoutApplicationFallsBackToKey()
    {
        Assert.Equal("L.Tree_ConflictSuffix", L10n.Tr("L.Tree_ConflictSuffix"));
    }

    [Fact]
    public void ConflictOutfitNode_UsesLocalizedSuffix()
    {
        var node = new OutfitNodeVM("A", hasConflict: true, isMember: false, isChecked: false);

        Assert.Equal("A" + L10n.Tr("L.Tree_ConflictSuffix"), node.Text);
    }
}
