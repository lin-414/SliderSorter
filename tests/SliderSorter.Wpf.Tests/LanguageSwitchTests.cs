using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SliderSorter.Core;
using SliderSorter.Wpf.ViewModels;
using SliderSorter.Wpf.Views.Pages;
using Xunit;

namespace SliderSorter.Wpf.Tests;

/// <summary>切语言后，**代码拼出来的**绑定串要跟着换。
/// <para>
/// XAML 里 {DynamicResource} 那半边由 L10n.Apply 换字典自动刷新，没人管也没事；靠 ViewModel
/// 属性暴露的那半边（L10n.Tr/TrF 拼好的句子）没有变更源——值在扫描完成那一刻就定下来了，
/// 之后没人再问一遍，界面上留着的还是旧语言那句。这类漏一处就静默错一处，编译、色板门禁、
/// 布局探针都不红，所以逐个钉在这里。
/// </para>
/// 2026-09-23 踩到的是「上次扫描：0 秒前 · 模组 0 · 服装 6877」：切到法语后满屏都换了，
/// 只有它还站着中文。
/// <para>
/// 这里刻意不调 <see cref="Services.L10n.Apply"/>：装载语言会把取词快照留在进程里，而本程序集
/// 一批用例钉的是"宿主不装语言、取词回落成键名"这个前提（见 OutputConflictPageTests 里那些
/// 按 L.xxx 键名写的断言）。所以分两半钉：语言切换确实为这些属性发了通知，且页面上有元素
/// 绑着这个通知的落点。
/// </para>
/// 挂 <see cref="WpfStaCollection"/> 串行：页面与模板要真窗口。</summary>
[Collection(WpfStaCollection.Name)]
public class LanguageSwitchTests
{
    [Fact]
    public void CodeComposedTextsAreRaisedAgainOnLanguageSwitch()
    {
        using var scope = IsolatedUserState.Enter();
        MainViewModel? vm = null;
        WpfHost.WithWindow(
            () =>
            {
                // LastScanAt 给上值：走的是"已经扫过"那一支，才是用户截图里的那句
                vm = new MainViewModel { LastScanAt = DateTime.Now };
                return new SettingsPage { DataContext = vm };
            },
            page =>
            {
                var raised = new List<string>();
                vm!.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);
                // 语言下拉的处理器在 L10n.Apply + 落盘之后调的就是它
                vm.OnLanguageChanged();

                Assert.Contains(nameof(MainViewModel.ScanSummaryText), raised);
                Assert.Contains(nameof(MainViewModel.UnconfiguredBanner), raised);
                // 通知还得有落点：设置页那句扫描状态确实挂在某个 TextBlock.Text 上
                Assert.Equal(nameof(MainViewModel.ScanSummaryText), TextBindingPath(page));
                return true;
            });
    }

    /// <summary>2026-09-25 踩到的「英文界面里冒出全局」：实例下拉显示的 DisplayName 是 CoreStrings
    /// 现拼的串，Mo2Instance 自身发不了通知，WPF 在容器生成时求值一次就定死。修法是包一层
    /// <see cref="InstanceItem"/>（INPC），OnLanguageChanged 逐项 RefreshDisplay。这里用假取词器让
    /// 取词结果在切换前后真的不同，直接断言下拉框显示的文字跟着变了；顺带钉两条：选中项不被冲掉，
    /// 语言切换不触发 OnSelectedInstanceChanged 的连锁（那条链失败会往日志写行）。</summary>
    [Fact]
    public void InstanceComboTextIsReevaluatedOnLanguageSwitch()
    {
        using var scope = IsolatedUserState.Enter();
        var saved = CoreStrings.Localizer;
        var kindText = "旧种类词";
        CoreStrings.Localizer = (_, _) => kindText;
        try
        {
            // 假实例不落盘 ini：GameName 取不到会走「未配置游戏」键，对假取词器来说都一样
            var instanceRoot = Path.Combine(Path.GetTempPath(), "bsgg-wpf-tests", Path.GetRandomFileName());
            var instanceDir = Path.Combine(instanceRoot, "Skyrim AE");
            Directory.CreateDirectory(instanceDir);
            var fake = new Mo2Instance(instanceDir, Path.Combine(instanceDir, "ModOrganizer.ini"),
                Mo2InstanceKind.Global);
            MainViewModel? vm = null;
            try
            {
                WpfHost.WithWindow(
                    () =>
                    {
                        vm = new MainViewModel();
                        vm.InstanceItems = new ObservableCollection<InstanceItem> { new(fake) };
                        vm.SelectedInstance = fake;
                        return new SettingsPage { DataContext = vm };
                    },
                    page =>
                    {
                        var combo = FindComboBoundTo(page, "InstanceItems");
                        Assert.NotNull(combo);
                        // 落点：下拉框显示的确实是 DisplayName 这个属性
                        Assert.Equal("DisplayName", combo.DisplayMemberPath);
                        // 切换前：容器装的是旧词
                        Assert.Equal("旧种类词", TextBoundTo(combo, "DisplayName"));
                        var logCountBefore = vm!.LogLines.Count;

                        kindText = "new kind word";
                        vm.OnLanguageChanged();
                        page.UpdateLayout();
                        WpfHost.Pump();
                        page.UpdateLayout();

                        // 通知真的让选中框重读了 DisplayName
                        Assert.Equal("new kind word", TextBoundTo(combo, "DisplayName"));
                        // 选中项没有被冲掉
                        Assert.Same(fake, vm.SelectedInstance);
                        // 没有顺带重跑 profile/探测链（那条链失败会往日志写行）
                        Assert.Equal(logCountBefore, vm.LogLines.Count);
                        return true;
                    });
            }
            finally
            {
                Directory.Delete(instanceRoot, recursive: true);
            }
        }
        finally
        {
            CoreStrings.Localizer = saved;
        }
    }

    /// <summary>按 ItemsSource 绑定路径找 ComboBox（设置页有好几个下拉，按路径认最稳）。</summary>
    private static ComboBox? FindComboBoundTo(DependencyObject node, string path)
    {
        if (node is ComboBox cb &&
            cb.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding?.Path?.Path == path)
            return cb;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (FindComboBoundTo(VisualTreeHelper.GetChild(node, i), path) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>组合框可视化树里那个绑定到指定路径的 TextBlock 的当前文字
    ///（DisplayMemberPath 生成的选中项文本就是它）；没生成/没绑定返回 null。</summary>
    private static string? TextBoundTo(DependencyObject node, string path)
    {
        if (node is TextBlock tb &&
            tb.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding?.Path?.Path == path)
            return tb.Text;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (TextBoundTo(VisualTreeHelper.GetChild(node, i), path) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>页面上显示扫描状态那句的 TextBlock 所绑的属性名（没绑就返回 null）。</summary>
    private static string? TextBindingPath(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is TextBlock tb
                && tb.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding?.Path?.Path
                    == nameof(MainViewModel.ScanSummaryText))
                return nameof(MainViewModel.ScanSummaryText);
            if (TextBindingPath(child) is { } found)
                return found;
        }
        return null;
    }
}
