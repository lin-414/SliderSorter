using System.IO;
using SliderSorter.Core;
using SliderSorter.Wpf.ViewModels;
using Xunit;

namespace SliderSorter.Wpf.Tests;

/// <summary>
/// 写入目标的落点：分组文件与 <c>BuildSelection.xml</c> 的关系。
/// <para>
/// 这条关系是 BodySlide 定的：分组读 <c>ProjectUtil::GetProjectPath() + "/SliderGroups"</c>，
/// BuildSelection.xml 读 <c>Config["AppDir"] + "\BuildSelection.xml"</c>（<c>BodySlideApp.cpp:1196</c>），
/// 标准布局下就是同一个 BodySlide 目录里的兄弟。MO2 下那个目录还是<b>虚拟</b>的——usvfs 把
/// <c>GetModuleFileNameW</c> 改写成虚拟路径，于是读写都经虚拟 Data 落到某个模组上，谁赢由 MO2 优先级决定。
/// </para>
/// <para>
/// 所以 BuildSelection.xml 必须跟着「输出位置」写：写进所选 BodySlide 安装的<b>真实</b>目录，
/// 会被优先级更高的模组挡住——用户机上正是这样（旧输出模组里那份 BuildSelection.xml 一直赢，
/// 新导出的选择 BodySlide 根本读不到）。
/// </para>
/// </summary>
public class OutputTargetTests
{
    private const string BsDir = @"D:\Mods\BodySlide and Outfit Studio\CalienteTools\BodySlide";
    private const string GameData = @"D:\Game\Data";

    private static ProjectPathResolution Resolution(ProjectPathKind kind) => new()
    {
        EffectivePath = Path.Combine(GameData, "CalienteTools", "BodySlide"),
        Kind = kind,
        GameDataPath = GameData,
    };

    /// <summary>一个能算出 mods 目录的 MO2 实例（ini 写在临时目录里，不碰用户真实安装）。</summary>
    private static Mo2Instance Instance(string root)
    {
        var mods = Directory.CreateDirectory(Path.Combine(root, "mods")).FullName;
        var ini = Path.Combine(root, "ModOrganizer.ini");
        File.WriteAllText(ini,
            "[General]\n" +
            "gameName=Skyrim Special Edition\n" +
            "[Settings]\n" +
            $"base_directory={root}\n" +
            $"mod_directory={mods}\n");
        return new Mo2Instance(root, ini, Mo2InstanceKind.Portable);
    }

    private static string ModBsFolder(Mo2Instance instance) =>
        Path.Combine(instance.ModsDirectory, OutputTarget.DedicatedModName, "CalienteTools", "BodySlide");

    private static string BsFileOf(OutputTarget target) => BuildSelectionFile.PathFor(target.BuildSelectionDir);

    [Fact]
    public void Mo2ModMode_WritesBuildSelectionIntoTheSameModAsTheGroups()
    {
        using var scope = IsolatedUserState.Enter();
        var instance = Instance(scope.Root);

        var target = OutputTarget.Resolve(Resolution(ProjectPathKind.GameDataCalienteTools), BsDir,
            instance, WriteMode.Mo2Mod, null);

        Assert.NotNull(target);
        Assert.Equal(Path.Combine(ModBsFolder(instance), "SliderGroups"), target!.Value.Dir);
        // 与分组文件同一个模组里的兄弟：MO2 下这就是 BodySlide 实际读到的那一份
        Assert.Equal(Path.Combine(ModBsFolder(instance), "BuildSelection.xml"), BsFileOf(target.Value));
        // 旧行为（写进所选 BodySlide 安装的真实目录）会被优先级更高的模组挡住
        Assert.NotEqual(Path.Combine(BsDir, "BuildSelection.xml"), BsFileOf(target.Value));
    }

    [Fact]
    public void AutoUnderVirtualData_PicksTheDedicatedModToo()
    {
        using var scope = IsolatedUserState.Enter();
        var instance = Instance(scope.Root);

        var target = OutputTarget.Resolve(Resolution(ProjectPathKind.GameDataCalienteTools), BsDir,
            instance, WriteMode.Auto, null);

        Assert.NotNull(target);
        Assert.Equal(Path.Combine(ModBsFolder(instance), "SliderGroups"), target!.Value.Dir);
        Assert.Equal(Path.Combine(ModBsFolder(instance), "BuildSelection.xml"), BsFileOf(target.Value));
    }

    [Theory]
    [InlineData(WriteMode.BodySlideDir, ProjectPathKind.AppDir)]
    [InlineData(WriteMode.RealGameData, ProjectPathKind.GameDataCalienteTools)]
    [InlineData(WriteMode.Auto, ProjectPathKind.AppDir)]
    public void StandardModes_PutBuildSelectionInTheFolderAboveSliderGroups(WriteMode mode, ProjectPathKind kind)
    {
        using var scope = IsolatedUserState.Enter();

        var target = OutputTarget.Resolve(Resolution(kind), BsDir, Instance(scope.Root), mode, null);

        Assert.NotNull(target);
        Assert.Equal("SliderGroups", Path.GetFileName(target!.Value.Dir));
        Assert.Equal(Path.GetDirectoryName(target.Value.Dir), target.Value.BuildSelectionDir);
        Assert.Equal(Path.Combine(target.Value.BuildSelectionDir, "BuildSelection.xml"), BsFileOf(target.Value));
    }

    [Fact]
    public void BodySlideDirMode_KeepsBothNextToTheProgramFolder()
    {
        using var scope = IsolatedUserState.Enter();

        var target = OutputTarget.Resolve(Resolution(ProjectPathKind.AppDir), BsDir,
            Instance(scope.Root), WriteMode.BodySlideDir, null);

        Assert.NotNull(target);
        Assert.Equal(Path.Combine(BsDir, "SliderGroups"), target!.Value.Dir);
        Assert.Equal(BsDir, target.Value.BuildSelectionDir);
    }

    [Fact]
    public void CustomMode_WritesBothIntoTheChosenFolder()
    {
        using var scope = IsolatedUserState.Enter();
        var custom = Path.Combine(scope.Root, "my groups");

        var target = OutputTarget.Resolve(Resolution(ProjectPathKind.AppDir), BsDir,
            Instance(scope.Root), WriteMode.Custom, custom);

        Assert.NotNull(target);
        // 自定义目录是"分组文件直接放在里面"的那种，没有可推断的 BodySlide 目录：两份产出同处一室
        Assert.Equal(custom, target!.Value.Dir);
        Assert.Equal(custom, target.Value.BuildSelectionDir);
    }

    [Fact]
    public void CustomWithoutDirectory_HasNoTarget()
    {
        using var scope = IsolatedUserState.Enter();

        Assert.Null(OutputTarget.Resolve(Resolution(ProjectPathKind.AppDir), BsDir,
            Instance(scope.Root), WriteMode.Custom, null));
    }

    [Fact]
    public void Mo2ModWithoutInstance_FallsBackToRealGameData()
    {
        using var scope = IsolatedUserState.Enter();

        var target = OutputTarget.Resolve(Resolution(ProjectPathKind.AppDir), BsDir,
            instance: null, WriteMode.Mo2Mod, null);

        Assert.NotNull(target);
        var bsFolder = Path.Combine(GameData, "CalienteTools", "BodySlide");
        Assert.Equal(Path.Combine(bsFolder, "SliderGroups"), target!.Value.Dir);
        Assert.Equal(Path.Combine(bsFolder, "BuildSelection.xml"), BsFileOf(target.Value));
    }
}
