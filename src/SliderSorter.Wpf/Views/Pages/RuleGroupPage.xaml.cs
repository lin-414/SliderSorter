using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SliderSorter.Core;
using SliderSorter.Wpf.Services;
using SliderSorter.Wpf.ViewModels;

namespace SliderSorter.Wpf.Views.Pages;

/// <summary>预设列表的一行。
/// <para>
/// 必须是具名类型，不能写成元组：元组的元素名只存在于编译期，运行时是 ValueTuple 的 Item1/Item2，
/// 而 WPF 绑定按反射解析路径 —— XAML 里的 {Binding Summary} 在元组上找不到就叫解析失败，
/// 静默给空串。列表于是显示成一片空白（行还在、但一个字都没有），看着像「预设根本没保存上」。
/// </para></summary>
public sealed record RulePresetRow(RulePreset Preset, string Name, string Summary, string Tooltip)
{
    /// <summary>把预设摊成列表行。抽成独立函数是为了让「行到底渲染成什么」能被单测直接调用。</summary>
    public static List<RulePresetRow> Build(IEnumerable<RulePreset> presets) => presets.Select(p =>
    {
        var group = p.GroupName.Length == 0 ? L10n.Tr("L.Word_Unspecified") : p.GroupName;
        var summary = L10n.TrF("L.RuleGroup_RowSummary",
                L10n.Tr(p.Add ? "L.Word_Add" : "L.Word_Remove"), group)
            + L10n.TrF("L.RuleGroup_RowKeywords",
                OrWord(p.ModInclude, "L.Word_Unlimited"),
                OrWord(p.OutfitInclude, "L.Word_All"),
                OrWord(p.OutfitExclude, "L.Word_None"));
        if (p.UnassignedOnly)
            summary += L10n.Tr("L.RuleGroup_UnassignedSuffix");
        // 两行都会截断，完整内容靠这条提示兜底
        return new RulePresetRow(p, p.Name, summary, p.Name + Environment.NewLine + summary);
    }).ToList();

    private static string OrWord(string value, string keyWhenBlank) =>
        string.IsNullOrWhiteSpace(value) ? L10n.Tr(keyWhenBlank) : value;
}

/// <summary>规则分组：一条自动归组规则的编辑、预览、存成预设与执行都在这一屏。
/// 三张卡并排（已存预设 | 规则条件 | 命中预览），底下一条操作行。
/// 它由「分组生成」页底部的抽屉承载，开关是组面板标题右侧那颗按钮（理由见本页 XAML 顶部）。</summary>
public partial class RuleGroupPage : UserControl
{
    private MainViewModel _vm = null!;
    private List<RulePresetRow> _rows = [];

    /// <summary>服装 → 所属模组名。重扫之后要作废，所以只在需要时算、切回本页时清。</summary>
    private Dictionary<string, string>? _ownerMap;

    /// <summary>「已保存」的那一份表单状态，与 Signature() 比对得出「未保存」标记。</summary>
    private string _savedSignature = "";

    /// <summary>程序性改表单（载入预设、恢复选中）期间不标脏、不重复算预览。</summary>
    private bool _loading;

    private readonly DispatcherTimer _previewDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public RuleGroupPage()
    {
        InitializeComponent();
        // VM 由壳经 DataContext 继承下来，构造期还没有；一拿到就先填一次，
        // 这样「这一页还没被切到过」时列表也是对的（壳会预先构造全部标签页的内容）。
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
                _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = (MainViewModel)DataContext;
            _vm.PropertyChanged += OnVmPropertyChanged;
            Reload(keepForm: false);
            MarkClean();
        };
        // 抽屉被摊开时：预设可能在别处被动过（新模组弹窗、设置页清档），行文本又是代码拼的
        // （不会跟着语言切换自己变），所以整列重读。但表单不能重灌 ——
        // 用户可能正改到一半，收起抽屉去搬两件衣服、回头再摊开，改动不该被抹掉。
        IsVisibleChanged += (_, _) =>
        {
            if (_vm is null)
                return;
            if (IsVisible)
                OnShown();
            else
                _previewDebounce.Stop();
        };
        Unloaded += (_, _) => _previewDebounce.Stop();
        _previewDebounce.Tick += (_, _) =>
        {
            _previewDebounce.Stop();
            UpdatePreview();
        };

        TextChangedEventHandler onType = (_, _) => OnFormEdited();
        NameBox.TextChanged += onType;
        ModIncludeBox.TextChanged += onType;
        OutfitIncludeBox.TextChanged += onType;
        OutfitExcludeBox.TextChanged += onType;
        RoutedEventHandler onPick = (_, _) => OnFormEdited();
        UnassignedCheck.Checked += onPick;
        UnassignedCheck.Unchecked += onPick;
        // 目标组与方向没挂防抖（只有文本框挂了），所以这两个当场刷一次预览：
        // 载入预设时下拉框变了、而文本框的防抖还没跑完，就会看到「预览还是上一条的条件」。
        GroupCombo.SelectionChanged += (_, _) =>
        {
            OnFormEdited();
            UpdateActions();
        };
        DirectionCombo.SelectionChanged += (_, _) =>
        {
            OnFormEdited();
            UpdateActions();
        };
    }

    /// <summary>重扫换掉树之后，「哪件服装属于哪个模组」这份缓存就过期了。它原本只在
    /// <see cref="OnShown"/> 里作废，而抽屉一直摊开时切页事件根本不会来 —— 于是命中预览与
    /// 「不在树中」的兜底都还按上一轮的归属算，看着像规则莫名其妙少了几条命中。</summary>
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.TreeRoots) || _ownerMap is null)
            return;
        _ownerMap = null;
        if (IsVisible)
            UpdatePreview(); // 命中数与预览树跟着新一轮的归属重算，别等用户动表单
    }

    private string GroupName => (GroupCombo.SelectedItem as SliderGroup)?.Name ?? "";
    private bool Add => DirectionCombo.SelectedIndex == 0;

    /// <summary>表单的可比较指纹。判「未保存」用它而不是逐个控件比：
    /// 载入预设与用户手打走的是同一批赋值，从事件上分不开。</summary>
    private string Signature() => string.Concat(
        NameBox.Text, "|", GroupName, "|", DirectionCombo.SelectedIndex, "|",
        ModIncludeBox.Text, "|", OutfitIncludeBox.Text, "|", OutfitExcludeBox.Text, "|",
        UnassignedCheck.IsChecked == true);

    private void MarkClean()
    {
        _savedSignature = Signature();
        DirtyMark.Visibility = Visibility.Collapsed;
    }

    private void OnFormEdited()
    {
        if (_loading)
            return;
        DirtyMark.Visibility = Signature() == _savedSignature ? Visibility.Collapsed : Visibility.Visible;
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    // ── 列表与表单的同步 ──

    private RulePresetRow? SelectedRow() =>
        PresetList.SelectedIndex >= 0 && PresetList.SelectedIndex < _rows.Count
            ? _rows[PresetList.SelectedIndex] : null;

    private void OnShown()
    {
        _ownerMap = null; // 可能已经重扫过
        // 表单改到一半时不去动它；干净的话目标组跟着组列表的当前选中走 ——
        // 这一屏就开在那个列表下面，用户刚点着哪个组，要填的就是它。
        var midEdit = Signature() != _savedSignature;
        Reload(keepForm: true);
        if (!midEdit && GroupName != (_vm.Store.Current?.Name ?? ""))
        {
            _loading = true;
            SelectGroup(_vm.Store.Current?.Name);
            _loading = false;
            MarkClean();
        }
        UpdatePreview();
        UpdateActions();
    }

    /// <summary>重读预设列表。
    /// <paramref name="keepForm"/> = true 时只把选中行恢复到原来那条，不把它灌回表单 ——
    /// 切回标签页属于"什么都没改地回来"，此时重灌会抹掉用户没保存的编辑。</summary>
    private void Reload(bool keepForm)
    {
        var keepName = SelectedRow()?.Preset.Name;
        var keepGroup = GroupName;

        _loading = true;
        GroupCombo.ItemsSource = _vm.Store.Groups;
        SelectGroup(keepGroup); // ItemsSource 一换选中就没了，先按名字捞回来
        _rows = RulePresetRow.Build(_vm.Settings.RulePresets);
        PresetList.ItemsSource = _rows;
        PresetList.SelectedIndex = keepName is null ? -1 : _rows.FindIndex(r => r.Preset.Name == keepName);
        _loading = false;

        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PresetCountLabel.Text = L10n.TrF("L.RuleGroup_PresetCount", _rows.Count);
        if (!keepForm && SelectedRow() is { } row)
            LoadRowIntoForm(row);
        UpdateActions();
    }

    /// <summary>把目标组下拉框指到某个组；组不存在（已删）时保持现状，不悄悄改成别的组。</summary>
    private void SelectGroup(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return;
        var groups = GroupCombo.ItemsSource?.Cast<SliderGroup>().ToList() ?? [];
        var index = groups.FindIndex(g => g.Name == name);
        if (index >= 0)
            GroupCombo.SelectedIndex = index;
    }

    private void LoadRowIntoForm(RulePresetRow row)
    {
        var p = row.Preset;
        _loading = true;
        NameBox.Text = p.Name;
        SelectGroup(p.GroupName);
        DirectionCombo.SelectedIndex = p.Add ? 0 : 1;
        ModIncludeBox.Text = p.ModInclude;
        OutfitIncludeBox.Text = p.OutfitInclude;
        OutfitExcludeBox.Text = p.OutfitExclude;
        UnassignedCheck.IsChecked = p.UnassignedOnly;
        _loading = false;
        MarkClean();
        _previewDebounce.Stop();
        UpdatePreview();
    }

    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && SelectedRow() is { } row)
            LoadRowIntoForm(row);
        UpdateActions();
    }

    /// <summary>双击一行 = 把它挑出来接着改。合并之前双击是「打开另一个窗口」，
    /// 现在那一屏就在右边，所以双击只做「选中并载入」，不再另起界面。</summary>
    private void PresetList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedRow() is { } row)
            LoadRowIntoForm(row);
    }

    // ── 预览 ──

    /// <summary>重新计算命中并重建预览树。</summary>
    private void UpdatePreview()
    {
        if (_vm is null)
            return;
        _ownerMap ??= _vm.OwnerByOutfit();
        var matched = _vm.RuleMatchPreview(_ownerMap, ModIncludeBox.Text, OutfitIncludeBox.Text,
            OutfitExcludeBox.Text, UnassignedCheck.IsChecked == true);
        PreviewCountLabel.Text = L10n.TrF("L.RuleGroup_HitCount", matched.Count);
        NoHitsHint.Visibility = matched.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildPreviewTree(new HashSet<string>(matched, StringComparer.Ordinal));
    }

    private void BuildPreviewTree(HashSet<string> matchedSet)
    {
        var roots = new ObservableCollection<NodeVM>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        SeparatorNodeVM? currentSepNode = null;
        string? currentSepName = null;
        var started = false;

        foreach (var (separator, owner, outfits) in _vm.GetTreeDisplayStructure())
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

        // 兜底：命中的服装不在模组树结构里（如所属模组未启用）也要能看到
        var rest = matchedSet.Where(n => !placed.Contains(n)).ToList();
        if (rest.Count > 0)
        {
            var otherNode = new ModNodeVM(L10n.TrF("L.RuleGroup_OutsideTree", rest.Count), "", [], _ => false, false)
            {
                Text = L10n.TrF("L.RuleGroup_OutsideTree", rest.Count),
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
    }

    // ── 动作 ──

    /// <summary>按钮的可点性与「为什么不能点」。这一页的三个执行动作各有各的前置
    /// （有没有组 / 扫没扫到服装 / 列表里有没有东西），而它们都不在页面上自明，
    /// 所以禁用时必须把原因挂在提示上 —— 光灰掉等于让人猜。</summary>
    private void UpdateActions()
    {
        if (_vm is null)
            return;
        var scanned = _vm.Scan?.Outfits.Count > 0;
        var hasGroup = GroupCombo.SelectedItem is not null;

        ApplyButton.IsEnabled = scanned && hasGroup;
        ApplyButton.ToolTip = !scanned
            ? L10n.Tr("L.RuleGroup_NoScanTip")
            : !hasGroup
                ? L10n.Tr("L.RuleGroup_NoGroupTip")
                : L10n.Tr("L.RuleGroup_ApplyTip");

        ApplyAllButton.IsEnabled = scanned && _rows.Count > 0;
        ApplyAllButton.ToolTip = !scanned
            ? L10n.Tr("L.RuleGroup_NoScanTip")
            : _rows.Count == 0
                ? L10n.Tr("L.RuleGroup_NoPresetsTip")
                : L10n.Tr("L.RuleGroup_ApplyAllTip");

        DeleteButton.IsEnabled = SelectedRow() is not null;
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        PresetList.SelectedIndex = -1;
        NameBox.Text = "";
        ModIncludeBox.Text = "";
        OutfitIncludeBox.Text = "";
        OutfitExcludeBox.Text = "";
        UnassignedCheck.IsChecked = true;
        DirectionCombo.SelectedIndex = 0;
        // 目标组留着不动：从某个组的菜单上进来时，用户要的正是「再给这个组加一条规则」
        _loading = false;
        MarkClean();
        UpdatePreview();
        UpdateActions();
        NameBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            _vm.ShowBanner(BannerKind.Info, L10n.Tr("L.RuleGroup_NameRequired"));
            NameBox.Focus();
            return;
        }
        _vm.SaveRulePreset(new RulePreset
        {
            Name = name,
            GroupName = GroupName,
            Add = Add,
            ModInclude = ModIncludeBox.Text,
            OutfitInclude = OutfitIncludeBox.Text,
            OutfitExclude = OutfitExcludeBox.Text,
            UnassignedOnly = UnassignedCheck.IsChecked == true,
        });
        // 存完把列表选中这一条：否则左列还停在旧行上，看着像存了另一条
        Reload(keepForm: true);
        var index = _rows.FindIndex(r => string.Equals(r.Preset.Name, name, StringComparison.Ordinal));
        if (index >= 0)
        {
            _loading = true;
            PresetList.SelectedIndex = index;
            _loading = false;
            EmptyHint.Visibility = Visibility.Collapsed;
            PresetCountLabel.Text = L10n.TrF("L.RuleGroup_PresetCount", _rows.Count);
        }
        MarkClean();
        UpdateActions();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow() is not { } row)
        {
            _vm.ShowBanner(BannerKind.Info, L10n.Tr("L.RuleGroup_SelectToDelete"));
            return;
        }
        // 删了就立刻落盘：页面没有「关窗口时统一保存」那个时刻，
        // 等关窗的代价是切去别页时列表与设置不一致。
        _vm.Settings.RulePresets.Remove(row.Preset);
        _vm.Settings.Save();
        _vm.Log(L10n.TrF("L.Log_PresetsUpdated", _vm.Settings.RulePresets.Count));
        // 表单保持原样（用户可能只是想换个名字重存），只把「未保存」如实重算一次
        Reload(keepForm: true);
        if (Signature() != _savedSignature)
            DirtyMark.Visibility = Visibility.Visible;
        UpdateActions();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (GroupName.Length == 0)
        {
            _vm.ShowBanner(BannerKind.Info, L10n.Tr("L.RuleGroup_NoGroupTip"));
            return;
        }
        var applied = _vm.RuleApply(GroupName, Add, ModIncludeBox.Text, OutfitIncludeBox.Text,
            OutfitExcludeBox.Text, UnassignedCheck.IsChecked == true);
        if (applied < 0)
            return;
        _vm.RefreshGroupsList();
        _vm.RefreshTree();
        UpdatePreview();
    }

    private void ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            _vm.ShowBanner(BannerKind.Info, L10n.Tr("L.RuleGroup_NoPresetsTip"));
            return;
        }
        _vm.ApplyRulePresets(); // 内部已刷新组列表与树
        UpdatePreview();
    }
}
