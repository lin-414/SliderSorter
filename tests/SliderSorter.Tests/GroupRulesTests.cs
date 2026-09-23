using Xunit;
using SliderSorter.Core;

namespace SliderSorter.Tests;

public class GroupRulesTests
{
    [Theory]
    [InlineData("ube; 3ba", new[] { "ube", "3ba" })]
    [InlineData("ube，3ba；bodyslide", new[] { "ube", "3ba", "bodyslide" })]
    [InlineData(" ; ，", new string[] { })]
    public void SplitKeywordsHandlesSeparators(string input, string[] expected)
        => Assert.Equal(expected, GroupRules.SplitKeywords(input));

    [Fact]
    public void OutfitKeywordsRequireAHit()
    {
        Assert.True(Match("Dawn Priestess UBE", "IVY模组", ["ube"], [], []));
        Assert.False(Match("Dawn Priestess UBE", "IVY模组", ["cbbe"], [], []));
        Assert.True(Match("任意服装", "IVY模组", [], [], [])); // 留空 = 全部
    }

    [Fact]
    public void ModKeywordsNarrowToMatchingModsOnly()
    {
        Assert.True(Match("某服装", "HIMBO Core", [], [], ["himbo"]));
        Assert.False(Match("某服装", "HIMBO Core", [], [], ["cbbe"]));
        // 模组关键字只筛模组、不参与匹配服装名，否则会把其他模组里的同名服装混进来
        Assert.False(Match("himbo 服装", "其他模组", [], [], ["himbo"]));
    }

    [Fact]
    public void ExcludeWinsOverInclude()
    {
        Assert.False(Match("UBE 汉化版", "模组", ["ube"], ["汉化"], []));
        Assert.True(Match("UBE 原版", "模组", ["ube"], ["汉化"], []));
    }

    [Fact]
    public void UnassignedOnlySkipsAlreadyGroupedOutfits()
    {
        Assert.False(Match("任意服装", "模组", [], [], [], unassignedOnly: true, isInAnyGroup: true));
        Assert.True(Match("任意服装", "模组", [], [], [], unassignedOnly: true, isInAnyGroup: false));
        // 未开启"仅未分配"时，已入组与否不影响匹配
        Assert.True(Match("任意服装", "模组", [], [], [], unassignedOnly: false, isInAnyGroup: true));
    }

    private static bool Match(string outfit, string owner,
        string[] outfitKeywords, string[] outfitExcludeKeywords, string[] modKeywords,
        bool unassignedOnly = false, bool isInAnyGroup = false) =>
        GroupRules.MatchesOutfit(outfit, owner,
            outfitKeywords, outfitExcludeKeywords, modKeywords, unassignedOnly, isInAnyGroup);
}
