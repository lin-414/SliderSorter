using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BSGroupGenerator.Core;
using BSGroupGenerator.Wpf.Services;
using BSGroupGenerator.Wpf.ViewModels;

namespace BSGroupGenerator.Wpf.Views.Pages;

/// <summary>预设列表的一行。
/// <para>
/// 必须是具名类型，不能写成 <c>(RulePreset Preset, string Title, string Detail)</c> 这样的元组：
/// 元组的元素名只存在于编译期，运行时是 <c>ValueTuple</c> 的 <c>Item1/Item2/Item3</c>，
/// 而 WPF 绑定是按反射解析路径的——XAML 里的 <c>{Binding Title}</c> 在元组上找不到就叫解析失败，
/// 静默给空串。列表于是显示成一片空白（行还在、但一个字都没有），看着就像「预设根本没保存上」。
/// </para></summary>
public sealed record RulePresetRow(RulePreset Preset, string Title, string Detail)
{
    /// <summary>把预设摊成列表行。抽成独立函数是为了让"行到底渲染成什么"能被单测直接调用——
    /// 这一层曾经是元组，绑定静默给空串，而造一个真窗口才能复现它不值当。</summary>
    public static List<RulePresetRow> Build(IEnumerable<RulePreset> presets) => presets.Select(p =>
    {
        var group = p.GroupName.Length == 0 ? L10n.Tr("L.Word_Unspecified") : p.GroupName;
        var title = L10n.TrF("L.RulePresets_RowTitle", p.Name,
            L10n.Tr(p.Add ? "L.Word_Add" : "L.Word_Remove"), group);
        var detail = L10n.TrF("L.RulePresets_RowDetail",
            string.IsNullOrWhiteSpace(p.ModInclude) ? L10n.Tr("L.Word_Unlimited") : p.ModInclude,
            string.IsNullOrWhiteSpace(p.OutfitInclude) ? L10n.Tr("L.Word_All") : p.OutfitInclude,
            string.IsNullOrWhiteSpace(p.OutfitExclude) ? L10n.Tr("L.Word_None") : p.OutfitExclude);
        if (p.UnassignedOnly)
            detail += L10n.Tr("L.RulePresets_UnassignedSuffix");
        return new RulePresetRow(p, title, detail);
    }).ToList();
}

/// <summary>规则预设页：说明工作流，查看/编辑/删除已保存的自动归组规则。
/// 「新建预设…」「编辑所选…」拉起规则归组编辑器（走 VM 注入的委托，编辑器全窗口只开一个）。</summary>
public partial class RulePresetsPage : UserControl
{
    private MainViewModel _vm = null!;
    private List<RulePresetRow> _rows = [];

    public RulePresetsPage()
    {
        InitializeComponent();
        // VM 由壳经 DataContext 继承下来，构造期还没有；一拿到就先填一次，
        // 这样"这一页还没被切到过"时列表也是对的（壳会预先构造全部标签页的内容）。
        DataContextChanged += (_, _) =>
        {
            _vm = (MainViewModel)DataContext;
            Reload();
        };
        // 编辑器是独立窗口，它存了新预设不会惊动这一页；切回标签页时重读一遍。
        // 顺带也覆盖"在设置页换了语言再回来"——行文本是代码拼的，不会自己跟着变。
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _vm is not null)
                Reload();
        };
    }

    private void Reload()
    {
        // 重读会重建 _rows，旧的行对象与 ListBox 里的选中项不再对应——
        // 先把选中索引记住，重建后按新集合的长度夹一下再恢复，
        // 否则删掉一条后会停在"索引越界"或"选中了别的那条"上。
        var keep = List.SelectedIndex;
        _rows = RulePresetRow.Build(_vm.Settings.RulePresets);
        List.ItemsSource = _rows;
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (keep >= 0 && keep < _rows.Count)
            List.SelectedIndex = keep;
        else
            List.SelectedIndex = -1;

        // ItemsSource 换掉后选中项可能已被清空，而 SelectionChanged 不一定再发一次；
        // 详情栏必须显式同步一次，否则会出现"列表没选中、右栏还留着上一条的详情"。
        ShowDetail(SelectedRow());
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowDetail(SelectedRow());

    private RulePresetRow? SelectedRow() =>
        List.SelectedIndex >= 0 && List.SelectedIndex < _rows.Count ? _rows[List.SelectedIndex] : null;

    /// <summary>把右栏切到"某条预设的详情"或"未选中"两态。</summary>
    private void ShowDetail(RulePresetRow? row)
    {
        DetailPanel.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        DetailHint.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        if (row is null)
            return;
        DetailTitle.Text = row.Title;
        DetailBody.Text = row.Detail;
    }

    private void NewPreset_Click(object sender, RoutedEventArgs e) => OpenEditor(null);

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPreset() is { } preset)
            OpenEditor(preset);
        else
            Notify.Info(Window.GetWindow(this), L10n.Tr("L.Title_Tip"), L10n.Tr("L.RulePresets_SelectToEdit"));
    }

    /// <summary>双击一行＝编辑它（列表里最自然的"打开"手势）。
    /// 双击空白处不弹提示：那多半只是想取消选中，弹框反而是噪音。</summary>
    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedPreset() is { } preset)
            OpenEditor(preset);
    }

    private RulePreset? SelectedPreset() =>
        List.SelectedIndex >= 0 && List.SelectedIndex < _rows.Count ? _rows[List.SelectedIndex].Preset : null;

    /// <summary>拉起编辑器。编辑器没能就绪（例如还没扫描过服装）时它自己会说明原因，
    /// 这一页什么都不做——以前是窗口时代"打不开就连带关掉自己"的分支，现在页面常驻，没有可关的东西。</summary>
    private void OpenEditor(RulePreset? preset) => _vm.RuleEditorOpener?.Invoke(preset);

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var idx = List.SelectedIndex;
        if (idx < 0 || idx >= _rows.Count)
        {
            Notify.Info(Window.GetWindow(this), L10n.Tr("L.Title_Tip"), L10n.Tr("L.RulePresets_SelectToDelete"));
            return;
        }
        // 以前是"关掉窗口时由宿主统一保存"，页面没有"关"这个时刻，删了就立刻落盘
        _vm.Settings.RulePresets.Remove(_rows[idx].Preset);
        _vm.Settings.Save();
        _vm.Log(L10n.TrF("L.Log_PresetsUpdated", _vm.Settings.RulePresets.Count));
        Reload();
    }
}
