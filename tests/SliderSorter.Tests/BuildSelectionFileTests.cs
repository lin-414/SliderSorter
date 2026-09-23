using System.Xml.Linq;
using Xunit;
using SliderSorter.Core;

namespace SliderSorter.Tests;

/// <summary>BodySlide 的 BuildSelection.xml：导出必须只动自己负责的条目，并且要让 BodySlide 读得回来。</summary>
public class BuildSelectionFileTests
{
    private const string PathA = @"meshes\actors\character\character assets\clothes\bikini";
    private const string PathB = @"meshes\actors\character\character assets\clothes\armor";

    /// <summary>造一份"BodySlide 已经存过"的文件：既有我们的输出路径，也有别人的条目和 ZapChoice。</summary>
    private static string Existing(string[]? ours = null, string foreignPath = "meshes\\other\\thing",
        string foreignChoice = "Someone Else", bool withZap = true)
    {
        var root = new XElement("BuildSelection");
        foreach (var pair in ours ?? Array.Empty<string>())
            root.Add(new XElement("OutputChoice", new XAttribute("path", pair.Split('=')[0]),
                new XAttribute("choice", pair.Split('=')[1])));
        root.Add(new XElement("OutputChoice", new XAttribute("path", foreignPath),
            new XAttribute("choice", foreignChoice)));
        if (withZap)
            root.Add(new XElement("ZapChoice", new XAttribute("project", "CBBE"),
                new XAttribute("zap", "3BBB"), new XAttribute("choice", "0")));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString();
    }

    [Fact]
    public void ExportCreatesFileThatReadsBackIdentical()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, BuildSelectionFile.FileName);

        var desired = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PathB] = "OtherStrong",
            [PathA] = "Strong",
        };
        Assert.True(BuildSelectionFile.TryExport(path, desired, new[] { PathA, PathB },
            out var written, out var removed, out var error));
        Assert.Equal(2, written);
        Assert.Equal(0, removed);
        Assert.Null(error);

        // BOM + 声明：BodySlide 用 tinyxml2 存盘时也是带 BOM 的（BuildSelectionFile::Save）
        var bytes = File.ReadAllBytes(path);
        Assert.Equal((byte)0xEF, bytes[0]);
        Assert.Equal((byte)0xBB, bytes[1]);
        Assert.Equal((byte)0xBF, bytes[2]);
        Assert.StartsWith("<?xml", File.ReadAllText(path).TrimStart('\uFEFF'));

        Assert.True(BuildSelectionFile.TryRead(path, out var back, out error));
        Assert.Null(error);
        Assert.Equal(desired, back);
        // 条目顺序固定（按路径 Ordinal），不然每次导出都产出一个纯换序的 diff
        var doc = XDocument.Load(path);
        Assert.Equal(new[] { PathB, PathA },
            doc.Root!.Elements("OutputChoice").Select(e => e.Attribute("path")!.Value).ToArray());
    }

    [Fact]
    public void ExportKeepsForeignEntriesAndPutsOutputChoicesFirst()
    {
        using var temp = new TempDir();
        var path = temp.File("BuildSelection.xml", Existing(ours: new[] { $"{PathA}=Old" }));

        var desired = new Dictionary<string, string>(StringComparer.Ordinal) { [PathA] = "Strong" };
        Assert.True(BuildSelectionFile.TryExport(path, desired, new[] { PathA },
            out var written, out var removed, out var error));
        Assert.Equal(1, written);
        Assert.Equal(0, removed);

        var doc = XDocument.Load(path);
        var names = doc.Root!.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.Equal(new[] { "OutputChoice", "OutputChoice", "ZapChoice" }, names);
        // 别人（其它 BodySlide 组/旧版本）写的条目不能被动过
        Assert.Equal("Someone Else",
            doc.Root!.Elements("OutputChoice").Single(e => e.Attribute("path")!.Value == "meshes\\other\\thing")
                .Attribute("choice")!.Value);
        Assert.True(BuildSelectionFile.TryRead(path, out var back, out _));
        Assert.Equal("Strong", back[PathA]);
        Assert.Equal(2, back.Count);
    }

    [Fact]
    public void OutputChoicesStayContiguousBecauseBodySlideStopsAtTheFirstAlienSibling()
    {
        using var temp = new TempDir();
        // 一份"ZapChoice 夹在 OutputChoice 中间"的文件：BodySlide 的遍历会在 ZapChoice 处停下，
        // 后面的 OutputChoice 直接被无视。导出后必须把它们排到连续的最前面。
        var path = temp.File("BuildSelection.xml",
            "<BuildSelection>\n" +
            $"  <OutputChoice path=\"{PathA}\" choice=\"Old\"/>\n" +
            "  <ZapChoice project=\"CBBE\" zap=\"3BBB\" choice=\"1\"/>\n" +
            $"  <OutputChoice path=\"{PathB}\" choice=\"Stale\"/>\n" +
            "</BuildSelection>\n");

        var desired = new Dictionary<string, string>(StringComparer.Ordinal) { [PathA] = "Strong" };
        Assert.True(BuildSelectionFile.TryExport(path, desired, new[] { PathA }, out _, out _, out var error));
        Assert.Null(error);

        var doc = XDocument.Load(path);
        var names = doc.Root!.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.Equal(new[] { "OutputChoice", "OutputChoice", "ZapChoice" }, names);
        Assert.Equal("Stale", // PathB 不在托管范围内，原样留着
            doc.Root!.Elements("OutputChoice").First(e => e.Attribute("path")!.Value == PathB).Attribute("choice")!.Value);
    }

    [Fact]
    public void DroppingAChoiceRemovesOnlyTheManagedEntry()
    {
        using var temp = new TempDir();
        var path = temp.File("BuildSelection.xml", Existing(ours: new[] { $"{PathA}=Strong", $"{PathB}=OtherStrong" }));

        // 用户在工具里取消了 PathA 的选择：PathA 的条目要被删掉，PathB 的（不在本次托管范围）不动
        Assert.True(BuildSelectionFile.TryExport(path,
            new Dictionary<string, string>(StringComparer.Ordinal), new[] { PathA },
            out var written, out var removed, out var error));
        Assert.Null(error);
        Assert.Equal(0, written);
        Assert.Equal(1, removed);

        Assert.True(BuildSelectionFile.TryRead(path, out var back, out _));
        Assert.False(back.ContainsKey(PathA));
        Assert.Equal("OtherStrong", back[PathB]);
    }

    [Fact]
    public void EmptyChoiceMeansUndecidedAndIsNotWritten()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "BuildSelection.xml");

        var desired = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PathA] = "",
            [PathB] = "Weak",
        };
        Assert.True(BuildSelectionFile.TryExport(path, desired, new[] { PathA, PathB },
            out var written, out _, out _));
        Assert.Equal(1, written);

        Assert.True(BuildSelectionFile.TryRead(path, out var back, out _));
        Assert.Equal(new[] { PathB }, back.Keys.ToArray());
        // 未决定的组连条目都不该出现——BodySlide 只在 path 命中等值比较时读 choice，留个空条目毫无意义
        var doc = XDocument.Load(path);
        Assert.DoesNotContain(doc.Root!.Elements("OutputChoice"),
            e => e.Attribute("path")!.Value == PathA);
    }

    [Fact]
    public void SecondExportWithSameChoicesTouchesNothing()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "BuildSelection.xml");
        var desired = new Dictionary<string, string>(StringComparer.Ordinal) { [PathA] = "Strong" };
        var managed = new[] { PathA };

        Assert.True(BuildSelectionFile.TryExport(path, desired, managed, out var first, out _, out _));
        Assert.Equal(1, first);
        var bytes = File.ReadAllBytes(path);

        Assert.True(BuildSelectionFile.TryExport(path, desired, managed, out var written, out var removed, out _));
        Assert.Equal(0, written);
        Assert.Equal(0, removed);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + BuildSelectionFile.BackupSuffix));
    }

    [Fact]
    public void OverwriteLeavesABackupOfThePreviousContent()
    {
        using var temp = new TempDir();
        var original = Existing(ours: new[] { $"{PathA}=Old" });
        var path = temp.File("BuildSelection.xml", original);

        Assert.True(BuildSelectionFile.TryExport(path,
            new Dictionary<string, string>(StringComparer.Ordinal) { [PathA] = "Strong" },
            new[] { PathA }, out var written, out _, out _));
        Assert.Equal(1, written);

        Assert.True(File.Exists(path + BuildSelectionFile.BackupSuffix));
        Assert.Equal(original, File.ReadAllText(path + BuildSelectionFile.BackupSuffix).TrimEnd());
    }

    [Fact]
    public void UnparsableFileIsReportedAndLeftAlone()
    {
        using var temp = new TempDir();
        var path = temp.File("BuildSelection.xml", "<BuildSelection><OutputChoice path=\"x\"");

        Assert.False(BuildSelectionFile.TryExport(path,
            new Dictionary<string, string>(StringComparer.Ordinal) { [PathA] = "Strong" },
            new[] { PathA }, out _, out _, out var error));
        Assert.Equal("L.Core_BuildSelParseFail", error);
        Assert.Equal("<BuildSelection><OutputChoice path=\"x\"", File.ReadAllText(path));

        Assert.False(BuildSelectionFile.TryRead(path, out _, out error));
        Assert.Equal("L.Core_BuildSelParseFail", error);
    }

    [Fact]
    public void WrongRootElementIsRefusedRatherThanDestroyed()
    {
        using var temp = new TempDir();
        var path = temp.File("BuildSelection.xml", "<SliderGroups><Group name=\"x\"/></SliderGroups>");

        // 根元素不对 = 这根本不是我理解的那个文件（用户改名/放错位置）。宁可拒绝也不清空别人的数据。
        Assert.False(BuildSelectionFile.TryExport(path,
            new Dictionary<string, string>(StringComparer.Ordinal) { [PathA] = "Strong" },
            new[] { PathA }, out _, out _, out var error));
        Assert.Equal("L.Core_BuildSelBadRoot", error);
        Assert.Equal("<SliderGroups><Group name=\"x\"/></SliderGroups>", File.ReadAllText(path));
    }

    [Fact]
    public void MissingFileReadsAsEmptyAndNotAsAnError()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "does-not-exist", BuildSelectionFile.FileName);

        Assert.True(BuildSelectionFile.TryRead(path, out var back, out var error));
        Assert.Null(error);
        Assert.Empty(back);
    }

    [Fact]
    public void EntriesWithoutUsableChoiceAreSkippedLikeBodySlideDoes()
    {
        using var temp = new TempDir();
        var path = temp.File("BuildSelection.xml",
            "<BuildSelection>\n" +
            $"  <OutputChoice path=\"{PathA}\"/>\n" +                      // 无 choice 属性
            $"  <OutputChoice path=\"{PathB}\" choice=\"\"/>\n" +          // choice 为空 = 未决定
            "  <OutputChoice choice=\"NoPath\"/>\n" +                       // 无 path 属性
            "</BuildSelection>\n");

        Assert.True(BuildSelectionFile.TryRead(path, out var back, out _));
        Assert.Empty(back);
    }

    [Fact]
    public void ExportIsNoOpWhenTargetFileAlreadyMatchesDespiteUnmanagedEntries()
    {
        using var temp = new TempDir();
        var path = temp.File("BuildSelection.xml", Existing(ours: new[] { $"{PathA}=Strong" }, withZap: true));

        // 托管范围内已经是我们想要的选择：不写、不留备份
        var bytes = File.ReadAllBytes(path);
        Assert.True(BuildSelectionFile.TryExport(path,
            new Dictionary<string, string>(StringComparer.Ordinal) { [PathA] = "Strong" },
            new[] { PathA }, out var written, out var removed, out _));
        Assert.Equal(0, written);
        Assert.Equal(0, removed);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void PathForIsNextToConfigXml()
    {
        Assert.Equal(Path.Combine("D:\\games\\BodySlide", "BuildSelection.xml"),
            BuildSelectionFile.PathFor("D:\\games\\BodySlide"));
    }
}
