namespace SliderSorter.Core;

/// <summary>输出文件冲突组里的一个候选 slider set。</summary>
public sealed record ConflictCandidate(
    string Name,
    string OwnerLabel,
    string SourceFile,
    int LayerIndex,
    bool GenWeights);

/// <summary>多个 slider set 写同一个输出文件——即 BodySlide 的"输出文件冲突"：
/// 批建时它不会让谁覆盖谁，而是弹一个"选择输出文件"的框（<c>BodySlideApp.cpp:4217-4435</c>），
/// 把没被勾中的那些**整个从这一轮构建列表里去掉**（<c>:4420-4426</c>）并写进 BuildSelection.xml。
/// 也就是说：一组冲突要么建赢家、要么一个都不建，"都建了、后建的赢"是不存在的。</summary>
public sealed class OutputConflictGroup
{
    /// <summary>BodySlide 口径的输出路径（不含扩展名），同时是 BuildSelection.xml 的 path 属性。
    /// 组内成员的拼写可能只差大小写（分组键忽略大小写，见 <see cref="OutputConflicts.Detect"/>），
    /// 这里取**第一个成员的原样写法**——BodySlide 那边 <c>std::map</c> 也保留首次插入的键拼写。</summary>
    public required string OutputFilePath { get; init; }

    /// <summary>这一组里实际出现过的所有拼写（按首见顺序，去重）。
    /// BodySlide 查选择用的 <c>BuildSelection::outputChoice</c> 是**区分大小写**的
    /// <c>std::map</c>（<c>BuildSelection.h:21</c>），而它的组键拼写取决于自己的目录遍历顺序——
    /// 那个顺序我们无从得知，于是导出时把每种拼写各写一条，总有一条正好是它的键。</summary>
    public required IReadOnlyList<string> KeySpellings { get; init; }

    /// <summary>赢家（已选定的那个，没选定则是最强层那个）的 GenWeights——决定后缀是
    /// <c>_0.nif（以及 _1.nif）</c> 还是单个 <c>.nif</c>。BodySlide 显示的后缀取当前激活的 set、
    /// 实际产物取真正被建的那个 set，两者在这里合流成"赢家的"。</summary>
    public required bool GenWeights { get; init; }

    /// <summary>候选，按覆盖层从强到弱排序（层序相同即同一模组内的多个文件，按发现顺序）。</summary>
    public required IReadOnlyList<ConflictCandidate> Candidates { get; init; }

    /// <summary>候选来自 2 个以上的模组/层——也就是"多个模组改了同一件衣服"。
    /// 同一个模组内部的多个预设共用一个输出文件（UBE 一件衣服几百个配色就是这个情况）同样会
    /// 争用同一个 .nif，但那是模组自己的设计，不是模组间冲突：树里的标记与状态栏计数都只认这种。</summary>
    public bool CrossMod => Candidates.Select(c => c.OwnerLabel).Distinct(StringComparer.Ordinal).Count() > 1;

    /// <summary>用户已选定的赢家；未选或所选的 set 已不在候选里（模组被禁用/卸载）则为 null。
    /// 名字按逐字节精确比较：<c>choice</c> 的大小写与 set 名不一致时 BodySlide 自己也认不出
    /// （<c>BodySlideApp.cpp:3496</c> 的 <c>choice != no</c>、<c>:5060</c> 的 <c>std::find</c>、
    /// 弹窗里的 <c>choices.Index(c)</c> 都是精确比较），所以这里也不认。</summary>
    public ConflictCandidate? Chosen { get; init; }

    /// <summary>MO2 优先级最强的那一层，也就是本工具建议的默认赢家——理由是"游戏实际会读到的那一个"。
    /// <para>
    /// 这不是 BodySlide 的默认：它按目录遍历顺序取**第一个被发现**的成员预勾（<c>:4289</c>），
    /// 而那个顺序在 USVFS 下与模组优先级无关。所以「全部按模组优先级」是一次**覆盖**，不是复刻。
    /// </para></summary>
    public ConflictCandidate Strongest => Candidates[0];

    /// <summary>显示用的目标文件名，与 BodySlide 主界面冲突标签同一写法。</summary>
    public string TargetDisplay => FormatTarget(OutputFilePath, GenWeights);

    public static string FormatTarget(string outputFilePath, bool genWeights) => genWeights
        ? CoreStrings.Format("L.Core_OutputConf_Weighted", outputFilePath)
        : CoreStrings.Format("L.Core_OutputConf_Single", outputFilePath);
}

/// <summary>一条冲突挂在某个分组名下时的一行：冲突本身 + 它对该分组是否属"组内互撞"。</summary>
public sealed record GroupedConflict(OutputConflictGroup Conflict, bool IntraCollision);

/// <summary>一个分组名下的冲突清单，或"未入组"那一桶。</summary>
public sealed class UserGroupConflicts
{
    /// <summary>分组名；空串是"未入组"——候选里没有任何 set 属于任何分组。
    /// 用空串当哨兵而不是本地化文案：Core 层不持有界面文字，显示名由视图取词。</summary>
    public required string GroupName { get; init; }

    /// <summary>该组名下的冲突，顺序与 <see cref="OutputConflicts.Detect"/> 一致（按输出路径）。</summary>
    public required IReadOnlyList<GroupedConflict> Rows { get; init; }

    public bool IsUngrouped => GroupName.Length == 0;

    /// <summary>其中"该组自己就有 ≥2 个成员争同一个输出文件"的条数。</summary>
    public int IntraCollisionCount => Rows.Count(r => r.IntraCollision);
}

/// <summary>从扫描结果里算出输出文件冲突，并把用户的选择套上去。</summary>
public static class OutputConflicts
{
    /// <summary>按输出路径聚类（<b>忽略大小写</b>，与 BodySlide 的
    /// <c>std::map&lt;std::string, std::vector&lt;std::string&gt;, case_insensitive_compare&gt;</c> 一致——
    /// <c>BodySlideApp.h:202</c>，从 v5.1 起就没变过），只保留 2 个及以上成员的组，
    /// 组内按覆盖层从强到弱排序（层序相同即同一模组内的多个文件，按发现顺序）。
    /// <para>
    /// 这个比较器只折 ASCII 字母（<c>StringStuff.h:56-62</c> 逐字节 <c>ToLower</c>），.NET 侧与之
    /// 等价的正是 <see cref="StringComparer.OrdinalIgnoreCase"/>：它同样不动非 ASCII 字符，
    /// 所以俄语/中文路径仍然按字面区分。
    /// </para></summary>
    public static List<OutputConflictGroup> Detect(ScanResult scan, IReadOnlyDictionary<string, string>? choices = null)
    {
        var byPath = new Dictionary<string, List<OutfitEntry>>(StringComparer.OrdinalIgnoreCase);
        // 选择表按忽略大小写建一份视图（键是路径），但逐条 TryAdd 而不是交给字典复制构造函数——
        // 老设置里可能留着只差大小写的两条键，复制构造会直接抛"相同的键已添加"。
        Dictionary<string, string>? choiceMap = null;
        if (choices is not null)
        {
            choiceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in choices)
                choiceMap.TryAdd(key, value);
        }
        foreach (var outfit in scan.Outfits)
        {
            if (outfit.OutputFilePath is not { Length: > 0 } path)
                continue;
            if (!byPath.TryGetValue(path, out var list))
                byPath[path] = list = [];
            list.Add(outfit);
        }

        var groups = new List<OutputConflictGroup>();
        foreach (var (path, members) in byPath)
        {
            if (members.Count < 2)
                continue;

            // OrderBy 是稳定排序：层序之内保持扫描顺序（层序 → 文件序 → 文件内文档序），
            // 那正是 BodySlide 组内成员的"发现顺序"形状；再按名字重排反而会造出一个它没有的顺序。
            var ordered = members
                .OrderBy(o => o.LayerIndex)
                .Select(o => new ConflictCandidate(o.Name, o.OwnerLabel, o.SourceFile, o.LayerIndex, o.GenWeights))
                .ToList();

            var chosenName = choiceMap is not null && choiceMap.TryGetValue(path, out var name) ? name : null;
            var chosen = ordered.FirstOrDefault(c => string.Equals(c.Name, chosenName, StringComparison.Ordinal));
            groups.Add(new OutputConflictGroup
            {
                // 规范键 = 第一个成员的原样拼写（std::map 保留首次插入的键拼写，同一个道理）
                OutputFilePath = members[0].OutputFilePath!,
                KeySpellings = DistinctSpellings(members),
                // 后缀跟赢家走：赢家被选定为另一个成员时，产物文件名由它决定，不是由最强层决定
                GenWeights = (chosen ?? ordered[0]).GenWeights,
                Candidates = ordered,
                Chosen = chosen,
            });
        }

        // 组的顺序也要稳定：按输出路径忽略大小写排（BodySlide 那个 map 就是这个序），
        // 字典枚举顺序不算实现细节的话就没人写测试了
        groups.Sort((a, b) => string.Compare(a.OutputFilePath, b.OutputFilePath, StringComparison.OrdinalIgnoreCase));
        return groups;
    }

    /// <summary>组内成员声明过的不同拼写（按<b>逐字节</b>去重、首见顺序）。
    /// 通常只有一个；只差大小写的整合包里才会有多条——导出时每种拼写各写一条选择条目，
    /// BodySlide 那条区分大小写的查询才必定命中一条。</summary>
    private static IReadOnlyList<string> DistinctSpellings(List<OutfitEntry> members)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var spellings = new List<string>();
        foreach (var member in members)
            if (member.OutputFilePath is { } path && seen.Add(path))
                spellings.Add(path);
        return spellings;
    }

    /// <summary>把一份选择字典的键换成这批组的<b>规范拼写</b>（其余键原样留着）。
    /// <para>
    /// 为什么需要：分组忽略大小写之后，一组冲突在设置里可能存的是另一种拼写（老版本分的组、
    /// 或另一个实例写下的）。界面按规范键查选择，查不到就显示"未指定"，而"未指定"在保存时是
    /// 要把这一组的所有拼写都清掉的——于是一次打开页面就把用户做过的决定抹了。这里先把它们
    /// 归到规范键上，界面与保存看到的才是同一份决定。
    /// </para>
    /// <para>同一组的多个拼写都有值时，规范拼写那条赢（它是界面显示的那一条）。</para></summary>
    public static Dictionary<string, string> NormalizeKeys(IReadOnlyList<OutputConflictGroup> groups,
        IReadOnlyDictionary<string, string> choices)
    {
        var result = new Dictionary<string, string>(choices, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            string? value = null;
            foreach (var spelling in group.KeySpellings)
            {
                if (!result.Remove(spelling, out var candidate) || candidate.Length == 0)
                    continue;
                if (value is null || string.Equals(spelling, group.OutputFilePath, StringComparison.Ordinal))
                    value = candidate;
            }
            if (value is not null)
                result[group.OutputFilePath] = value;
        }
        return result;
    }

    /// <summary>导出用的托管范围：本工具认得的这些组的<b>全部</b>拼写。
    /// 只有落在这个范围内的既有条目才会被改动或删除——别的安装、别的实例留下的条目一律不碰。</summary>
    public static List<string> ManagedPaths(IEnumerable<OutputConflictGroup> groups) =>
        groups.SelectMany(g => g.KeySpellings).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>导出内容：已选定赢家的那些组，<b>每种拼写各一条</b>（键=拼写、值=set 名，逐字节精确）。
    /// <para>
    /// 为什么要写多条：BodySlide 查选择用的 <c>BuildSelection::outputChoice</c> 区分大小写
    /// （<c>BuildSelection.h:21</c>），而它那一组冲突的键拼写取决于自己的目录遍历顺序——
    /// 那个顺序在 USVFS 下无从得知，于是把组内所有拼写都写上，总有一条正好是它的键；
    /// 落空的那几条它读得到却用不到，留着也不会指错赢家。
    /// </para>
    /// <para>未指定的组不进结果：它们在文件里的旧条目（若有）会因为落在托管范围内而被删掉，
    /// 于是"取消选择"也能同步回去，而不是留一条早已作废的赢家。</para></summary>
    public static Dictionary<string, string> ExportEntries(IEnumerable<OutputConflictGroup> groups)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var chosen = group.Chosen?.Name;
            if (string.IsNullOrEmpty(chosen))
                continue;
            foreach (var spelling in group.KeySpellings)
                result[spelling] = chosen!;
        }
        return result;
    }

    /// <summary>把"按 MO2 优先级自动决定"应用到给定的若干组上（每组选最强层），返回新的选择字典。</summary>
    public static Dictionary<string, string> AutoPick(IEnumerable<OutputConflictGroup> groups,
        IReadOnlyDictionary<string, string> existing)
    {
        var result = new Dictionary<string, string>(existing, StringComparer.Ordinal);
        foreach (var group in groups)
            result[group.OutputFilePath] = group.Strongest.Name;
        return result;
    }

    /// <summary>取消某组的选择——回到"BodySlide 里再问我一次"的状态。</summary>
    public static Dictionary<string, string> Clear(IEnumerable<OutputConflictGroup> groups,
        IReadOnlyDictionary<string, string> existing)
    {
        var result = new Dictionary<string, string>(existing, StringComparer.Ordinal);
        foreach (var group in groups)
            result.Remove(group.OutputFilePath);
        return result;
    }

    /// <summary>按用户分组给冲突分类，供界面"直观地看各个分组之中的冲突"。
    ///
    /// 归类判据是**卷入**：某个分组只要有成员出现在这条冲突的候选里，这条冲突就归到它名下。
    /// 一条冲突因此可能同时挂在多个分组下，而那是对的——它确实同时影响那几个组的批建结果：
    /// 赢家全局只有一个，输掉的那个组的成员就不会被建出来。
    /// 没有任何分组成员卷入的冲突进"未入组"那一桶（<see cref="UserGroupConflicts.GroupName"/> 为空串），
    /// 不能丢——丢了它们会从界面上凭空消失。
    ///
    /// 每行另带 <see cref="GroupedConflict.IntraCollision"/>：该组自己就有 ≥2 个成员卷入。
    /// 那是"分组之中"最需要人定夺的一档——争的是组内两件衣服，与组外的人无关，
    /// 而界面上"这条冲突属不属于这个组"和"这个组内部在不在自相残杀"是两个问题，故分成两个字段。
    ///
    /// 桶按 <paramref name="userGroups"/> 的给定顺序返回（即「分组生成」页组列表的顺序，
    /// 也就是用户认得的那个顺序），空桶不返回；"未入组"桶（若有内容）恒排最后。
    /// 组内成员按 Ordinal 精确比较，与 BodySlide 的成员匹配一致（<see cref="SliderGroupFile"/> 的约定：
    /// <c>SliderSetGroup::HasMember</c> 是 <c>m.compare(search) == 0</c>，<c>SliderGroup.cpp:90-96</c>）。
    /// 注意这与"同名 set 判重忽略大小写"（<see cref="Detect"/> / 扫描器）是两套比较器——
    /// BodySlide 本来就不对称：服装清单用 case_insensitive_compare 的 map，组成员用逐字节比较。
    /// 逐字节那一侧我们照抄，不改。</summary>
    public static List<UserGroupConflicts> CategorizeByUserGroup(
        IEnumerable<OutputConflictGroup> conflicts, IReadOnlyList<SliderGroup> userGroups)
    {
        // set 名 → 它所属的分组名。分组里重复登记同一个成员（手改分组 XML 能造出来）只按一次算，
        // 否则"≥2 个成员卷入"会被同一件衣服的两条重复记录冒充成组内互撞。
        var groupsOfMember = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var group in userGroups)
            foreach (var member in group.Members)
            {
                if (!groupsOfMember.TryGetValue(member, out var names))
                    groupsOfMember[member] = names = [];
                if (!names.Contains(group.Name, StringComparer.Ordinal))
                    names.Add(group.Name);
            }

        var byGroup = new Dictionary<string, List<GroupedConflict>>(StringComparer.Ordinal);
        var ungrouped = new List<GroupedConflict>();
        foreach (var conflict in conflicts)
        {
            // 这条冲突里，每个分组各有几个成员卷入
            var involved = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var candidate in conflict.Candidates)
                if (groupsOfMember.TryGetValue(candidate.Name, out var names))
                    foreach (var name in names)
                        involved[name] = involved.GetValueOrDefault(name) + 1;

            if (involved.Count == 0)
            {
                ungrouped.Add(new GroupedConflict(conflict, false));
                continue;
            }

            foreach (var (name, count) in involved)
            {
                if (!byGroup.TryGetValue(name, out var rows))
                    byGroup[name] = rows = [];
                rows.Add(new GroupedConflict(conflict, count >= 2));
            }
        }

        var result = new List<UserGroupConflicts>();
        // 同名分组在 userGroups 里出现两次（调用方递来重名列表）时只出一桶：
        // byGroup 是按名字存的，不去重就会把同一份 rows 挂两遍。
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in userGroups)
            if (emitted.Add(group.Name) && byGroup.TryGetValue(group.Name, out var rows))
                result.Add(new UserGroupConflicts { GroupName = group.Name, Rows = rows });

        if (ungrouped.Count > 0)
            result.Add(new UserGroupConflicts { GroupName = "", Rows = ungrouped });
        return result;
    }
}
