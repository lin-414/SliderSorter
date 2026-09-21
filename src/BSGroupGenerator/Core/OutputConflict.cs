namespace BSGroupGenerator.Core;

/// <summary>输出文件冲突组里的一个候选 slider set。</summary>
public sealed record ConflictCandidate(
    string Name,
    string OwnerLabel,
    string SourceFile,
    int LayerIndex,
    bool GenWeights);

/// <summary>多个 slider set 写同一个输出文件——即 BodySlide 的"输出文件冲突"：
/// 批建时它们会互相覆盖同一个 .nif，最后建的那个赢。</summary>
public sealed class OutputConflictGroup
{
    /// <summary>BodySlide 口径的输出路径（不含扩展名），同时是 BuildSelection.xml 的 path 属性。</summary>
    public required string OutputFilePath { get; init; }

    /// <summary>组内各成员通常一致（只有显式写了 &lt;OutputFile GenWeights="0"&gt; 的 set 才会是否），
    /// 取组内最强层的成员为准，与 BodySlide 用"当前激活的 set"决定显示后缀的做法一致。</summary>
    public required bool GenWeights { get; init; }

    /// <summary>候选，按覆盖层从强到弱排序。</summary>
    public required IReadOnlyList<ConflictCandidate> Candidates { get; init; }

    /// <summary>候选来自 2 个以上的模组/层——也就是"多个模组改了同一件衣服"。
    /// 同一个模组内部的多个预设共用一个输出文件（UBE 一件衣服几百个配色就是这个情况）同样会
    /// 争用同一个 .nif，但那是模组自己的设计，不是模组间冲突：树里的标记与状态栏计数都只认这种。</summary>
    public bool CrossMod => Candidates.Select(c => c.OwnerLabel).Distinct(StringComparer.Ordinal).Count() > 1;

    /// <summary>用户已选定的赢家；未选或所选的 set 已不在候选里（模组被禁用/卸载）则为 null。</summary>
    public ConflictCandidate? Chosen { get; init; }

    /// <summary>游戏实际会读到的那一个：MO2 优先级最强的层，也就是不选时的默认赢家。</summary>
    public ConflictCandidate Strongest => Candidates[0];

    /// <summary>显示用的目标文件名，与 BodySlide 主界面冲突标签同一写法。</summary>
    public string TargetDisplay => FormatTarget(OutputFilePath, GenWeights);

    public static string FormatTarget(string outputFilePath, bool genWeights) => genWeights
        ? CoreStrings.Format("L.Core_OutputConf_Weighted", outputFilePath)
        : CoreStrings.Format("L.Core_OutputConf_Single", outputFilePath);
}

/// <summary>从扫描结果里算出输出文件冲突，并把用户的选择套上去。</summary>
public static class OutputConflicts
{
    /// <summary>按输出路径聚类（Ordinal，与 BodySlide 的 std::map 一致），只保留 2 个及以上成员的组，
    /// 组内按覆盖层从强到弱排序（层序相同即同一模组内的多个文件，按发现顺序）。</summary>
    public static List<OutputConflictGroup> Detect(ScanResult scan, IReadOnlyDictionary<string, string>? choices = null)
    {
        var byPath = new Dictionary<string, List<OutfitEntry>>(StringComparer.Ordinal);
        // 调用方可能传来忽略大小写的字典，而 BodySlide 的比较是逐字节的——这里统一成 Ordinal
        var choiceMap = choices is null
            ? null
            : new Dictionary<string, string>(choices, StringComparer.Ordinal);
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

            var ordered = members
                .OrderBy(o => o.LayerIndex)
                .ThenBy(o => o.Name, StringComparer.Ordinal)
                .Select(o => new ConflictCandidate(o.Name, o.OwnerLabel, o.SourceFile, o.LayerIndex, o.GenWeights))
                .ToList();

            var chosenName = choiceMap is not null && choiceMap.TryGetValue(path, out var name) ? name : null;
            groups.Add(new OutputConflictGroup
            {
                OutputFilePath = path,
                GenWeights = ordered[0].GenWeights,
                Candidates = ordered,
                Chosen = ordered.FirstOrDefault(c => string.Equals(c.Name, chosenName, StringComparison.Ordinal)),
            });
        }

        // 组的顺序也要稳定：按输出路径 Ordinal 排，字典枚举顺序不算实现细节的话就没人写测试了
        groups.Sort((a, b) => string.CompareOrdinal(a.OutputFilePath, b.OutputFilePath));
        return groups;
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
}
