namespace SliderSorter.Core;

/// <summary>
/// 已见过的服装模组（OwnerLabel）基线：扫描后与当前对比，识别新安装的模组。
/// </summary>
public static class KnownMods
{
    /// <summary>返回 current 中不在 known 基线里的模组，保持 current 的出现顺序。</summary>
    public static List<string> Diff(IEnumerable<string>? known, IEnumerable<string> current)
    {
        var set = new HashSet<string>(known ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        return current.Where(owner => !set.Contains(owner)).ToList();
    }
}
