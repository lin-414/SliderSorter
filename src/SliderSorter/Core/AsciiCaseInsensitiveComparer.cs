namespace SliderSorter.Core;

/// <summary>
/// 只折叠 ASCII 字母的忽略大小写字符串比较器，用来复刻 BodySlide 的
/// <c>case_insensitive_compare</c>（<c>StringStuff.h:56-62</c>：逐字节 <c>std::tolower</c>，
/// 非 ASCII 字节原样留下）。
/// <para>
/// 为什么不能直接用 <see cref="StringComparer.OrdinalIgnoreCase"/>：它按不变文化的简单大小写折叠，
/// 西里尔字母与带音标的拉丁字母都会被折到一起（<c>Ы</c> 与 <c>ы</c>、<c>É</c> 与 <c>é</c> 实测相等）。
/// 而 BodySlide 那边它们是两个不同的键。用错比较器的后果是把"BodySlide 眼里的两件衣服"
/// 合成一条：弱层那条整条从清单消失，它声明的输出路径也不再参与冲突判定。
/// 本仓库界面有俄语包，服装名带西里尔字母不算冷门。
/// </para>
/// <para>
/// 与 <c>OrdinalIgnoreCase</c> 的差别只在 0x80 以上的字符上，ASCII 路径与名字两边行为一致。
/// 逐字符比较、不产生用于比较的中间字符串——这几个字典在扫描时会上千次查找。
/// </para>
/// </summary>
public sealed class AsciiCaseInsensitiveComparer : StringComparer
{
    public static AsciiCaseInsensitiveComparer Instance { get; } = new();

    /// <summary>BodySlide 用 C 区域的 <c>std::tolower</c>：只有 A-Z 会变小写。</summary>
    private static char Fold(char c) => c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c;

    public override int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        var common = Math.Min(x.Length, y.Length);
        for (var i = 0; i < common; i++)
        {
            var diff = Fold(x[i]) - Fold(y[i]);
            if (diff != 0)
                return diff < 0 ? -1 : 1;
        }
        return x.Length.CompareTo(y.Length);
    }

    public override bool Equals(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return true;
        if (x is null || y is null || x.Length != y.Length)
            return false;

        for (var i = 0; i < x.Length; i++)
            if (Fold(x[i]) != Fold(y[i]))
                return false;
        return true;
    }

    /// <summary>哈希与 <see cref="Equals"/> 同口径（折 ASCII 后按字符累加），否则字典会漏查。</summary>
    public override int GetHashCode(string s)
    {
        var hash = 0;
        foreach (var c in s)
            hash = (hash << 5) + hash + Fold(c);
        return hash;
    }
}
