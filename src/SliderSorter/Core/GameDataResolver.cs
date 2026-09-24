using System.Threading.Tasks;

namespace SliderSorter.Core;

/// <summary>
/// 按 Creation Engine 的口径解析数据文件：一个相对 Data 根的路径（<c>textures\...\x.dds</c>、
/// <c>meshes\...\y.nif</c>）跨 MO2 覆盖层从强到弱找，每层先看散文件、再看该层的归档
/// （天际 SE/AE 的 .bsa，辐射4 的 .ba2）。这正是游戏在 usvfs 下看到的视图，
/// 所以预览里的贴图应当和进游戏看到的一致。
/// <para>
/// 层根目录就是各模组的根目录本身（MO2 的模组目录等价于 Data），最后一层是真实的 Data 目录。
/// </para>
/// 线程安全：预览会在后台线程上被反复触发，归档索引只建一次并用锁保护。
/// </summary>
public sealed class GameDataResolver : IDisposable
{
    private readonly IReadOnlyList<string> _roots;
    private readonly object _gate = new();

    /// <summary>相对路径 → 含它的归档下标列表，按层从强到弱（同层内先枚举到的那个）。
    /// 必须是列表而不是单个下标：判定"这一层有没有"要按层问，把整张表压成"最强层那一个"
    /// 就退化成"所有层的散文件都优先于任何归档"（见 <see cref="TryRead"/>）。</summary>
    private Dictionary<string, List<int>>? _archiveIndex;
    private readonly List<string> _archivePaths = new();
    private readonly List<int> _archiveLayer = new();

    public GameDataResolver(IReadOnlyList<string> roots)
    {
        _roots = roots;
    }

    /// <summary>读出这个数据相对路径的内容；哪一层都没有时返回 null。
    /// 只给字节：DDS 解码、缓存策略都是消费端的事。</summary>
    public byte[]? TryRead(string relativePath)
    {
        if (Normalize(relativePath) is not { } rel)
            return null;

        // 一层一层往下走，层内先散文件、再查这一层的归档——游戏与 MO2 的 usvfs 都是这个口径。
        // 反过来（全部层的散文件读完才碰归档）会让强层打进 .bsa 的贴图被弱层的同名散文件盖掉，
        // 预览于是和进游戏所见不一致，而用户正是照着预览在冲突页选赢家。
        for (var layer = 0; layer < _roots.Count; layer++)
        {
            if (TryReadLoose(_roots[layer], rel) is { } loose)
                return loose;
            if (TryReadFromArchive(rel, layer) is { } archived)
                return archived;
        }

        return null;
    }

    /// <summary>这一层根目录下的散文件。读不到（不存在、被占用、路径非法）返回 null，由调用方接着找下一层。</summary>
    private static byte[]? TryReadLoose(string root, string rel)
    {
        if (Combine(root, rel) is not { } full)
            return null;
        try
        {
            return File.Exists(full) ? File.ReadAllBytes(full) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>归一数据相对路径：正斜杠换反斜杠、去掉开头的 <c>.\</c> 与多余分隔符（作者写法不一）、
    /// 压掉首尾空白。空串、含盘符、或是 UNC 共享（<c>\\server\...</c>）时返回 null——
    /// 这个类只接受相对 Data 根的路径，UNC 必须先于去前导斜杠判掉，否则 <c>\\nas\a</c> 会被削成
    /// <c>nas\a</c> 当成合法相对路径放过去。</summary>
    public static string? Normalize(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;
        var s = relativePath.Trim().Replace('/', '\\');
        if (s.StartsWith(@"\\", StringComparison.Ordinal) || s.Contains(':'))
            return null;
        s = s.TrimStart('\\', '.');
        return s.Length == 0 ? null : s;
    }

    private static string? Combine(string root, string rel)
    {
        try
        {
            return Path.Combine(root, rel);
        }
        catch (Exception)
        {
            return null; // 路径过长或含非法字符：当作这一层没有
        }
    }

    /// <summary>只在指定层里找这个相对路径的归档条目；该层没有归档含它时返回 null（不是"没有这个文件"，
    /// 调用方还要继续往下层走）。</summary>
    private byte[]? TryReadFromArchive(string rel, int layer)
    {
        var index = EnsureArchiveIndex();
        if (index is null || !index.TryGetValue(rel, out var ids))
            return null;

        // ids 按层从强到弱追加，同层只留一个（先枚举到的那个归档）
        foreach (var id in ids)
        {
            if (_archiveLayer[id] == layer)
                return ReadEntryFromArchive(id, rel);
        }
        return null;
    }

    private byte[]? ReadEntryFromArchive(int archiveId, string rel)
    {
        // 每次重新打开归档、按路径找回条目：一次预览只有个位数张贴图，而整合包里有几百个归档，
        // 常驻文件句柄不值当；解出来的位图由渲染端缓存，所以这里不会被反复走到。
        try
        {
            var archive = OpenArchive(_archivePaths[archiveId]);
            if (archive is null)
                return null;
            try
            {
                var entry = archive.Files?.FirstOrDefault(e =>
                    string.Equals(Normalize(e.FullPath), rel, StringComparison.OrdinalIgnoreCase));
                return entry?.GetDataStream() is { } stream ? ReadAll(stream) : null;
            }
            finally
            {
                archive.Close(); // Archive 不实现 IDisposable，句柄要显式还
            }
        }
        catch (Exception)
        {
            // 归档损坏、条目压缩格式不认识、文件被 MO2 占用……都当成"这张贴图拿不到"，
            // 让那一个形状退回纯色，而不是把整件预览赔进去
            return null;
        }
    }

    private static byte[]? ReadAll(MemoryStream stream)
    {
        if (stream.Length == 0)
            return null;
        // GetDataStream 返回的是整个解压后的 MemoryStream，ToArray 会再拷一份；
        // 一张 2K 的 DXT5 约 5 MB，预览期间只有个位数张，可以接受
        return stream.ToArray();
    }

    private Dictionary<string, List<int>>? EnsureArchiveIndex()
    {
        lock (_gate)
        {
            if (_archiveIndex is not null)
                return _archiveIndex;

            var index = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            // 清单必须按层序串行收集：同名文件归谁由层序决定，并行填字典会把胜负顺序写乱
            // （和 SliderSetScanner.BuildShapeDataIndex 同一套规矩）。记下每条来自哪一层，
            // TryRead 才问得出"这一层有没有归档含它"。
            var layers = new List<int>();
            var paths = new List<string>();
            for (var layer = 0; layer < _roots.Count; layer++)
            {
                var root = _roots[layer];
                foreach (var pattern in new[] { "*.bsa", "*.ba2" })
                {
                    try
                    {
                        if (!Directory.Exists(root))
                            continue;
                        foreach (var file in Directory.GetFiles(root, pattern))
                        {
                            paths.Add(file);
                            layers.Add(layer);
                        }
                    }
                    catch (Exception)
                    {
                        // 单层读不到不影响别的层
                    }
                }
            }

            // 读文件名表（不读数据）可以并行：这一步在 2667 层的整合包上约一秒
            var names = new string[paths.Count][];
            Parallel.For(0, paths.Count, i =>
            {
                var archive = OpenArchive(paths[i]);
                if (archive is null)
                    return;
                try
                {
                    names[i] = archive.Files?
                        .Where(e => !string.IsNullOrEmpty(e.FullPath))
                        .Select(e => e.FullPath!)
                        .ToArray() ?? [];
                }
                finally
                {
                    archive.Close();
                }
            });

            // 装配仍是串行的、按 layers 的强到弱顺序，所以每个键下的下标表天然按层序排好
            for (var i = 0; i < names.Length; i++)
            {
                if (names[i] is null)
                    continue;
                _archivePaths.Add(paths[i]);
                _archiveLayer.Add(layers[i]);
                var id = _archivePaths.Count - 1;
                foreach (var name in names[i])
                {
                    if (Normalize(name) is not { } key)
                        continue;
                    if (!index.TryGetValue(key, out var list))
                        index[key] = list = [];
                    // 同一层里多个归档含同一个文件时先见的赢；换层了才再记一个
                    if (list.Count == 0 || _archiveLayer[list[^1]] != layers[i])
                        list.Add(id);
                }
            }

            _archiveIndex = index;
            return index;
        }
    }

    /// <summary>开一个归档只读文件名表。打不开（版本太老、文件损坏、不是归档）返回 null。</summary>
    private static SharpBSABA2.Archive? OpenArchive(string path)
    {
        try
        {
            return path.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase)
                ? new SharpBSABA2.BA2Util.BA2(path)
                : new SharpBSABA2.BSAUtil.BSA(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        // 归档都是即用即关，这里没有常驻句柄要还
    }
}
