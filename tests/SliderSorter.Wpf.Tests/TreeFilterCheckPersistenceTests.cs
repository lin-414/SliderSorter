using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SliderSorter.Core;
using SliderSorter.Wpf.ViewModels;
using Xunit;

namespace SliderSorter.Wpf.Tests;

/// <summary>在服装过滤框里打字，不该把刚勾好的勾选清掉。
/// <para>
/// 勾选原本只长在节点上，而过滤重建是整棵换新 <see cref="NodeVM"/>：用户"先勾一批、
/// 再打字找剩下的"，敲一个字勾选全灭，「添加勾选(N)」跟着归零。同一套树在新装模组弹窗里
/// 是保留勾选的（<c>NewModsFilterTests</c> 钉着那条契约），两处行为不一致。
/// </para>
/// 走真实链路：赋 <see cref="MainViewModel.FilterText"/> → 350ms 防抖 → RebuildTree。
/// 所以本用例必须跑在有 Dispatcher 同步上下文的线程上（防抖续体要投回去），见 WpfHost。</summary>
[Collection(WpfStaCollection.Name)]
public class TreeFilterCheckPersistenceTests
{
    [Fact]
    public void TypingInTheFilterKeepsCheckedOutfitsChecked()
    {
        using var scope = IsolatedUserState.Enter();

        var (before, after, visibleMods) = WpfHost.WithWindow(() => new Grid(), _ =>
        {
            var vm = new MainViewModel();
            InjectFakeScan(vm, "M1", "M2");
            vm.RebuildTree();

            // 用户动作：展开 M1 勾它两件，再展开 M2 勾一件
            foreach (var outfit in MaterializedOutfits(vm, "M1"))
                outfit.IsChecked = true;
            MaterializedOutfits(vm, "M2").First().IsChecked = true;
            var beforeCount = vm.CheckedOutfitCount;

            vm.FilterText = "M1"; // 只有 M1 的两件留着；M2 被滤掉
            PumpWait(() => vm.TreeRoots.OfType<ModNodeVM>().Count() == 1);

            return (beforeCount, vm.CheckedOutfitCount,
                vm.TreeRoots.OfType<ModNodeVM>().Select(m => m.Owner).ToList());
        });

        Assert.Equal(3, before);
        Assert.Equal(["M1"], visibleMods);
        // M2 那件被过滤掉了，剩下 M1 的两件必须还是勾着的（修复前这里是 0）
        Assert.Equal(2, after);
    }

    /// <summary>展开模组节点会物化出服装行（懒加载），勾选要落在真节点上。</summary>
    private static List<OutfitNodeVM> MaterializedOutfits(MainViewModel vm, string owner)
    {
        var mod = vm.TreeRoots.OfType<ModNodeVM>().Single(n => n.Owner == owner);
        mod.IsExpanded = true;
        return mod.Children.OfType<OutfitNodeVM>().ToList();
    }

    /// <summary>造一份假扫描结果注入 VM：真实扫描要磁盘上有 BodySlide 目录，
    /// 而这里只关心重建树时勾选怎么迁过去。服装名带 BSGG_ 前缀，避开用户真实分组里的名字。</summary>
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

        // Scan 的 setter 是私有的（只有扫描收尾那条路会写），测试按同一条路注入
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.Scan))!.SetValue(vm, scan);
    }

    /// <summary>在 UI 线程上等防抖那一次重建：只能泵消息队列，干等会把 Dispatcher 饿死。</summary>
    private static void PumpWait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.Background);
            Thread.Sleep(20);
        }
        Assert.True(condition(), "等到超时，过滤词的防抖重建没有发生");
    }
}
