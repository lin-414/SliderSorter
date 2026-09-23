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

    /// <summary>相对路径 → <see cref="_archivePaths"/> 下标。按层序从强到弱插入，先见的获胜。</summary>
    private Dictionary<string, int>? _archiveIndex;
    private readonly List<string> _archivePaths = new();

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

        foreach (var root in _roots)
        {
            var full = Combine(root, rel);
            if (full is null)
                continue;
            try
            {
                if (File.Exists(full))
                    return File.ReadAllBytes(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // 这一层读不到不影响别的层：接着往下找
            }
        }

        return TryReadFromArchive(rel);
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

    private byte[]? TryReadFromArchive(string rel)
    {
        var index = EnsureArchiveIndex();
        if (index is null || !index.TryGetValue(rel, out var archiveId))
            return null;

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

    private Dictionary<string, int>? EnsureArchiveIndex()
    {
        lock (_gate)
        {
            if (_archiveIndex is not null)
                return _archiveIndex;

            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // 清单必须按层序串行收集：同名文件归谁由层序决定，并行填字典会把胜负顺序写乱
            // （和 SliderSetScanner.BuildShapeDataIndex 同一套规矩）
            var paths = new List<string>();
            foreach (var root in _roots)
            {
                foreach (var pattern in new[] { "*.bsa", "*.ba2" })
                {
                    try
                    {
                        if (!Directory.Exists(root))
                            continue;
                        foreach (var file in Directory.GetFiles(root, pattern))
                            paths.Add(file);
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

            for (var i = 0; i < names.Length; i++)
            {
                if (names[i] is null)
                    continue;
                _archivePaths.Add(paths[i]);
                var id = _archivePaths.Count - 1;
                foreach (var name in names[i])
                    index.TryAdd(Normalize(name) ?? name, id); // 先见的强层获胜
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
