using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using SliderSorter.Core;
using SliderSorter.Wpf.ViewModels;

using SliderSorter.Wpf.Services;

namespace SliderSorter.Wpf.Views;

/// <summary>
/// 新装模组归组弹窗：三层勾选树（分隔符→模组→服装），支持分多批换目标组归组；
/// 已应用的项即时移除，全部处理完自动关闭。
///
/// 顶部两个过滤框与主界面、「查看组」同一套语义：左框只匹配服装名，右框匹配模组名与分隔符名
/// （命中时显示该模组全部服装，都不命中时退化为按服装名匹配）。
/// 勾选状态记在 <see cref="_checked"/> 上而不是只存在节点里：过滤会整棵树重建，状态若只跟着
/// 节点走，「先勾几个、再过滤找剩下的」就会把已勾的悄悄清空。
/// </summary>
public partial class NewModsWindow : Window
{
    /// <summary>本次待归组的一条模组记录。服装表随归组进度就地删减（见 <see cref="RemoveApplied"/>）。</summary>
    private sealed record Entry(string? Separator, string Owner, List<string> Outfits);

    /// <summary>勾选状态的键。同名服装可能出现在不同模组下，所以模组与分隔符都要进键。</summary>
    private readonly record struct OutfitKey(string? Separator, string Owner, string Outfit);

    private readonly NewModsRequest _request;
    private readonly int _modCount;
    private readonly int _totalCount;
    private readonly List<Entry> _entries;
    private readonly Dictionary<OutfitKey, bool> _checked = [];

    public NewModsWindow(NewModsRequest request)
    {
        InitializeComponent();
        _request = request;
        // 复制一份服装表：归组时本窗口就地删减，不该改动调用方（主 VM）的结构快照
        _entries = request.Entries.Select(e => new Entry(e.Separator, e.Owner, [.. e.Outfits])).ToList();
        _modCount = _entries.Count;
        _totalCount = _entries.Sum(e => e.Outfits.Count);

        GroupCombo.ItemsSource = request.Groups;
        var preselect = request.Groups.ToList().FindIndex(g => g.Name == request.PreselectGroup);
        GroupCombo.SelectedIndex = preselect >= 0 ? preselect : (request.Groups.Count > 0 ? 0 : -1);

        if (request.PresetNames.Count > 0)
        {
            PresetsButton.Content = L10n.TrF("L.NewMods_PresetsBtn", request.PresetNames.Count);
            ToolTipService.SetToolTip(PresetsButton,
                L10n.Tr("L.NewMods_PresetsTip"));
        }
        else
        {
            PresetsButton.Visibility = Visibility.Collapsed;
        }

        FilterBox.TextChanged += (_, _) => Rebuild();
        ModFilterBox.TextChanged += (_, _) => Rebuild();

        // 空格勾选 / 左右展开折叠 / 上下移动行（见 TreeKeyboard）
        TreeKeyboard.Attach(Tree);

        Rebuild();
    }

    /// <summary>尚未归组的服装总数（不受过滤影响）。</summary>
    private int RemainingTotal => _entries.Sum(e => e.Outfits.Count);

    private string TopText()
    {
        var remaining = RemainingTotal;
        return remaining < _totalCount
            ? L10n.TrF("L.NewMods_TopRemaining", remaining, _modCount, _totalCount) +
              "\n" + L10n.Tr("L.NewMods_TopRemainingHint")
            : L10n.TrF("L.NewMods_TopFirst", _modCount, remaining) +
              "\n" + L10n.Tr("L.NewMods_TopFirstHint");
    }

    /// <summary>按当前过滤条件重建可见树。勾选状态从 <see cref="_checked"/> 还原，与节点对象解耦。</summary>
    private void Rebuild()
    {
        var filter = FilterBox.Text.Trim();
        var modFilter = ModFilterBox.Text.Trim();
        var filtering = filter.Length > 0 || modFilter.Length > 0;

        var roots = new ObservableCollection<NodeVM>();
        var shown = 0;

        // 连续相同名称的分隔符共用同一个节点（与主界面一致）
        SeparatorNodeVM? currentSepNode = null;
        string? currentSepName = null;
        var started = false;

        foreach (var entry in _entries)
        {
            var visibleOutfits = VisibleOutfits(entry, filter, modFilter);
            if (visibleOutfits.Count == 0)
                continue;

            if (!started || currentSepName != entry.Separator)
            {
                currentSepName = entry.Separator;
                started = true;
                currentSepNode = string.IsNullOrEmpty(entry.Separator) ? null : new SeparatorNodeVM(entry.Separator!);
                if (currentSepNode is not null)
                {
                    // 过滤生效时展开分隔符：命中内容必须立即可见，否则用户看到的是一个折叠的
                    // 分组标题，得再点一下才知道里面有没有他要找的东西
                    currentSepNode.IsExpanded = filtering;
                    roots.Add(currentSepNode);
                }
            }

            // 过滤掉了一部分服装时标明「匹配 x/总数」——否则勾上这个模组会少选中几个，看着像丢了
            var header = visibleOutfits.Count < entry.Outfits.Count
                ? L10n.TrF("L.Tree_MatchHeader", entry.Owner, visibleOutfits.Count, entry.Outfits.Count)
                : $"{entry.Owner}　({entry.Outfits.Count})";

            var modNode = new ModNodeVM(header, entry.Owner, [], _ => false, false)
            {
                Text = header,
            };
            modNode.Children.Clear(); // 移除懒物化占位子节点
            foreach (var outfit in visibleOutfits)
            {
                var outfitNode = NewOutfitNode(entry, outfit);
                // Parent 必须接上：摘要里的「来自 N 个模组」与勾选向上聚合都沿 Parent 链走，
                // 漏接就变成"0 个模组"、且勾单个服装不会点亮所在模组
                outfitNode.Parent = modNode;
                modNode.Children.Add(outfitNode);
            }
            modNode.MarkMaterialized(); // 子节点已手动构建，防止展开时被懒物化清空
            modNode.IsExpanded = true;

            if (currentSepNode is not null)
            {
                modNode.Parent = currentSepNode;
                currentSepNode.Children.Add(modNode);
            }
            else
            {
                roots.Add(modNode);
            }

            shown += visibleOutfits.Count;
        }

        if (roots.Count == 0)
            roots.Add(new NodeVM(NodeKind.Outfit) { Text = L10n.Tr("L.Tree_NoMatchingMods"), IsPlaceholder = true });

        Tree.ItemsSource = roots;
        RefreshAggregates(roots);
        // 勾选变化要立刻反映到摘要那一句：容器的回调只挂在根上（见 NodeVM.CheckedChanged 的接线说明），
        // 挂上之前只有 Rebuild 末尾那一次 UpdateSummary，于是勾完一行读数还停在初始值，
        // 要等用户动过滤框或点「添加所选」才变——按钮上写的数和摘要说的数能对不上。
        foreach (var root in roots)
            root.CheckedChanged = UpdateSummary;

        TopLabel.Text = TopText();
        FilterCountLabel.Text = filtering ? L10n.TrF("L.NewMods_FilterCount", shown, RemainingTotal) : "";
        FilterCountLabel.Visibility = filtering ? Visibility.Visible : Visibility.Collapsed;
        UpdateSummary();
    }

    /// <summary>与「查看组」同一套过滤语义：服装框只匹配服装名；模组框先匹配模组名 / 分隔符名
    /// （命中即显示其下全部服装），都不命中时退化为按服装名匹配——一个框同时能搜到两类名字。</summary>
    private static List<string> VisibleOutfits(Entry entry, string filter, string modFilter)
    {
        var result = entry.Outfits.Where(o => TextFilter.Matches(o, filter)).ToList();
        var modPass = TextFilter.Matches(entry.Owner, modFilter) ||
                      (entry.Separator is not null && TextFilter.Matches(entry.Separator, modFilter));
        return modPass
            ? result
            : result.Where(o => TextFilter.Matches(o, modFilter)).ToList();
    }

    private OutfitNodeVM NewOutfitNode(Entry entry, string outfit)
    {
        var key = new OutfitKey(entry.Separator, entry.Owner, outfit);
        // 首次见到（还没被用户动过）默认勾选：新装模组的默认意图就是全选归组
        var node = new OutfitNodeVM(outfit, hasConflict: false, isMember: false,
            isChecked: !_checked.TryGetValue(key, out var isChecked) || isChecked);
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NodeVM.IsChecked))
                _checked[key] = node.IsChecked == true;
        };
        return node;
    }

    /// <summary>手动构建/剪枝不会触发聚合，自底向上重算所有容器的勾选态。</summary>
    private static void RefreshAggregates(IEnumerable<NodeVM> roots)
    {
        foreach (var node in Walk(roots).Where(n => n is ModNodeVM or SeparatorNodeVM).Reverse())
            node.RefreshAggregated();
    }

    private List<string> SelectedOutfits =>
        Walk(Tree.ItemsSource!.Cast<NodeVM>())
            .Where(n => n.IsChecked == true && n is OutfitNodeVM)
            .Select(n => ((OutfitNodeVM)n).OutfitName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private void UpdateSummary()
    {
        var selected = SelectedOutfits;
        SummaryLabel.Text = selected.Count == 0
            ? L10n.Tr("L.NewMods_NoneSelected")
            : L10n.TrF("L.NewMods_Summary", selected.Count, CountOwners(selected));
    }

    private int CountOwners(List<string> outfits)
    {
        // 批次口径是"当前可见且勾选"（见 NewModsFilterTests 第 ⑦ 步的提交契约），
        // 这里只负责把查表从 List.Contains 的 O(选中×节点) 换成 HashSet
        var set = new HashSet<string>(outfits, StringComparer.Ordinal);
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in Walk(Tree.ItemsSource!.Cast<NodeVM>()))
        {
            if (node is OutfitNodeVM outfit && set.Contains(outfit.OutfitName) && node.Parent is ModNodeVM parent)
                owners.Add(parent.Owner);
        }
        return owners.Count;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedOutfits;
        if (selected.Count == 0)
        {
            Notify.Info(this, L10n.Tr("L.Title_Tip"), L10n.Tr("L.NewMods_SelectFirst"));
            return;
        }
        if (GroupCombo.SelectedItem is not SliderGroup group)
        {
            Notify.Info(this, L10n.Tr("L.Title_Tip"), L10n.Tr("L.NewMods_SelectTarget"));
            return;
        }
        _request.ApplyBatch(group.Name, selected);
        RemoveApplied(selected);
    }

    private void Presets_Click(object sender, RoutedEventArgs e)
    {
        var newlyAssigned = _request.ApplyPresets();
        if (newlyAssigned.Count > 0)
            RemoveApplied(newlyAssigned);
    }

    /// <summary>把已归组的服装从待办里剔除（空模组一并丢弃）后重建；全部处理完自动关闭。
    /// 过滤只影响显示，这里的计数走完整清单——被过滤词挡住的服装不该让窗口提前关闭。</summary>
    private void RemoveApplied(IEnumerable<string> outfits)
    {
        var applied = new HashSet<string>(outfits, StringComparer.Ordinal);
        foreach (var entry in _entries)
            entry.Outfits.RemoveAll(applied.Contains);
        _entries.RemoveAll(e => e.Outfits.Count == 0);
        foreach (var key in _checked.Keys.Where(k => applied.Contains(k.Outfit)).ToList())
            _checked.Remove(key);

        Rebuild();
        if (RemainingTotal == 0)
            Close();
    }

    private static IEnumerable<NodeVM> Walk(IEnumerable<NodeVM> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Walk(node.Children))
                yield return child;
        }
    }
}
