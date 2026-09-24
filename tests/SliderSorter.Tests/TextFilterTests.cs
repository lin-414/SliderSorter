using Xunit;
using SliderSorter.Core;

namespace SliderSorter.Tests;

public class TextFilterTests
{
    [Theory]
    [InlineData("Dawn Priestess UBE - Silver", "ube", true)]    // 连续子串（忽略大小写）
    [InlineData("撼地UBEBodyslide", "ube", true)]
    [InlineData("XXX UB EBodyslide", "ube", false)]             // 字母被空格隔开 ≠ 命中
    [InlineData("Unlocked Buckle Edge", "ube", false)]          // 字母分散 ≠ 命中
    [InlineData("CBBE Body", "ube", false)]
    [InlineData("Anything", "", true)]                          // 空过滤 = 全部显示
    [InlineData("UBE", "UBE", true)]
    public void MatchesRequiresContiguousSubstring(string name, string filter, bool expected)
    {
        Assert.Equal(expected, TextFilter.Matches(name, filter));
    }

    // ── 多关键字（输出归属页的过滤框：`;` / `,` 分词，任一命中即留下）──

    private static IReadOnlyList<string> Keywords(string input) => GroupRules.SplitKeywords(input);

    [Fact]
    public void MatchesAnyHitsOnAnyKeywordRatherThanAllTogether()
    {
        Assert.True(TextFilter.MatchesAny("Dawn Priestess UBE - Silver", Keywords("cbbe;ube")));
        Assert.True(TextFilter.MatchesAny("CBBE Body", Keywords("cbbe;ube")));
        // 一个都不挨着才不命中——"同时含两词"那种语义在服装名上几乎没有用处
        Assert.False(TextFilter.MatchesAny("Armor Steel", Keywords("cbbe;ube")));
    }

    [Theory]
    [InlineData("")]              // 空过滤 = 全部显示
    [InlineData(";")]             // 只打了分隔符
    [InlineData("， ,")]          // 全角逗号也算分隔符，中间只剩空白
    public void NothingButSeparatorsIsNoFilterAtAll(string input)
    {
        // 切完必须是空集合：留一条空白关键字当过滤词，MatchesAny 会把带空格的名字全判成命中，
        // 表现是"输入了却什么都没筛掉"，比筛错了更难查
        Assert.Empty(Keywords(input));
        Assert.True(TextFilter.MatchesAny("Anything At All", Keywords(input)));
    }

    [Fact]
    public void ASingleKeywordBehavesExactlyLikeTheSubstringMatch()
    {
        // 单词时不能改变老语义：不跨分隔符拼词、忽略大小写取连续子串
        foreach (var (name, filter, expected) in new[]
                 {
                     ("撼地UBEBodyslide", "ube", true),
                     ("XXX UB EBodyslide", "ube", false),
                     ("Unlocked Buckle Edge", "ube", false),
                 })
            Assert.Equal(expected, TextFilter.MatchesAny(name, Keywords(filter)));
    }
}
