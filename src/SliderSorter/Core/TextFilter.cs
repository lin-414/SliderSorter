namespace SliderSorter.Core;

/// <summary>服装名过滤：filter 必须是 name 的连续子串（不区分大小写），绝不按单字符拆开匹配。</summary>
public static class TextFilter
{
    public static bool Matches(string name, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;
        return name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>多关键字<b>任一</b>命中即命中（关键字由调用方 <see cref="GroupRules.SplitKeywords"/> 切好）。
    /// 空集合 = 全部通过，与 <see cref="Matches"/> 的空过滤词同语义。
    /// <para>这里另开一个方法而不是改 <see cref="Matches"/>：单子串的连续匹配是别的页面（服装树、
    /// 新模组列表）依赖的行为，把分隔符语义塞进去会让"搜 `a,b`"在那些地方变成搜不到任何东西。</para></summary>
    public static bool MatchesAny(string name, IReadOnlyList<string> keywords) =>
        keywords.Count == 0 || keywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
}
