using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SliderSorter.Core;
using SliderSorter.Wpf.ViewModels;
using Xunit;

namespace SliderSorter.Wpf.Tests;

/// <summary>「重新扫描」必须每次真的扫一遍。
/// <para>
/// 钉住的是真实故障：工具条那颗按钮绑的是 <c>DetectBodySlideCommand</c>，而它只做"探测 → 把结果
/// 赋给 <see cref="MainViewModel.SelectedBodySlide"/>"，扫描挂在属性的 changed 钩子上。
/// <see cref="BodySlideCandidate"/> 是位置 record，而探测会把"上次用的那个目录"作为第一条候选返回、
/// 连来源标签都相同 —— 于是第二次起点上属性值逐字段相等，setter 判定"没变"，钩子不跑，
/// 按钮成了纯 no-op，而检测遮罩照旧闪一下，看着像扫过了。同一实例内切 Profile 走的是同一条路：
/// BodySlide 目录通常没变，界面就一直停在旧 Profile 的树与冲突清单上。
/// </para>
/// 用例造一个真能探测到 BodySlide 的最小 MO2 实例，点按钮数扫描次数——不靠反射、不改可见性。
/// </summary>
[Collection(WpfStaCollection.Name)]
public class RescanTriggerTests
{
    /// <summary>在常驻 UI 线程上等：只能泵消息队列，SpinWait 会把 Dispatcher 饿死
    /// （探测与扫描的续体都要回这条线程跑）。</summary>
    private static void PumpWait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.Background);
            Thread.Sleep(15);
        }
    }

    /// <summary>造一个最小但真实的 MO2 实例：ini + profile 的 modlist.txt + 装着 BodySlide 的模组。
    /// <see cref="BodySlideLocator.FindCandidates"/> 认的是"目录里有 BodySlide*.exe 与 Config.xml"，
    /// 所以这三样缺一样探测就找不到，用例也就测不到"值相等"那一条。</summary>
    private static string CreateInstanceWithBodySlide()
    {
        var root = Path.Combine(Mo2Discovery.GlobalRootOverride!, "Rescan Instance");
        var bodySlide = Path.Combine(root, "mods", "BodySlide Mod", "CalienteTools", "BodySlide");
        Directory.CreateDirectory(bodySlide);
        Directory.CreateDirectory(Path.Combine(bodySlide, "SliderSets"));
        File.WriteAllText(Path.Combine(bodySlide, "BodySlide x64.exe"), "");
        File.WriteAllText(Path.Combine(bodySlide, "Config.xml"),
            "<Config><General><TargetGame>2</TargetGame><GameDataPath/></General></Config>");

        var profile = Path.Combine(root, "profiles", "Default");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "+BodySlide Mod\n");

        File.WriteAllText(Path.Combine(root, "ModOrganizer.ini"),
            "[General]\ngameName=Skyrim Special Edition\nselected_profile=Default\n\n"
            + $"[Settings]\nbase_directory={root.Replace("\\", "\\\\")}\n");
        return root;
    }

    [Fact]
    public void EachRescanClickRunsAnotherScan()
    {
        using var scope = IsolatedUserState.Enter();
        CreateInstanceWithBodySlide();

        var (detectedDir, scans, instanceCount) = WpfHost.WithWindow(() => new Grid(), _ =>
        {
            var vm = new MainViewModel(); // 构造期就选中实例 → 探测 → 第一次扫描
            var started = 0;
            PropertyChangedEventHandler onData = (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsScanning) && vm.IsScanning)
                    started++;
            };
            vm.PropertyChanged += onData;
            PumpWait(() => !vm.IsBusy);

            Assert.True(started > 0, "实例选中后连第一次扫描都没跑，用例现场搭错了");
            var afterSetup = started;

            // 用户点两下「重新扫描」：两次探测回到的都是同一个 BodySlide 目录
            vm.DetectBodySlideCommand.Execute(null);
            PumpWait(() => !vm.IsBusy);
            vm.DetectBodySlideCommand.Execute(null);
            PumpWait(() => !vm.IsBusy);

            vm.PropertyChanged -= onData;
            return (vm.SelectedBodySlide?.AppDir, started - afterSetup, vm.Instances.Count);
        });

        Assert.Equal(1, instanceCount); // 造的实例被发现了（选不中实例就等于什么都没测到）
        Assert.NotNull(detectedDir);
        Assert.Equal(2, scans); // 两次点击 = 两次扫描；修复前这里只有 1（第二次是静默 no-op）
    }
}
