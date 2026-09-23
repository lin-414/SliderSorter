using System.IO;
using SliderSorter.Core;
using SliderSorter.Wpf.ViewModels;
using Xunit;

namespace SliderSorter.Wpf.Tests;

/// <summary>「界面上的一次操作 = 撤销栈里的一步」。
///
/// 快照压在 ViewModel 侧（而不是 Store 内部）时，一次重命名/删除/导入会占掉两步撤销：
/// 用户按第二次撤销时状态没变化，看起来像"点了没反应"，再想撤销真正的上一步就得按第三次。
/// 这里直接数撤销步数（按 GroupStore.Undo() 的返回结构判断），不依赖界面文案。</summary>
[Collection(WpfStaCollection.Name)]
public class UndoStepTests
{
    [Fact]
    public void RenameTakesExactlyOneUndoStep()
    {
        using var scope = IsolatedUserState.Enter();
        var vm = new MainViewModel();
        vm.Store.Load(new List<SliderGroup> { new("Old", new[] { "A" }) });

        vm.RenameGroupCommand.Execute("New");
        Assert.Equal("New", vm.Store.Current!.Name);

        Assert.True(vm.Store.Undo().Ok);      // ① 回到改名之前
        Assert.Equal("Old", vm.Store.Current!.Name);
        Assert.False(vm.Store.CanUndo);
        // ② 栈已空：应当失败，而不是"成功退回同一个状态"（后者说明 VM 侧还多压了一次快照）
        Assert.False(vm.Store.Undo().Ok);
    }

    [Fact]
    public void DeleteTakesExactlyOneUndoStep()
    {
        using var scope = IsolatedUserState.Enter();
        var vm = new MainViewModel();
        vm.Store.Load(new List<SliderGroup> { new("A", new[] { "x" }), new("B", new[] { "y" }) });

        vm.DeleteGroupCommand.Execute(null);
        Assert.Equal(1, vm.Store.Count);

        Assert.True(vm.Store.Undo().Ok);
        Assert.Equal(2, vm.Store.Count);
        Assert.False(vm.Store.Undo().Ok);
    }

    [Fact]
    public void ImportTakesExactlyOneUndoStepPerCall()
    {
        using var scope = IsolatedUserState.Enter();
        var vm = new MainViewModel();

        var imported = Path.Combine(scope.Root, "imported.xml");
        SliderGroupFile.Save(imported, new[] { new SliderGroup("Imported", new[] { "x", "y" }) });

        vm.ImportFiles(new[] { imported });
        Assert.Equal(1, vm.Store.Count);

        Assert.True(vm.Store.Undo().Ok);   // 一次导入 = 一步撤销
        Assert.Equal(0, vm.Store.Count);
        Assert.False(vm.Store.Undo().Ok);
    }
}
