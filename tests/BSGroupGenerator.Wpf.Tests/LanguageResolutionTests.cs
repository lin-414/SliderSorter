using System.Globalization;
using BSGroupGenerator.Core;
using BSGroupGenerator.Wpf.Services;
using BSGroupGenerator.Wpf.ViewModels;
using Xunit;

namespace BSGroupGenerator.Wpf.Tests;

/// <summary>「界面语言 = 跟随系统」这条链上可以用纯函数验的那几段：设置里那个值怎么规范化、
/// 系统 UI 语言怎么映射到受支持的语言、以及设置页下拉认的是"选的那一项"而不是"生效语言"。
///
/// 为什么要单独立一组用例：这一整条链的失效都是**静默**的——系统语言认错了只是界面语言不对
/// （编译期、色板门禁、布局探针全都不红），而"跟随系统"被写成语言码之后，
/// 用户下次在 Windows 里换显示语言程序也不会跟，症状是"设置里明明写着跟随系统"。
///
/// 刻意**不调用** L10n.Apply：它会装载 ResourceDictionary 并写下取词快照，
/// 而 <see cref="L10nTests"/> 的「无 Application 时回落到键名」正依赖那份快照为 null
/// （见该文件的说明）。这里要验的三件事都不需要真的装载语言。
/// 挂 <see cref="WpfStaCollection"/> 是为串行化：VM 构造会读用户设置，本用例已把设置目录与
/// MO2 全局实例根重定向到临时目录（见 <see cref="IsolatedUserState"/>）。</summary>
[Collection(WpfStaCollection.Name)]
public class LanguageResolutionTests
{
    /// <summary>两字母名那一步才是真正生效的：语言文件就叫 Lang.zh.xaml。
    /// 地区变体（zh-TW / fr-CA）必须落到同一个文件上——目前每种语言只有一份，没有繁中/加法语之分。</summary>
    [Theory]
    [InlineData("zh-CN", L10n.Zh)]
    [InlineData("zh-Hans", L10n.Zh)]
    [InlineData("zh-TW", L10n.Zh)]
    [InlineData("en-US", L10n.En)]
    [InlineData("ru-RU", L10n.Ru)]
    [InlineData("fr-CA", L10n.Fr)]
    public void SystemLanguageMapsToItsTwoLetterLanguageFile(string culture, string expected)
    {
        Assert.Equal(expected, L10n.ResolveSystemLanguage(new CultureInfo(culture)));
    }

    /// <summary>不支持的显示语言回落英语而不是中文：走这条路径的本来就不是中文用户。
    /// 这条断言同时钉住"回落目标"这个决定——改成 Zh 会让德语/日语/西语用户的界面突然变中文。</summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    [InlineData("es-ES")]
    [InlineData("pl-PL")]
    public void UnsupportedSystemLanguageFallsBackToEnglish(string culture)
    {
        Assert.Equal(L10n.En, L10n.ResolveSystemLanguage(new CultureInfo(culture)));
    }

    /// <summary>不变文化（没有语言信息）与"不支持的语言"走同一条回落。
    /// TwoLetterISOLanguageName 在这里是 "iv"，不是空串，所以它撞不进受支持列表。</summary>
    [Fact]
    public void InvariantCultureFallsBackToEnglish()
    {
        Assert.Equal(L10n.En, L10n.ResolveSystemLanguage(CultureInfo.InvariantCulture));
    }

    /// <summary>规范化只做三件事：认得出「跟随系统」、认得出受支持的语言码（不分大小写）、
    /// 其余一律回落到 zh。回落到 zh 是既有行为（老设置文件里从没出现过 "system"）。</summary>
    [Theory]
    [InlineData(null, L10n.Zh)]
    [InlineData("", L10n.Zh)]
    [InlineData("de", L10n.Zh)]
    [InlineData("zh-CN", L10n.Zh)]
    [InlineData(L10n.Zh, L10n.Zh)]
    [InlineData("EN", L10n.En)]
    [InlineData(L10n.Fr, L10n.Fr)]
    [InlineData(L10n.System, L10n.System)]
    [InlineData("SYSTEM", L10n.System)]
    public void NormalizeKeepsKnownChoicesAndCollapsesUnknownToChinese(string? raw, string expected)
    {
        Assert.Equal(expected, L10n.Normalize(raw));
    }

    /// <summary>新装的默认值就是「跟随系统」——这条断言是那个产品决定本身。
    /// 改回 "zh" 会让非中文 Windows 的新用户一进来看到中文界面（旧行为），
    /// 中文 Windows 的用户两种默认值下看到的东西一样。</summary>
    [Fact]
    public void FreshSettingsFollowSystemByDefault()
    {
        Assert.Equal(L10n.System, new AppSettings().UiLanguage);
    }

    /// <summary>设置页语言下拉：第一项是「跟随系统」，且设置里没写过语言时它就选中这一项。
    /// 这一条盖住两个静默失效——把「跟随系统」加进了列表却没接上回填（下拉显示成「中文」），
    /// 以及回填误用 L10n.Current（在非中文系统上显示成系统命中的那种语言）。
    /// 顺序也一并钉住：它是"我要什么行为"与"我要哪种语言"两组意图的分界。</summary>
    [Fact]
    public void LanguageOptionsOfferFollowSystemFirstAndSelectItByDefault()
    {
        using var scope = IsolatedUserState.Enter();
        var vm = new MainViewModel();

        Assert.Equal([L10n.System, L10n.Zh, L10n.En, L10n.Ru, L10n.Fr],
            vm.LanguageOptions.Select(o => o.Value));
        Assert.Equal(L10n.System, vm.SelectedLanguageOption?.Value);
    }
}
