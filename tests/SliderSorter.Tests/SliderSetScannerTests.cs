using Xunit;
using SliderSorter.Core;

namespace SliderSorter.Tests;

public class SliderSetScannerTests
{
    private static ProjectPathResolution VirtualResolution(string gameData, string suffix) => new()
    {
        EffectivePath = System.IO.Path.Combine(gameData, suffix),
        Kind = ProjectPathKind.GameDataCalienteTools,
        GameDataPath = gameData,
    };

    private static string SliderSetXml(params string[] names) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<SliderSetInfo version=\"2\">\n" +
        string.Join("", names.Select(n => $"    <SliderSet name=\"{n}\">\n        <Mesh name=\"x\"/>\n    </SliderSet>\n")) +
        "</SliderSetInfo>\n";

    [Fact]
    public void VirtualLayersOverlayByPriority()
    {
        using var temp = new TempDir();
        var gameData = temp.Sub("Data");
        var modA = temp.Sub("mods", "ArmorPackA"); // 优先级更高
        var modB = temp.Sub("mods", "ArmorPackB");

        temp.File("mods", "ArmorPackA", "CalienteTools", "BodySlide", "SliderSets", "A.xml", SliderSetXml("SharedOutfit", "OnlyA"));
        temp.File("mods", "ArmorPackB", "CalienteTools", "BodySlide", "SliderSets", "A.xml", SliderSetXml("SharedOutfit", "OnlyB"));
        temp.File("mods", "ArmorPackB", "CalienteTools", "BodySlide", "SliderSets", "B.xml", SliderSetXml("OnlyBFile"));
        temp.File("Data", "CalienteTools", "BodySlide", "SliderSets", "Vanilla.xml", SliderSetXml("DataOutfit"));

        var mods = new List<(ModEntry, string)>
        {
            (new ModEntry("ArmorPackA", true, false, false, 0), modA),
            (new ModEntry("ArmorPackB", true, false, false, 1), modB),
        };

        var result = SliderSetScanner.Scan(VirtualResolution(gameData, @"CalienteTools\BodySlide"), mods);

        var names = result.Outfits.Select(o => o.Name).ToList();
        // modA 的 A.xml 覆盖 modB 的同名文件：OnlyB 只能来自 B.xml
        Assert.Contains("SharedOutfit", names);
        Assert.Contains("OnlyA", names);
        Assert.Contains("OnlyBFile", names);
        Assert.Contains("DataOutfit", names);
        Assert.DoesNotContain("OnlyB", names);

        var shared = result.Outfits.First(o => o.Name == "SharedOutfit");
        Assert.Equal("ArmorPackA", shared.OwnerLabel);

        var data = result.Outfits.First(o => o.Name == "DataOutfit");
        // 自 i18n 改造起，Core 只负责产出本地化键，措辞在 Lang.*.xaml 里；
        // 这里断言键而不是中文：文案改词不该弄红 Core 的测试，而"基础游戏 Data 层被
        // 单列为一层"这件事由键本身钉住。
        Assert.Equal("L.Core_LayerGameData", data.OwnerLabel);
    }

    [Fact]
    public void DuplicateSetNamesAcrossDifferentFilesFlagConflict()
    {
        using var temp = new TempDir();
        var gameData = temp.Sub("Data");
        var modA = temp.Sub("mods", "A");
        var modB = temp.Sub("mods", "B");

        temp.File("mods", "A", "CalienteTools", "BodySlide", "SliderSets", "X.xml", SliderSetXml("Dup", "A1"));
        temp.File("mods", "B", "CalienteTools", "BodySlide", "SliderSets", "Y.xml", SliderSetXml("Dup", "B1"));

        var mods = new List<(ModEntry, string)>
        {
            (new ModEntry("A", true, false, false, 0), modA),
            (new ModEntry("B", true, false, false, 1), modB),
        };

        var result = SliderSetScanner.Scan(VirtualResolution(gameData, @"CalienteTools\BodySlide"), mods);

        Assert.Equal(3, result.Outfits.Count);
        var dup = result.Outfits.First(o => o.Name == "Dup");
        Assert.True(dup.HasConflict);
        Assert.Equal("A", dup.OwnerLabel); // 先见者胜
    }

    [Fact]
    public void DuplicateSetNamesWithinSameLayerResolveDeterministically()
    {
        using var temp = new TempDir();
        var gameData = temp.Sub("Data");
        var modA = temp.Sub("mods", "A");

        // 同一模组内两个文件定义同名滑块组：层内按相对路径字面序取先者，不依赖文件系统枚举顺序
        temp.File("mods", "A", "CalienteTools", "BodySlide", "SliderSets", "B.xml", SliderSetXml("Dup", "B1"));
        temp.File("mods", "A", "CalienteTools", "BodySlide", "SliderSets", "A.xml", SliderSetXml("Dup", "A1"));

        var mods = new List<(ModEntry, string)> { (new ModEntry("A", true, false, false, 0), modA) };
        var result = SliderSetScanner.Scan(VirtualResolution(gameData, @"CalienteTools\BodySlide"), mods);

        Assert.Equal(3, result.Outfits.Count);
        var dup = result.Outfits.First(o => o.Name == "Dup");
        Assert.True(dup.HasConflict);
        Assert.Equal("A.xml", Path.GetFileName(dup.SourceFile));
        Assert.Equal("A", dup.OwnerLabel);
    }

    [Fact]
    public void ParsesOspAndSkipsBrokenFiles()
    {
        using var temp = new TempDir();
        var gameData = temp.Sub("Data");
        var modA = temp.Sub("mods", "A");

        temp.File("mods", "A", "CalienteTools", "BodySlide", "SliderSets", "Proj.osp", SliderSetXml("OspOutfit"));
        temp.File("mods", "A", "CalienteTools", "BodySlide", "SliderSets", "Broken.xml",
            "<SliderSetInfo><SliderSet name='Unfinished");

        var mods = new List<(ModEntry, string)> { (new ModEntry("A", true, false, false, 0), modA) };
        var result = SliderSetScanner.Scan(VirtualResolution(gameData, @"CalienteTools\BodySlide"), mods);

        Assert.Contains(result.Outfits, o => o.Name == "OspOutfit");
        // 坏文件必须留下一条警告（键由 Core 产出，措辞见 Lang.*.xaml 的 L.Core_ScanParseFail）
        Assert.Contains(result.Warnings, w => w.Contains("L.Core_ScanParseFail"));
    }

    [Fact]
    public void AppDirKindScansSingleRealDirectory()
    {
        using var temp = new TempDir();
        var appDir = temp.Sub("BS");
        Directory.CreateDirectory(System.IO.Path.Combine(appDir, "SliderSets"));
        temp.File("BS", "SliderSets", "CBBE.xml", SliderSetXml("CBBE Body"));

        temp.File("mods", "A", "CalienteTools", "BodySlide", "SliderSets", "X.xml", SliderSetXml("ShouldNotAppear"));

        var mods = new List<(ModEntry, string)> { (new ModEntry("A", true, false, false, 0), temp.Sub("mods", "A")) };
        var resolution = new ProjectPathResolution
        {
            EffectivePath = appDir,
            Kind = ProjectPathKind.AppDir,
            GameDataPath = temp.Path,
        };

        var result = SliderSetScanner.Scan(resolution, mods);

        Assert.Single(result.Outfits);
        Assert.Equal("CBBE Body", result.Outfits[0].Name);
    }

    /// <summary>
    /// 解析阶段是并行的，但"同名滑块组先见者胜"依赖文件顺序。用跨文件重名 + 多种并行度断言：
    /// 结果必须与顺序实现完全一致，**包括 Outfits 的出现顺序**（它就是归属判定的落点）。
    /// 若把并行改成"边解析边写共享字典"，这里会随线程调度而变，从而暴露问题。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public void ParallelParsingKeepsFirstSeenWinsOrder(int parallelism)
    {
        using var temp = new TempDir();
        var gameData = temp.Sub("Data");
        var modA = temp.Sub("mods", "A");
        var modB = temp.Sub("mods", "B");

        // 12 个文件，名字空间故意重叠，制造大量跨文件重名
        for (var i = 0; i < 6; i++)
        {
            var shared = $"Shared{i % 3:D2}";
            temp.File("mods", "A", "CalienteTools", "BodySlide", "SliderSets", $"A{i:D2}.xml",
                SliderSetXml(shared, $"A{i:D2}"));
            temp.File("mods", "B", "CalienteTools", "BodySlide", "SliderSets", $"B{i:D2}.xml",
                SliderSetXml(shared, $"B{i:D2}"));
        }

        var mods = new List<(ModEntry, string)>
        {
            (new ModEntry("A", true, false, false, 0), modA),
            (new ModEntry("B", true, false, false, 1), modB),
        };
        var resolution = VirtualResolution(gameData, @"CalienteTools\BodySlide");

        var baseline = SliderSetScanner.Scan(resolution, mods, progress: null, parseParallelism: 1);
        var actual = SliderSetScanner.Scan(resolution, mods, progress: null, parseParallelism: parallelism);

        Assert.Equal(baseline.Outfits.Select(o => o.Name), actual.Outfits.Select(o => o.Name));
        Assert.Equal(baseline.Outfits.Select(o => o.OwnerLabel), actual.Outfits.Select(o => o.OwnerLabel));
        Assert.Equal(baseline.Outfits.Select(o => o.SourceFile), actual.Outfits.Select(o => o.SourceFile));
        Assert.Equal(baseline.Outfits.Select(o => o.HasConflict), actual.Outfits.Select(o => o.HasConflict));
        // 重名确实被触发，否则这个用例什么都没测到
        Assert.Contains(actual.Outfits, o => o.HasConflict);
    }

    /// <summary>
    /// 损坏文件**不贡献任何名字**（半途读到的也不能留下），且只留一条警告——
    /// 与改用 XmlReader 之前 XDocument.Load 一次性失败的语义一致。
    /// </summary>
    [Fact]
    public void BrokenFileContributesNothingAndWarnsOnce()
    {
        using var temp = new TempDir();
        var broken = temp.File("broken.xml",
            "<SliderSetInfo><SliderSet name=\"ReadBeforeFailure\"><SliderSet name='Unfinished");

        var warnings = new List<string>();
        var names = SliderSetScanner.ParseSliderSetNames(broken, warnings).ToList();

        Assert.Empty(names);
        Assert.Single(warnings);
        Assert.Equal("L.Core_ScanParseFail", warnings[0]);
    }

    /// <summary>带命名空间的 &lt;SliderSet&gt; 不算命中，对齐原实现 DescendantsAndSelf("SliderSet") 的匹配范围。</summary>
    [Fact]
    public void NamespacedSliderSetIsIgnored()
    {
        using var temp = new TempDir();
        var file = temp.File("ns.xml",
            "<?xml version=\"1.0\"?>\n<Root xmlns=\"urn:example\">\n    <SliderSet name=\"Ignored\"/>\n</Root>\n");

        var warnings = new List<string>();
        Assert.Empty(SliderSetScanner.ParseSliderSetNames(file, warnings).ToList());
        Assert.Empty(warnings);
    }

    /// <summary>
    /// 只扫各层的 SliderSets 目录：CalienteTools\BodySlide 下同级的 SliderCategories / SliderGroups /
    /// SliderPresets 等都不是滑块组文件（BodySlide 也不读它们）。它们里面即便有 &lt;SliderSet&gt;
    /// 或语法错误，也不该影响结果、更不该变成警告——曾经的实现会把这些目录一起递归进去。
    /// </summary>
    [Fact]
    public void VirtualLayersIgnoreFilesOutsideSliderSets()
    {
        using var temp = new TempDir();
        var gameData = temp.Sub("Data");
        var modA = temp.Sub("mods", "A");

        temp.File("mods", "A", "CalienteTools", "BodySlide", "SliderSets", "Real.xml", SliderSetXml("RealOutfit"));
        temp.File("mods", "A", "CalienteTools", "BodySlide", "SliderCategories", "Cat.xml",
            "<SliderCategories>\n\t<Slider name=\"Hips\"displayname=\"Size\" />\n</SliderCategories>\n");
        temp.File("mods", "A", "CalienteTools", "BodySlide", "SliderGroups", "Grp.xml", SliderSetXml("ShouldNotAppear"));
        temp.File("mods", "A", "CalienteTools", "BodySlide", "Config.xml", "<Config><ProjectPath>x</ProjectPath></Config>");

        var mods = new List<(ModEntry, string)> { (new ModEntry("A", true, false, false, 0), modA) };
        var result = SliderSetScanner.Scan(VirtualResolution(gameData, @"CalienteTools\BodySlide"), mods);

        Assert.Equal(new[] { "RealOutfit" }, result.Outfits.Select(o => o.Name));
        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// 警告里要带上"哪个模组的哪个文件"：同名文件（CBBE.osp、CBBE.xml）在多个模组里都可能存在，
    /// 只给文件名根本没法定位该去修哪一个。
    /// </summary>
    [Fact]
    public void ParseWarningIdentifiesTheOwningModAndRelativePath()
    {
        using var temp = new TempDir();
        var gameData = temp.Sub("Data");
        var modA = temp.Sub("mods", "ArmorPackA");
        temp.File("mods", "ArmorPackA", "CalienteTools", "BodySlide", "SliderSets", "Broken.xml",
            "<SliderSetInfo><SliderSet name='Unfinished");

        var mods = new List<(ModEntry, string)> { (new ModEntry("ArmorPackA", true, false, false, 0), modA) };

        // Core 未注入取词器时只返回键名（实参被丢掉），这里装一个最小取词器把实参渲染出来。
        var previous = CoreStrings.Localizer;
        CoreStrings.Localizer = (key, args) => $"{key}({string.Join(" | ", args)})";
        string warning;
        try
        {
            warning = Assert.Single(
                SliderSetScanner.Scan(VirtualResolution(gameData, @"CalienteTools\BodySlide"), mods).Warnings);
        }
        finally
        {
            CoreStrings.Localizer = previous;
        }

        Assert.StartsWith("L.Core_ScanParseFail", warning);
        Assert.Contains("ArmorPackA", warning);
        Assert.Contains("Broken.xml", warning);
    }
}
