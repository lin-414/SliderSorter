using BSGroupGenerator.Core;
using Xunit;

namespace BSGroupGenerator.Tests;

/// <summary>
/// 预览取数据文件的口径：跨覆盖层从强到弱，每层先散文件后归档。
/// <para>
/// 这里只测得动散文件那一段。归档（.bsa/.ba2）没有纯托管的写入器可用来造夹具，
/// 那一段是对着真机上的官方归档与 6937 个 ShapeData 网格验的，不在这份清单里。
/// </para>
/// </summary>
public class GameDataResolverTests
{
    private const string Tex = @"textures\actors\character\female\body_1.dds";

    [Theory]
    [InlineData("textures/a/b.dds", @"textures\a\b.dds")]          // 作者用正斜杠
    [InlineData(@"\textures\a.dds", @"textures\a.dds")]            // 开头多一个分隔符
    [InlineData("  textures\\a.dds  ", @"textures\a.dds")]          // 前后空白
    [InlineData("./textures/a.dds", @"textures\a.dds")]
    public void NormalizeCanonicalisesSeparatorsAndTrim(string raw, string expected) =>
        Assert.Equal(expected, GameDataResolver.Normalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\absolute\path.dds")]   // 绝对路径不接受：这个类只处理相对 Data 根的路径
    [InlineData(@"\\nas\share\a.dds")]
    public void NormalizeRejectsNothingOrAbsolute(string? raw) =>
        Assert.Null(GameDataResolver.Normalize(raw));

    [Fact]
    public void StrongestLayerWithTheFileWins()
    {
        using var temp = new TempDir();
        var strong = temp.Sub("mods", "Strong");
        var weak = temp.Sub("mods", "Weak");
        temp.File("mods", "Strong", Tex, "strong bytes");
        temp.File("mods", "Weak", Tex, "weak bytes");

        var resolver = new GameDataResolver([strong, weak]);
        var bytes = resolver.TryRead(Tex);
        Assert.NotNull(bytes);
        Assert.Equal("strong bytes", System.Text.Encoding.UTF8.GetString(bytes!));
    }

    [Fact]
    public void FallsThroughToWeakerLayerThatHasIt()
    {
        using var temp = new TempDir();
        var strong = temp.Sub("mods", "Strong");
        var weak = temp.Sub("mods", "Weak");
        // 强层只有别的文件；这正是"mesh 模组和 texture 模组分开装"的常见形状
        temp.File("mods", "Strong", @"textures\other\nope.dds", "x");
        temp.File("mods", "Weak", Tex, "weak bytes");

        var resolver = new GameDataResolver([strong, weak]);
        Assert.Equal("weak bytes", System.Text.Encoding.UTF8.GetString(resolver.TryRead(Tex)!));
    }

    [Fact]
    public void MissingEverywhereYieldsNull()
    {
        using var temp = new TempDir();
        var resolver = new GameDataResolver([temp.Sub("mods", "A"), temp.Sub("mods", "B")]);
        Assert.Null(resolver.TryRead(Tex));
    }

    [Fact]
    public void UnreadableArchiveDoesNotBreakResolution()
    {
        using var temp = new TempDir();
        var a = temp.Sub("mods", "A");
        var b = temp.Sub("mods", "B");
        // 一个名字像归档但内容不是的东西：建索引时必须被静默跳过，而不是把整次解析带崩
        temp.File("mods", "A", "Textures.bsa", "not a bsa at all");
        temp.File("mods", "B", @"meshes\actors\nif.nif", "nif bytes");

        var resolver = new GameDataResolver([a, b]);
        Assert.Equal("nif bytes", System.Text.Encoding.UTF8.GetString(resolver.TryRead(@"meshes\actors\nif.nif")!));
        // 散文件全落空后才去查归档；归档读不出来同样是 null，而不是异常
        Assert.Null(resolver.TryRead(Tex));
    }

    [Fact]
    public void EmptyLayerListResolvesNothing()
    {
        var resolver = new GameDataResolver([]);
        Assert.Null(resolver.TryRead(Tex));
    }
}
