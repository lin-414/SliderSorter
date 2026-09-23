using System.IO;
using SliderSorter.Core;
using SliderSorter.Wpf.ViewModels;
using Xunit;

namespace SliderSorter.Wpf.Tests;

/// <summary>「加入分组」后左侧列表的展开/折叠状态（端到端回归用例）。
///
/// 走的是真实按钮链路：<c>ApplyCheckedToCurrentGroupCommand</c> → Store → <c>RefreshTree()</c>，
/// 而 RefreshTree 会**重建整棵树**——用户就是在这条路径上发现"所有节点都被展开了"。
/// 断言覆盖两头：展开过的保持展开、用户手动折叠过的保持折叠（只查前者的话，把"全展开"
/// 当实现也能过）。
///
/// 挂 <see cref="WpfStaCollection"/> 是为串行化：VM 构造会读用户设置；本用例已把设置目录与
/// MO2 全局实例根重定向到临时目录（见 <see cref="IsolatedUserState"/>），不再改动真实
/// settings.json，也不会因为机器上装了 MO2 而真的去扫一遍。</summary>
[Collection(WpfStaCollection.Name)]
public class AddToGroupExpandStateTests
{
    [Fact]
    public void AddToGroup_KeepsExpandedAndManuallyCollapsedNodesAsIs()
    {
        using var scope = IsolatedUserState.Enter();
        var vm = new MainViewModel();
        WaitUntilIdle(vm);
        InjectFakeScan(vm, "M1", "M2", "M3");
        vm.RebuildTree();

        // 用户的动作：展开 M2 挑服装；展开过 M1 又手动折了回去
        Mod(vm, "M2").IsExpanded = true;
        Mod(vm, "M1").IsExpanded = true;
        Mod(vm, "M1").IsExpanded = false;
        Mod(vm, "M2").IsChecked = true; // 勾上 M2 下全部服装

        var (ok, error) = vm.Store.NewGroup("BSGG_Test_Group");
        Assert.True(ok, error);
        Assert.Equal("BSGG_Test_Group", vm.Store.Current!.Name);

        vm.ApplyCheckedToCurrentGroupCommand.Execute("add");

        Assert.Equal(["BSGG_M2_A", "BSGG_M2_B"],
            vm.Store.Current!.Members.OrderBy(m => m, StringComparer.Ordinal));

        // 重建后拿到的是新节点对象：按 Owner 重新定位
        Assert.True(Mod(vm, "M2").IsExpanded);            // 展开的仍展开
        Assert.False(Mod(vm, "M1").IsExpanded);           // 手动折叠的仍折叠（修复前这里是 true）
        Assert.False(Mod(vm, "M3").IsExpanded);
        Assert.Equal(1, vm.TreeRoots.Count(n => n.IsExpanded));

        // 设置落在隔离目录（VM 的 Settings 就是共享实例，Save 也走同一份覆盖）
        var marker = scope.MarkSaved();
        Assert.True(File.Exists(scope.SettingsFile), "隔离目录下没有生成 settings.json");
        Assert.Contains(marker, File.ReadAllText(scope.SettingsFile));
    }

    private static ModNodeVM Mod(MainViewModel vm, string owner) =>
        vm.TreeRoots.OfType<ModNodeVM>().Single(n => n.Owner == owner);

    /// <summary>造一份假扫描结果注入 VM。Scan 是公开属性但 setter 私有——真实扫描要磁盘上
    /// 有 BodySlide 目录，本用例只关心重建树时的展开状态，所以反射注入。
    /// 服装名带 BSGG_ 前缀，避免与用户真实分组里的名字相撞。</summary>
    private static void InjectFakeScan(MainViewModel vm, params string[] owners)
    {
        var scan = new ScanResult();
        foreach (var owner in owners)
            foreach (var suffix in new[] { "A", "B" })
                scan.Outfits.Add(new OutfitEntry
                {
                    Name = $"BSGG_{owner}_{suffix}",
                    OwnerLabel = owner,
                    SourceFile = "test.xml",
                });

        var property = typeof(MainViewModel).GetProperty(nameof(MainViewModel.Scan))!;
        property.SetValue(vm, scan);
    }

    /// <summary>等构造期那次真实扫描跑完再注入假数据：扫描结束时 ApplyScanOutcome → RefreshTree
    /// 会把 TreeRoots 换成真实数据，正在断言的树就没了。
    /// 连续两次观察到空闲才算结束——RunScanAsync 的 finally 里是先把 IsScanning 置 false 再补
    /// 排队的那一次（中间有个空窗）。没配 MO2 实例的机器上第一个观察就空闲，这里只花 150ms。</summary>
    private static void WaitUntilIdle(MainViewModel vm)
    {
        var deadline = Environment.TickCount64 + 30_000;
        var idleStreak = 0;
        while (Environment.TickCount64 < deadline && idleStreak < 2)
        {
            idleStreak = vm.IsDetecting || vm.IsScanning ? 0 : idleStreak + 1;
            Thread.Sleep(150);
        }
        Assert.True(idleStreak >= 2, "等待构造期自动扫描结束超时");
    }
}
