using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
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
