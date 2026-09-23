using SliderSorter.Core;
using Xunit;

namespace SliderSorter.Tests;

public class KnownModsTests
{
    [Fact]
    public void Diff_NullBaseline_AllCurrentAreNew()
    {
        var result = KnownMods.Diff(null, ["A", "B"]);
        Assert.Equal(["A", "B"], result);
    }

    [Fact]
    public void Diff_ReturnsOnlyMissing_InCurrentOrder()
    {
        var result = KnownMods.Diff(["B", "D"], ["A", "B", "C"]);
        Assert.Equal(["A", "C"], result);
    }

    [Fact]
    public void Diff_AllKnown_ReturnsEmpty()
    {
        var result = KnownMods.Diff(["A", "B"], ["B", "A"]);
        Assert.Empty(result);
    }
}
