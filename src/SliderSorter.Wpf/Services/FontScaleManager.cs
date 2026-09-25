using System.Windows;

namespace SliderSorter.Wpf.Services;

/// <summary>
/// 界面字号缩放：按用户选的档位改写字阶基准值（Themes/TypeScale.xaml 的 Type.H1–H4）。
/// 字号 setter 一律 DynamicResource 引用那四个键，改写后所有已打开窗口即时生效。
/// </summary>
public static class FontScaleManager
{
    /// <summary>默认档（100%，即不缩放）。设置文件里没有该字段时回落到它。</summary>
    public const int DefaultPercent = 100;

    /// <summary>可选档位（%）。档位间距要能一眼分辨：90/100/110/125 是"缩小一档 / 原大 /
    /// 大一号 / 更大一档"；再往上（150%+）正文 20 号起，FieldCombo 的定高 30 会开始夹字，
    /// 也不再有"顺手调调"的意义，真需要更大字时该谈的是系统级 DPI 缩放。</summary>
    public static readonly int[] Presets = [90, 100, 110, 125];

    /// <summary>TypeScale.xaml 里的四个键，顺序与 <see cref="_pristine"/> 一一对应。</summary>
    private static readonly string[] ScaleKeys = ["Type.H1", "Type.H2", "Type.H3", "Type.H4"];

    /// <summary>TypeScale.xaml 的 100% 基准值，首次 Apply 时从字典里取走缓存。
    /// 之后字典里的值会被历次 Apply 改写，只有这份缓存始终是原值。
    /// Apply 是字典唯一的改写者，所以"首次看到的"必然是 XAML 里的原值。</summary>
    private static double[]? _pristine;

    /// <summary>当前生效档（%；设置文件里的值经 <see cref="Normalize"/> 吸到最近档后的结果）。</summary>
    public static int CurrentPercent { get; private set; } = DefaultPercent;

    /// <summary>缩放后的像素值：代码里拼的文本（使用说明的 FlowDocument）取不到
    /// DynamicResource，用这条拿到与资源键同一口径的字号。</summary>
    public static double Scaled(double baseSize) =>
        Math.Round(baseSize * CurrentPercent / 100.0, MidpointRounding.AwayFromZero);

    /// <summary>吸到最近的预设档。设置文件可能被手改出任意值（137、0、负数），
    /// 不吸档的话下拉框里没有那一项，外观节会显示成空。</summary>
    public static int Normalize(int? percent)
    {
        if (percent is not { } p)
            return DefaultPercent;
        return Presets.MinBy(x => Math.Abs(x - p));
    }

    /// <summary>应用一个档位（%）。必须待设置装载完、在首窗口创建前调用；
    /// 运行中换档由 MainViewModel 的下拉框走这里，已打开的窗口经 DynamicResource 失效即时跟随。</summary>
    public static void Apply(int? percent)
    {
        CurrentPercent = Normalize(percent);
        if (Application.Current is not { } app)
            return; // 单测/探针环境没有 Application：保持基准值即可

        // 基准值只活在 TypeScale.xaml（字阶表的唯一出处）。就地改写那份合并字典——
        // 订阅它的 DynamicResource 表达式收到的是"值变了"的失效通知，必然重解析。
        // ⚠️ 不能把缩放值写到 app 资源根上"遮住"默认：运行期实测那种影子写入对
        // 已解析的 DynamicResource 不传播（合并字典里已有同名键时尤其如此），
        // 症状就是"点了档位、正文不动"，ThemeManager 换色板用整字典替换也是同一个教训。
        var target = app.Resources.MergedDictionaries
            .FirstOrDefault(d => d.Source?.OriginalString.Contains("TypeScale.xaml") == true);
        if (target is null)
            return;

        // 基准值用首次缓存的那份：target 里的值上一次 Apply 已被改写，
        // 拿它乘档位就是复利（125% 连点两次变 156%）。
        _pristine ??= [.. ScaleKeys.Select(k => (double)target[k])];
        for (var i = 0; i < ScaleKeys.Length; i++)
            target[ScaleKeys[i]] = Scaled(_pristine[i]);
    }
}
