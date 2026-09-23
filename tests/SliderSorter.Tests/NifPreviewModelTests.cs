using System.Numerics;
using SliderSorter.Core;
using Xunit;

namespace SliderSorter.Tests;

/// <summary>预览模型上那两个决定"要不要再垫一具身体"的判定。
/// 实测 350 个 set 里 32% 的作者已经把身体一起导出了，再垫就是两层皮肉互相穿插，
/// 所以 CarriesOwnBody 判错会直接画出一张烂图。</summary>
public class NifPreviewModelTests
{
    private static NifShape Shape(string? texture, int uvCount = 3, int vertexCount = 3) => new()
    {
        Name = "s",
        Positions = Enumerable.Range(0, vertexCount).Select(i => new Vector3(i, i, i)).ToList(),
        Normals = Enumerable.Repeat(Vector3.UnitZ, vertexCount).ToList(),
        Uvs = Enumerable.Repeat(Vector2.One, uvCount).ToList(),
        Indices = [0, 1, 2],
        TexturePath = texture,
    };

    private static NifPreviewModel Model(params NifShape[] shapes) => new()
    {
        Shapes = shapes,
        BoundsMin = new Vector3(-1),
        BoundsMax = new Vector3(1),
    };

    [Theory]
    [InlineData(@"textures\actors\character\female\femalebody_1.dds", true)]
    [InlineData(@"textures\actors\character\male\MaleBody_1.dds", true)]
    [InlineData(@"textures\3ba_bdsm_night\blk1.dds", false)]
    [InlineData(@"textures\clothes\nocturnal\OutfitF.dds", false)]
    [InlineData(null, false)]
    public void CarriesOwnBodyReadsTheDiffuseName(string? texture, bool expected) =>
        Assert.Equal(expected, Model(Shape(texture)).CarriesOwnBody);

    [Fact]
    public void CarriesOwnBodyIsTrueIfAnyShapeHasIt()
    {
        var model = Model(
            Shape(@"textures\armor\boots.dds"),
            Shape(@"textures\actors\character\female\femalebody_0.dds"));
        Assert.True(model.CarriesOwnBody);
    }

    [Fact]
    public void TexturedCountIgnoresShapesWhoseUvCountDisagrees()
    {
        // UV 数量对不上顶点的形状不能贴图：错位比没贴图更糟，所以它不算"有贴图"
        var model = Model(Shape(@"textures\a.dds", uvCount: 2, vertexCount: 3), Shape(@"textures\b.dds"));
        Assert.Equal(1, model.TexturedShapeCount);
        Assert.Equal(2, model.ShapeCount);
    }

    [Fact]
    public void TriangleCountSumsShapes()
    {
        var model = Model(Shape(null), Shape(null));
        Assert.Equal(2, model.TriangleCount);
    }
}
