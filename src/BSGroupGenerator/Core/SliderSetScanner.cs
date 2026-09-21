using System.Text;
using System.Xml;

namespace BSGroupGenerator.Core;

public class OutfitEntry
{
    public required string Name { get; init; }
    public required string OwnerLabel { get; init; }
    public required string SourceFile { get; init; }
    /// <summary>同样的滑块组名在更弱的覆盖层（或更后的文件）里也出现过。</summary>
    public bool HasConflict { get; set; }

    /// <summary>BodySlide 写出的目标路径（不含扩展名），见 <see cref="SliderSetInfo.BuildOutputFilePath"/>；
    /// 该 set 没写 &lt;OutputPath&gt; 时为 null，BodySlide 也不把它计入输出文件冲突。</summary>
    public string? OutputFilePath { get; init; }

    /// <summary>BodySlide 是否为其生成带权重的 _0/_1 双份 nif（&lt;OutputFile GenWeights&gt;，缺省为 true）。</summary>
    public bool GenWeights { get; init; } = true;

    /// <summary>发现该 set 的覆盖层序号（0 = 最强模组）。层序即文件冲突时的胜负顺序。</summary>
    public int LayerIndex { get; init; }

    /// <summary>有**别的模组**的 set 与本 set 写同一个输出文件（BodySlide 的输出文件冲突）。
    /// 同一模组内部多个预设共用一个输出文件不算——那是模组自己的配色变体，标注出来只会淹没树列表。</summary>
    public bool HasOutputConflict { get; set; }

    /// <summary>该 set 的源网格（BodySlide 建它时读的 .nif）在本机磁盘上的实际位置；
    /// 按 <c>&lt;项目路径&gt;\ShapeData\&lt;DataFolder&gt;\&lt;SourceFile&gt;</c> 跨覆盖层从强到弱解析，找不到为 null。</summary>
    public string? SourceNif { get; init; }
}

public class ScanResult
{
    public List<OutfitEntry> Outfits { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> LayerNotes { get; } = new();
    public int WinnerFileCount { get; set; }
}

/// <summary>一个 &lt;SliderSet&gt; 的解析产物；OutputFilePath 为 null 表示该 set 没写 &lt;OutputPath&gt;。</summary>
public sealed record SliderSetInfo(
    string Name,
    string? OutputFilePath,
    bool GenWeights,
    string? DataFolder = null,
    string? SourceFile = null);

public enum ScanPhase
{
    /// <summary>逐模组枚举覆盖层目录。</summary>
    Enumerating,
    /// <summary>解析获胜的滑块组文件。</summary>
    Parsing,
}

/// <summary>扫描进度：Current/Total 按阶段计；FilesFound 为至今发现的滑块组文件数。</summary>
public sealed record ScanProgress(ScanPhase Phase, int Current, int Total, int FilesFound);

/// <summary>
/// 按 BodySlide 的实际行为扫描服装（滑块组）：
/// 有效项目路径若是虚拟 Data 之下的目录（MO2 常态），则按 modlist 优先级模拟 USVFS 覆盖——
/// 相对路径相同的文件由更强的模组获胜；然后对获胜文件解析 &lt;SliderSet name="..."&gt;，
/// 同名滑块组先见者胜（与 BodySlideApp::LoadSliderSets 一致，成员名大小写敏感、不做任何变换）。
/// 顺带读 &lt;OutputPath&gt; / &lt;OutputFile&gt;，按 BodySlide 的口径拼出每个 set 的输出文件路径，
/// 供「输出文件冲突」判定使用（见 <see cref="OutputConflicts"/>）。
/// 只扫各层 <c>SliderSets</c> 目录下的 *.xml / *.osp——BodySlide 也只认这一处。
/// </summary>
public static class SliderSetScanner
{
    /// <summary>
    /// 解析阶段的默认并行度：按处理器数，但封顶 8。
    /// 超过 8 路并发读文件的收益已很有限（实测 1000 个文件 31 MB：8 路与 32 路相差不大），
    /// 而机械盘上并发过多会因寻道抖动反而变慢。
    /// </summary>
    private static readonly int DefaultParseParallelism =
        Math.Max(1, Math.Min(Environment.ProcessorCount, 8));

    /// <summary>滑块组文件所在子目录名（BodySlide 的固定约定）。</summary>
    private const string SliderSetsDirName = "SliderSets";

    /// <param name="parseParallelism">解析阶段的并行度；0 = 自动（见 <see cref="DefaultParseParallelism"/>）。</param>
    public static ScanResult Scan(ProjectPathResolution resolution, List<(ModEntry Entry, string Dir)> enabledMods,
        IProgress<ScanProgress>? progress = null, int parseParallelism = 0)
    {
        var result = new ScanResult();

        // 覆盖层：从强到弱
        var layers = new List<(string Label, string Dir)>();
        var suffix = ProjectPathKind.AppDir == resolution.Kind
            ? null
            : BodySlideLocator.GetSuffixUnder(resolution.EffectivePath, resolution.GameDataPath);

        if (suffix is not null && !string.IsNullOrWhiteSpace(resolution.GameDataPath))
        {
            // 层目录取 <模组>\<后缀>\SliderSets，而不是 <模组>\<后缀>：
            // BodySlide 只从 <项目路径>\SliderSets 读滑块组，CalienteTools\BodySlide 下同级的
            // SliderCategories / SliderGroups / SliderPresets / RefTemplates / PoseData 等都不是滑块组文件，
            // 递归进去只会白读一遍（实测约占 9%），还会把它们的语法错误报成"无法解析"。
            var layerSuffix = Path.Combine(suffix, SliderSetsDirName);
            foreach (var (_, dir) in enabledMods)
                layers.Add(($"{Path.GetFileName(dir.TrimEnd('\\', '/'))}", Path.Combine(dir, layerSuffix)));
            layers.Add((CoreStrings.Get("L.Core_LayerGameData"), Path.Combine(resolution.GameDataPath, layerSuffix)));
            result.LayerNotes.Add(CoreStrings.Format("L.Core_ScanVirtualLayers", layerSuffix, layers.Count, enabledMods.Count));
        }
        else
        {
            layers.Add((CoreStrings.Get("L.Core_LayerBodySlide"), Path.Combine(resolution.EffectivePath, SliderSetsDirName)));
            result.LayerNotes.Add(CoreStrings.Format("L.Core_ScanSingleDir", layers[0].Dir));
        }

        // 源网格（预览要读的 .nif）与滑块组文件一样是跨层汇聚的：BodySlide 用的是
        // <项目路径>\ShapeData，MO2 启动时那是虚拟 Data 的合并视图，所以按同一套层序从强到弱找第一个存在的。
        var shapeDataRoots = layers
            .Select(l => ShapeDataDirFor(l.Dir))
            .Where(d => d is not null)
            .Select(d => d!)
            .ToList();

        // 相对路径 → 最强层的文件
        var winners = new List<(string RelPath, string Label, string FullPath, int LayerIndex)>();
        var winnerByRel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var filesFound = 0;

        for (var layerIndex = 0; layerIndex < layers.Count; layerIndex++)
        {
            var (label, dir) = layers[layerIndex];
            if (!Directory.Exists(dir))
            {
                progress?.Report(new ScanProgress(ScanPhase.Enumerating, layerIndex + 1, layers.Count, filesFound));
                continue;
            }
            var layerFileCount = 0;
            List<string> files;
            try
            {
                // 一次遍历所有文件再按扩展名过滤；忽略无权限目录，避免个别目录异常中断整个扫描
                files = Directory.EnumerateFiles(
                        dir, "*",
                        new EnumerationOptions
                        {
                            RecurseSubdirectories = true,
                            IgnoreInaccessible = true,
                        })
                    .Where(f =>
                    {
                        var ext = Path.GetExtension(f);
                        return ext.Equals(".xml", StringComparison.OrdinalIgnoreCase)
                            || ext.Equals(".osp", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                result.Warnings.Add(CoreStrings.Format("L.Core_ScanEnumFail", dir, ex.Message));
                continue;
            }

            // 层内文件顺序决定同名滑块组的归属（先见者胜），必须确定：显式按相对路径排序，
            // 对齐 BodySlide 的 wxDir::GetAllFiles 在 NTFS 上返回的字面序，避免依赖文件系统枚举顺序。
            files.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                var rel = BodySlideLocator.GetSuffixUnder(file, dir);
                if (rel is null)
                    continue;
                layerFileCount++;
                if (winnerByRel.TryAdd(rel, label))
                    winners.Add((rel, label, file, layerIndex));
            }
            filesFound += layerFileCount;
            result.LayerNotes.Add(CoreStrings.Format("L.Core_ScanLayerFiles", label, layerFileCount));
            progress?.Report(new ScanProgress(ScanPhase.Enumerating, layerIndex + 1, layers.Count, filesFound));
        }

        result.WinnerFileCount = winners.Count;

        // 解析获胜文件中的滑块组名。文件之间彼此独立，可并行；但"同名先见者胜"依赖 winners 的顺序，
        // 所以**按索引收集、再按原顺序合并**，绝不能边解析边写共享字典——否则结果随线程调度而变。
        // 实测（1000 个文件 / 31 MB，801 层）：端到端 166 ms → 47 ms；
        // 其中解析阶段并行度 1 为 134 ms、默认并行度 55 ms（2.45×）。
        var setsPerFile = new List<SliderSetInfo>[winners.Count];
        var errorPerFile = new string?[winners.Count];
        var parsed = 0;
        var parallelism = parseParallelism > 0 ? parseParallelism : DefaultParseParallelism;
        progress?.Report(new ScanProgress(ScanPhase.Parsing, 0, winners.Count, winners.Count));

        Parallel.For(0, winners.Count,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            i =>
            {
                var sets = new List<SliderSetInfo>();
                errorPerFile[i] = ReadSliderSets(winners[i].FullPath, sets, Describe(winners[i].Label, winners[i].RelPath));
                setsPerFile[i] = sets;
                // 节流：Progress<T> 每次 Report 都会投递到 UI 线程，不能逐个上报
                var done = Interlocked.Increment(ref parsed);
                if (done % 32 == 0 || done == winners.Count)
                    progress?.Report(new ScanProgress(ScanPhase.Parsing, done, winners.Count, winners.Count));
            });

        // 结果顺序 = winners 顺序 → 文件内出现顺序，显式用 List 承载（不靠 Dictionary 的枚举顺序：
        // 那是实现细节，换个运行时/实现就可能变，而"先见者胜"是必须稳定的对外行为）。
        // HashSet 负责判重，firstIndex 只用于回查首个条目以打冲突标记。
        var outfits = new List<OutfitEntry>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var firstIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < winners.Count; i++)
        {
            var (rel, label, _, layerIndex) = winners[i];
            // 警告也按文件顺序合并，保证多次扫描的警告次序稳定
            if (errorPerFile[i] is { } error)
                result.Warnings.Add(error);
            foreach (var set in setsPerFile[i])
            {
                if (!seenNames.Add(set.Name))
                {
                    outfits[firstIndex[set.Name]].HasConflict = true;
                    // 重名的第二个 set 对 BodySlide 而言根本不会被加载（先见者胜），
                    // 所以它声明的输出路径也不参与输出文件冲突——不能记在这里。
                    continue;
                }
                firstIndex[set.Name] = outfits.Count;
                outfits.Add(new OutfitEntry
                {
                    Name = set.Name,
                    OwnerLabel = label,
                    SourceFile = rel,
                    OutputFilePath = set.OutputFilePath,
                    GenWeights = set.GenWeights,
                    LayerIndex = layerIndex,
                    SourceNif = ResolveSourceNif(shapeDataRoots, set.DataFolder, set.SourceFile),
                });
            }
        }

        MarkOutputConflicts(outfits);

        result.Outfits.AddRange(outfits);
        return result;
    }

    /// <summary>按输出路径聚类，给"与别的模组争用同一目标文件"的 set 打标记。
    /// 分组键用 Ordinal：BodySlide 侧是 <c>std::map&lt;std::string, ...&gt;</c>，逐字节比较、区分大小写，
    /// 我们多标或漏标都会让它读回的 BuildSelection 条目对不上号。</summary>
    private static void MarkOutputConflicts(List<OutfitEntry> outfits)
    {
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var outfit in outfits)
        {
            if (outfit.OutputFilePath is not { } path)
                continue;
            if (!owners.TryGetValue(path, out var set))
                owners[path] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(outfit.OwnerLabel);
        }

        foreach (var outfit in outfits)
            if (outfit.OutputFilePath is { } path)
                outfit.HasOutputConflict = 1 < owners[path].Count;
    }

    /// <summary>解析 &lt;SliderSet name="..."&gt;——服装名就是这个 name 属性，逐字符原样使用。</summary>
    public static IEnumerable<string> ParseSliderSetNames(string path, IList<string> warnings)
    {
        var sets = new List<SliderSetInfo>();
        if (ReadSliderSets(path, sets) is { } error)
        {
            warnings.Add(error);
            yield break;
        }
        foreach (var set in sets)
            yield return set.Name;
    }

    /// <summary>
    /// 流式读取 &lt;SliderSet&gt; 的 name 属性及其 &lt;OutputPath&gt; / &lt;OutputFile&gt; 子元素：
    /// 成功返回 null，失败返回错误消息。
    /// 用 XmlReader 而非 XDocument——不建 DOM，实测快 1.3–1.8 倍，分配也少得多（并行时这点更关键）。
    /// 结果先收进 <paramref name="sets"/>，整文件读通才算数：文件损坏则该文件**不贡献任何名字**、
    /// 只留一条警告，与原先 XDocument.Load 一次性失败的语义一致（不会因为读了一半就留下部分结果）。
    /// 另外要求命名空间为空，对齐原实现 DescendantsAndSelf("SliderSet") 的匹配范围。
    /// </summary>
    /// <param name="display">警告里怎么称呼这个文件；不给则只用文件名。</param>
    private static string? ReadSliderSets(string path, List<SliderSetInfo> sets, string? display = null)
    {
        var settings = new XmlReaderSettings
        {
            IgnoreWhitespace = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            DtdProcessing = DtdProcessing.Prohibit, // 既省事，也避免外部实体（XXE）
            CloseInput = true,
        };

        try
        {
            using var reader = XmlReader.Create(path, settings);
            string? name = null;
            string? rawOutputPath = null;
            string? rawOutputFile = null;
            string? dataFolder = null;
            string? sourceFile = null;
            var genWeights = true;
            var setDepth = -1;
            string? field = null; // 正在等文本的直接子元素名
            var text = new StringBuilder();

            void FlushField()
            {
                if (field is null)
                    return;
                var value = text.ToString().Trim();
                text.Clear();
                var taken = field;
                field = null;
                if (value.Length == 0)
                    return;
                // 同名元素出现多次时取第一个，对齐 BodySlide 的 FirstChildElement("OutputPath")
                switch (taken)
                {
                    case "OutputPath":
                        rawOutputPath ??= value;
                        break;
                    case "OutputFile":
                        rawOutputFile ??= value;
                        break;
                    case "DataFolder":
                        dataFolder ??= value;
                        break;
                    case "SourceFile":
                        sourceFile ??= value;
                        break;
                }
            }

            void Commit()
            {
                FlushField();
                if (name is { Length: > 0 } setName)
                    sets.Add(new SliderSetInfo(
                        setName,
                        BuildOutputFilePath(rawOutputPath, rawOutputFile),
                        genWeights,
                        dataFolder,
                        sourceFile));
                name = null;
                rawOutputPath = null;
                rawOutputFile = null;
                dataFolder = null;
                sourceFile = null;
                genWeights = true;
                setDepth = -1;
            }

            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        if (reader.LocalName == "SliderSet" && reader.NamespaceURI.Length == 0)
                        {
                            Commit(); // 上一个 set 收尾
                            name = reader.GetAttribute("name");
                            setDepth = reader.Depth;
                            if (reader.IsEmptyElement)
                                Commit();
                            continue;
                        }
                        field = null;
                        if (name is null || reader.Depth != setDepth + 1)
                            continue; // 只认 set 的直接子元素：嵌套层级里的同名元素不算
                        if (reader.LocalName is "OutputPath" or "OutputFile" or "DataFolder" or "SourceFile")
                        {
                            field = reader.LocalName;
                            if (field == "OutputFile")
                                genWeights = ParseTrue(reader.GetAttribute("GenWeights"));
                        }
                        break;

                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                        if (field is not null)
                            text.Append(reader.Value);
                        break;

                    case XmlNodeType.EndElement:
                        if (reader.Depth == setDepth && reader.LocalName == "SliderSet")
                            Commit();
                        else if (field is not null && reader.Depth == setDepth + 1)
                            FlushField();
                        break;
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            sets.Clear();
            return CoreStrings.Format("L.Core_ScanParseFail", display ?? Path.GetFileName(path), ex.Message);
        }
    }

    /// <summary>
    /// 复刻 tinyxml2 的 <c>BoolAttribute(name, true)</c>：属性写成 0 / false / no（不分大小写）才算否，
    /// 其余任何取值、以及属性缺省都算"是"。
    /// </summary>
    private static bool ParseTrue(string? attribute) =>
        attribute is null
        || !(attribute.Equals("0", StringComparison.OrdinalIgnoreCase)
             || attribute.Equals("false", StringComparison.OrdinalIgnoreCase)
             || attribute.Equals("no", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 按 BodySlide <c>SliderSetFile::GetSetOutputFilePath</c>（SliderSet.cpp:826-841）拼出输出文件路径：
    /// <c>&lt;OutputPath&gt;</c> 经 ToOSSlashes（Windows 上 / → \），有 <c>&lt;OutputFile&gt;</c> 时再追加分隔符和它。
    /// 这个字符串就是 BodySlide 存进 BuildSelection.xml 的 <c>path</c> 属性，必须逐字符一致，
    /// 否则我们导出的选择它读不到。没有 OutputPath 的 set 返回 null（BodySlide 也不把它计入冲突）。
    /// </summary>
    public static string? BuildOutputFilePath(string? outputPath, string? outputFile)
    {
        if (string.IsNullOrEmpty(outputPath))
            return null;
        var key = outputPath!.Replace('/', '\\');
        if (!string.IsNullOrEmpty(outputFile))
            key += '\\' + outputFile;
        return key;
    }

    /// <summary>
    /// <c>&lt;项目路径&gt;\SliderSets</c> 对应的 <c>&lt;项目路径&gt;\ShapeData</c>——BodySlide 的
    /// <c>baseDataPath</c>（<c>BodySlideApp.cpp</c>：<c>SetBaseDataPath(GetProjectPath() + PathSep + "ShapeData")</c>）。
    /// 目录名不是 SliderSets（异常布局）时返回 null。
    /// </summary>
    static string? ShapeDataDirFor(string layerDir)
    {
        var trimmed = layerDir.TrimEnd('\\', '/');
        if (!Path.GetFileName(trimmed).Equals("SliderSets", StringComparison.OrdinalIgnoreCase))
            return null;
        // 自己截最后一段：Path.GetDirectoryName 对结尾带分隔符的路径行为容易看错，这里要的就是"去掉 \SliderSets"
        var cut = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        return cut <= 0 ? null : Path.Combine(trimmed[..cut], "ShapeData");
    }

    /// <summary>按 BodySlide 的 <c>SliderSet::GetInputFileName()</c>（<c>SliderSet.cpp:644-649</c>：
    /// <c>baseDataPath \ DataFolder \ SourceFile</c>）在个覆盖层里解析源网格，返回第一个真实存在的文件。
    /// <c>&lt;SourceFile&gt;</c> 有的模组写 "Foo.nif"、有的只写 "Foo"，两种都试。</summary>
    static string? ResolveSourceNif(IReadOnlyList<string> shapeDataRoots, string? dataFolder, string? sourceFile)
    {
        if (string.IsNullOrWhiteSpace(sourceFile))
            return null;

        var name = sourceFile!.Trim();
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(dataFolder))
            parts.Add(dataFolder!.Trim().Replace('/', Path.DirectorySeparatorChar));
        parts.Add(name);
        var relative = Path.Combine(parts.ToArray());

        var candidates = name.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)
            ? new[] { relative }
            : new[] { relative, relative + ".nif" };

        foreach (var root in shapeDataRoots)
            foreach (var candidate in candidates)
            {
                try
                {
                    var full = Path.GetFullPath(Path.Combine(root, candidate));
                    if (File.Exists(full))
                        return full;
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // XML 里的路径可能带非法字符（模组作者手打错）：跳过这个候选，别让整个扫描失败
                }
            }
        return null;
    }

    /// <summary>
    /// 警告里的文件标识：模组名 + 层内相对路径。同一个文件名（`CBBE.osp`、`CBBE.xml`）在多个模组里都可能有，
    /// 只给文件名分不清该去修哪一个。
    /// </summary>
    private static string Describe(string label, string relPath) =>
        $"{label}{Path.DirectorySeparatorChar}{relPath}";
}
