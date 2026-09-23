using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SliderSorter.Core;
using SliderSorter.Wpf.ViewModels;

using SliderSorter.Wpf.Services;

namespace SliderSorter.Wpf.Views;

/// <summary>
/// 规则归组窗口（非模态）：按服装名/模组名关键字批量把服装加入或移出某个组，
/// 预览按 分隔符 → 模组 → 命中服装 树形展示；点「应用」由宿主执行并入撤销栈。
/// </summary>
public partial class RuleGroupWindow : Window
{
    private readonly DispatcherTimer _previewDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly Func<string, string, string, bool, List<string>> _preview;
    private readonly Action _onApply;
    private readonly Action<RulePreset>? _onSavePreset;

    /// <summary>当前正在编辑的预设名（新建时为空）。「存为预设」把这个名字预填进输入框，
    /// 于是直接确定＝同名覆盖，改成别的名字＝另存为一条新预设。</summary>
    private string _editingName = "";

    public RuleGroupWindow(IReadOnlyList<SliderGroup> groups, string? preselectGroupName,
        List<(string? Separator, string Owner, List<string> Outfits)> structure,
        Func<string, string, string, bool, List<string>> preview, Action onApply,
        Action<RulePreset>? onSavePreset = null, RulePreset? presetToEdit = null)
    {
        InitializeComponent();
        _preview = preview;
        _onApply = onApply;
        _onSavePreset = onSavePreset;
        _structure = structure;

        GroupCombo.ItemsSource = groups;
        var preselect = groups.ToList().FindIndex(g => g.Name == preselectGroupName);
        GroupCombo.SelectedIndex = preselect >= 0 ? preselect : (groups.Count > 0 ? 0 : -1);

        TextChangedEventHandler onText = (_, _) => { _previewDebounce.Stop(); _previewDebounce.Start(); };
        RoutedEventHandler onCheck = (_, _) => { _previewDebounce.Stop(); _previewDebounce.Start(); };
        ModIncludeBox.TextChanged += onText;
        OutfitIncludeBox.TextChanged += onText;
        OutfitExcludeBox.TextChanged += onText;
        UnassignedCheck.Checked += onCheck;
        UnassignedCheck.Unchecked += onCheck;
        _previewDebounce.Tick += (_, _) =>
        {
            _previewDebounce.Stop();
            UpdatePreview();
        };
        // 窗口关掉后 Tick 还挂着就等于计时器继续活着（窗口对象也一起被吊住）。
        // DispatcherTimer 不是 IDisposable，Stop 掉即可。
        Closed += (_, _) => _previewDebounce.Stop();
        if (_onSavePreset is null)
            PresetButton.Visibility = Visibility.Collapsed;
        if (presetToEdit is null)
            UpdatePreview();
        else
            LoadPreset(presetToEdit); // 里面已经刷过预览
    }

    /// <summary>把一条已有预设载入各控件（「编辑所选」用）。
    /// 已打开的编辑器复用同一个窗口时也走这里，所以末尾要显式刷一次预览：
    /// 只有文本框/复选框挂了防抖，目标组与方向的下拉框变化不会自己触发重算。</summary>
    public void LoadPreset(RulePreset preset)
    {
        _editingName = preset.Name;

        var groups = GroupCombo.ItemsSource?.Cast<SliderGroup>().ToList() ?? [];
        var index = groups.FindIndex(g => g.Name == preset.GroupName);
        // 预设指向的组已被删除时落到第一个组：下拉框上看得见，不会悄悄改成别的组
        GroupCombo.SelectedIndex = index >= 0 ? index : (groups.Count > 0 ? 0 : -1);
        DirectionCombo.SelectedIndex = preset.Add ? 0 : 1;
        ModIncludeBox.Text = preset.ModInclude;
        OutfitIncludeBox.Text = preset.OutfitInclude;
        OutfitExcludeBox.Text = preset.OutfitExclude;
        UnassignedCheck.IsChecked = preset.UnassignedOnly;

        _previewDebounce.Stop(); // 上面的赋值会启动防抖，这里立刻算并停掉
        UpdatePreview();
    }

    public string GroupName => (GroupCombo.SelectedItem as SliderGroup)?.Name ?? "";
    public bool Add => DirectionCombo.SelectedIndex == 0;
    public string ModInclude => ModIncludeBox.Text;
    public string OutfitInclude => OutfitIncludeBox.Text;
    public string OutfitExclude => OutfitExcludeBox.Text;
    public bool UnassignedOnly => UnassignedCheck.IsChecked == true;

    public RulePreset CapturePreset(string name) => new()
    {
        Name = name,
        GroupName = GroupName,
        Add = Add,
        ModInclude = ModInclude,
        OutfitInclude = OutfitInclude,
        OutfitExclude = OutfitExclude,
        UnassignedOnly = UnassignedOnly,
    };

    /// <summary>重新计算命中并重建预览树；宿主在应用后调用，让预览与最新分组状态同步。</summary>
    public void UpdatePreview()
    {
        var matched = _preview(ModInclude, OutfitInclude, OutfitExclude, UnassignedOnly);
        PreviewLabel.Text = L10n.TrF("L.RuleGroup_Preview", matched.Count);
        var matchedSet = new HashSet<string>(matched, StringComparer.Ordinal);
        BuildPreviewTree(matchedSet);
    }

    private void BuildPreviewTree(HashSet<string> matchedSet)
    {
        var structure = GetStructure();
        PreviewTree.BeginInit();
        var roots = new ObservableCollection<NodeVM>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        SeparatorNodeVM? currentSepNode = null;
        string? currentSepName = null;
        var started = false;

        foreach (var (separator, owner, outfits) in structure)
        {
            var hits = outfits.Where(matchedSet.Contains).ToList();
            if (hits.Count == 0)
                continue;

            if (!started || currentSepName != separator)
            {
                currentSepName = separator;
                started = true;
                currentSepNode = separator is not null ? new SeparatorNodeVM(separator) : null;
                if (currentSepNode is not null)
                    roots.Add(currentSepNode);
            }

            var modNode = new ModNodeVM($"{owner}　({hits.Count})", owner, [], _ => false, false)
            {
                Text = $"{owner}　({hits.Count})",
                IsExpanded = true,
            };
            foreach (var outfit in hits)
            {
                modNode.Children.Add(new OutfitNodeVM(outfit, hasConflict: false, isMember: false, isChecked: false)
                {
                    Parent = modNode,
                });
                placed.Add(outfit);
            }
            modNode.MarkMaterialized(); // 子节点已手动构建，防止展开时被懒物化清空

            if (currentSepNode is not null)
            {
                modNode.Parent = currentSepNode;
                currentSepNode.Children.Add(modNode);
            }
            else
            {
                roots.Add(modNode);
            }
        }

        // 兜底：命中的服装不在左侧树结构里（如所属模组未启用）也要能看到
        var rest = matchedSet.Where(n => !placed.Contains(n)).ToList();
        if (rest.Count > 0)
        {
            var otherNode = new ModNodeVM(L10n.TrF("L.RuleGroup_NotInTree", rest.Count), "", [], _ => false, false)
            {
                Text = L10n.TrF("L.RuleGroup_NotInTree", rest.Count),
                IsExpanded = true,
            };
            foreach (var name in rest)
                otherNode.Children.Add(new OutfitNodeVM(name, hasConflict: false, isMember: false, isChecked: false)
                {
                    Parent = otherNode,
                });
            otherNode.MarkMaterialized();
            roots.Add(otherNode);
        }

        PreviewTree.ItemsSource = roots;
        PreviewTree.EndInit();
    }

    private List<(string? Separator, string Owner, List<string> Outfits)>? _structure;
    private List<(string? Separator, string Owner, List<string> Outfits)> GetStructure() => _structure ?? [];

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        _onApply?.Invoke();
        UpdatePreview();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_onSavePreset is null)
            return;
        var name = InputWindow.Show(this, L10n.Tr("L.RuleGroup_SavePresetTitle"),
            L10n.TrF("L.RuleGroup_SavePresetPrompt", GroupName.Length == 0 ? L10n.Tr("L.Word_Unselected") : GroupName),
            _editingName);
        if (string.IsNullOrEmpty(name))
            return;
        _onSavePreset.Invoke(CapturePreset(name));
    }
}
