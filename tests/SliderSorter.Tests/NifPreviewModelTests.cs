using System.Numerics;
using SliderSorter.Core;
using Xunit;

namespace SliderSorter.Tests;

/// <summary>预览模型上那两个决定"要不要再垫一具身体"的判定。
/// 实测 350 个 set 里 32% 的作者已经把身体一起导出了，再垫就是两层皮肉互相穿插，
/// 所以 CarriesOwnBody 判错会直接画出一张烂图。</summary>
public class NifPreviewModelTests
{
    /// <summary>默认给一个"从头到脚"的形状（Z 是 nif 的竖直方向，女体约 103 高），
    /// 因为绝大多数用例测的是贴图名那半边；测高度门槛的用例显式传 <paramref name="height"/>。</summary>
    private static NifShape Shape(string? texture, int uvCount = 3, int vertexCount = 3, float height = 103) => new()
    {
        Name = "s",
        Positions = Enumerable.Range(0, vertexCount).Select(i => new Vector3(i, i, i)).ToList(),
        Normals = Enumerable.Repeat(Vector3.UnitZ, vertexCount).ToList(),
        Uvs = Enumerable.Repeat(Vector2.One, uvCount).ToList(),
        Indices = [0, 1, 2],
        TexturePath = texture,
        BoundsMin = new Vector3(-10, -10, 0),
        BoundsMax = new Vector3(10, 10, height),
    };

    private const string FemaleBody = @"textures\actors\character\female\femalebody_1.dds";

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
            Shape(FemaleBody));
        Assert.True(model.CarriesOwnBody);
    }

    /// <summary>手套、靴子、袜子、高跟鞋的源网格里也有一块贴 femalebody 的皮肤，但那只是手或脚：
    /// 按贴图名判会得出"这件自带身体"，于是预览里不垫身体，用户看到一双悬空的凉鞋
    /// （本机实测：判为自带身体的 147 件里有 41 件属于这种，如 UBE Shino_Maid Stocking and shoes）。
    /// 真实数据是双峰的——皮肤块要么 90~120 高（真身体），要么 11~18 高（手脚），中间没有，
    /// 所以这条高度门槛落在空档里，不会把真身体挡在门外。</summary>
    [Theory]
    [InlineData(18)]    // 手套/袖套上那块手臂皮肤
    [InlineData(11)]    // 凉鞋、高跟鞋上的脚
    [InlineData(59.9f)] // 门槛下方
    public void CarriesOwnBodyIgnoresLimbFragments(float skinHeight) =>
        Assert.False(Model(Shape(FemaleBody, height: skinHeight)).CarriesOwnBody);

    [Theory]
    [InlineData(60)]   // 门槛本身
    [InlineData(103)]  // 女体
    [InlineData(120)]  // 男体
    public void CarriesOwnBodyAcceptsAShapeTallEnoughToBeABody(float skinHeight) =>
        Assert.True(Model(Shape(FemaleBody, height: skinHeight)).CarriesOwnBody);

    [Fact]
    public void LimbFragmentStillGetsTheBodyUnderneath()
    {
        // 一件只带脚部皮肤的凉鞋：不垫身体就等于把用户晾在空画布前
        var sandals = Model(
            Shape(@"textures\actors\character\female\femalebody_0.dds", height: 11),
            Shape(@"textures\clothes\shino\shoes.dds", height: 14));
        Assert.False(sandals.CarriesOwnBody);
        // 而"高度"看的是那块皮肤本身，不是整件衣服的包围盒——整件 14 高也救不了它
        Assert.True(Model(Shape(FemaleBody, height: 100), Shape(@"textures\x.dds", height: 4)).CarriesOwnBody);
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

    // ── 取景框：离身体一大截的散落碎片不该把镜头拉走 ──

    /// <summary>Z 从 zMin 到 zMax 的一块形状（nif 的 Z 是竖直方向），XY 固定占 ±10。</summary>
    private static NifShape Box(float zMin, float zMax) => new()
    {
        Name = "box",
        Positions = [new Vector3(0, 0, zMin)],
        Normals = [Vector3.UnitZ],
        Uvs = Array.Empty<Vector2>(),
        Indices = [0, 0, 0],
        TexturePath = null,
        BoundsMin = new Vector3(-10, -10, zMin),
        BoundsMax = new Vector3(10, 10, zMax),
    };

    private static NifPreviewModel Parts(params NifShape[] shapes) => new()
    {
        Shapes = shapes,
        BoundsMin = new Vector3(shapes.Min(s => s.BoundsMin.X), shapes.Min(s => s.BoundsMin.Y), shapes.Min(s => s.BoundsMin.Z)),
        BoundsMax = new Vector3(shapes.Max(s => s.BoundsMax.X), shapes.Max(s => s.BoundsMax.Y), shapes.Max(s => s.BoundsMax.Z)),
    };

    [Fact]
    public void FramingBoundsExcludesPiecesFarOffTheBody()
    {
        // 本机实例：影歌项链的四块坠子蒙皮到头颈骨骼，静态读顶点落在脚底下（Z 0..1，而身体 0..103）。
        // 取景只框身体与贴身的那些块，否则镜头为这 22 个顶点的碎片拉远，衣服缩成几个像素。
        var outfit = Parts(Box(88, 96), Box(-40, -38));
        var body = Parts(Box(0, 103));
        var (min, max) = NifPreviewModel.FramingBounds(outfit, body);
        Assert.Equal(0, min.Z);
        Assert.Equal(103, max.Z);
        // 真实包围盒仍然算上那块碎片——被剔的只是取景，画照样画
        Assert.Equal(-40, outfit.BoundsMin.Z);
    }

    [Fact]
    public void FramingBoundsKeepsLegitimatelySeparatedPieces()
    {
        // 上衣与腰带本来就隔着几十厘米，但都在身体范围内——按"形状互相距离"判会把它们误伤
        var outfit = Parts(Box(70, 95), Box(35, 45));
        var body = Parts(Box(0, 103));
        var (min, max) = NifPreviewModel.FramingBounds(outfit, body);
        Assert.Equal(0, min.Z);
        Assert.Equal(103, max.Z);
    }

    [Fact]
    public void FramingBoundsWithoutABodyFramesEverything()
    {
        // 用户选了「无身体」时没有参照物，全部形状都算数
        var outfit = Parts(Box(70, 95), Box(-40, -38));
        var (min, max) = NifPreviewModel.FramingBounds(outfit, null);
        Assert.Equal(-40, min.Z);
        Assert.Equal(95, max.Z);
    }

    [Fact]
    public void FramingBoundsFallsBackToTheWholeThingWhenNothingIsOnTheBody()
    {
        // 身体读到了而衣服整件都在它外面（异形体型、或身体根本对不上）：退回整体，
        // 至少东西在画面里，不能框出一个零尺寸的盒
        var outfit = Parts(Box(300, 320));
        var body = Parts(Box(0, 103));
        var (min, max) = NifPreviewModel.FramingBounds(outfit, body);
        Assert.Equal(0, min.Z);
        Assert.Equal(320, max.Z);
    }

    [Fact]
    public void FramingBoundsWithoutAnyModelIsDegenerate()
    {
        var (min, max) = NifPreviewModel.FramingBounds(null, null);
        Assert.Equal(Vector3.Zero, min);
        Assert.Equal(Vector3.Zero, max);
    }
}
