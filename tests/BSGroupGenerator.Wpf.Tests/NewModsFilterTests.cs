using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BSGroupGenerator.Core;
using BSGroupGenerator.Wpf.Services;
using BSGroupGenerator.Wpf.ViewModels;
using BSGroupGenerator.Wpf.Views;
using Xunit;
using Xunit.Abstractions;

namespace BSGroupGenerator.Wpf.Tests;

/// <summary>新装模组弹窗的过滤框（端到端回归用例）。
///
/// 该弹窗的树是「过滤一次、整棵重建」的，勾选状态若只跟着节点走，用户「先勾几个、再过滤找剩下的」
/// 就会把已勾的悄悄清空——那是本用例第 ④ 步钉住的行为。其余几步覆盖两个框各自的作用域
/// （服装名 / 模组与分隔符名）、模组框不命中模组时退化为按服装名匹配，以及第 ⑦ 步的提交契约：
/// 过滤生效时点「加入所选组」，只提交**当前可见且勾选**的那些，不看被过滤词挡住的项。
///
/// 与 GroupMembersWindow 的过滤语义一致（那里是同一个「按可见树取勾选」的约定）。
///
/// 挂 <see cref="WpfStaCollection"/> 串行：要构造真实窗口，而 WPF 的 Application 是进程级单例。
/// 刻意**不调用</c>L10n.Apply</c>——那会写下取词快照，让 L10nTests 的「无 Application 时回落到键名」
/// 在同集合内失去前提。因此断言只针对树结构与勾选状态，不依赖任何一句译文的具体措辞。</summary>
[Collection(WpfStaCollection.Name)]
public class NewModsFilterTests(ITestOutputHelper output)
{
    [Fact]
    public void FilteringNarrowsTheTreeAndKeepsTicks()
    {
        Exception? captured = null;
        var evidence = new List<string>();

        // WPF 窗口只能在 STA 线程上构造；xUnit 默认跑在 MTA 线程池线程上。
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                WpfHost.AddAppDictionaries(app);              // {StaticResource AppFont}/{StaticResource AppWindow}/树行样式
                WpfHost.AddDictionary(app, "Strings/Lang.zh.xaml");   // 过滤框占位提示是 DynamicResource

                var applied = new List<string>();
                var window = new NewModsWindow(new NewModsRequest
                {
                    Entries =
                    [
                        ("SepA", "M1", ["M1_A", "M1_B"]),
                        ("SepA", "M2", ["M2_A"]),
                        (null, "M3", ["M3_A", "M3_B"]),
                    ],
                    Groups = [new SliderGroup("G1")],
                    PreselectGroup = "G1",
                    PresetNames = [],
                    ApplyBatch = (_, names) => applied.AddRange(names),
                    ApplyPresets = () => [],
                });

                var filter = Box(window, "FilterBox");
                var modFilter = Box(window, "ModFilterBox");

                // 两个过滤框的占位提示必须真的取到新加的键（键名拼错时这里是空的）
                Assert.Equal(app.Resources["L.NewMods_FilterOutfits"], Watermark.GetText(filter));
                Assert.Equal(app.Resources["L.NewMods_FilterMods"], Watermark.GetText(modFilter));

                // ① 打开时默认全部勾选
                evidence.Add($"[初始] 模组={Join(Mods(window))} 服装={Join(Outfits(window))}");
                Assert.Equal(["M1", "M2", "M3"], Mods(window));
                Assert.Equal(["M1_A", "M1_B", "M2_A", "M3_A", "M3_B"], Outfits(window));
                Assert.Equal(Outfits(window), Checked(window));

                // 每个服装节点都必须挂在它的模组节点上（Parent 链）。摘要里的「来自 N 个模组」与
                // 勾选向上聚合都沿这条链走——漏接时摘要恒为「来自 0 个模组」，界面上看着像坏了。
                Assert.All(Walk(window).OfType<OutfitNodeVM>(),
                    o => Assert.NotNull(o.Parent as ModNodeVM));
                Assert.Equal("M1", (Outfit(window, "M1_A").Parent as ModNodeVM)!.Owner);

                // 勾选沿 Parent 链向上聚合：M1 下两个服装取消一个 → 模组呈半选（null）
                Outfit(window, "M1_B").IsChecked = false;
                evidence.Add($"[半选] M1.IsChecked={Mod(window, "M1").IsChecked}");
                Assert.Null(Mod(window, "M1").IsChecked);
                Outfit(window, "M1_B").IsChecked = true;
                Assert.True(Mod(window, "M1").IsChecked == true);

                // 未过滤时模组标题是朴素形式，不带「匹配 x/总数」标注
                Assert.Equal("M1　(2)", Mod(window, "M1").Text);

                // ② 用户的动作：取消勾选 M1_B
                Outfit(window, "M1_B").IsChecked = false;

                // ③ 服装名过滤：只剩命中的那个服装所在的模组
                filter.Text = "M2_A";
                evidence.Add($"[服装过滤 M2_A] 模组={Join(Mods(window))} 服装={Join(Outfits(window))}");
                Assert.Equal(["M2"], Mods(window));
                Assert.Equal(["M2_A"], Outfits(window));

                // 只筛掉一部分服装时，模组标题要标明「匹配 x/总数」——否则勾上它只选中可见的
                // 那几个，看着像丢了（过滤词换成一个 M1 下命中的服装）
                filter.Text = "M1_A";
                evidence.Add($"[服装过滤 M1_A] 模组标题={Mod(window, "M1").Text}");
                Assert.Equal(["M1"], Mods(window));
                Assert.Equal(["M1_A"], Outfits(window));
                Assert.NotEqual("M1　(2)", Mod(window, "M1").Text);

                // ④ 清空过滤：勾选状态原样保留（M1_B 仍是取消的）
                filter.Text = "";
                evidence.Add($"[清空过滤] 模组={Join(Mods(window))} 已勾={Join(Checked(window))}");
                Assert.Equal(["M1", "M2", "M3"], Mods(window));
                Assert.Equal(["M1_A", "M2_A", "M3_A", "M3_B"], Checked(window));

                // ⑤ 模组框命中分隔符名：其下模组全部可见
                modFilter.Text = "SepA";
                evidence.Add($"[模组过滤 SepA] 模组={Join(Mods(window))}");
                Assert.Equal(["M1", "M2"], Mods(window));

                // 过滤生效时分隔符自动展开——命中内容必须立即可见，否则用户看到的是一个折叠的
                // 分组标题，还得再点一下才知道里面有没有他要找的（清空过滤后恢复默认折叠）
                Assert.True(Separator(window, "SepA").IsExpanded);
                modFilter.Text = "";
                Assert.False(Separator(window, "SepA").IsExpanded);

                // ⑥ 模组与分隔符都不命中时，退化为按服装名匹配——一个框能同时搜到两类名字
                modFilter.Text = "M3_B";
                evidence.Add($"[模组过滤 M3_B] 模组={Join(Mods(window))} 服装={Join(Outfits(window))}");
                Assert.Equal(["M3"], Mods(window));
                Assert.Equal(["M3_B"], Outfits(window));

                // ⑦ 过滤生效时应用：只提交当前可见且勾选的（M1_B 已被取消勾选、又不可见，两个理由都不该提交）
                Assert.Equal(0, GroupCombo(window).SelectedIndex); // 选中预设目标组，否则会弹提示框
                Click(window, "ApplyButton");
                evidence.Add($"[应用] 提交={Join(applied)}");
                Assert.Equal(["M3_B"], applied);

                // 应用后该项从待办里消失：仍被 M3_B 过滤着，于是树里什么都不剩，只剩提示行
                Assert.Empty(Outfits(window));
                Assert.True(Roots(window).Single().IsPlaceholder, "过滤后无命中时应给一行提示，而不是空白树");

                // 清空过滤：已归组的 M3_B 不再出现，其余勾选状态不变
                modFilter.Text = "";
                evidence.Add($"[应用后清空过滤] 服装={Join(Outfits(window))} 已勾={Join(Checked(window))}");
                Assert.Equal(["M1_A", "M1_B", "M2_A", "M3_A"], Outfits(window));
                Assert.Equal(["M1_A", "M2_A", "M3_A"], Checked(window));

                window.Close();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // 超时兜底：万一走到 Notify.Info（模态框）这里会一直等下去，超时至少让用例失败而不是挂住测试进程
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "用例线程超时未结束——多半是弹出了模态提示框");

        foreach (var line in evidence)
            output.WriteLine(line);
        Assert.Null(captured);
    }

    /// <summary>x:Name 生成的字段是 internal，跨程序集取不到；走 FindName（窗口是 XAML 名称域的根）。</summary>
    private static T Named<T>(Window window, string name) where T : class =>
        Assert.IsType<T>(window.FindName(name));

    private static TextBox Box(Window window, string name) => Named<TextBox>(window, name);

    private static ComboBox GroupCombo(Window window) => Named<ComboBox>(window, "GroupCombo");

    private static void Click(Window window, string name) =>
        Named<Button>(window, name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static IEnumerable<NodeVM> Walk(Window window) =>
        Roots(window).SelectMany(root => root.WalkSelfAndDescendants());

    private static List<NodeVM> Roots(Window window) =>
        Named<TreeView>(window, "Tree").ItemsSource.Cast<NodeVM>().ToList();

    private static List<string> Mods(Window window) =>
        Walk(window).OfType<ModNodeVM>().Select(m => m.Owner).ToList();

    private static List<string> Outfits(Window window) =>
        Walk(window).OfType<OutfitNodeVM>().Select(o => o.OutfitName).ToList();

    private static List<string> Checked(Window window) =>
        Walk(window).OfType<OutfitNodeVM>().Where(o => o.IsChecked == true).Select(o => o.OutfitName).ToList();

    private static ModNodeVM Mod(Window window, string owner) =>
        Walk(window).OfType<ModNodeVM>().Single(m => m.Owner == owner);

    private static SeparatorNodeVM Separator(Window window, string name) =>
        Walk(window).OfType<SeparatorNodeVM>().Single(s => s.Text == name);

    private static OutfitNodeVM Outfit(Window window, string name) =>
        Walk(window).OfType<OutfitNodeVM>().Single(o => o.OutfitName == name);

    private static string Join(List<string> items) => string.Join("、", items);
}
