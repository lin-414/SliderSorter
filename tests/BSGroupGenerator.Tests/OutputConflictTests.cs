using System.Text;
using Xunit;
using BSGroupGenerator.Core;

namespace BSGroupGenerator.Tests;

/// <summary>输出文件冲突（BodySlide 的"多个 set 写同一个 .nif"）的解析与聚类。</summary>
public class OutputConflictTests
{
    private const string PathA = @"meshes\actors\character\character assets\clothes\bikini";
    private const string PathB = @"meshes\actors\character\character assets\clothes\armor";

    private static string Set(string name, string? outputPath = null, string? outputFile = null,
        string? genWeights = null, string? dataFolder = null, string? sourceFile = null)
    {
        var sb = new StringBuilder();
        sb.Append($"<SliderSet name=\"{name}\">");
        if (dataFolder is not null)
            sb.Append($"<DataFolder>{dataFolder}</DataFolder>");
        if (sourceFile is not null)
            sb.Append($"<SourceFile>{sourceFile}</SourceFile>");
        if (outputPath is not null)
            sb.Append($"<OutputPath>{outputPath}</OutputPath>");
        if (outputFile is not null)
            sb.Append($"<OutputFile{(genWeights is null ? "" : $" GenWeights=\"{genWeights}\"")}>{outputFile}</OutputFile>");
        sb.Append("<Target file=\"somebody.nif\"/></SliderSet>");
        return sb.ToString();
    }

    private static string Doc(params string[] sets) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<SliderSetInfo version=\"2\">\n"
        + string.Join("\n", sets) + "\n</SliderSetInfo>\n";

    /// <summary>
    /// 两个模组各出一个滑块组文件：A 的优先级高于 B（层序 0 与 1）。
    /// 文件名故意不同——同名的话 B 的文件会被 VFS 覆盖掉整个文件，那是
    /// <see cref="SliderSetScannerTests"/> 管的事，这里要的是"两个模组的 set 同时在清单里"。
    /// </summary>
    private static ScanResult ScanTwoMods(TempDir temp, string xmlA, string xmlB)
    {
        var gameData = temp.Sub("Data");
        temp.File("mods", "A", "CalienteTools", "BodySlide", "SliderSets", "FromA.xml", xmlA);
        temp.File("mods", "B", "CalienteTools", "BodySlide", "SliderSets", "FromB.xml", xmlB);

        var mods = new List<(ModEntry, string)>
        {
            (new ModEntry("A", true, false, false, 0), temp.Sub("mods", "A")),
            (new ModEntry("B", true, false, false, 1), temp.Sub("mods", "B")),
        };
        var resolution = new ProjectPathResolution
        {
            EffectivePath = Path.Combine(gameData, @"CalienteTools\BodySlide"),
            Kind = ProjectPathKind.GameDataCalienteTools,
            GameDataPath = gameData,
        };
        return SliderSetScanner.Scan(resolution, mods);
    }

    [Fact]
    public void OutputPathIsNormalizedToBackslashesLikeBodySlide()
    {
        using var temp = new TempDir();
        var scan = ScanTwoMods(temp,
            Doc(Set("Solo", "meshes/actors/character/character assets/clothes/bikini")),
            Doc(Set("Other")));

        var outfit = scan.Outfits.First(o => o.Name == "Solo");
        // BodySlide 侧是 ToOSSlashes(OutputPath) + PathSep + OutputFile（SliderSet.cpp:826-841），
        // 这个字符串会被原样写进 BuildSelection.xml 的 path 属性，必须逐字符一致
        Assert.Equal(PathA, outfit.OutputFilePath);
        Assert.True(outfit.GenWeights); // 没写 <OutputFile> 时 BodySlide 的 genWeights 保持 true
    }

    [Fact]
    public void OutputFileIsAppendedAndCarriesGenWeights()
    {
        using var temp = new TempDir();
        var scan = ScanTwoMods(temp,
            Doc(Set("Solo", "meshes/actors/character/character assets/clothes/bikini", "extra", "0")),
            Doc(Set("Other")));

        var outfit = Assert.Single(scan.Outfits, o => o.Name == "Solo");
        Assert.Equal(PathA + @"\extra", outfit.OutputFilePath);
        Assert.False(outfit.GenWeights);
    }

    [Fact]
    public void OutputFileWithoutGenWeightsStillMeansWeights()
    {
        using var temp = new TempDir();
        var scan = ScanTwoMods(temp,
            Doc(Set("Solo", "meshes/x", "extra")),
            Doc(Set("Other")));

        // tinyxml2 的 BoolAttribute("GenWeights", true)：属性缺省算"是"
        Assert.True(Assert.Single(scan.Outfits, o => o.Name == "Solo").GenWeights);
    }

    [Fact]
    public void NestedOutputPathIsNotMistakenForTheSetsOwn()
    {
        using var temp = new TempDir();
        var scan = ScanTwoMods(temp,
            Doc("<SliderSet name=\"Nested\"><Sliders><Slider><OutputPath>meshes/ghost</OutputPath></Slider></Sliders></SliderSet>"),
            Doc(Set("Other")));

        // 只认 <SliderSet> 的直接子元素：嵌套层里的同名元素不该改判决策
        Assert.Null(Assert.Single(scan.Outfits, o => o.Name == "Nested").OutputFilePath);
    }

    [Fact]
    public void SetsWithoutOutputPathNeverConflict()
    {
        using var temp = new TempDir();
        var scan = ScanTwoMods(temp,
            Doc(Set("OnlyA"), Set("Shared", PathA)),
            Doc(Set("OnlyB"), Set("Shared2", PathB)));

        Assert.All(scan.Outfits.Where(o => o.Name is "OnlyA" or "OnlyB"), o => Assert.False(o.HasOutputConflict));
        Assert.Empty(OutputConflicts.Detect(scan));
    }

    [Fact]
    public void TwoModsWritingTheSameOutputPathConflict()
    {
        using var temp = new TempDir();
        var scan = ScanTwoMods(temp,
            Doc(Set("Armorsmith Enhanced", PathA)),
            Doc(Set("UNPB Bikini", PathA), Set("Unrelated", PathB)));

        var group = Assert.Single(OutputConflicts.Detect(scan));
        Assert.Equal(PathA, group.OutputFilePath);
        Assert.Equal(new[] { "Armorsmith Enhanced", "UNPB Bikini" }, group.Candidates.Select(c => c.Name));
        Assert.Equal("A", group.Strongest.OwnerLabel);
        Assert.Equal(new[] { "Armorsmith Enhanced", "UNPB Bikini" },
            scan.Outfits.Where(o => o.HasOutputConflict).Select(o => o.Name));
        // 写另一个文件的 set 不该被卷进来
        Assert.False(scan.Outfits.First(o => o.Name == "Unrelated").HasOutputConflict);
    }

    [Fact]
    public void SameModVariantsSharingOneOutputPathAreNotCrossMod()
    {
        using var temp = new TempDir();
        // UBE 那类模组：一件衣服几百个配色预设，OutputPath 全一样。这确实是同一批 set 在争用
        // 同一个文件（BodySlide 照样会问），但它不是"多个模组改了同一件衣服"，
        // 于是树里不打标记、状态栏也不计数——否则一个模组就能刷出几百行徽标。
        var scan = ScanTwoMods(temp,
            Doc(Set("Cosplay Red", PathA), Set("Cosplay Blue", PathA), Set("Cosplay White", PathA)),
            Doc(Set("Unrelated", PathB)));

        var group = Assert.Single(OutputConflicts.Detect(scan));
        Assert.Equal(3, group.Candidates.Count);
        Assert.False(group.CrossMod);
        Assert.All(scan.Outfits, o => Assert.False(o.HasOutputConflict));
    }

    [Fact]
    public void CrossModFlagLooksAtOwnersNotAtSetCount()
    {
        using var temp = new TempDir();
        var scan = ScanTwoMods(temp,
            Doc(Set("A1", PathA), Set("A2", PathA)),
            Doc(Set("B1", PathA)));

        var group = Assert.Single(OutputConflicts.Detect(scan));
        Assert.True(group.CrossMod);
        Assert.Equal(3, scan.Outfits.Count(o => o.HasOutputConflict));
    }

    [Fact]
    public void CandidateOrderFollowsLayerStrength()
    {
        using var temp = new TempDir();
        // B 模组先声明两个、A 模组后声明一个，但 A 层更强：候选顺序按层而非按文件内出现顺序
        var scan = ScanTwoMods(temp,
            Doc(Set("Winner", PathA)),
            Doc(Set("Loser1", PathA), Set("Loser2", PathA)));

        var group = Assert.Single(OutputConflicts.Detect(scan));
        Assert.Equal("Winner", group.Strongest.Name);
        Assert.Equal(new[] { 0, 1, 1 }, group.Candidates.Select(c => c.LayerIndex));
    }

    [Fact]
    public void PathComparisonIsCaseSensitiveBecauseBodySidesIs()
    {
        using var temp = new TempDir();
        var scan = ScanTwoMods(temp,
            Doc(Set("Upper", "meshes/Foo")),
            Doc(Set("Lower", "meshes/foo")));

        // BodySlide 侧是 std::map<std::string,...>（逐字节比较），两个大小写不同的路径是两个不同的键：
        // 我们若按忽略大小写聚类，就会导出一条 BodySlide 永远读不到的选择
        Assert.Empty(OutputConflicts.Detect(scan));
        Assert.All(scan.Outfits, o => Assert.False(o.HasOutputConflict));
    }

    [Fact]
    public void DuplicateSetNameInWeakerLayerDoesNotJoinTheConflict()
    {
        using var temp = new TempDir();
        // 同名 set：BodySlide 先见者胜，弱层那份根本不会被加载，它声明的输出路径也就无从参与冲突
        var scan = ScanTwoMods(temp,
            Doc(Set("Dup", PathA)),
            Doc(Set("Dup", PathB)));

        var dup = Assert.Single(scan.Outfits, o => o.Name == "Dup");
        Assert.True(dup.HasConflict);
        Assert.Equal(PathA, dup.OutputFilePath);
        Assert.False(dup.HasOutputConflict);
        Assert.Empty(OutputConflicts.Detect(scan));
    }

    [Fact]
    public void ChoiceResolvesWinnerAndStaleChoiceIsIgnored()
    {
        using var temp = new TempDir();
        var scan = ScanTwoMods(temp,
            Doc(Set("Strong", PathA)),
            Doc(Set("Weak", PathA)));

        var chosen = Assert.Single(OutputConflicts.Detect(scan,
            new Dictionary<string, string> { [PathA] = "Weak" }));
        Assert.Equal("Weak", chosen.Chosen?.Name);

        // 选了个已经不存在的 set（模组被卸载/禁用）：视为未决定，而不是随便回落给谁
        var stale = Assert.Single(OutputConflicts.Detect(scan,
            new Dictionary<string, string> { [PathA] = "Removed" }));
        Assert.Null(stale.Chosen);

        Assert.Null(Assert.Single(OutputConflicts.Detect(scan)).Chosen);
    }

    [Fact]
    public void AutoPickThenClearRoundTrips()
    {
        using var temp = new TempDir();
        var scan = ScanTwoMods(temp,
            Doc(Set("Strong", PathA), Set("OtherStrong", PathB)),
            Doc(Set("Weak", PathA), Set("OtherWeak", PathB)));
        var groups = OutputConflicts.Detect(scan);
        Assert.Equal(2, groups.Count);

        var picked = OutputConflicts.AutoPick(groups, new Dictionary<string, string>(StringComparer.Ordinal));
        // 两条键都进了选择；顺序由调用方排（PathB 里的 armor 在 PathA 的 bikini 之前）
        Assert.Equal(new[] { PathB, PathA }, picked.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal("Strong", picked[PathA]);
        Assert.Equal("OtherStrong", picked[PathB]);

        // 只清一组：另一组的选择必须留着
        var cleared = OutputConflicts.Clear(
            new[] { groups.Single(g => g.OutputFilePath == PathA) }, picked);
        Assert.Equal(new[] { PathB }, cleared.Keys.ToArray());
    }

    [Fact]
    public void ChoicesAreLookedUpByteWiseEvenFromAnIgnoreCaseDictionary()
    {
        using var temp = new TempDir();
        var scan = ScanTwoMods(temp,
            Doc(Set("Strong", "meshes/Foo")),
            Doc(Set("Weak", "meshes/foo")));

        // 调用方递来忽略大小写的字典（JSON 反序列化不保证比较器），也不能把选择串到另一个大小写的键上
        var ignoreCase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["MESHES/FOO"] = "Hijack" };
        foreach (var group in OutputConflicts.Detect(scan, ignoreCase))
            Assert.Null(group.Chosen);
    }

    [Fact]
    public void SourceNifResolvesFromShapeDataAcrossLayers()
    {
        using var temp = new TempDir();
        var gameData = temp.Sub("Data");
        // B 模组有源网格，A 模组只有滑块组定义：按 BodySlide 的口径，源网格跨层汇聚，
        // 所以 A 的 set 该拿到 B 那份（A 层更强时先试 A、落空后退到 B）
        temp.File("mods", "A", "CalienteTools", "BodySlide", "SliderSets", "FromA.xml",
            Doc(Set("Outfit A", dataFolder: "Sub", sourceFile: "missing")));
        temp.File("mods", "B", "CalienteTools", "BodySlide", "SliderSets", "FromB.xml",
            Doc(Set("Outfit B", dataFolder: "Sub", sourceFile: "b")));
        temp.File("mods", "B", "CalienteTools", "BodySlide", "ShapeData", "Sub", "b.nif", "not a real nif");

        var mods = new List<(ModEntry, string)>
        {
            (new ModEntry("A", true, false, false, 0), temp.Sub("mods", "A")),
            (new ModEntry("B", true, false, false, 1), temp.Sub("mods", "B")),
        };
        var scan = SliderSetScanner.Scan(new ProjectPathResolution
        {
            EffectivePath = Path.Combine(gameData, @"CalienteTools\BodySlide"),
            Kind = ProjectPathKind.GameDataCalienteTools,
            GameDataPath = gameData,
        }, mods);

        var a = scan.Outfits.First(o => o.Name == "Outfit A");
        Assert.Null(a.SourceNif); // 谁都没提供 Sub\missing.nif：预览要说"没有源网格"，而不是猜一个

        var b = scan.Outfits.First(o => o.Name == "Outfit B");
        // <SourceFile> 写的是 "b"（不带 .nif）也能命中同目录下的 b.nif
        Assert.NotNull(b.SourceNif);
        Assert.EndsWith(Path.Combine("ShapeData", "Sub", "b.nif"), b.SourceNif);
        Assert.StartsWith(temp.Sub("mods", "B"), b.SourceNif);
    }

    [Fact]
    public void SourceNifPrefersTheStrongestLayerThatHasIt()
    {
        using var temp = new TempDir();
        var gameData = temp.Sub("Data");
        temp.File("mods", "A", "CalienteTools", "BodySlide", "SliderSets", "FromA.xml",
            Doc(Set("Shared", dataFolder: null, sourceFile: "s")));
        temp.File("mods", "A", "CalienteTools", "BodySlide", "ShapeData", "s.nif", "weaker content");
        temp.File("mods", "B", "CalienteTools", "BodySlide", "ShapeData", "s.nif", "unused");

        var mods = new List<(ModEntry, string)>
        {
            (new ModEntry("A", true, false, false, 0), temp.Sub("mods", "A")),
            (new ModEntry("B", true, false, false, 1), temp.Sub("mods", "B")),
        };
        var scan = SliderSetScanner.Scan(new ProjectPathResolution
        {
            EffectivePath = Path.Combine(gameData, @"CalienteTools\BodySlide"),
            Kind = ProjectPathKind.GameDataCalienteTools,
            GameDataPath = gameData,
        }, mods);

        // 两个层都有同名源网格：按层序取更强的 A 层，与 BodySlide 在 USVFS 里看到的一致
        var outfit = Assert.Single(scan.Outfits);
        Assert.Equal(Path.Combine("s.nif"), Path.GetFileName(outfit.SourceNif));
        Assert.Contains(Path.Combine("mods", "A", "CalienteTools"), outfit.SourceNif);
    }

    [Fact]
    public void TargetDisplayPassesPathAndWeightedFlagToLocalizer()
    {
        var previous = CoreStrings.Localizer;
        CoreStrings.Localizer = (key, args) => $"{key}({string.Join(" | ", args)})";
        try
        {
            // 措辞（"（以及 _1.nif）"、".nif" 后缀）在 Lang.*.xaml 里，Core 只负责把路径和权重标记交出去
            Assert.Equal($"L.Core_OutputConf_Weighted({PathA})", OutputConflictGroup.FormatTarget(PathA, true));
            Assert.Equal($"L.Core_OutputConf_Single({PathA})", OutputConflictGroup.FormatTarget(PathA, false));
        }
        finally
        {
            CoreStrings.Localizer = previous;
        }
    }
}
