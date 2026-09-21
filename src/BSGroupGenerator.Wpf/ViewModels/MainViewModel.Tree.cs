using System.Collections.ObjectModel;
using System.IO;
using System.Collections.Specialized;
using BSGroupGenerator.Core;
using BSGroupGenerator.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BSGroupGenerator.Wpf.ViewModels;

public partial class MainViewModel
{
    /// <summary>重建树（RefreshTree 别名，与 WinForms 命名对齐）。</summary>
    public void RefreshTree() => RebuildTree();

    [ObservableProperty] private ObservableCollection<NodeVM> _treeRoots = [];
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _modFilterText = "";
    [ObservableProperty] private bool _unassignedOnly;

    private CancellationTokenSource? _filterDebounce;

    partial void OnFilterTextChanged(string value) => DebounceRebuild();
    partial void OnModFilterTextChanged(string value) => DebounceRebuild();
    partial void OnUnassignedOnlyChanged(bool value) => RebuildTree();

    private void DebounceRebuild()
    {
        // Cancel 之后旧 CTS 已无用（每次输入都会新建一个）：不 Dispose 就是把内核等待句柄
        // 攒到下一次 GC——频繁敲过滤词时这些句柄会成堆滞留
        _filterDebounce?.Cancel();
        _filterDebounce?.Dispose();
        var cts = _filterDebounce = new CancellationTokenSource();
        var scheduler = TaskScheduler.FromCurrentSynchronizationContext();
        _ = Task.Delay(350, cts.Token).ContinueWith(_ =>
        {
            if (!cts.IsCancellationRequested)
                RebuildTree();
        }, cts.Token, TaskContinuationOptions.OnlyOnRanToCompletion, scheduler);
    }

    /// <summary>窗口关闭时收尾防抖计时（ViewModel 不会再收到输入变更）。</summary>
    private void DisposeDebounce()
    {
        _filterDebounce?.Cancel();
        _filterDebounce?.Dispose();
        _filterDebounce = null;
    }

    private bool OutfitVisible(OutfitEntry outfit)
    {
        if (UnassignedOnly && IsInAnyGroup(outfit.Name))
            return false;
        return TextFilter.Matches(outfit.Name, FilterText);
    }

    private bool ModVisible(string modName, int outfitTotal, int visibleCount, string outfitFilter)
    {
        if (outfitTotal == 0)
            return false;
        if (ModFilterText.Trim().Length > 0 &&
            !modName.Contains(ModFilterText.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        // 服装关键字过滤或"仅看未分配"下，没有可见内容的模组一并隐藏
        if ((outfitFilter.Length > 0 || UnassignedOnly) && visibleCount == 0)
            return false;
        return true;
    }

    public void RebuildTree()
    {
        if (_closed)
            return;

        // 记录展开状态，重建后恢复（键的算法见 TreeExpandState）
        var expanded = TreeExpandState.Capture(TreeRoots);

        var filter = FilterText.Trim();
        var modFilter = ModFilterText.Trim();
        var newRoots = new ObservableCollection<NodeVM>();
        if (Scan is not null)
        {
            var outfitsByOwner = new Dictionary<string, List<OutfitEntry>>();
            foreach (var outfit in Scan.Outfits)
            {
                if (!outfitsByOwner.TryGetValue(outfit.OwnerLabel, out var list))
                    outfitsByOwner[outfit.OwnerLabel] = list = [];
                list.Add(outfit);
            }

            if (IsVirtualScan)
                BuildStructuredTree(newRoots, outfitsByOwner, filter);
            else
                BuildFlatTree(newRoots, outfitsByOwner, filter);

            if (newRoots.Count == 0)
                newRoots.Add(new NodeVM(NodeKind.Outfit) { Text = L10n.Tr("L.Tree_NoMatchingMods"), IsPlaceholder = true });
        }

        TreeRoots = newRoots;
        // 只给根节点挂回调与根集合：子节点经 Parent 链向上取，懒物化的服装行不必逐个接线
        foreach (var root in newRoots)
        {
            root.CheckedChanged = RefreshTransferState;
            root.Roots = newRoots;
        }
        TreeExpandState.Restore(newRoots, expanded);

        UpdateCounts();
        UpdateTitle();
        RefreshTransferState();
        OnPropertyChanged(nameof(ShowTreeEmptyState));
    }

    /// <summary>树完全为空（还没扫描）时给一句空状态文案。
    /// 扫描完成但被过滤空的情况由树内的占位节点负责，两者不会同时出现。</summary>
    public bool ShowTreeEmptyState => Scan is null;

    private IEnumerable<NodeVM> WalkRoots()
    {
        foreach (var root in TreeRoots)
            foreach (var n in root.WalkSelfAndDescendants())
                yield return n;
    }

    private void BuildFlatTree(ObservableCollection<NodeVM> roots,
        Dictionary<string, List<OutfitEntry>> outfitsByOwner, string filter)
    {
        foreach (var (owner, outfits) in outfitsByOwner)
        {
            var visibleOutfits = outfits.Where(OutfitVisible).ToList();
            if (!ModVisible(owner, outfits.Count, visibleOutfits.Count, filter))
                continue;
            roots.Add(BuildOutfitModNode(roots, owner, outfits, visibleOutfits, filter));
        }
    }

    private void BuildStructuredTree(ObservableCollection<NodeVM> roots,
        Dictionary<string, List<OutfitEntry>> outfitsByOwner, string filter)
    {
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        SeparatorNodeVM? separator = null;

        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            var entry = Entries[i];
            if (entry.IsForeign)
                continue;

            if (entry.IsSeparator)
            {
                separator = new SeparatorNodeVM(SeparatorDisplayName(entry.Name));
                roots.Add(separator);
                continue;
            }

            var onDisk = SelectedInstance is not null &&
                         ModDirExists(Path.Combine(SelectedInstance.ModsDirectory, entry.Name));
            outfitsByOwner.TryGetValue(entry.Name, out var outfits);
            outfits ??= [];
            if (outfits.Count > 0)
                consumed.Add(entry.Name);

            // 未启用或目录缺失的模组不可能被 BodySlide 加载，跳过
            if (!entry.Enabled || !onDisk)
                continue;

            var visibleOutfits = outfits.Where(OutfitVisible).ToList();
            if (!ModVisible(entry.Name, outfits.Count, visibleOutfits.Count, filter))
                continue;
            var node = BuildOutfitModNode(roots, entry.Name, outfits, visibleOutfits, filter);
            if (separator is not null)
            {
                node.Parent = separator;
                separator.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        // 没有归属到任何启用模组的服装（如游戏真实 Data 本体的文件），放在最后
        foreach (var (owner, outfits) in outfitsByOwner)
        {
            if (consumed.Contains(owner))
                continue;
            var visibleOutfits = outfits.Where(OutfitVisible).ToList();
            if ((UnassignedOnly || filter.Length > 0) && visibleOutfits.Count == 0)
                continue;
            if (!ModVisible(owner, outfits.Count, visibleOutfits.Count, filter))
                continue;
            roots.Add(BuildOutfitModNode(roots, owner, outfits, visibleOutfits, filter));
        }

        // 清理没有可见内容的分隔符节点
        for (var i = roots.Count - 1; i >= 0; i--)
        {
            if (roots[i] is SeparatorNodeVM && roots[i].Children.Count == 0)
                roots.RemoveAt(i);
        }
    }

    private ModNodeVM BuildOutfitModNode(ObservableCollection<NodeVM> roots, string owner,
        List<OutfitEntry> outfits, List<OutfitEntry> visibleOutfits, string filter)
    {
        // 过滤/仅看未分配时标明是"该模组有几个服装可见"
        var narrowed = filter.Length > 0 || UnassignedOnly;
        var header = narrowed && visibleOutfits.Count < outfits.Count
            ? L10n.TrF("L.Tree_MatchHeader", owner, visibleOutfits.Count, outfits.Count)
            : $"{owner}　({outfits.Count})";

        var group = Store.Current;
        var inGroup = visibleOutfits.Count(o => group is not null && group.Members.Contains(o.Name, StringComparer.Ordinal));

        var node = new ModNodeVM(header, owner, visibleOutfits, IsInAnyGroup, false);
        if (inGroup > 0)
        {
            node.Text = L10n.TrF("L.Tree_InGroupBadge", header, inGroup, visibleOutfits.Count);
            node.IsMember = inGroup == visibleOutfits.Count && visibleOutfits.Count > 0;
        }

        node.Parent = null;
        if (filter.Length > 0)
        {
            // 过滤：命中内容必须立即可见
            node.IsExpanded = true; // 触发物化
        }
        return node;
    }

    /// <summary>成员标注就地更新（绿色 ✔ / [组内 x/y]），不重建树——保留展开与滚动位置。</summary>
    private void UpdateMembershipMarks()
    {
        var group = Store.Current;
        foreach (var node in WalkRoots())
        {
            switch (node)
            {
                case OutfitNodeVM outfit:
                {
                    var member = group is not null && group.Members.Contains(outfit.OutfitName, StringComparer.Ordinal);
                    // 成员标记由视图的独立徽标呈现（绑定 IsMember），文本里不再拼 "✔ "
                    var targetText = OutfitNodeVM.FormatText(outfit.OutfitName, outfit.HasConflict,
                        outfit.HasOutputConflict);
                    if (node.Text != targetText)
                        node.Text = targetText;
                    node.IsMember = member;
                    break;
                }
                case ModNodeVM mod:
                {
                    var inGroup = mod.Outfits.Count(o =>
                        group is not null && group.Members.Contains(o.Name, StringComparer.Ordinal));
                    // BaseHeader 是不含 [组内 x/y] 徽标的原始头部，直接取用，无需从 Text 里剥离
                    var targetText = inGroup > 0
                        ? L10n.TrF("L.Tree_InGroupBadge", mod.BaseHeader, inGroup, mod.Outfits.Count)
                        : mod.BaseHeader;
                    if (node.Text != targetText)
                        node.Text = targetText;
                    node.IsMember = mod.Outfits.Count > 0 && inGroup == mod.Outfits.Count;
                    break;
                }
            }
        }
    }

    /// <summary>把左侧勾选的内容（分隔符/模组/服装）应用到当前组。</summary>
    [RelayCommand]
    private void ApplyCheckedToCurrentGroup(string parameter)
    {
        var add = parameter != "remove";
        if (Store.Current is null)
        {
            NotifyUser(L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_SelectGroupFirstSide"));
            return;
        }

        var names = CollectCheckedOutfitNames();
        if (names.Count == 0)
        {
            NotifyUser(L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_NothingChecked"));
            return;
        }

        Store.ApplyToCurrent(names, add);
        Log(L10n.TrF("L.Log_Applied", names.Count, L10n.Tr(add ? "L.Word_Add" : "L.Word_Remove"), Store.Current!.Name, Store.Current!.Members.Count));
        RefreshTree();
        RefreshGroupsListPreserveSelection();
    }

    /// <summary>当前勾选内容展开后的服装名集合。搬运按钮上的数量与实际执行用同一份计算，
    /// 不会出现"按钮说 12 个、实际动了 9 个"的偏差。</summary>
    private HashSet<string> CollectCheckedOutfitNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in WalkRoots())
        {
            if (node.IsChecked != true)
                continue;
            switch (node)
            {
                case OutfitNodeVM outfit:
                    names.Add(outfit.OutfitName);
                    break;
                case ModNodeVM mod:
                    foreach (var outfit in mod.Outfits)
                        names.Add(outfit.Name);
                    break;
                case SeparatorNodeVM:
                    foreach (var modNode in node.WalkSelfAndDescendants().OfType<ModNodeVM>())
                        foreach (var outfit in modNode.Outfits)
                            names.Add(outfit.Name);
                    break;
            }
        }
        return names;
    }

    // ── 搬运操作栏（中间列） ──
    // 勾选数量与按钮可用性都从树上现算：不再需要用户先点按钮才被告知"还没勾选"。
    [ObservableProperty] private int _checkedOutfitCount;

    public string TransferAddLabel => CheckedOutfitCount > 0
        ? L10n.TrF("L.Transfer_AddCount", CheckedOutfitCount)
        : L10n.Tr("L.Transfer_Add");

    public string TransferRemoveLabel => CheckedOutfitCount > 0
        ? L10n.TrF("L.Transfer_RemoveCount", CheckedOutfitCount)
        : L10n.Tr("L.Transfer_Remove");

    public bool CanTransferAdd => CheckedOutfitCount > 0 && Store.Current is not null;
    public bool CanTransferRemove => CanTransferAdd;

    public string TransferAddTip => TransferTip("L.Transfer_AddTip");
    public string TransferRemoveTip => TransferTip("L.Transfer_RemoveTip");

    /// <summary>按钮置灰时也要能说清"为什么点不了"，所以把原因放在 ToolTip 上
    /// （配合 ToolTipService.ShowOnDisabled），而不是等用户点了再弹框。</summary>
    private string TransferTip(string actionKey) =>
        Store.Current is null ? L10n.Tr("L.Transfer_NoGroup")
        : CheckedOutfitCount == 0 ? L10n.Tr("L.Transfer_NoneChecked")
        : L10n.TrF(actionKey, CheckedOutfitCount, Store.Current.Name);

    public void RefreshTransferState()
    {
        CheckedOutfitCount = CollectCheckedOutfitNames().Count;
        OnPropertyChanged(nameof(TransferAddLabel));
        OnPropertyChanged(nameof(TransferRemoveLabel));
        OnPropertyChanged(nameof(CanTransferAdd));
        OnPropertyChanged(nameof(CanTransferRemove));
        OnPropertyChanged(nameof(TransferAddTip));
        OnPropertyChanged(nameof(TransferRemoveTip));
    }

    [RelayCommand]
    private void Undo()
    {
        var (ok, error) = Store.Undo();
        if (!ok)
        {
            NotifyUser(L10n.Tr("L.Title_Tip"), error ?? L10n.Tr("L.Msg_NothingToUndo"));
            return;
        }
        Log(L10n.Tr("L.Log_Undone"));
        RefreshGroupsList();
        RefreshTree();
    }

    private void UpdateCounts()
    {
        if (Scan is null)
        {
            StatusCounts = L10n.Tr("L.Vm_NotScanned");
            return;
        }
        var total = Scan.Outfits.Count;
        var assigned = Scan.Outfits.Count(o => IsInAnyGroup(o.Name));
        var modCount = WalkRoots().Count(n => n.Kind == NodeKind.Mod);
        StatusCounts = L10n.TrF("L.Vm_StatusCounts", modCount, total, assigned, total - assigned);
    }

    public void UpdateTitle()
    {
        IsDirty = Store.Dirty;
        WindowTitle = Store.Dirty ? AppTitle + L10n.Tr("L.Vm_UnsavedSuffix") : AppTitle;
    }

    // ── 组列表 ──
    public bool IsGroupsEmpty => Groups.Count == 0;

    public void RefreshGroupsList()
    {
        var selectedName = Store.Current?.Name;
        Groups = new ObservableCollection<GroupItem>(
            Store.Groups.Select(g => new GroupItem(g.Name, g.Members.Count)));
        var index = Store.Groups.ToList().FindIndex(g => g.Name == selectedName);
        SelectedGroupIndex = index >= 0 ? index : (Store.Count > 0 ? 0 : -1);
        UpdateGroupInfo();
        OnPropertyChanged(nameof(IsGroupsEmpty));
        RefreshTransferState();
    }

    /// <summary>应用勾选后调用：不清空选中位置。</summary>
    private void RefreshGroupsListPreserveSelection() => RefreshGroupsList();

    partial void OnSelectedGroupIndexChanged(int value)
    {
        RefreshTransferState(); // 目标组变了，搬运按钮的可用性与提示随之变
        if (value < 0 || value >= Store.Count)
            return;
        Store.SelectGroup(Store.Groups[value].Name);
        UpdateGroupInfo();
        UpdateMembershipMarks();
    }

    private void UpdateGroupInfo()
    {
        GroupInfo = Store.Current is null ? L10n.Tr("L.Vm_NoGroupSelected") : L10n.TrF("L.Vm_GroupInfo", Store.Current.Name, Store.Current.Members.Count);
    }

    // ── 组操作 ──
    [RelayCommand]
    private void NewGroup(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        var (ok, error) = Store.NewGroup(name);
        if (!ok)
        {
            NotifyUser(L10n.Tr("L.Title_Tip"), error ?? L10n.Tr("L.Msg_CreateFailed"));
            return;
        }
        Log(L10n.TrF("L.Log_NewGroup", name));
        RefreshGroupsList();
    }

    [RelayCommand]
    private void RenameGroup(string? newName)
    {
        var group = Store.Current;
        if (group is null || string.IsNullOrWhiteSpace(newName) || newName == group.Name)
            return;
        if (Store.GroupNameExists(newName, group))
        {
            NotifyUser(L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_DuplicateGroup"), warning: true);
            return;
        }
        // 不再在这里额外 Store.Snapshot()：RenameGroup 内部已经入了一次快照。多压一次会让
        // 用户的一次重命名占掉两步撤销——第二次撤销看起来"点了没反应"。
        var (ok, error) = Store.RenameGroup(group.Name, newName);
        if (!ok)
        {
            NotifyUser(L10n.Tr("L.Title_Tip"), error ?? L10n.Tr("L.Msg_RenameFailed"));
            return;
        }
        Log(L10n.TrF("L.Log_Renamed", group.Name, newName));
        RefreshGroupsList();
    }

    [RelayCommand]
    private void DeleteGroup()
    {
        var group = Store.Current;
        if (group is null)
            return;
        // 快照由 DeleteGroup 内部负责（同重命名，避免一次操作占两步撤销）
        Store.DeleteGroup(group.Name);
        Log(L10n.TrF("L.Log_DeletedGroup", group.Name));
        SelectedGroupIndex = -1;
        RefreshGroupsList();
        RefreshTree();
    }

    public void ImportFiles(IEnumerable<string> files)
    {
        var any = false;
        var failures = new List<string>();
        foreach (var file in files)
        {
            if (!SliderGroupFile.TryLoad(file, out var imported, out var error))
            {
                failures.Add($"{Path.GetFileName(file)}：{error}");
                continue;
            }
            any = true;
            // 快照由 Store.Import 内部负责（一次导入 = 一步撤销；这里再压一次会让撤销多走一步空转）
            var (addedGroups, addedMembers) = Store.Import(imported);
            Log(L10n.TrF("L.Log_Imported", file, addedGroups, addedMembers));
        }
        if (any)
        {
            RefreshGroupsList();
            RefreshTree();
        }
        if (failures.Count > 0)
            NotifyUser(L10n.Tr("L.Title_Error"), L10n.Tr("L.Msg_ImportFailed") + string.Join("\n", failures), warning: true);
    }
}
