using System.Text;
using System.Xml;

namespace SliderSorter.Core;

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

    /// <summary>发现该 set 的覆盖层序号（0 = 最强模组）。<para>
    /// 注意它只是本工具自己的排序与"游戏会读到哪一个"的判据，**不是** BodySlide 判定冲突胜负的依据：
    /// BodySlide 看不到模组层序，它按目录遍历顺序分组，没存过选择时默认勾的是**该组第一个被发现**的成员
    /// （<c>BodySlideApp.cpp:4289</c> 的 <c>j == 0</c>）。</para></summary>
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
/// 同名滑块组先见者胜（与 BodySlideApp::LoadSliderSets 一致：它按 <c>outfitNameSource</c> 判重，
/// 而那个容器是 <c>std::map&lt;std::string, std::string, case_insensitive_compare&gt;</c>
/// ——<c>BodySlideApp.h:125</c>——所以"同名"是**忽略大小写**的，且只折 ASCII 字母）。
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
        var shapeDataIndex = BuildShapeDataIndex(shapeDataRoots);

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

            // 层内文件顺序决定"同名不同文件"里谁先被解析，必须确定：显式按相对路径排序，
            // 对齐 BodySlide 的 wxDir::GetAllFiles 在 NTFS 上返回的字面序，避免依赖文件系统枚举顺序。
            // （*.osp 与 *.xml 分成两批这件事在后面的 loadOrder 里单独处理——那才是 BodySlide 真有的行为。）
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

        // 同名 set 归谁，按 BodySlide 的**加载顺序**判：它先按 *.osp 把整个目录读一遍、再按 *.xml 读第二遍
        //（BodySlideApp.cpp:837-839 是两次 GetAllFiles 追加进同一个数组），所以一个 .osp 里的 set 名
        // 总是赢过 .xml 里的同名 set，与模组优先级无关。第二、第三判据仍用层序与路径序——
        // BodySlide 那趟目录遍历的真实顺序在 USVFS 下无从得知，而"更强的模组先赢"是本工具一贯的口径。
        var loadOrder = Enumerable.Range(0, winners.Count)
            .OrderBy(i => SetFilePriority(winners[i].RelPath))
            .ThenBy(i => winners[i].LayerIndex)
            .ThenBy(i => winners[i].RelPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 名字 → 拥有它的那一条（winners 下标 + 文件内下标）。判重忽略大小写：BodySlide 的 outfitNameSource
        // 是 case_insensitive_compare（StringStuff.h:56-62 逐字节 tolower，**只有 ASCII 字母会折**），
        // 所以这里用 AsciiCaseInsensitiveComparer 而不是 OrdinalIgnoreCase——后者连 Ы/ы、É/é 都折成相等，
        // 会把 BodySlide 视为两件衣服的 set 合成一条。声明次数 >1 即"同名先见者胜之外还有人被丢掉"，
        // 也就是树里要标注的 HasConflict。
        var owner = new Dictionary<string, (int Winner, int Set)>(AsciiCaseInsensitiveComparer.Instance);
        var declarations = new Dictionary<string, int>(AsciiCaseInsensitiveComparer.Instance);
        foreach (var i in loadOrder)
        {
            var sets = setsPerFile[i];
            for (var j = 0; j < sets.Count; j++)
            {
                owner.TryAdd(sets[j].Name, (i, j));
                declarations[sets[j].Name] = declarations.GetValueOrDefault(sets[j].Name) + 1;
            }
        }

        // 产出顺序不变（winners 顺序 → 文件内出现顺序，显式用 List 承载：字典的枚举顺序是实现细节，
        // 而"清单里服装的先后"是用户看得见的对外行为），只是每条都要过一遍"你是不是同名里的名主"。
        var outfits = new List<OutfitEntry>();
        for (var i = 0; i < winners.Count; i++)
        {
            var (rel, label, _, layerIndex) = winners[i];
            // 警告也按文件顺序合并，保证多次扫描的警告次序稳定
            if (errorPerFile[i] is { } error)
                result.Warnings.Add(error);
            var fileSets = setsPerFile[i];
            for (var j = 0; j < fileSets.Count; j++)
            {
                var set = fileSets[j];
                if (owner[set.Name] != (i, j))
                {
                    // 重名的另一个 set 对 BodySlide 而言根本不会被加载（先见者胜），
                    // 所以它声明的输出路径也不参与输出文件冲突——不能记在这里。
                    continue;
                }
                outfits.Add(new OutfitEntry
                {
                    Name = set.Name,
                    OwnerLabel = label,
                    SourceFile = rel,
                    OutputFilePath = set.OutputFilePath,
                    GenWeights = set.GenWeights,
                    LayerIndex = layerIndex,
                    HasConflict = 1 < declarations[set.Name],
                    SourceNif = ResolveSourceNif(shapeDataIndex, set.DataFolder, set.SourceFile),
                });
            }
        }

        MarkOutputConflicts(outfits);

        result.Outfits.AddRange(outfits);
        return result;
    }

    /// <summary>按输出路径聚类，给"与别的模组争用同一目标文件"的 set 打标记。
    /// 分组键忽略大小写：BodySlide 侧的 <c>outFileCount</c> 是
    /// <c>std::map&lt;std::string, std::vector&lt;std::string&gt;, case_insensitive_compare&gt;</c>
    /// （<c>BodySlideApp.h:202</c>，从 v5.1 到 master 都是），所以 <c>Meshes/Foo</c> 与 <c>meshes/foo</c>
    /// 在它眼里是**一组**冲突、批建时会弹窗；这里若按 Ordinal 分就成了两组各一人、谁都不算冲突，
    /// 于是这个功能正好漏掉它存在的理由。折大小写的范围照它来（只折 ASCII，见
    /// <see cref="AsciiCaseInsensitiveComparer"/>），不是按 .NET 的 OrdinalIgnoreCase——后者把
    /// Ы/ы 也折在一起，那在 BodySlide 里是两组。模组名仍按 Ordinal 去重（那是本工具自己的概念）。</summary>
    private static void MarkOutputConflicts(List<OutfitEntry> outfits)
    {
        var owners = new Dictionary<string, HashSet<string>>(AsciiCaseInsensitiveComparer.Instance);
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

    /// <summary>BodySlide 枚举滑块组文件的批次：<c>*.osp</c> 那一批整体先于 <c>*.xml</c> 那一批
    /// （两次 <c>wxDir::GetAllFiles</c> 追加进同一个数组），所以跨批次的同名 set 由 .osp 获胜。</summary>
    private static int SetFilePriority(string relativePath)
    {
        var ext = Path.GetExtension(relativePath);
        if (ext.Equals(".osp", StringComparison.OrdinalIgnoreCase))
            return 0;
        return ext.Equals(".xml", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
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
            // 跳过 DTD 而不是禁止它：Prohibit 会让**任何**带 <!DOCTYPE> 的文件当场抛异常、
            // 整件作废（下面 catch 里 sets.Clear()），而 tinyxml2 是跳过 DTD 照常读里面的 SliderSet。
            // 用某些 XML 工具或导出器存过的 .xml/.osp 就长这样，代价是那个模组的服装凭空消失。
            // 安全性不打折：Ignore 不解析也不取 DTD 里的内容，外部实体不会去读；
            // XmlResolver 再显式关掉，等于彻底不许解析期碰网络与磁盘上的第二份文件。
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            CloseInput = true,
        };

        try
        {
            using var reader = XmlReader.Create(path, settings);
            // 老格式（根上 version 缺省或 < 1）里数据目录叫 <SetFolder>：BodySlide 在打开文件时
            // 就地把它改名成 <DataFolder> 并把 version 补成 1（SliderSet.cpp:705-746），
            // 于是解析时两种名字都会以 <DataFolder> 出现。这里等价地两个都认。
            var dataFolderNames = (string[]?)null; // null = 还没读到根元素
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
                var taken = field;
                field = null;
                // OutputPath / OutputFile 不 Trim：BodySlide 用的是 tinyxml2 的 GetText()，元素内的空白与
                // 换行会原样进字符串、进而进冲突键；Trim 了就会导出一个它读不到的键。
                // 数据目录与源网格名只用来在磁盘上找文件，那里的空白是作者手滑，去掉才对得上网格。
                var value = taken is "OutputPath" or "OutputFile"
                    ? text.ToString()
                    : text.ToString().Trim();
                text.Clear();
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
                    case "SetFolder":
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
                        if (dataFolderNames is null && reader.Depth == 0)
                        {
                            var declared = reader.GetAttribute("version");
                            dataFolderNames = ParseIntPrefix(declared) >= 1
                                ? new[] { "DataFolder" }
                                : new[] { "DataFolder", "SetFolder" };
                        }
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
                        if (reader.LocalName is "OutputPath" or "OutputFile" or "SourceFile"
                            || (dataFolderNames?.Contains(reader.LocalName) ?? false))
                        {
                            field = reader.LocalName;
                            if (field == "OutputFile")
                                genWeights = ParseBool(reader.GetAttribute("GenWeights"), defaultValue: true);
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
    /// 复刻 tinyxml2 的 <c>BoolAttribute(name, defaultValue)</c>（<c>lib/TinyXML-2/tinyxml2.cpp:624-649</c>）：
    /// 先按整数读前缀（<c>%d</c>，带 <c>0x</c> 前缀时按 <c>%x</c>），0 为否、非 0 为是；否则只接受
    /// **大小写完全一致**的 <c>true/True/TRUE</c> 与 <c>false/False/FALSE</c>；再其它写法——包括
    /// <c>no</c>、<c>off</c>、<c>FaLsE</c> 这类它读不懂的值——解析失败，回落到 <paramref name="defaultValue"/>。
    /// <para>
    /// 原先按"0/false/no 不分大小写即为否"判，于是 <c>GenWeights="no"</c> 与 <c>GenWeights="FaLsE"</c>
    /// 被算成否，而 BodySlide 那边仍是是：带权重的 <c>_0/_1</c> 后缀就标错了。
    /// </para>
    /// </summary>
    private static bool ParseBool(string? attribute, bool defaultValue)
    {
        if (string.IsNullOrEmpty(attribute))
            return defaultValue;
        if (TryReadIntPrefix(attribute!, out var number))
            return number != 0;
        switch (attribute)
        {
            case "true":
            case "True":
            case "TRUE":
                return true;
            case "false":
            case "False":
            case "FALSE":
                return false;
        }
        return defaultValue;
    }

    /// <summary>复刻 tinyxml2 <c>ToInt</c> 对属性值的读法：跳过前导空白、可选正负号，
    /// <c>0x</c>/<c>0X</c> 前缀走十六进制、否则读十进制；一个像样的数字都读不出来就返回 false
    /// （"12abc" 读到 12 算成功，"abc"、"-"、"+ 5" 算失败——和 <c>sscanf</c> 一致）。</summary>
    private static bool TryReadIntPrefix(string s, out int value)
    {
        value = 0;
        var i = 0;
        while (i < s.Length && IsAsciiSpace(s[i]))
            i++;
        var negative = false;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
        {
            negative = s[i] == '-';
            i++;
        }
        var hex = i + 1 < s.Length && s[i] == '0' && (s[i + 1] == 'x' || s[i + 1] == 'X');
        if (hex)
            i += 2;

        long accumulated = 0;
        var digits = 0;
        for (; i < s.Length; i++)
        {
            var digit = HexDigit(s[i]);
            if (digit < 0 || (!hex && 9 < digit))
                break;
            // 溢出无所谓：tinyxml2 那边是 UB，而我们只用到"是不是 0"
            accumulated = accumulated * (hex ? 16 : 10) + digit;
            if (accumulated > int.MaxValue)
                accumulated = int.MaxValue;
            digits++;
        }
        if (digits == 0)
            return false;
        value = (int)(negative ? -accumulated : accumulated);
        return true;
    }

    /// <summary>按 <c>IntAttribute</c> 的读法取根元素的 <c>version</c>：缺省或读不出数字都算 0
    /// （BodySlide 由此判定老格式，<c>SliderSet.cpp:701</c>）。</summary>
    private static int ParseIntPrefix(string? attribute) =>
        !string.IsNullOrEmpty(attribute) && TryReadIntPrefix(attribute!, out var parsed) ? parsed : 0;

    private static bool IsAsciiSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    private static int HexDigit(char c) =>
        c is >= '0' and <= '9' ? c - '0'
        : c is >= 'a' and <= 'f' ? c - 'a' + 10
        : c is >= 'A' and <= 'F' ? c - 'A' + 10
        : -1;

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

    /// <summary>
    /// 一次性列出各层 ShapeData 里的 *.nif，键 = 相对该层 ShapeData 根的路径。
    /// <para>
    /// 为什么不能"每个服装 × 每层"直接 <c>File.Exists</c>：实测一份 2464 个启用模组、6877 件服装的
    /// 整合包会打出约 3400 万次探测，扫描从 1 秒变 65 秒，进度条停在 100% 像是死掉了。
    /// 目录本来就要整层遍历一遍才知道有什么，索引一次、之后查表才是对的形状。
    /// </para>
    /// 枚举并行、合并按层序串行：层序决定同名文件归谁，绝不能边枚举边写共享字典。
    /// </summary>
    static Dictionary<string, string> BuildShapeDataIndex(IReadOnlyList<string> shapeDataRoots)
    {
        var perRoot = new List<(string Rel, string Full)>[shapeDataRoots.Count];

        Parallel.For(0, shapeDataRoots.Count, i =>
        {
            var list = new List<(string, string)>();
            var root = Path.GetFullPath(shapeDataRoots[i]);
            try
            {
                if (!Directory.Exists(root))
                {
                    perRoot[i] = list;
                    return;
                }
                var files = Directory.EnumerateFiles(root, "*.nif", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                }).ToList();
                var cut = root.TrimEnd(Path.DirectorySeparatorChar, '/').Length;
                foreach (var file in files)
                    list.Add((file[cut..].TrimStart(Path.DirectorySeparatorChar, '/'), file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or ArgumentException or NotSupportedException or PathTooLongException)
            {
                // 单层读不到不影响别的层：预览最多就是少一个候选的网格
                list.Clear();
            }
            perRoot[i] = list;
        });

        // 相对路径在 Windows 上不区分大小写，键必须按忽略大小写比
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < perRoot.Length; i++)
            foreach (var (rel, full) in perRoot[i])
                index.TryAdd(rel, full); // 先见的强层获胜
        return index;
    }

    /// <summary>按 BodySlide 的 <c>SliderSet::GetInputFileName()</c>（<c>SliderSet.cpp:644-649</c>：
    /// <c>baseDataPath \ DataFolder \ SourceFile</c>）在索引里查源网格。
    /// <c>&lt;SourceFile&gt;</c> 有的模组写 "Foo.nif"、有的只写 "Foo"，两种都试。</summary>
    static string? ResolveSourceNif(IReadOnlyDictionary<string, string> shapeDataIndex, string? dataFolder, string? sourceFile)
    {
        if (string.IsNullOrWhiteSpace(sourceFile))
            return null;

        var name = sourceFile!.Trim();
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(dataFolder))
            parts.Add(dataFolder!.Trim().Replace('/', Path.DirectorySeparatorChar));
        parts.Add(name);
        string relative;
        try
        {
            relative = Path.Combine(parts.ToArray());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null; // XML 里的路径带非法字符（模组作者手打错）：当作没有源网格
        }

        if (shapeDataIndex.TryGetValue(relative, out var hit))
            return hit;
        return name.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)
            ? null
            : shapeDataIndex.GetValueOrDefault(relative + ".nif");
    }

    /// <summary>
    /// 警告里的文件标识：模组名 + 层内相对路径。同一个文件名（`CBBE.osp`、`CBBE.xml`）在多个模组里都可能有，
    /// 只给文件名分不清该去修哪一个。
    /// </summary>
    private static string Describe(string label, string relPath) =>
        $"{label}{Path.DirectorySeparatorChar}{relPath}";
}
