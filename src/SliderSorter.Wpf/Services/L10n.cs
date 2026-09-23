using System.Globalization;
using System.Windows;

namespace SliderSorter.Wpf.Services;

/// <summary>界面语言：向 Application 资源装载 Strings/Lang.zh|en|ru|fr 字典（合并字典后加优先，追加到末尾），
/// 与 ThemeManager 同一套换字典机制，切换对 DynamicResource 绑定即时生效。
/// 代码后台/ViewModel 经 Tr/TrF 取词——Tr 读的是普通字典快照，见 <see cref="Apply"/>。</summary>
public static class L10n
{
    public const string Zh = "zh";
    public const string En = "en";
    public const string Ru = "ru";
    public const string Fr = "fr";

    /// <summary>「跟随系统」：不落成具体语言码，而是每次启动按系统 UI 语言现算。
    /// 设置文件里存的必须是这个值本身（而不是算出来的语言码），否则用户在 Windows 里换了显示语言、
    /// 或者程序将来多支持了一种语言，它都不会自己跟上——"跟随系统"就退化成"首次启动时是什么就是什么"。</summary>
    public const string System = "system";

    /// <summary>系统 UI 语言不在受支持列表时的回落（德语 / 日语系统 → 英文界面）。
    /// 刻意不用 Zh：走这条路径的本来就不是中文用户，给英语比给中文有用；
    /// 而"设置里没写"或"写了不认识的值"仍按老规矩回落到 Zh（见 <see cref="Normalize"/>）。</summary>
    public const string SystemFallback = En;

    /// <summary>受支持的语言代码，同时也是 Strings/Lang.*.xaml 的文件名。
    /// 新增语言只需在此追加常量并补一份同名语言文件；Apply 对未知值一律回落到 Zh。
    /// <see cref="System"/> 不是一个语言文件，所以不在此列——它由 <see cref="ResolveSystemLanguage"/> 解析。</summary>
    public static readonly string[] Supported = [Zh, En, Ru, Fr];

    /// <summary>实际装载的语言，恒为 <see cref="Supported"/> 里的某一个：界面上真正显示的就是它。
    /// 代码里要判断"现在是什么语言"（换语言后重建缓存串之类）一律看这个。
    ///
    /// 注意它与"设置里选了什么"并不总是同一个值：选「跟随系统」时设置里存的是
    /// <see cref="System"/>，这里存的是系统语言恰好命中的那一种。设置页下拉的选中项
    /// 要比对的是**设置值经 <see cref="Normalize"/> 之后**的结果，不是这个——
    /// 否则下拉会显示成系统命中的那种语言，"跟随系统"看起来像是没选上。</summary>
    public static string Current { get; private set; } = Zh;

    /// <summary>当前语言的普通字典快照，只读、整体替换。扫描与 BodySlide 探测在 Task.Run 的后台线程里
    /// 取词，而 ResourceDictionary 属于 WPF 对象树（跨线程读就是线程违规）；快照是不可变引用，
    /// 后台线程读到的要么是完整旧值、要么是完整新值，不存在读到半个字典的中间态。</summary>
    private static volatile Dictionary<string, string>? _snapshot;

    /// <summary>把设置里那个值规范化成"设置页该选中哪一项"：受支持的语言码原样返回（大小写不敏感），
    /// "system" 归一成常量本身，其余（null / 空串 / 手改设置文件写进来的未知值）一律回落到 Zh。
    ///
    /// 与 <see cref="Apply"/> 的解析分开：Apply 交出去的是"实际装载哪种语言"（跟随系统时已经算成了
    /// 具体语言），这个交出去的仍是"用户选的是哪一项"。设置页的下拉框与它的去重判断都走这里——
    /// 拿 Current 去比对的话，英文系统上用户手动点一下「English」，生效语言恰好也是 en，
    /// 就会被当成"没变化"直接忽略，设置里那句 "system" 永远清不掉。</summary>
    public static string Normalize(string? lang)
    {
        if (string.Equals(lang, System, StringComparison.OrdinalIgnoreCase))
            return System;
        return Array.Find(Supported, l => string.Equals(l, lang, StringComparison.OrdinalIgnoreCase)) ?? Zh;
    }

    /// <summary>按系统 UI 语言挑一种受支持的语言。
    ///
    /// 取的是 <see cref="CultureInfo.CurrentUICulture"/>——即 Windows「设置 → 时间和语言 → 语言」
    /// 里用户自己选的那个显示语言。刻意不用另外两个近亲：
    /// <c>CurrentCulture</c> 是区域格式（可以日期用德语而界面用英语），
    /// <c>InstalledUICulture</c> 是系统**安装**语言（用户把显示语言改成中文后它仍是安装时的英文）。
    /// ⚠️ 更不能读 <c>FrameworkElement.LanguageProperty</c>：WPF 给元素上的默认值恒为 "en-US"，
    /// 拿它当"系统语言"，中文 Windows 上也会解析出 en。
    ///
    /// 先按完整名匹配（zh-Hans / fr-CA），再退化到两字母名（zh / fr）——语言文件是按两字母命名的，
    /// 所以第二步才是真正生效的那一步；第一步留着是为了将来加 "zh-Hant" 这类地区变体时不必改这里。
    /// 两步都不中就回落 <see cref="SystemFallback"/>（不变文化 InvariantCulture 也走这条）。</summary>
    public static string ResolveSystemLanguage(CultureInfo culture)
    {
        return Array.Find(Supported, l => string.Equals(l, culture.Name, StringComparison.OrdinalIgnoreCase))
            ?? Array.Find(Supported, l => string.Equals(l, culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
            ?? SystemFallback;
    }

    public static void Apply(string? lang)
    {
        var requested = Normalize(lang);
        Current = requested == System ? ResolveSystemLanguage(CultureInfo.CurrentUICulture) : requested;
        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Strings/Lang.{Current}.xaml"),
        };

        // ① 合并字典：XAML 侧的 DynamicResource 引用靠它，换语言后自动刷新
        var dicts = Application.Current?.Resources.MergedDictionaries;
        if (dicts is not null)
        {
            for (var i = dicts.Count - 1; i >= 0; i--)
            {
                if (dicts[i].Source?.OriginalString.Contains("/Strings/Lang.", StringComparison.OrdinalIgnoreCase) == true)
                    dicts.RemoveAt(i);
            }
            dicts.Add(dictionary);
        }

        // ② 快照：代码侧（Tr）只读这一份。键用 Ordinal——资源键是 ASCII 标识符，
        // 忽略大小写只会让 L.xxx 与 L.Xxx 这种拼写漂移静默取到值。
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in dictionary.Keys)
        {
            if (key is string name && dictionary[key] is string value)
                snapshot[name] = value;
        }
        _snapshot = snapshot;
    }

    /// <summary>取词；缺键返回键名本身（缺翻译时可见、不空白）。尚未装载语言（单测、设计器）
    /// 或没有 Application 上下文的场景下同样返回键名。</summary>
    public static string Tr(string key) =>
        _snapshot is { } snapshot && snapshot.TryGetValue(key, out var value) ? value : key;

    /// <summary>取词 + 格式化。参数用 object?（而非 object）：格式化实参允许为 null，
    /// string.Format 会把它渲染成空串；若声明为非空 object，所有传入可空值的调用点都会报 CS8604。</summary>
    public static string TrF(string key, params object?[] args) =>
        string.Format(Tr(key), args ?? []);
}
