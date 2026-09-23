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
}
