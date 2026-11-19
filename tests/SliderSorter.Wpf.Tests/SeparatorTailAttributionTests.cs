using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Controls;
using SliderSorter.Core;
using SliderSorter.Wpf.ViewModels;
using Xunit;

namespace SliderSorter.Wpf.Tests;

/// <summary>MO2 列表里**最后一个分隔器之后**的模组（用户没拖进任何分隔器、堆在列表末尾的那些）
/// 在本工具的树里会挂在最后一个分隔器名下——这不是实现失误，而是与 MO2 自身完全一致的唯一可判定语义：
/// <list type="number">
/// <item>MO2 2.4.4/2.5.x 的 <c>ModListByPriorityProxy::buildTree</c>（各版本逐字相同）按显示序迭代，
///       "最近遇到的分隔器"之后的模组一律成为其子节点——最后一个分隔器之下的尾部同样如此
///       （折叠该分隔器时尾部模组跟着隐藏，即 MO2 自己也把它们算作其成员）。</item>
/// <item><c>ModInfoSeparator</c> 没有任何独立的成员记录，成员关系纯靠 modlist 位置；
///       因此"最后一个分隔器的真成员"与"末尾未分组模组"在数据上**不可区分**——
///       想把尾部踢出去的任何按位置规则必然把真成员一并踢出，最后一个分组随之清空、
///       被清理逻辑整个删掉（试过：<c>below[i] = 文件序 i 之前还有 separator</c> 这条规则就是这样）。</item>
/// </list>
/// 用户侧的解法是在 MO2 里给未整理模组单独放一个分隔器（或移到列表顶部），README 已写明。
/// 本用例把这组契约钉住，防止将来有人再把"尾部踢出分组"当 bug 修一遍。走真实链路：
/// ParseContent → 注入 VM → RebuildTree。</summary>
[Collection(WpfStaCollection.Name)]
public class SeparatorTailAttributionTests
{
    private static readonly string[] AllOwners = ["TopMod", "yangjiMod", "MrMMod", "GambierMod", "TailMod"];

    /// <summary>modlist.txt 行序 = MO2 显示序的倒序（首行 = 界面最底部，见 ModListParser 注释；
    /// MO2 的 Profile::doWriteModlist 按优先级降序写文件）。所以**分隔器行排在其成员之后**。
    /// 显示序（倒过来读）：TopMod（顶部未分组）→ yangji[yangjiMod] → MrM[MrMMod]
    /// → GambierBay[GambierMod, TailMod]，其中 TailMod 在最后一个分隔器之后的列表末尾。</summary>
    private const string Modlist = """
        +TailMod
        +GambierMod
        +GambierBay_separator
        +MrMMod
        +MrM_separator
        +yangjiMod
        +yangji_separator
        +TopMod
        """;

    [Fact]
    public void TreeMirrorsMo2SeparatorSemantics()
    {
        using var scope = IsolatedUserState.Enter();
        var root = Path.Combine(Path.GetTempPath(), "ss-tail-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mods = Path.Combine(root, "mods");
            foreach (var name in AllOwners)
                Directory.CreateDirectory(Path.Combine(mods, name));

            var vm = WpfHost.WithWindow(() => new Grid(), _ =>
            {
                var m = BuildVm(root, mods);
                m.RebuildTree();
                return m;
            });

            // 分隔器之间的模组归上方最近的分隔器；首个分隔器之前的未分组模组留在根部
            var yangji = vm.TreeRoots.OfType<SeparatorNodeVM>().Single(s => s.Text == "yangji");
            Assert.Contains(yangji.Children, c => c is ModNodeVM m && m.Owner == "yangjiMod");
            var rootMods = vm.TreeRoots.OfType<ModNodeVM>().ToList();
            Assert.True(rootMods.Select(m => m.Owner).SequenceEqual(["TopMod"]),
                $"实际树：{Dump(vm.TreeRoots)}");

            // 尾部模组跟随 MO2 的规则归入最后一个分隔器（见类注释：这是唯一可判定的语义）
            var gambier = vm.TreeRoots.OfType<SeparatorNodeVM>().Single(s => s.Text == "GambierBay");
            Assert.True(gambier.Children.OfType<ModNodeVM>().Select(m => m.Owner)
                    .SequenceEqual(["GambierMod", "TailMod"]),
                $"实际树：{Dump(vm.TreeRoots)}");

            // 同一套归属也进「新装模组提醒」弹窗的数据源
            var structure = vm.GetTreeDisplayStructure();
            Assert.Equal("GambierBay", structure.Single(e => e.Owner == "TailMod").Separator);
            Assert.Equal("GambierBay", structure.Single(e => e.Owner == "GambierMod").Separator);
            Assert.Null(structure.Single(e => e.Owner == "TopMod").Separator);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* 温盘上偶尔被占用，不影响判定 */ }
        }
    }

    private static string Dump(ObservableCollection<NodeVM> roots) => string.Join(" | ", roots.Select(n =>
        n is SeparatorNodeVM s
            ? $"{s.Text}[{string.Join(",", s.Children.OfType<ModNodeVM>().Select(c => c.Owner))}]"
            : n is ModNodeVM m ? m.Owner : n.Text));

    private static MainViewModel BuildVm(string root, string mods)
    {
        var vm = new MainViewModel();
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.Entries))!.SetValue(
            vm, ModListParser.ParseContent(Modlist));
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.Resolution))!.SetValue(
            vm, new ProjectPathResolution
            {
                EffectivePath = Path.Combine(root, "bs"),
                Kind = ProjectPathKind.GameDataCalienteTools,
                GameDataPath = Path.Combine(root, "data"),
            });
        typeof(MainViewModel).GetField("_selectedInstance",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(
            vm, new Mo2Instance(root, Path.Combine(root, "ModOrganizer.ini"), Mo2InstanceKind.Portable));

        var scan = new ScanResult();
        foreach (var owner in AllOwners)
            scan.Outfits.Add(new OutfitEntry
            {
                Name = $"BSGG_{owner}_A",
                OwnerLabel = owner,
                SourceFile = "test.xml",
            });
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.Scan))!.SetValue(vm, scan);
        return vm;
    }
}
