using Xunit;
using SliderSorter.Core;

namespace SliderSorter.Tests;

public class ProjectPathTests
{
    private static BodySlideConfig WriteConfig(TempDir temp, string gameDataPath, string projectPath = "")
    {
        var configXml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Config>
                <TargetGame>4</TargetGame>
                <GameDataPath>{gameDataPath}</GameDataPath>
                <ProjectPath>{projectPath}</ProjectPath>
            </Config>
            """;
        var path = temp.File("BS", "Config.xml", configXml);
        return new BodySlideConfig(path);
    }

    private static List<(ModEntry, string)> Mods(params string[] dirs) =>
        dirs.Select((d, i) => (new ModEntry($"Mod{i}", true, false, false, i), d)).ToList();

    [Fact]
    public void AppDirWithSliderSetsWinsOverEverything()
    {
        using var temp = new TempDir();
        var appDir = temp.Sub("BS");
        temp.File("BS", "SliderSets", "CBBE.xml", "<SliderSetInfo><SliderSet name=\"CBBE\"/></SliderSetInfo>");
        var gameData = temp.Sub("Data");
        Directory.CreateDirectory(System.IO.Path.Combine(gameData, "CalienteTools", "BodySlide"));
        var config = WriteConfig(temp, gameData, projectPath: @"C:\Elsewhere");

        var res = BodySlideLocator.ResolveProjectPath(config, appDir, Mods(), mo2GamePath: null);

        Assert.Equal(ProjectPathKind.AppDir, res.Kind);
        Assert.Equal(appDir, res.EffectivePath);
    }

    /// <summary>模组托管的 BodySlide（exe 所在模组自带非空 SliderSets）不能把有效项目路径钉在
    /// 该模组的真实目录上：BodySlide 经 MO2（usvfs）启动时 AppDir 是虚拟 Data 下的同相对路径目录，
    /// 那里是全部模组按优先级合并的视图——别的模组的滑块组它读得到，单目录扫描会全体漏掉。
    /// 命中托管布局且合并视图里有滑块组 → 按虚拟路径分层扫描。</summary>
    [Fact]
    public void ModHostedBodySlideWithOwnSliderSetsScansTheMergedView()
    {
        using var temp = new TempDir();
        var gameData = temp.Sub("Data"); // 真实磁盘上没有 CalienteTools
        var bsMod = temp.Sub("mods", "BOS");
        temp.File("mods", "BOS", "CalienteTools", "BodySlide", "SliderSets", "BOS.xml",
            "<SliderSetInfo version=\"2\"><SliderSet name=\"BosSet\"/></SliderSetInfo>");
        var outfitMod = temp.Sub("mods", "CBBE");
        temp.File("mods", "CBBE", "CalienteTools", "BodySlide", "SliderSets", "CBBE.xml",
            "<SliderSetInfo version=\"2\"><SliderSet name=\"CbbeSet\"/></SliderSetInfo>");
        var config = WriteConfig(temp, gameData);
        var appDir = System.IO.Path.Combine(bsMod, "CalienteTools", "BodySlide");

        var res = BodySlideLocator.ResolveProjectPath(config, appDir, Mods(bsMod, outfitMod), mo2GamePath: null);

        Assert.Equal(ProjectPathKind.GameDataCalienteTools, res.Kind);
        Assert.Equal(System.IO.Path.Combine(gameData, "CalienteTools", "BodySlide"), res.EffectivePath);
        Assert.Contains("L.Core_StepVirtualAppDirFound", res.Steps);
    }

    /// <summary>托管布局但合并视图里一个滑块组都没有（FOMOD 留空目录的老场景）：不命中虚拟
    /// AppDir，按候选顺序回落——宿主模组提供的目录仍然存在，最终落点相同。</summary>
    [Fact]
    public void ModHostedBodySlideWithEmptyMergedViewFallsThrough()
    {
        using var temp = new TempDir();
        var gameData = temp.Sub("Data");
        var bsMod = temp.Sub("mods", "BOS");
        Directory.CreateDirectory(System.IO.Path.Combine(bsMod, "CalienteTools", "BodySlide", "SliderSets")); // 空目录
        var config = WriteConfig(temp, gameData);
        var appDir = System.IO.Path.Combine(bsMod, "CalienteTools", "BodySlide");

        var res = BodySlideLocator.ResolveProjectPath(config, appDir, Mods(bsMod), mo2GamePath: null);

        Assert.Equal(ProjectPathKind.GameDataCalienteTools, res.Kind);
        Assert.Equal(System.IO.Path.Combine(gameData, "CalienteTools", "BodySlide"), res.EffectivePath);
        Assert.Contains("L.Core_StepVirtualAppDirEmpty", res.Steps);
    }

    /// <summary>没有装载任何模组表（纯 standalone）时不走托管判断：虚拟视图就是真实磁盘，
    /// AppDir 命中必须原样保留（否则状态栏的写盘描述会凭空变成"游戏 Data"）。</summary>
    [Fact]
    public void StandaloneBodySlideKeepsTheAppDirShortcut()
    {
        using var temp = new TempDir();
        var appDir = temp.Sub("BS");
        temp.File("BS", "SliderSets", "CBBE.xml", "<SliderSetInfo><SliderSet name=\"CBBE\"/></SliderSetInfo>");
        var gameData = temp.Sub("Data");
        var config = WriteConfig(temp, gameData);

        var res = BodySlideLocator.ResolveProjectPath(config, appDir, Mods(), mo2GamePath: null);

        Assert.Equal(ProjectPathKind.AppDir, res.Kind);
        Assert.Equal(appDir, res.EffectivePath);
    }

    [Fact]
    public void EmptyAppDirSliderSetsFallsThroughToModProvidedVirtualPath()
    {
        // 真实场景：FOMOD 安装留下空的 SliderSets 目录，服装实际由模组经虚拟 Data 提供
        using var temp = new TempDir();
        var appDir = temp.Sub("BS");
        Directory.CreateDirectory(System.IO.Path.Combine(appDir, "SliderSets")); // 空目录
        var gameData = temp.Sub("Data"); // 真实磁盘上没有 CalienteTools
        var modDir = temp.Sub("mods", "CBBE");
        Directory.CreateDirectory(System.IO.Path.Combine(modDir, "CalienteTools", "BodySlide"));
        var config = WriteConfig(temp, gameData);

        var res = BodySlideLocator.ResolveProjectPath(config, appDir, Mods(modDir), mo2GamePath: null);

        Assert.Equal(ProjectPathKind.GameDataCalienteTools, res.Kind);
        Assert.Equal(System.IO.Path.Combine(gameData, "CalienteTools", "BodySlide"), res.EffectivePath);
    }

    [Fact]
    public void FallsBackToVirtualCalienteToolsProvidedByMod()
    {
        using var temp = new TempDir();
        var appDir = temp.Sub("BS"); // 没有 SliderSets
        var gameData = temp.Sub("Data"); // 真实磁盘上没有 CalienteTools
        var modDir = temp.Sub("mods", "CBBE");
        Directory.CreateDirectory(System.IO.Path.Combine(modDir, "CalienteTools", "BodySlide"));
        var config = WriteConfig(temp, gameData);

        var res = BodySlideLocator.ResolveProjectPath(config, appDir, Mods(modDir), mo2GamePath: null);

        Assert.Equal(ProjectPathKind.GameDataCalienteTools, res.Kind);
        Assert.Equal(System.IO.Path.Combine(gameData, "CalienteTools", "BodySlide"), res.EffectivePath);
    }

    [Fact]
    public void RealGameDataCalienteToolsIsUsedWhenPresent()
    {
        using var temp = new TempDir();
        var appDir = temp.Sub("BS");
        var gameData = temp.Sub("Data");
        var expected = System.IO.Path.Combine(gameData, "CalienteTools", "BodySlide");
        Directory.CreateDirectory(expected);
        var config = WriteConfig(temp, gameData);

        var res = BodySlideLocator.ResolveProjectPath(config, appDir, Mods(), mo2GamePath: null);

        Assert.Equal(ProjectPathKind.GameDataCalienteTools, res.Kind);
        Assert.Equal(expected, res.EffectivePath);
    }

    [Fact]
    public void CustomProjectPathUsedBeforeGameData()
    {
        using var temp = new TempDir();
        var appDir = temp.Sub("BS");
        var custom = temp.Sub("CustomProject");
        var gameData = temp.Sub("Data");
        var config = WriteConfig(temp, gameData, projectPath: custom);

        var res = BodySlideLocator.ResolveProjectPath(config, appDir, Mods(), mo2GamePath: null);

        Assert.Equal(ProjectPathKind.Custom, res.Kind);
        Assert.Equal(custom, res.EffectivePath);
    }

    [Fact]
    public void FallsBackToAppDirWhenNothingExists()
    {
        using var temp = new TempDir();
        var appDir = temp.Sub("BS");
        var gameData = temp.Sub("Data");
        var config = WriteConfig(temp, gameData);

        var res = BodySlideLocator.ResolveProjectPath(config, appDir, Mods(), mo2GamePath: null);

        Assert.Equal(ProjectPathKind.Fallback, res.Kind);
        Assert.Equal(appDir, res.EffectivePath);
    }

    [Fact]
    public void UsesMo2GameDataWhenConfigEmpty()
    {
        using var temp = new TempDir();
        var appDir = temp.Sub("BS");
        var gameRoot = temp.Sub("Game");
        var modDir = temp.Sub("mods", "CBBE");
        Directory.CreateDirectory(System.IO.Path.Combine(modDir, "CalienteTools", "BodySlide"));
        var config = WriteConfig(temp, gameDataPath: ""); // Config.xml 没记录

        var res = BodySlideLocator.ResolveProjectPath(config, appDir, Mods(modDir), mo2GamePath: gameRoot);

        Assert.True(res.GameDataPathFromMo2);
        Assert.Equal(System.IO.Path.Combine(gameRoot, "Data"), res.GameDataPath);
        Assert.Equal(ProjectPathKind.GameDataCalienteTools, res.Kind);
    }

    [Fact]
    public void TargetGameNameMapping()
    {
        using var temp = new TempDir();
        var config = WriteConfig(temp, gameDataPath: "");
        Assert.Equal("SkyrimSpecialEdition", config.TargetGameName); // TargetGame=4
    }
}
