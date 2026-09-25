using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using SliderSorter.Core;
using SliderSorter.Wpf.Services;
using SliderSorter.Wpf.ViewModels;
using Xunit;

namespace SliderSorter.Wpf.Tests;

/// <summary>
/// 字号缩放的四条静默失效。
/// <para>
/// ① <b>TypeScale.xaml 里的基准值没被改写。</b>FontScaleManager.Apply 就地改写那份合并字典，
/// 遮住 Controls/TypeScale 的默认。哪天字典换了个加载路径、或者取整口径变了，
/// 界面就会"档位点了、字号不动"——门禁与布局探针都量不到这一点（探针刻意只按 100% 基准量）。
/// </para>
/// <para>
/// ② <b>DynamicResource 没跟着重解析。</b>这正是第一版踩过的坑：把缩放值写到 app 资源根上
/// "遮住"默认值，对已解析的表达式不传播（症状：段标题缩放了——它走显式样式、正文纹丝不动——
/// 它靠窗口继承）。所以这里必须用一个**先排版、后 Apply** 的在树元素验证真实解析值。
/// </para>
/// <para>
/// ③ <b>重复 Apply 复利。</b>Apply 的基准值每次从新读的字典实例取；若哪天改成从被改写过的
/// 字典里取，连点两次下拉就是平方缩放，且设置里的档位与实际字号永远对不上。
/// </para>
/// <para>
/// ④ <b>下拉框与生效档脱节。</b>回填按设置值吸档（与 Apply 同一份 Normalize），换档比对按
/// CurrentPercent——两条口径一旦分岔，手改出的非档位值（137）就会显示成空，或者
/// "点了实际生效的那一档却被当成没变化"。
/// </para>
/// 挂 <see cref="WpfStaCollection"/> 串行：改写的是进程级 Application 资源，末尾必须还原 100%，
/// 别把同集合里其他用例的布局量测带偏。动 Application 的用例一律经 <see cref="InHost"/>：
/// 那条常驻线程才配好了字典与 Application（FontScaleManager.Apply 在裸环境是显式静默的）。
/// </summary>
[Collection(WpfStaCollection.Name)]
public class FontScaleTests
{
    [Fact]
    public void ApplyScalesTheTypeScaleDictionary()
    {
        InHost(() =>
        {
            FontScaleManager.Apply(125);
            var dict = TypeScaleDict();
            Assert.NotNull(dict);
            // 四个整档值同时锁住"乘法口径"（18/16/13/11 × 1.25）与"取整口径"（AwayFromZero）：
            // 22.5 → 23、16.25 → 16、13.75 → 14
            Assert.Equal(23, (double)dict!["Type.H1"]);
            Assert.Equal(20, (double)dict["Type.H2"]);
            Assert.Equal(16, (double)dict["Type.H3"]);
            Assert.Equal(14, (double)dict["Type.H4"]);
        });
    }

    /// <summary>用户可见的那份契约：先挂树排版（表达式已按 100% 解析），后换档——
    /// 在树元素的 FontSize 必须经 DynamicResource 失效即时跟到新值。</summary>
    [Fact]
    public void LiveElementsFollowTheScaleChange()
    {
        WpfHost.WithWindow<object?>(() =>
        {
            var host = new ContentControl();
            host.SetResourceReference(Control.FontSizeProperty, "Type.H3");
            var text = new TextBlock { Text = "BodySlide" };
            text.SetResourceReference(TextElement.FontSizeProperty, "Type.H4");
            host.Content = text;
            return host;
        }, host =>
        {
            var hostControl = (ContentControl)host;
            var text = (TextBlock)hostControl.Content!;
            WpfHost.Pump();
            Assert.Equal(11, text.FontSize); // 100% 基准先就位

            FontScaleManager.Apply(125);
            WpfHost.Pump();
            // 11 × 1.25 = 13.75 → 14；写资源根的影子写法在这里会仍是 11（②的坑）
            Assert.Equal(14, text.FontSize);
            Assert.Equal(16, hostControl.FontSize);

            return (object?)null;
        });
    }

    [Fact]
    public void RepeatedApplyDoesNotCompound()
    {
        InHost(() =>
        {
            FontScaleManager.Apply(125);
            FontScaleManager.Apply(110);
            // 16 × 1.1 = 17.6 → 18；若从上一次的结果往上乘，这里会是 20 × 1.1 = 22
            Assert.Equal(18, (double)TypeScaleDict()!["Type.H2"]);
        });
    }

    [Theory]
    [InlineData(null, 100)]
    [InlineData(90, 90)]
    [InlineData(125, 125)]
    [InlineData(0, 90)]
    [InlineData(-50, 90)]
    [InlineData(137, 125)]
    [InlineData(300, 125)]
    [InlineData(105, 100)]
    public void NormalizeSnapsToNearestPreset(int? input, int expected) =>
        Assert.Equal(expected, FontScaleManager.Normalize(input));

    [Fact]
    public void ScaledMatchesTheResourceRoundTrip()
    {
        InHost(() =>
        {
            FontScaleManager.Apply(125);
            // 代码侧拼的文本（使用说明 FlowDocument）取不到 DynamicResource，
            // Scaled 必须与资源键改写同一口径，否则同一个"125%"下两套字号
            Assert.Equal((double)TypeScaleDict()!["Type.H3"], FontScaleManager.Scaled(13));
        });
    }

    [Fact]
    public void ViewModelBackfillsAndPersistsTheSelectedPreset()
    {
        InHost(() =>
        {
            using var scope = IsolatedUserState.Enter();
            // 启动口径：设置里存的是 125，VM 回填要显示 125%（不依赖 Apply 先跑过）
            AppSettings.Shared.UiFontScalePercent = 125;
            var vm = new MainViewModel();
            Assert.Equal(FontScaleManager.Presets.Length, vm.FontScaleOptions.Count);
            Assert.Equal("125%", vm.SelectedFontScaleOption?.Label);

            // 换档即生效并落盘（落进隔离目录，不碰真实 %APPDATA%）
            vm.SelectedFontScaleOption = vm.FontScaleOptions.First(o => o.Value == "90");
            Assert.Equal(90, FontScaleManager.CurrentPercent);
            Assert.Equal(90, AppSettings.Shared.UiFontScalePercent);
            Assert.True(File.Exists(scope.SettingsFile));
        });
    }

    [Fact]
    public void ViewModelBackfillSnapsHandEditedSettings()
    {
        InHost(() =>
        {
            using var scope = IsolatedUserState.Enter();
            AppSettings.Shared.UiFontScalePercent = 137; // 手改的非档位值
            var vm = new MainViewModel();
            // 下拉框里没有 137% 这一档：必须吸到最近档显示，而不是显示成空
            Assert.Equal("125%", vm.SelectedFontScaleOption?.Label);
        });
    }

    /// <summary>被改写的那份合并字典（按 Source 定位，与 FontScaleManager 同一口径）。</summary>
    private static ResourceDictionary? TypeScaleDict() =>
        Application.Current.Resources.MergedDictionaries
            .FirstOrDefault(d => d.Source?.OriginalString.Contains("TypeScale.xaml") == true);

    /// <summary>在常驻 UI 线程上跑一段（Application 与 app 级字典就位），并保证收尾把字号还原到 100%——
    /// 这些用例改的是进程级资源，同集合里还有量布局的用例。</summary>
    private static void InHost(Action action) =>
        WpfHost.WithWindow<object?>(() => new FrameworkElement(), _ =>
        {
            try
            {
                action();
            }
            finally
            {
                FontScaleManager.Apply(FontScaleManager.DefaultPercent);
            }
            return null;
        });
}
