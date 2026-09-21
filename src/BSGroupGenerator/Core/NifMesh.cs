using System.Numerics;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;

namespace BSGroupGenerator.Core;

/// <summary>预览用的三角网格：位置与法线一一对应，索引每三个一组，外加一个轴对齐包围盒用于摆相机。</summary>
public sealed class NifMesh
{
    public required IReadOnlyList<Vector3> Positions { get; init; }
    public required IReadOnlyList<Vector3> Normals { get; init; }
    public required IReadOnlyList<int> Indices { get; init; }

    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }

    /// <summary>合并进来的形状数（一件衣服常由多个 shape 组成）。</summary>
    public int ShapeCount { get; init; }

    public int TriangleCount => Indices.Count / 3;
}

/// <summary>用 Nifly（GPL-3.0，见 README 的许可一节）把 Creation Engine 的 .nif 读成可渲染的三角形。
/// 只取几何：材质、纹理、骨骼权重与滑块变形都不做——预览要回答的是"这件衣服长什么样"，
/// 而滑块后的样子本来就得回 BodySlide 里看。</summary>
public static class NifMeshLoader
{
    /// <summary>三角形上限。再大 WPF 的 Viewport3D 转起来就卡成幻灯片，而预览也看不出更多细节。</summary>
    public const int MaxTriangles = 200_000;

    /// <summary>超过这个大小就直接拒绝：多半是带碰撞体的高模或整份场景，解析要几十秒且必然超限。</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    public static bool TryLoad(string path, out NifMesh? mesh, out string? error)
    {
        mesh = null;
        error = null;

        try
        {
            var size = new FileInfo(path).Length;
            if (size > MaxFileBytes)
            {
                error = CoreStrings.Format("L.Core_NifTooBig", size / (1024 * 1024));
                return false;
            }
        }
        catch (Exception ex)
        {
            error = CoreStrings.Format("L.Core_NifLoadFail", ex.Message);
            return false;
        }

        var nif = new NifFile();
        try
        {
            if (nif.Load(path) != 0)
            {
                error = CoreStrings.Get("L.Core_NifLoadRejected");
                return false;
            }

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var indices = new List<int>();
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            var shapes = 0;

            foreach (var shape in nif.GetShapes())
            {
                var (verts, shapeNormals) = GeometryOf(shape);
                var tris = shape.Triangles;
                if (verts is null || tris is null || verts.Count == 0 || tris.Count == 0)
                    continue;

                if (indices.Count / 3 + tris.Count > MaxTriangles)
                {
                    error = CoreStrings.Format("L.Core_NifTooBig", MaxTriangles);
                    return false;
                }

                var baseIndex = positions.Count;
                positions.AddRange(verts);
                for (var i = 0; i < verts.Count; i++)
                {
                    min = Vector3.Min(min, verts[i]);
                    max = Vector3.Max(max, verts[i]);
                }

                var source = shapeNormals is { Count: > 0 } && shapeNormals.Count == verts.Count
                    ? shapeNormals
                    : null;
                if (source is not null)
                    normals.AddRange(source);
                else
                    normals.AddRange(ComputeFlatNormals(verts, tris));

                foreach (var tri in tris)
                {
                    indices.Add(baseIndex + tri.V1);
                    indices.Add(baseIndex + tri.V2);
                    indices.Add(baseIndex + tri.V3);
                }
                shapes++;
            }

            if (shapes == 0)
            {
                error = CoreStrings.Get("L.Core_NifNoGeometry");
                return false;
            }

            mesh = new NifMesh
            {
                Positions = positions,
                Normals = normals,
                Indices = indices,
                BoundsMin = min,
                BoundsMax = max,
                ShapeCount = shapes,
            };
            return true;
        }
        catch (Exception ex)
        {
            // NIF 变体太多（作者手改、别的工具导出、只支持到某个版本），任何解析异常都当成"这个看不了"，
            // 让界面能说清是哪一件、为什么，而不是整个预览坏掉
            error = CoreStrings.Format("L.Core_NifLoadFail", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 取一个 shape 的顶点与法线。Creation Engine 的网格有两套存放位置：
    /// BSTriShape 及其派生（BSMeshLODTriShape / BSSubIndexTriShape / BSDynamicTriShape）把顶点数据内联在自己身上，
    /// 而老式的 NiGeometry 派生（含 BSDismemberedShape）放在独立的 GeometryData 块里。
    /// INiShape 接口本身不暴露顶点，所以只能按具体类型分派。
    /// </summary>
    static (List<Vector3>? Vertices, List<Vector3>? Normals) GeometryOf(INiShape shape) => shape switch
    {
        BSTriShape tri => (tri.VertexPositions, tri.Normals),
        NiGeometry legacy => (legacy.GeometryData?.Vertices, legacy.GeometryData?.Normals),
        _ => (null, null),
    };

    /// <summary>没有法线时按面算平法线再累加到顶点上（相邻面共享顶点会平均一次，得到略圆化的效果）。
    /// 退化三角形（叉积为零）跳过；没有任何有效面的顶点给个固定朝向，免得渲染成纯黑。</summary>
    static List<Vector3> ComputeFlatNormals(IReadOnlyList<Vector3> verts, IReadOnlyList<Triangle> tris)
    {
        var result = new Vector3[verts.Count];

        foreach (var tri in tris)
        {
            var a = verts[tri.V1];
            var b = verts[tri.V2];
            var c = verts[tri.V3];
            var face = Vector3.Cross(b - a, c - a);
            var length = face.Length();
            if (length <= float.Epsilon)
                continue;
            face /= length;

            for (var k = 0; k < 3; k++)
            {
                var index = k switch { 0 => tri.V1, 1 => tri.V2, _ => tri.V3 };
                result[index] += face;
            }
        }

        var normals = new List<Vector3>(verts.Count);
        for (var i = 0; i < verts.Count; i++)
        {
            var v = result[i];
            var length = v.Length();
            // 孤立顶点（不在任何有效三角形里）给个固定朝向：零法线在 WPF 里会渲染成纯黑块
            normals.Add(length > float.Epsilon ? v / length : Vector3.UnitZ);
        }
        return normals;
    }
}
