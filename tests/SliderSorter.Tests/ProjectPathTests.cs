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
