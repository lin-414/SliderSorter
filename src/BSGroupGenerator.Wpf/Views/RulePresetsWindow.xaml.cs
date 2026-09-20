using System.Windows;
using System.Windows.Input;
using BSGroupGenerator.Core;

using BSGroupGenerator.Wpf.Services;

namespace BSGroupGenerator.Wpf.Views;

/// <summary>预设列表的一行。
/// <para>
/// 必须是具名类型，不能写成 <c>(RulePreset Preset, string Title, string Detail)</c> 这样的元组：
/// 元组的元素名只存在于编译期，运行时是 <c>ValueTuple</c> 的 <c>Item1/Item2/Item3</c>，
/// 而 WPF 绑定是按反射解析路径的——XAML 里的 <c>{Binding Title}</c> 在元组上找不到就叫解析失败，
/// 静默给空串。列表于是显示成一片空白（行还在、但一个字都没有），看着就像「预设根本没保存上」。
/// </para></summary>
public sealed record RulePresetRow(RulePreset Preset, string Title, string Detail);

/// <summary>规则预设管理：说明工作流，查看/编辑/删除已保存预设；「新建预设」「编辑所选」拉起规则归组编辑器。</summary>
public partial class RulePresetsWindow : Window
{
    private readonly List<RulePreset> _presets;
    private readonly Func<RulePreset?, bool> _openEditor;
    private List<RulePresetRow> _rows = [];

    /// <summary>是否有预设被删除（宿主据此保存设置）。</summary>
    public bool Changed { get; private set; }

    /// <param name="openEditor">拉起规则归组编辑器，返回它是否已就绪。
    /// 传 null = 新建，「编辑所选」传选中的那条。</param>
    public RulePresetsWindow(List<RulePreset> presets, Func<RulePreset?, bool> openEditor)
    {
        InitializeComponent();
        _presets = presets;
        _openEditor = openEditor;
        Reload();
    }

    private void Reload()
    {
        _rows = _presets.Select(p =>
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
        List.ItemsSource = _rows;
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NewPreset_Click(object sender, RoutedEventArgs e) => OpenEditor(null);

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPreset() is { } preset)
            OpenEditor(preset);
        else
            Notify.Info(this, L10n.Tr("L.Title_Tip"), L10n.Tr("L.RulePresets_SelectToEdit"));
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

    /// <summary>拉起编辑器。编辑器没能就绪（例如还没扫描过服装）时**不关本窗口**——
    /// 先关窗再发现新窗口没出现，用户看到的就是"点了没反应"。</summary>
    private void OpenEditor(RulePreset? preset)
    {
        // 编辑器是独立窗口，本窗口不会随之刷新；成功了就关掉，避免留下一份过期的列表
        if (!_openEditor(preset))
            return;
        Close();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var idx = List.SelectedIndex;
        if (idx < 0 || idx >= _rows.Count)
        {
            Notify.Info(this, L10n.Tr("L.Title_Tip"), L10n.Tr("L.RulePresets_SelectToDelete"));
            return;
        }
        _presets.Remove(_rows[idx].Preset);
        Changed = true;
        Reload();
    }
}
