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
}
