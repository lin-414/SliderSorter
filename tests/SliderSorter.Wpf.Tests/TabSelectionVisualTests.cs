using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace SliderSorter.Wpf.Tests;

/// <summary>
/// 标签头的"当前在哪一页"标记只取决于选中，不取决于键盘焦点。
/// <para>
/// 钉住的是用户报的实机故障：刚打开程序时「分组生成」已经是当前页，标签头却没有颜色标识，
/// 再点一次才亮。根因是底色 B.Selected 原先挂在 <c>IsKeyboardFocused</c> 触发器上，
/// 而启动时标签头拿不到键盘焦点——那块底色其实是"焦点框"，不是"选中框"。
/// </para>
/// <para>
/// 宿主窗口是 <c>ShowActivated = false</c>、挪到屏幕外且全透明的，所以这里天然就是
/// "选中标签没有键盘焦点"的场景。换模板后系统默认的聚焦虚框已经没了，焦点必须自己画，
/// 因此焦点改成描边环，且不许再换底色——同一个底色不能同时表示两种状态。
/// </para>
/// </summary>
[Collection(WpfStaCollection.Name)]
public class TabSelectionVisualTests
{
    /// <summary>B.Selected（暗色色板 C.Selected #3A5F54）。</summary>
    private static readonly Color Selected = Color.FromRgb(0x3A, 0x5F, 0x54);

    [Fact]
    public void SelectedTabIsFilledWithoutKeyboardFocus()
    {
        var (picked, unpicked, ring) = Read(tc =>
        {
            var current = Item(tc, 0);
            return (Fill(current), Fill(Item(tc, 1)), Ring(current));
        });

        Assert.Equal(Selected, picked);
        Assert.NotEqual(Selected, unpicked);
        Assert.Equal(Visibility.Collapsed, ring);
    }

    [Fact]
    public void KeyboardFocusDrawsRingWithoutReplacingFill()
    {
        var (fillBefore, gotFocus, fillAfter, ring) = Read(tc =>
        {
            var current = Item(tc, 0);
            var before = Fill(current);
            var focused = current.Focus();
            WpfHost.Pump();
            return (before, focused, Fill(current), Ring(current));
        });

        Assert.Equal(Selected, fillBefore);
        Assert.Equal(Selected, fillAfter);
        // 窗口没激活时拿不到键盘焦点（CI 上就是这种），此时只能断言"焦点没把底色换掉"
        if (gotFocus)
            Assert.Equal(Visibility.Visible, ring);
    }

    private static TabItem Item(TabControl tc, int index)
    {
        var item = (TabItem)tc.Items[index];
        item.ApplyTemplate();
        return item;
    }

    private static T Read<T>(Func<TabControl, T> inspect) =>
        WpfHost.WithWindow(NewTabs, content =>
        {
            var tc = (TabControl)content;
            WpfHost.Pump();
            tc.UpdateLayout();
            return inspect(tc);
        });

    private static FrameworkElement NewTabs()
    {
        // 宿主默认只合并 Controls/Components，色板不在场时 DynamicResource 根本不落值，
        // 量到的"没有底色"是假的。
        WpfHost.AddDictionary(Application.Current!, "Themes/Palette.Boutique.xaml");
        var tc = new TabControl();
        tc.Items.Add(new TabItem { Header = "分组生成", IsSelected = true });
        tc.Items.Add(new TabItem { Header = "设置" });
        return tc;
    }

    private static Color Fill(TabItem item) =>
        Assert.IsType<SolidColorBrush>(Part(item, "bd").Background).Color;

    private static Visibility Ring(TabItem item) => Part(item, "focus").Visibility;

    private static Border Part(TabItem item, string name) =>
        Assert.IsType<Border>(item.Template!.FindName(name, item));
}
