namespace SliderSorter.Core;

/// <summary>
/// Core 层面向用户文案的取词出口。
/// <para>
/// Core 是纯 C# 类库、不引用 UI 层，无法直接调 <c>L10n</c>；改由 WPF 层在启动时把取词函数注入进来。
/// 这样日志、诊断报告、分组校验错误才会跟着界面语言走 —— 否则英文/俄语界面里会夹着大段中文。
/// </para>
/// <para>
/// 未注入时（单测、设计器、命令行场景）返回键名本身：宁可露出 <c>L.Core_Xxx</c> 这种明显的占位，
/// 也不要让中文字面量从 Core 里漏出去 —— 后者正是这条规则要消灭的东西。
/// </para>
/// </summary>
public static class CoreStrings
{
    /// <summary>取词函数：(键, 格式化实参) =&gt; 文案。由 UI 层注入，通常就是 L10n.TrF。</summary>
    public static Func<string, object?[], string>? Localizer { get; set; }

    /// <summary>取词（无格式化实参）。</summary>
    public static string Get(string key) => Localizer?.Invoke(key, []) ?? key;

    /// <summary>取词 + 格式化。实参可空（string.Format 会渲染成空串）。</summary>
    public static string Format(string key, params object?[] args) => Localizer?.Invoke(key, args) ?? key;
}
