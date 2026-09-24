using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using SliderSorter.Core;
using SliderSorter.Wpf.Views.Pages;
using Xunit;

namespace SliderSorter.Wpf.Tests;

/// <summary>背面材质按不按 BodySlide 的口径设 —— 这条决定"裙摆内侧是暗面挡在前面，还是透过去看内衬"。
/// <para>
/// 走的是页面里那个真的把形状变成几何＋材质的私有静态方法，而不是在测试里复刻一份判断：
/// 复刻的那份永远会通过，而绑定/条件写反时界面照画不误，只有真代码能被测到。
/// </para>
/// 挂 <see cref="WpfStaCollection"/> 串行：Model3D 是 DependencyObject，只能在宿主那条线程上造，
/// 而且**断言也得在那条线程上做完**——把 Model3D 递回 xUnit 线程再读属性会撞 VerifyAccess。</summary>
[Collection(WpfStaCollection.Name)]
public class PreviewMaterialTests
{
    private static NifShape Shape(bool doubleSided) => new()
    {
        Name = "skirt",
        Positions = [new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(10, 0, 10)],
        Normals = [new Vector3(0, -1, 0), new Vector3(0, -1, 0), new Vector3(0, -1, 0)],
        Uvs = Array.Empty<Vector2>(),
        Indices = [0, 1, 2],
        TexturePath = null,
        BoundsMin = new Vector3(0, 0, 0),
        BoundsMax = new Vector3(10, 0, 10),
        DoubleSided = doubleSided,
    };

    private sealed record Built(bool HasMaterial, bool HasBackMaterial, Point3D ThirdPosition, Vector3D ThirdNormal);

    /// <summary>调 OutputConflictPage.AddShape（私有静态），把结果收成跨线程安全的值类型再交回用例。</summary>
    private static Built AddShape(bool doubleSided) => WpfHost.WithWindow(() => new Border(), _ =>
    {
        var group = new Model3DGroup();
        typeof(OutputConflictPage)
            .GetMethod("AddShape", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [group, Shape(doubleSided), null]);
        var model = Assert.IsType<GeometryModel3D>(Assert.Single(group.Children));
        var geometry = Assert.IsType<MeshGeometry3D>(model.Geometry);
        return new Built(model.Material is not null, model.BackMaterial is not null,
            geometry.Positions[2], geometry.Normals[2]);
    });

    [Fact]
    public void SingleSidedShapeGetsNoBackMaterialSoWpfCullsIt()
    {
        // BodySlide 对单面网格是 glCullFace(GL_BACK)。WPF 里等价的做法是不设 BackMaterial ——
        // 实测那样背面整个不渲染（离屏回读覆盖 0 像素），不是画成黑的。
        var built = AddShape(doubleSided: false);
        Assert.True(built.HasMaterial);
        Assert.False(built.HasBackMaterial);
    }

    [Fact]
    public void DoubleSidedShapeDrawsTheInsideToo()
    {
        // 本机实测 53% 的形状开着双面位（裙摆、披风、布料类基本都开），这些必须两面都画
        Assert.True(AddShape(doubleSided: true).HasBackMaterial);
    }

    [Fact]
    public void NormalsFollowTheSameAxisSwapAsPositions()
    {
        // nif 的 Z 朝上换成 WPF 的 Y 朝上：位置与法线必须走同一个映射，否则光照会跟几何错位
        var built = AddShape(doubleSided: true);
        Assert.Equal(new Point3D(10, 10, 0), built.ThirdPosition);   // nif (10,0,10) → (x, z, -y)
        Assert.Equal(new Vector3D(0, 0, 1), built.ThirdNormal);      // nif (0,-1,0) → (0,0,1)
    }
}
