using System.Numerics;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using NiflySharp.Structs;

namespace SliderSorter.Core;

/// <summary>预览用的一个可独立上色的形状。一件衣服通常由好几个形状拼成（衣身、袖子、腰带…），
/// 各自引用自己的贴图，所以不能合并成一整块网格共用一个材质。</summary>
public sealed class NifShape
{
    public required string Name { get; init; }
    public required IReadOnlyList<Vector3> Positions { get; init; }
    public required IReadOnlyList<Vector3> Normals { get; init; }

    /// <summary>与 Positions 一一对应。UV 数量对不上顶点时是空表，渲染端据此退回纯色。</summary>
    public required IReadOnlyList<Vector2> Uvs { get; init; }

    public required IReadOnlyList<int> Indices { get; init; }

    /// <summary>diffuse 贴图相对 Data 根的路径（如 <c>textures\actors\character\female\xxx.dds</c>），
    /// 已归一为反斜杠；没有贴图为 null。它落在哪一层、是散文件还是在归档里，是渲染端的事。</summary>
    public string? TexturePath { get; init; }

    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }

    /// <summary>这个形状要不要连背面一起画。照 BodySlide 的口径两条：着色器的双面位，
    /// 以及模板属性里的 <c>DRAW_BOTH</c>（<c>GLSurface.cpp:1439-1458</c> 里 cullMode 就是这么定的）。
    /// 没开的还硬把背面画出来，裙摆内侧那一片只有环境光的暗面会挡在身体前面，而游戏与 BodySlide
    /// 那里是"透过去看到内衬的另一侧"。本机实测 53% 的形状开着。</summary>
    public bool DoubleSided { get; init; }

    public int TriangleCount => Indices.Count / 3;

    public bool IsTextured => TexturePath is not null && Uvs.Count == Positions.Count;
}

/// <summary>一次预览要画的全部形状，外加一个总的轴对齐包围盒用于摆相机。</summary>
public sealed class NifPreviewModel
{
    public required IReadOnlyList<NifShape> Shapes { get; init; }

    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }

    public int ShapeCount => Shapes.Count;
    public int TriangleCount => Shapes.Sum(s => s.TriangleCount);
    public int TexturedShapeCount => Shapes.Count(s => s.IsTextured);

    /// <summary>这份网格里已经有一个贴了身体贴图、而且**高到像一整个人**的形状——也就是作者把身体
    /// 一起导出了。本机 400 件抽样里 26% 属于这种，所以"垫一个身体打底"必须先问这一条，
    /// 否则那四分之一的件会出现两层身体互相穿插。</summary>
    public bool CarriesOwnBody => Shapes.Any(s => SkinShapeHeight(s) >= MinBodyShapeHeight);

    /// <summary>光看贴图名会把配件误判成"自带身体"：手套、靴子、袜子、高跟鞋这些件的源网格里也有
    /// 一块贴 femalebody 的皮肤，但那只是手或脚。本机 400 件抽样里判为"自带身体"的 147 件分成两堆——
    /// 皮肤块高 90~120 的 106 件是真身体，高 11~18 的 41 件是手脚，60~90 之间一件都没有，
    /// 所以阈值划在 60（nif 单位＝厘米，一个女体约 103 高）落在空档里。</summary>
    public const float MinBodyShapeHeight = 60f;

    /// <summary>这块皮肤形状有多高（nif 的 Z 是竖直方向）；不是皮肤形状则为 0。</summary>
    static float SkinShapeHeight(NifShape s) => s.TexturePath is not { } texture ||
        !texture.Contains("femalebody", StringComparison.OrdinalIgnoreCase) &&
        !texture.Contains("malebody", StringComparison.OrdinalIgnoreCase)
        ? 0
        : s.BoundsMax.Z - s.BoundsMin.Z;

    /// <summary>因为超出三角形预算而没画出来的形状数。非零时界面要说明这不是"衣服本来就这么点"。</summary>
    public int SkippedShapeCount { get; init; }

    /// <summary>离打底的身体超过这个距离（nif 单位＝厘米）的衣服形状，不参与取景。</summary>
    public const float OffBodyGapUnits = 20f;

    /// <summary>摆相机用的包围盒。
    /// <para>
    /// 蒙皮到四肢/头颈的配件（项链坠、护臂那类）顶点是按骨骼空间存的，静态读顶点会把它留在绑定位置——
    /// 本机实测有一件项链的四块落在脚底下。这种形状照样画（用户转着看时该发现它在那儿），
    /// 但不该拿它框镜头：为它拉远会把整件衣服压成画面中央的几个像素。
    /// </para>
    /// 参照是**打底的身体**而不是"其它形状"：一件套装的上衣和腰带本来就隔着几十厘米，
    /// 按形状互距判会把它们误判成碎片；而"在身体范围外一大截"只有一种解释。
    /// 没垫身体（用户选了「无」）时全部参与取景——那种情况下没有参照物可言。</summary>
    public static (Vector3 Min, Vector3 Max) FramingBounds(NifPreviewModel? outfit, NifPreviewModel? body)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var any = false;

        void Add(Vector3 lo, Vector3 hi)
        {
            min = Vector3.Min(min, lo);
            max = Vector3.Max(max, hi);
            any = true;
        }

        if (body is not null)
            Add(body.BoundsMin, body.BoundsMax);

        var keepWholeOutfit = body is null;
        var anyOnBody = false;
        foreach (var shape in outfit?.Shapes ?? [])
            if (keepWholeOutfit || GapBetween(shape, body!.BoundsMin, body.BoundsMax) <= OffBodyGapUnits)
            {
                anyOnBody = true;
                Add(shape.BoundsMin, shape.BoundsMax);
            }

        // 衣服整件都不在打底身体的范围里（体型对不上、或那具身体本来就不是它的参照）：
        // 只框身体的话衣服会跑到画面外，所以退回把衣服一起框住
        if (!anyOnBody && outfit?.Shapes.Count > 0)
            Add(outfit.BoundsMin, outfit.BoundsMax);

        // 两个都空（读不出网格时页面根本走不到取景）才到这里：给一个退化盒
        return any ? (min, max) : (Vector3.Zero, Vector3.Zero);
    }

    /// <summary>形状到某个轴对齐包围盒的最短距离；落在盒内（含贴边）为 0。</summary>
    static float GapBetween(NifShape shape, Vector3 boxMin, Vector3 boxMax)
    {
        static float Axis(float aMin, float aMax, float bMin, float bMax) =>
            Math.Max(0, Math.Max(aMin - bMax, bMin - aMax));
        var dx = Axis(shape.BoundsMin.X, shape.BoundsMax.X, boxMin.X, boxMax.X);
        var dy = Axis(shape.BoundsMin.Y, shape.BoundsMax.Y, boxMin.Y, boxMax.Y);
        var dz = Axis(shape.BoundsMin.Z, shape.BoundsMax.Z, boxMin.Z, boxMax.Z);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

/// <summary>用 Nifly（GPL-3.0，见 README 的许可一节）把 Creation Engine 的 .nif 读成可渲染的形状。
/// 只取几何与"用哪张贴图"：法线/遮罩/环境贴图都不读——WPF 的 Viewport3D 是顶点级光照的固定管线，
/// 没有像素着色器，那些贴图在这里没有消费者。骨骼权重与滑块形变同样不做，
/// 滑块后的样子本来就得回 BodySlide 里看。
/// <para>
/// 节点变换也不施加：实测 2342 个形状里只有 21 个带非恒等变换，而 Creation Engine 的
/// "缩放 × 旋转 × (平移 + 顶点)" 约定一旦记错轴序，代价是把 99% 正常衣服画歪。
/// </para>
/// </summary>
public static class NifPreviewLoader
{
    /// <summary>三角形预算，沿用旧版就有的 20 万这个上限（用户已经在界面上感受过它的流畅度）。
    /// 超预算的形状逐个丢弃而不是整件拒绝——实测最贵的一个 set 有 29 万个三角形。</summary>
    public const int TriangleBudget = 200_000;

    /// <summary>超过这个大小就直接拒绝：多半是带碰撞体的高模或整份场景，解析要几十秒且必然超预算。</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    public static bool TryLoad(string path, out NifPreviewModel? model, out string? error)
    {
        model = null;
        error = null;

        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception ex)
        {
            error = CoreStrings.Format("L.Core_NifLoadFail", ex.Message);
            return false;
        }
        if (size > MaxFileBytes)
        {
            error = CoreStrings.Format("L.Core_NifTooBig", size / (1024 * 1024));
            return false;
        }

        try
        {
            return LoadFrom(File.OpenRead(path), out model, out error);
        }
        catch (Exception ex)
        {
            error = CoreStrings.Format("L.Core_NifLoadFail", ex.Message);
            return false;
        }
    }

    /// <summary>从内存里的 .nif 载入。打底的身体网格通常躺在官方 BSA 里，没有磁盘路径可给。</summary>
    public static bool TryLoad(byte[] data, out NifPreviewModel? model, out string? error)
    {
        if (data.LongLength > MaxFileBytes)
        {
            model = null;
            error = CoreStrings.Format("L.Core_NifTooBig", data.LongLength / (1024 * 1024));
            return false;
        }
        try
        {
            return LoadFrom(new MemoryStream(data, writable: false), out model, out error);
        }
        catch (Exception ex)
        {
            model = null;
            error = CoreStrings.Format("L.Core_NifLoadFail", ex.Message);
            return false;
        }
    }

    static bool LoadFrom(Stream stream, out NifPreviewModel? model, out string? error)
    {
        model = null;
        error = null;
        using (stream)
        {
            var nif = new NifFile();
            try
            {
                if (nif.Load(stream) != 0)
                {
                    error = CoreStrings.Get("L.Core_NifLoadRejected");
                    return false;
                }

                var shapes = new List<NifShape>();
                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                var used = 0;
                var skipped = 0;

                foreach (var shape in nif.GetShapes())
                {
                    // 单个形状读崩不该赔掉整件衣服：NIF 的变体太多（作者手改、别的工具导出、只支持到某个版本）
                    NifShape? read;
                    try
                    {
                        read = ReadShape(nif, shape);
                    }
                    catch (Exception)
                    {
                        read = null;
                    }
                    if (read is null)
                        continue;

                    if (used + read.TriangleCount > TriangleBudget)
                    {
                        skipped++;
                        continue;
                    }

                    used += read.TriangleCount;
                    min = Vector3.Min(min, read.BoundsMin);
                    max = Vector3.Max(max, read.BoundsMax);
                    shapes.Add(read);
                }

                if (shapes.Count == 0)
                {
                    // 一个都没留下：被预算挡掉过就说三角形太多，否则是真没有可读的三角形网格。
                    // 没有着色器的辅助形状不算"被挡掉"，所以不会误报成网格过大
                    error = skipped > 0
                        ? CoreStrings.Format("L.Core_NifTooBig", TriangleBudget)
                        : CoreStrings.Get("L.Core_NifNoGeometry");
                    return false;
                }


                model = new NifPreviewModel
                {
                    Shapes = shapes,
                    BoundsMin = min,
                    BoundsMax = max,
                    SkippedShapeCount = skipped,
                };
                return true;
            }
            catch (Exception ex)
            {
                error = CoreStrings.Format("L.Core_NifLoadFail", ex.Message);
                return false;
            }
        }
    }

    static NifShape? ReadShape(NifFile nif, INiShape shape)
    {
        var (verts, normals, uvs) = GeometryOf(shape);
        if (verts is not { Count: > 0 })
            return null;
        if (shape.Triangles is not { Count: > 0 } tris)
            return null;

        // 没有着色器的形状在游戏里也不会被画出来——那是碰撞体、NifSkope 的 VirtualGround、
        // Blender 导出残留的 boxfull 一类辅助网格。留着它们只会让预览里多出几块莫名其妙的灰面。
        var shader = ReadShader(nif, shape);
        if (shader is null)
            return null;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in verts)
        {
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }

        var indices = new List<int>(tris.Count * 3);
        foreach (var tri in tris)
        {
            // 越界索引会让 WPF 在渲染线程绘制时才抛，那里捕获不到，所以先挡掉
            if (tri.V1 >= verts.Count || tri.V2 >= verts.Count || tri.V3 >= verts.Count)
                continue;
            indices.Add(tri.V1);
            indices.Add(tri.V2);
            indices.Add(tri.V3);
        }
        if (indices.Count == 0)
            return null;

        return new NifShape
        {
            Name = NameOf(shape),
            Positions = verts,
            // 法线数量对不上（导出工具只写了一半）就自己按面算，否则整个形状渲染成纯黑
            Normals = normals is { Count: > 0 } && normals.Count == verts.Count ? normals : ComputeFlatNormals(verts, tris),
            Uvs = uvs is { Count: > 0 } && uvs.Count == verts.Count ? uvs : Array.Empty<Vector2>(),
            Indices = indices,
            TexturePath = TexturePathOf(nif, shader),
            DoubleSided = DoubleSidedOf(nif, shape, shader),
            BoundsMin = min,
            BoundsMax = max,
        };
    }

    /// <summary>这个形状要不要连背面一起画。照 BodySlide 的口径两条：着色器的双面位，
    /// 以及模板属性里的 <c>DRAW_BOTH</c>（<c>GLSurface.cpp:1439-1458</c> 里 cullMode 就是这么定的）。
    /// 取属性数组可能像 <see cref="ReadShader"/> 那样抛（有的导出工具干脆不给 Properties 赋值），就地兜住。</summary>
    static bool DoubleSidedOf(NifFile nif, INiShape shape, INiShader shader)
    {
        if (shader.DoubleSided)
            return true;
        try
        {
            return nif.GetPropertyOfType<NiStencilProperty>(shape)?.DrawMode == StencilDrawMode.DRAW_BOTH;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 取一个 shape 的顶点、法线与 UV。Creation Engine 的网格有两套存放位置：
    /// BSTriShape 及其派生（BSMeshLODTriShape / BSSubIndexTriShape / BSDynamicTriShape）把顶点数据内联在自己身上，
    /// 而老式的 NiGeometry 派生（含 BSDismemberedShape）放在独立的 GeometryData 块里。
    /// INiShape 接口本身不暴露顶点，所以只能按具体类型分派。
    /// </summary>
    static (List<Vector3>? Vertices, List<Vector3>? Normals, List<Vector2>? Uvs) GeometryOf(INiShape shape)
    {
        switch (shape)
        {
            case BSTriShape tri:
                return (tri.VertexPositions, tri.Normals, Of(tri.UVs));
            case NiGeometry legacy:
                var data = legacy.GeometryData;
                return (data?.Vertices, data?.Normals, Of(data?.UVSets));
            default:
                return (null, null, null);
        }

        // Nifly 把 UV 存成它自己的 TexCoord（字段就叫 U/V），这里换成 System.Numerics 的 Vector2
        static List<Vector2>? Of(List<TexCoord>? coords) =>
            coords?.ConvertAll(c => new Vector2(c.U, c.V));
    }

    /// <summary>取形状上的着色器。Nifly 的 GetShader 会遍历 Properties，而有些导出工具
    /// 干脆不给这个数组赋值（实测会抛 NullReferenceException），所以只能就地兜住。</summary>
    static INiShader? ReadShader(NifFile nif, INiShape shape)
    {
        try
        {
            return nif.GetShader(shape);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>取这个形状要贴的那张 diffuse。Creation Engine 的贴图槽 0 恒为 diffuse——
    /// 实测 2342 个形状，槽 0 为空时其余 8 个槽也全空，所以不需要"按词干猜哪张是 diffuse"那套兜底。
    /// 特效着色器（BSEffectShaderProperty）不走贴图集，贴图直接挂在 SourceTexture 上。</summary>
    static string? TexturePathOf(NifFile nif, INiShader shader)
    {
        if (shader.HasTextureSet)
        {
            var set = nif.GetBlock<BSShaderTextureSet>(shader.TextureSetRef);
            if (set?.Textures is { Count: > 0 } list)
                if (Clean(list[0]?.Content) is { } path)
                    return path;
        }
        return shader is BSEffectShaderProperty effect ? Clean(effect.SourceTexture.Content) : null;
    }

    /// <summary>归一贴图路径：反斜杠、去掉开头的分隔符（有的作者写成 <c>\textures\...</c>）。
    /// 大小写原样保留——查表按忽略大小写比，但显示出来要跟模组作者写的一致。</summary>
    static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var s = raw.Trim().Replace('/', '\\').TrimStart('\\');
        return s.Length == 0 ? null : s;
    }

    static string NameOf(INiShape shape) =>
        shape is NiObjectNET { Name.String: { Length: > 0 } } named ? named.Name.String : shape.GetType().Name;

    /// <summary>没有法线时按面算平法线再累加到顶点上（相邻面共享顶点会平均一次，得到略圆化的效果）。
    /// 退化三角形（叉积为零）跳过；没有任何有效面的顶点给个固定朝向，免得渲染成纯黑。</summary>
    static List<Vector3> ComputeFlatNormals(IReadOnlyList<Vector3> verts, IReadOnlyList<Triangle> tris)
    {
        var result = new Vector3[verts.Count];

        foreach (var tri in tris)
        {
            if (tri.V1 >= verts.Count || tri.V2 >= verts.Count || tri.V3 >= verts.Count)
                continue;
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
