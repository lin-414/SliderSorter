using Xunit;
using SliderSorter.Core;

namespace SliderSorter.Tests;

public class GroupStoreTests
{
    [Fact]
    public void NewGroupValidatesAndSelects()
    {
        var store = new GroupStore();
        Assert.False(store.NewGroup("").Ok);
        store.NewGroup("3BA");
        Assert.False(store.NewGroup("3ba").Ok); // 忽略大小写的重名
        Assert.Equal("3BA", store.CurrentGroupName);
        Assert.True(store.Dirty);
    }

    [Fact]
    public void ApplyToCurrentAddsAndRemovesWithoutDuplication()
    {
        var store = new GroupStore();
        store.NewGroup("UBE");
        store.ApplyToCurrent(new[] { "OutfitA", "OutfitB" }, add: true);
        store.ApplyToCurrent(new[] { "OutfitA" }, add: true); // 重复加入

        var group = store.Current!;
        Assert.Equal(new[] { "OutfitA", "OutfitB" }, group.Members);

        store.ApplyToCurrent(new[] { "OutfitA" }, add: false);
        Assert.Equal(new[] { "OutfitB" }, group.Members);
    }

    [Fact]
    public void UndoRestoresPreviousState()
    {
        var store = new GroupStore();
        store.NewGroup("UBE");
        store.ApplyToGroup("UBE", new[] { "A", "B" }, add: true);
        store.ApplyToGroup("UBE", new[] { "C" }, add: true);

        Assert.True(store.Undo().Ok);
        Assert.Equal(new[] { "A", "B" }, store.GetGroup("UBE")!.Members);

        Assert.True(store.Undo().Ok);
        Assert.Empty(store.GetGroup("UBE")!.Members);
    }

    [Fact]
    public void UndoSurvivesDeleteAndRestoreGroup()
    {
        var store = new GroupStore();
        store.NewGroup("UBE");
        store.ApplyToGroup("UBE", new[] { "A" }, add: true);
        store.DeleteGroup("UBE");

        Assert.True(store.Undo().Ok);
        Assert.NotNull(store.GetGroup("UBE"));
        Assert.Contains("UBE", store.Groups.Select(g => g.Name));
    }

    [Fact]
    public void RenameUpdatesCurrentSelection()
    {
        var store = new GroupStore();
        store.NewGroup("Old");
        store.RenameGroup("Old", "New");
        Assert.Equal("New", store.CurrentGroupName);
    }

    [Fact]
    public void ImportMergesAndCounts()
    {
        var store = new GroupStore();
        store.NewGroup("UBE");
        store.ApplyToGroup("UBE", new[] { "A" }, add: true);

        var incoming = new List<SliderGroup>
        {
            new("UBE", new[] { "A", "B" }),
            new("3BA", new[] { "X" }),
        };
        var (addedGroups, addedMembers) = store.Import(incoming);

        Assert.Equal(1, addedGroups);
        Assert.Equal(2, addedMembers);
        Assert.Equal(new[] { "A", "B" }, store.GetGroup("UBE")!.Members);
    }

    [Fact]
    public void LoadReplacesAndClearsDirty()
    {
        var store = new GroupStore();
        store.NewGroup("Temp");
        Assert.True(store.Dirty);

        store.Load(new List<SliderGroup> { new("Loaded", new[] { "X" }) });
        Assert.False(store.Dirty);
        Assert.Equal("Loaded", store.CurrentGroupName);
    }

    [Fact]
    public void RuleApplyAddsByNames()
    {
        var store = new GroupStore();
        store.NewGroup("UBE");

        var applied = store.ApplyToGroup("UBE", new[] { "A", "A", "B" }, add: true);
        Assert.Equal(2, applied);
        Assert.Equal(new[] { "A", "B" }, store.GetGroup("UBE")!.Members);
    }

    /// <summary>界面侧就地改成员（如成员预览里移除）后，IsInAnyGroup 必须看到新状态：
    /// 成员缓存不失效时，"刚移除的服装"会被一直答复为"仍在组内"。</summary>
    [Fact]
    public void MarkDirtyFromUiInvalidatesMembershipCache()
    {
        var store = new GroupStore();
        store.NewGroup("UBE");
        store.ApplyToCurrent(new[] { "A", "B" }, add: true);

        // 先问一次，把成员缓存建起来（不建缓存的话本用例测不到这个缺陷）
        Assert.True(store.IsInAnyGroup("A"));
        Assert.True(store.IsInAnyGroup("B"));

        store.Current!.Members.RemoveAll(m => m == "A");
        store.MarkDirtyFromUi();

        Assert.False(store.IsInAnyGroup("A"));
        Assert.True(store.IsInAnyGroup("B"));
    }

    /// <summary>组名大小写不同的选中名也要能定位到组：CurrentGroupName 可能来自外部/界面输入。</summary>
    [Fact]
    public void CurrentMatchesGroupNameIgnoringCase()
    {
        var store = new GroupStore();
        store.NewGroup("UBE");

        store.SelectGroup("ube");

        Assert.NotNull(store.Current);
        Assert.Same(store.GetGroup("UBE"), store.Current);
    }

    /// <summary>一次重命名只占一步撤销（ViewModel 侧不再额外压快照）：撤销到"改名之前"之后就没了。
    /// 多压一次快照时，第二次 Undo 会返回成功却什么都没变——用户看到的是"撤销点了没反应"。</summary>
    [Fact]
    public void RenameIsExactlyOneUndoStep()
    {
        var store = new GroupStore();
        store.Load(new List<SliderGroup> { new("Old", new[] { "A" }) });

        store.RenameGroup("Old", "New");

        Assert.True(store.Undo().Ok);                    // 第一次：回到 Old
        Assert.Equal("Old", store.GetGroup("Old")!.Name);
        Assert.False(store.Undo().Ok);                   // 第二次：撤销栈已空
        Assert.False(store.CanUndo);
    }

    /// <summary>导入一次也只占一步撤销。</summary>
    [Fact]
    public void ImportIsExactlyOneUndoStepPerCall()
    {
        var store = new GroupStore(); // 全新的 store，撤销栈本来就是空的
        store.Import(new[] { new SliderGroup("A", new[] { "x" }) });

        Assert.True(store.Undo().Ok);
        Assert.False(store.Undo().Ok);
    }


    /// <summary>组名查找必须一个口径。原先 <c>GetGroup</c>/<c>GroupNameExists</c> 忽略大小写，
    /// 而 <c>ApplyToGroup</c>/<c>DeleteGroup</c>/<c>RenameGroup</c> 按 Ordinal —— 于是把组
    /// <c>Armor</c> 重命名成 <c>ARMOR</c>（允许，因为重名检查排除了自身）之后，预设里那句
    /// <c>Group="Armor"</c> 一声不响地一条都不动，而界面还按 GetGroup 打出成员数，看着像执行过了。</summary>
    [Fact]
    public void ApplyToGroupFollowsTheSameCaseRuleAsLookup()
    {
        var store = new GroupStore();
        store.NewGroup("Armor");
        Assert.True(store.RenameGroup("Armor", "ARMOR").Ok);

        var applied = store.ApplyToGroup("Armor", new[] { "Bikini" }, add: true);

        Assert.Equal(1, applied);
        Assert.Equal(["Bikini"], store.GetGroup("armor")!.Members);
    }

    [Fact]
    public void DeleteAndRenameFindTheGroupRegardlessOfCase()
    {
        var store = new GroupStore();
        store.NewGroup("3BA");
        Assert.True(store.DeleteGroup("3ba"));
        Assert.Equal(0, store.Count);
    }
}
