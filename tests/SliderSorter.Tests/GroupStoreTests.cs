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

    /// <summary>撤销栈攒满 30 步之后，新快照必须进得去、且每次撤销恰好回退一步。
    /// 原先的 Stack 写法超容量时弹掉的是**刚压入的最新快照**：栈被冻结在前 30 步，
    /// 一次撤销静默回跳几十步，中间的修改既回不去也重做不了（丢用户工作）。</summary>
    [Fact]
    public void UndoStepsBackExactlyOneChangeAfterCapacityIsReached()
    {
        var store = new GroupStore();
        store.NewGroup("UBE");
        for (var i = 0; i < 40; i++)
            store.ApplyToCurrent(new[] { $"Outfit{i}" }, add: true);

        // 第 40 次搬运之后按撤销：只允许丢掉第 40 次，前 39 件必须还在
        Assert.True(store.Undo().Ok);
        Assert.Equal(39, store.Current!.Members.Count);
    }

    /// <summary>重新扫描载入后撤销栈必须清空：留着旧快照的话，新清单上按一次撤销
    /// 会把上一轮的组整个恢复回来并置脏——状态穿越且无提示。</summary>
    [Fact]
    public void LoadClearsUndoStack()
    {
        var store = new GroupStore();
        store.NewGroup("Temp");
        Assert.True(store.CanUndo);

        store.Load(new List<SliderGroup> { new("Loaded", new[] { "X" }) });
        Assert.False(store.CanUndo);
        Assert.False(store.Undo().Ok);
        Assert.NotNull(store.GetGroup("Loaded"));
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

    /// <summary>移动 = 目标补上（去重）+ 来源移除。目标已有的服装不能再添一份，
    /// 但仍要从来源拿走——"移动到 B"的终态是"只在 B"，不是"两边各一份"。</summary>
    [Fact]
    public void MoveMembersAddsToTargetAndRemovesFromSource()
    {
        var store = new GroupStore();
        store.NewGroup("A");
        store.NewGroup("B");
        store.ApplyToGroup("A", new[] { "X", "Y" }, add: true);
        store.ApplyToGroup("B", new[] { "Y" }, add: true); // Y 两边都有：搬过去只该从 A 消失

        var moved = store.MoveMembers("A", "B", new[] { "X", "Y" });

        Assert.Equal(2, moved); // 两个都算移动过：都离开了 A
        Assert.Empty(store.GetGroup("A")!.Members);
        Assert.Equal(new[] { "Y", "X" }, store.GetGroup("B")!.Members); // 已有的 Y 没有重复入列
    }

    /// <summary>一次移动只占一步撤销：来自成员窗口的一颗按钮，不该拆成"目标加了""本组少了"两截。</summary>
    [Fact]
    public void MoveMembersIsExactlyOneUndoStep()
    {
        // Load 起步：撤销栈清空、Dirty 复位，后面数快照才数得清（NewGroup 自己也要压一步）
        var store = new GroupStore();
        store.Load(new List<SliderGroup> { new("A", new[] { "X" }), new("B") });

        store.MoveMembers("A", "B", new[] { "X" });
        Assert.True(store.Undo().Ok);
        Assert.Equal(new[] { "X" }, store.GetGroup("A")!.Members);
        Assert.Empty(store.GetGroup("B")!.Members);
        Assert.False(store.Undo().Ok);
    }

    /// <summary>找不到组（或 from、to 同组）必须显式失败返回 -1，而不是静默成功让界面白刷一遍。</summary>
    [Fact]
    public void MoveMembersRejectsMissingOrSameGroup()
    {
        var store = new GroupStore();
        store.Load(new List<SliderGroup> { new("A", new[] { "X" }) });

        Assert.Equal(-1, store.MoveMembers("A", "不存在的组", new[] { "X" }));
        Assert.Equal(-1, store.MoveMembers("不存在的组", "A", new[] { "X" }));
        Assert.Equal(-1, store.MoveMembers("A", "a", new[] { "X" })); // 忽略大小写后是同一个组
        Assert.Equal(new[] { "X" }, store.GetGroup("A")!.Members);
        Assert.False(store.Dirty); // 什么都没动就别置脏
    }

    // ── 空操作不占撤销步、不置脏 ──────────────────────────────────────
    // 把已在组内的服装再"加入"一遍、或把不在组内的再"移出"一遍，组表原样：
    // 不得出现"未保存"、不得空占一步撤销（撤销按下去像没反应的那种）。

    [Fact]
    public void NoOpApplyTakesNoUndoStepAndStaysClean()
    {
        var store = new GroupStore();
        store.Load(new List<SliderGroup> { new("UBE", new[] { "A" }) });

        var addAgain = store.ApplyToCurrent(new[] { "A" }, add: true);      // 已在组内
        var removeAbsent = store.ApplyToCurrent(new[] { "B" }, add: false); // 不在组内

        Assert.Equal((true, null, 0), addAgain);
        Assert.Equal((true, null, 0), removeAbsent);
        Assert.False(store.Dirty);
        Assert.False(store.CanUndo);
        Assert.Equal(new[] { "A" }, store.Current!.Members);
    }

    /// <summary>部分重叠的搬运按实际生效数记账：5 个勾选里 2 个已在组内，只新增 3 个。</summary>
    [Fact]
    public void ApplyToCurrentCountsOnlyActualChanges()
    {
        var store = new GroupStore();
        store.Load(new List<SliderGroup> { new("UBE", new[] { "A", "B" }) });

        var (_, _, applied) = store.ApplyToCurrent(new[] { "A", "C", "D", "E", "F" }, add: true);

        Assert.Equal(4, applied);
        Assert.Equal(new[] { "A", "B", "C", "D", "E", "F" }, store.Current!.Members);
    }

    /// <summary>规则归组 0 命中不占撤销步：按预设批量跑一轮，没命中的规则不该各空占一步。</summary>
    [Fact]
    public void NoOpRuleApplyTakesNoUndoStep()
    {
        // Load 起步：Dirty=false、撤销栈空，后面"没被空操作弄脏"才断言得出来
        var store = new GroupStore();
        store.Load(new List<SliderGroup> { new("UBE") });

        Assert.Equal(0, store.ApplyToGroup("UBE", new[] { "A" }, add: false)); // A 不在组内：移除 0 命中
        Assert.False(store.Dirty);
        Assert.False(store.CanUndo);

        store.ApplyToGroup("UBE", new[] { "A" }, add: true); // 真添加
        Assert.Equal(0, store.ApplyToGroup("UBE", new[] { "A" }, add: true)); // 已在组内：再加 0 命中
        Assert.Equal(0, store.ApplyToGroup("UBE", new[] { "B" }, add: false)); // B 不在组内：移除 0 命中
        Assert.True(store.Dirty);
        Assert.Equal(1, StoreUndoDepth(store)); // 只有添加 A 那一步，空操作都没占位
    }

    /// <summary>导入一堆与现状完全重复的条目：组表原样，撤销栈不动。</summary>
    [Fact]
    public void ImportOfNothingNewTakesNoUndoStep()
    {
        var store = new GroupStore();
        store.Load(new List<SliderGroup> { new("UBE", new[] { "A", "B" }) });

        var (addedGroups, addedMembers) = store.Import(new[]
        {
            new SliderGroup("UBE", new[] { "A", "B" }),   // 完全重复
            new SliderGroup("ube", new[] { "A" }),        // 忽略大小写后同名同成员
        });

        Assert.Equal((0, 0), (addedGroups, addedMembers));
        Assert.False(store.Dirty);
        Assert.False(store.CanUndo);
    }

    /// <summary>移动两侧都不涉及的名字（from 没有、to 已有）是纯空操作；
    /// 而"往 to 补了 from 里没有的名字"必须算变更——早先按 moved（只数从 from 移除的）
    /// 判脏，这种补入会漏置脏、成员缓存也不失效。</summary>
    [Fact]
    public void MoveMembersDirtyFollowsActualChange()
    {
        var store = new GroupStore();
        store.Load(new List<SliderGroup>
        {
            new("A", new[] { "X" }),
            new("B", new[] { "Y" }),
        });
        Assert.False(store.Dirty);

        // Y：to 已有、from 没有 → 什么都不变
        Assert.Equal(0, store.MoveMembers("A", "B", new[] { "Y" }));
        Assert.False(store.Dirty);
        Assert.False(store.CanUndo);

        // Z：两边都没有 → 会补进 to（moved 记 0，但组表变了，必须置脏并占一步撤销）
        Assert.Equal(0, store.MoveMembers("A", "B", new[] { "Z" }));
        Assert.True(store.Dirty);
        Assert.True(store.CanUndo);
        Assert.Equal(new[] { "Y", "Z" }, store.GetGroup("B")!.Members);
        Assert.Equal(new[] { "X" }, store.GetGroup("A")!.Members);
    }

    /// <summary>撤销栈深度：Load 清栈后逐次压入，数 CanUndo 之前的撤销次数。
    /// Store 没有直接暴露栈深的口子，用"撤到不能撤"计数。</summary>
    private static int StoreUndoDepth(GroupStore store)
    {
        var depth = 0;
        while (store.Undo().Ok)
            depth++;
        return depth;
    }
}
