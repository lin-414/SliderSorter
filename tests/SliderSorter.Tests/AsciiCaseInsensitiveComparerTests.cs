using SliderSorter.Core;
using Xunit;

namespace SliderSorter.Tests;

/// <summary>
/// 「忽略大小写」的边界：BodySlide 的 <c>case_insensitive_compare</c> 逐字节 <c>tolower</c>，
/// 只有 ASCII 字母会折，非 ASCII 原样比（<c>StringStuff.h:56-62</c>）。
/// <para>
/// 这条界线先前是按 .NET 的 <see cref="StringComparer.OrdinalIgnoreCase"/> 实现的，注释还写着
/// "它同样不动非 ASCII 字符"—— 那句是错的：<c>Ы</c>/<c>ы</c>、<c>É</c>/<c>é</c> 在它眼里相等
/// （下面的断言就是钉这一点）。用错的后果不是显示难看，而是把 BodySlide 眼里的两件衣服合成一件：
/// 弱层那条整条从清单消失，它声明的输出路径也不再参与冲突判定。本仓库界面带俄语包，
/// 服装名只差在西里尔字母大小写上并不冷门。
/// </para>
/// </summary>
public class AsciiCaseInsensitiveComparerTests
{
    private static readonly AsciiCaseInsensitiveComparer Fold = AsciiCaseInsensitiveComparer.Instance;

    [Theory]
    [InlineData("Meshes/Foo", "meshes/foo", true)]   // ASCII 大小写：BodySlide 算同一个键
    [InlineData("3BA", "3ba", true)]
    [InlineData("Ы", "ы", false)]                   // 西里尔：它折不动，于是是两个键
    [InlineData("É", "é", false)]
    [InlineData("Ъ", "ъ", false)]
    [InlineData("游", "游", true)]                   // 完全相同的非 ASCII 仍然相等
    [InlineData("Ы", "ы ", false)]                   // 长度不同
    [InlineData("a-Ы", "A-ы", false)]                // 混排：ASCII 部分折了，非 ASCII 部分没折
    public void FoldsOnlyAsciiLetters(string left, string right, bool equal) =>
        Assert.Equal(equal, Fold.Equals(left, right));

    /// <summary>哈希必须跟着 <see cref="AsciiCaseInsensitiveComparer.Equals"/> 走，
    /// 否则字典查找会"看着相等却查不到"。</summary>
    [Fact]
    public void EqualStringsShareTheHashCode()
    {
        Assert.Equal(Fold.GetHashCode("Meshes/Foo"), Fold.GetHashCode("meshes/foo"));
        Assert.NotEqual(Fold.GetHashCode("Ы"), Fold.GetHashCode("ы"));

        var dict = new Dictionary<string, int>(Fold) { ["MESHES/Ы"] = 7 };
        Assert.True(dict.ContainsKey("meshes/Ы"));
        Assert.False(dict.ContainsKey("meshes/ы"));
    }

    /// <summary>与 <see cref="StringComparer.OrdinalIgnoreCase"/> 的关键差别，写在这里免得有人
    /// "顺手改回去"。</summary>
    [Fact]
    public void OrdinalIgnoreCaseWouldHaveFoldedTooMuch() =>
        Assert.True(StringComparer.OrdinalIgnoreCase.Equals("Ы", "ы"));

    /// <summary>输出冲突聚类按 BodySlide 的口径分组：只差 ASCII 大小写的两个输出路径是**一组**冲突
    /// （批建会弹窗），只差西里尔大小写的两组各一人（它不弹，我们也不该替它弹）。</summary>
    [Fact]
    public void OutputPathsDifferingOnlyInCyrillicCaseAreNotOneConflict()
    {
        var scan = new ScanResult();
        scan.Outfits.Add(Outfit("Upper", "meshes\\Ы.nif", 0));
        scan.Outfits.Add(Outfit("Lower", "meshes\\ы.nif", 1));
        scan.Outfits.Add(Outfit("AsciiUpper", "meshes\\Armor.nif", 0));
        scan.Outfits.Add(Outfit("AsciiLower", "meshes\\armor.nif", 1));

        var groups = OutputConflicts.Detect(scan);

        var group = Assert.Single(groups); // 西里尔那两条不该成为冲突
        Assert.Equal(["AsciiLower", "AsciiUpper"],
            group.Candidates.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    private static OutfitEntry Outfit(string name, string outputPath, int layer) => new()
    {
        Name = name,
        OwnerLabel = name + " mod",
        SourceFile = name + ".xml",
        OutputFilePath = outputPath,
        LayerIndex = layer,
    };
}

/// <summary>带 &lt;!DOCTYPE&gt; 的滑块组文件：tinyxml2 跳过 DTD 照常读里面的 set，
/// 而 <c>DtdProcessing.Prohibit</c> 会让整个文件当场抛异常、一条服装都不贡献（只留一行警告）——
/// 用某些 XML 工具或导出器存过的 .xml/.osp 就长这样。改成 Ignore，同时显式关掉外部实体解析。</summary>
public class SliderSetDtdTests
{
    [Fact]
    public void SetsAfterADeclarationAreStillRead()
    {
        using var temp = new TempDir();
        var file = temp.File("sets.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE SliderSetInfo SYSTEM "sliderset.dtd">
            <SliderSetInfo version="2">
              <SliderSet name="Bikini"><OutputPath>meshes/foo</OutputPath></SliderSet>
            </SliderSetInfo>
            """);

        var warnings = new List<string>();
        var names = SliderSetScanner.ParseSliderSetNames(file, warnings).ToList();

        Assert.Equal(["Bikini"], names);
        Assert.Empty(warnings);
    }
}
