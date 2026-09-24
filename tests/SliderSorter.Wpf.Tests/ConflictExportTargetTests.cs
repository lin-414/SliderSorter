using System.IO;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;
using SliderSorter.Core;
using SliderSorter.Wpf.ViewModels;
using Xunit;

namespace SliderSorter.Wpf.Tests;

/// <summary>
/// 「写入 BodySlide…」的落点：端到端钉住"落在输出位置那个模组里、与分组文件同一个 BodySlide 目录"。
/// <para>
/// 起因是真实故障：导出原先写进所选 BodySlide 安装的<b>真实</b>目录，而 MO2 下 BodySlide 眼里的程序目录
/// 是虚拟 Data 里的 <c>CalienteTools\BodySlide</c>（usvfs 把 <c>GetModuleFileNameW</c> 改写成虚拟路径），
/// 落到哪个模组由 MO2 优先级决定——于是写进真实目录那份被优先级更高的模组挡住，
/// 新导出的选择 BodySlide 一直读不到。
/// </para>
/// 用例现场搭一个最小实例：BodySlide（带 SliderSets）+ 两个模组各出一个 set 写同一个输出文件，
/// 再走真 VM 的扫描 → 冲突清单 → 导出整条链，最后看文件落在哪儿。
/// </summary>
[Collection(WpfStaCollection.Name)]
public class ConflictExportTargetTests
{
    private const string OutputPath = @"meshes\actors\character\character assets\clothes\bikini";

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

    private static (string ModsDir, string BodySlideDir) CreateInstance()
    {
        var root = Path.Combine(Mo2Discovery.GlobalRootOverride!, "Export Instance");
        var modsDir = Path.Combine(root, "mods");
        var gameData = Directory.CreateDirectory(Path.Combine(root, "Game", "Data")).FullName;

        var bsDir = Path.Combine(modsDir, "BodySlide Mod", "CalienteTools", "BodySlide");
        Directory.CreateDirectory(Path.Combine(bsDir, "SliderSets"));
        File.WriteAllText(Path.Combine(bsDir, "BodySlide x64.exe"), "");
        File.WriteAllText(Path.Combine(bsDir, "Config.xml"),
            $"<Config><TargetGame>4</TargetGame><GameDataPath>{gameData}</GameDataPath></Config>");

        // 两个模组各出一个 set、声明同一个输出文件：跨模组冲突，页面里选得出赢家
        WriteSet(modsDir, "Mod A", "A Set");
        WriteSet(modsDir, "Mod B", "B Set");

        var profile = Path.Combine(root, "profiles", "Default");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "+Mod A\n+Mod B\n+BodySlide Mod\n");
        File.WriteAllText(Path.Combine(root, "ModOrganizer.ini"),
            "[General]\ngameName=Skyrim Special Edition\nselected_profile=Default\n"
            + $"gamePath={Path.Combine(root, "Game")}\n"
            + "\n[Settings]\n"
            + $"base_directory={root.Replace("\\", "\\\\")}\n");
        return (modsDir, bsDir);
    }

    private static void WriteSet(string modsDir, string mod, string setName)
    {
        var dir = Path.Combine(modsDir, mod, "CalienteTools", "BodySlide", "SliderSets");
        Directory.CreateDirectory(dir);
        // 文件名按模组区分：同名文件会被 VFS 覆盖掉整个文件（强的赢），两个 set 就只剩一个了
        File.WriteAllText(Path.Combine(dir, mod.Replace(" ", "") + ".xml"),
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<SliderSetInfo version=\"2\">\n"
            + $"<SliderSet name=\"{setName}\"><OutputPath>{OutputPath}</OutputPath><Target file=\"body.nif\"/></SliderSet>\n"
            + "</SliderSetInfo>\n");
    }

    [Fact]
    public void ExportWritesIntoTheOutputModNextToTheGroupFiles()
    {
        using var scope = IsolatedUserState.Enter();
        var (modsDir, bsDir) = CreateInstance();
        // 共享设置实例跨用例存活（IsolatedUserState 只换目录，不重建实例）：改完还回去，
        // 免得住后的用例读到一个别人留下的写入模式
        var previousMode = AppSettings.Shared.WriteMode;
        try
        {
            AppSettings.Shared.WriteMode = WriteMode.Mo2Mod;
            AppSettings.Shared.OutputChoices[OutputPath] = "A Set";

            var expected = Path.Combine(modsDir, OutputTarget.DedicatedModName,
                "CalienteTools", "BodySlide", "BuildSelection.xml");
            var legacy = Path.Combine(bsDir, "BuildSelection.xml");

            WpfHost.WithWindow(() => new Grid(), _ =>
            {
                var vm = new MainViewModel();
                PumpWait(() => !vm.IsBusy);
                PumpWait(() => vm.CurrentConflict is not null);

                var request = vm.CurrentConflict;
                Assert.NotNull(request);
                // 现场没搭出冲突的话，下面那条断言就成了空转
                Assert.True(request!.Groups.Count == 1,
                    $"outfits={vm.Scan?.Outfits.Count}, groups={vm.ConflictGroups.Count}, "
                    + $"bs={vm.SelectedBodySlide?.AppDir}, inst={vm.SelectedInstance?.InstanceDir}, "
                    + $"mods={vm.Mods.Count}, notes={string.Join(" | ", vm.Scan?.LayerNotes ?? [])}");
                Assert.Equal(expected, request.BuildSelectionPath());

                var (ok, message) = request.Export();
                Assert.True(ok, message);
                return 0;
            });

            Assert.True(File.Exists(expected), $"导出没有落到输出位置那个模组里：{expected}");
            Assert.Contains("choice=\"A Set\"", File.ReadAllText(expected));
            Assert.False(File.Exists(legacy), "不该再写进所选 BodySlide 安装的真实目录");
        }
        finally
        {
            AppSettings.Shared.WriteMode = previousMode;
            AppSettings.Shared.OutputChoices.Remove(OutputPath);
        }
    }
}
