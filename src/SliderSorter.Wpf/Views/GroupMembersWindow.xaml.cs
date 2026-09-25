using System.Collections.ObjectModel;
using SliderSorter.Wpf.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SliderSorter.Core;
using SliderSorter.Wpf.ViewModels;

namespace SliderSorter.Wpf.Views;

/// <summary>预览某个组内的全部服装：按 分隔符 → 模组 分层显示，可过滤、勾选批量移出或移动到别的组。</summary>
public partial class GroupMembersWindow : Window
{
    private readonly SliderGroup _group;
    private readonly GroupStore _store;
    private readonly List<(string? Separator, string Owner, List<string> Outfits)> _structure;
    private readonly Action _beforeChange;
    private readonly Action _onChanged;

    public GroupMembersWindow(SliderGroup group, GroupStore store,
        List<(string? Separator, string Owner, List<string> Outfits)> structure,
        Action beforeChange, Action onChanged)
    {
        InitializeComponent();
        _group = group;
        _store = store;
        _structure = structure;
        _beforeChange = beforeChange;
        _onChanged = onChanged;
        Title = L10n.TrF("L.Members_Title", group.Name);
        FilterBox.TextChanged += (_, _) => Rebuild();
        ModFilterBox.TextChanged += (_, _) => Rebuild();
        // 空格勾选 / 左右展开折叠 / 上下移动行（见 TreeKeyboard）
        TreeKeyboard.Attach(Tree);
        Rebuild();
    }

    private void Rebuild()
    {
        var filter = FilterBox.Text.Trim();
        var modFilter = ModFilterBox.Text.Trim();
        Tree.BeginInit();
        var roots = new ObservableCollection<NodeVM>();
        var shown = 0;
        // 成员判等用 HashSet 一次建好：原先 List.Contains 嵌在按结构的遍历里，是
        // O(成员数×服装数)——千级成员 × 千级结构时，打开窗口、每次键入过滤词、每次移除
        // 后的重建都是数百万次比较
        var memberSet = new HashSet<string>(_group.Members, StringComparer.Ordinal);

        // 连续相同名称的分隔符共用同一个节点（与主界面一致）
        SeparatorNodeVM? currentSepNode = null;
        string? currentSepName = null;
        var started = false;

        foreach (var (separator, owner, outfits) in _structure)
        {
            var memberOutfits = outfits
                .Where(memberSet.Contains)
                .ToList();
            if (memberOutfits.Count == 0)
                continue;

            var visibleOutfits = filter.Length == 0
                ? memberOutfits
                : memberOutfits.Where(o => o.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            var modPass = modFilter.Length == 0
                          || owner.Contains(modFilter, StringComparison.OrdinalIgnoreCase)
                          || (separator is not null &&
                              separator.Contains(modFilter, StringComparison.OrdinalIgnoreCase));
            if (!modPass)
                visibleOutfits = visibleOutfits
                    .Where(o => o.Contains(modFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            if (visibleOutfits.Count == 0)
                continue;

            if (!started || currentSepName != separator)
            {
                currentSepName = separator;
                started = true;
                currentSepNode = separator is not null ? new SeparatorNodeVM(separator) : null;
                if (currentSepNode is not null)
                    roots.Add(currentSepNode);
            }

            var modNode = new ModNodeVM($"{owner}　({visibleOutfits.Count})", owner, [], _ => false, false)
            {
                Text = $"{owner}　({visibleOutfits.Count})",
            };
            modNode.Children.Clear(); // 移除懒加载占位子节点
            foreach (var outfit in visibleOutfits)
            {
                modNode.Children.Add(new OutfitNodeVM(outfit, hasConflict: false, isMember: false, isChecked: false)
                {
                    Parent = modNode,
                });
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

            if (filter.Length > 0 || modFilter.Length > 0)
                modNode.IsExpanded = true;
            shown += visibleOutfits.Count;
        }

        Tree.ItemsSource = roots;
        Tree.EndInit();
        CountLabel.Text = L10n.TrF("L.Members_Count", _group.Members.Count, shown);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = CollectSelected();

        if (selected.Count == 0)
        {
            Notify.Info(this, L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_SelectOutfitsFirst"));
            return;
        }
        if (!Notify.Confirm(this, L10n.Tr("L.Title_Confirm"),
                L10n.TrF("L.Msg_ConfirmRemove", selected.Count, _group.Name), destructive: true))
            return;

        _beforeChange();
        var removing = new HashSet<string>(selected, StringComparer.Ordinal);
        _group.Members.RemoveAll(removing.Contains); // 一趟清完，别逐个名字扫整张成员表
        _onChanged();
        Rebuild();
    }

    // ── 移动到别的组 ──────────────────────────────────────────────────
    /// <summary>最近一次弹出的目标菜单。菜单是点击时临时 new 的、没挂在任何属性上，
    /// 留这一份引用只是为了接线测试能摸到它（运行时无人再读）。</summary>
    private ContextMenu? _moveMenu;

    /// <summary>「移动到…」：先收勾选，再把其他组列成菜单让用户点目标。菜单每次点击时重建
    /// 而不是窗口打开时备好一份——目标清单来自 Store 的活引用，按钮是唯一入口，重建的成本忽略不计。</summary>
    private void MoveTo_Click(object sender, RoutedEventArgs e)
    {
        var selected = CollectSelected();
        if (selected.Count == 0)
        {
            Notify.Info(this, L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_SelectOutfitsFirst"));
            return;
        }

        var menu = BuildMoveMenu(selected);
        menu.PlacementTarget = MoveButton;
        menu.Placement = PlacementMode.Top; // 按钮贴着窗口底边，朝下开会被裁掉
        menu.IsOpen = true;
        _moveMenu = menu;
    }

    /// <summary>目标菜单：一项一个其他组，按 Store 里的顺序（与主界面组列表一致）。
    /// 组名是数据不是资源键：长组名在限宽菜单里会被无声裁掉，整名进 ToolTip
    /// ——与「按模组指定」菜单同一套做法（见 OutputConflictPage.OwnerItem）。</summary>
    private ContextMenu BuildMoveMenu(List<string> selected)
    {
        var menu = new ContextMenu { MaxHeight = 420, MaxWidth = 560 };
        // 只排除当前组本身（按引用）：按名字比会把改过大小写的自己漏进目标里
        var targets = _store.Groups.Where(g => !ReferenceEquals(g, _group)).ToList();
        if (targets.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = L10n.Tr("L.Members_MoveNoTarget"), IsEnabled = false });
            return menu;
        }
        foreach (var target in targets)
        {
            var group = target;
            var item = new MenuItem
            {
                Header = new TextBlock
                {
                    Text = group.Name,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = group.Name,
                },
            };
            item.Click += (_, _) => ConfirmAndMove(group, selected);
            menu.Items.Add(item);
        }
        return menu;
    }

    private void ConfirmAndMove(SliderGroup target, List<string> outfits)
    {
        if (!Notify.Confirm(this, L10n.Tr("L.Title_Confirm"),
                L10n.TrF("L.Msg_ConfirmMove", outfits.Count, _group.Name, target.Name)))
            return;
        // 移动语义（目标补上 + 本组移除）在 Store.MoveMembers 里：一份快照包住两个改动，
        // 撤销一步到位，不会落成"目标加了、本组还在"的半截状态。
        // 返回 -1 = 组找不到（两个组名都来自同一 Store，正常流程到不了）：兜住它，
        // 别在异常情形下把"什么都没发生"再刷新给用户看
        if (_store.MoveMembers(_group.Name, target.Name, outfits) < 0)
            return;
        _onChanged();
        Rebuild();
    }

    /// <summary>勾选的服装名去重收拢。移出与移动共用同一份勾选语义（<see cref="Walk"/> 全树扫）。</summary>
    private List<string> CollectSelected() =>
        Walk(Tree.ItemsSource!.Cast<NodeVM>())
            .Where(n => n.IsChecked == true && n is OutfitNodeVM)
            .Select(n => ((OutfitNodeVM)n).OutfitName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

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
