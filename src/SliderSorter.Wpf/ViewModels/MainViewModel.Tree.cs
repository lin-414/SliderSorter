using System.Collections.ObjectModel;
using System.IO;
using System.Collections.Specialized;
using SliderSorter.Core;
using SliderSorter.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SliderSorter.Wpf.ViewModels;

public partial class MainViewModel
{
    /// <summary>重建树（RefreshTree 别名，与 WinForms 命名对齐）。</summary>
    public void RefreshTree() => RebuildTree();

    [ObservableProperty] private ObservableCollection<NodeVM> _treeRoots = [];
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _modFilterText = "";
    [ObservableProperty] private bool _unassignedOnly;

    private CancellationTokenSource? _filterDebounce;

    // 过滤这三条路都保留勾选：用户是"先勾一批、再打字找剩下的"，打字不该把刚勾的清掉
    //（弹窗那边早就这么做了，见 NewModsWindow._checked 的说明）。其它重建路径（应用勾选、
    // 重新扫描、撤销）仍按新扫描结果重算，勾选是那一轮的一次性选择，不该跨扫描留着。
    partial void OnFilterTextChanged(string value) => DebounceRebuild();
    partial void OnModFilterTextChanged(string value) => DebounceRebuild();
    partial void OnUnassignedOnlyChanged(bool value) => RebuildTree(preserveChecks: true);

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
                RebuildTree(preserveChecks: true);
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

    /// <param name="preserveChecks">把当前勾选回灌到重建后的树上（过滤词变化时走 true）。
    /// 勾选状态原本只长在节点上，而重建是全换新技术节点，不接这一手就是"打个字，勾全灭"。</param>
    public void RebuildTree(bool preserveChecks = false)
    {
        if (_closed)
            return;

        // 记录展开状态，重建后恢复（键的算法见 TreeExpandState）
        var expanded = TreeExpandState.Capture(TreeRoots);
        var checks = preserveChecks ? CaptureChecks() : default;

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
        if (preserveChecks)
            RestoreChecks(newRoots, checks); // 要在上面那一步之后：展开才会物化出服装行，回灌才有对象可改

        UpdateCounts();
        UpdateTitle();
        RefreshTransferState();
        OnPropertyChanged(nameof(ShowTreeEmptyState));
        // 状态行那句"上次扫描：… · 模组 N"里的 N 数的是这棵树（StatusCountsShort 走 WalkRoots），
        // 而 LastScanAt 是在扫描收尾时先设的——那时树上还是旧一轮的模组。树换完必须重算一次，
        // 否则首扫恒显示「模组 0」、之后每次都慢一轮。
        OnPropertyChanged(nameof(ScanSummaryText));
    }

    /// <summary>勾过了什么的快照：模组级一个（整组勾上，含还没物化的服装），服装级按 (模组, 服装名)。
    /// 按身份记而不是按位置，重建后才对得回来。</summary>
    private (HashSet<string> Mods, HashSet<(string Owner, string Outfit)> Outfits) CaptureChecks()
    {
        var mods = new HashSet<string>(StringComparer.Ordinal);
        // 值组的默认相等就是逐字段 string.Equals（Ordinal），与 Store 判定成员同一口径
        var outfits = new HashSet<(string, string)>();
        foreach (var node in WalkRoots())
        {
            switch (node)
            {
                case OutfitNodeVM outfit when outfit.IsChecked == true && outfit.Parent is ModNodeVM parent:
                    outfits.Add((parent.Owner, outfit.OutfitName));
                    break;
                case ModNodeVM mod when mod.IsChecked == true:
                    mods.Add(mod.Owner);
                    break;
            }
        }
        return (mods, outfits);
    }

    private static void RestoreChecks(ObservableCollection<NodeVM> roots,
        (HashSet<string> Mods, HashSet<(string Owner, string Outfit)> Outfits) saved)
    {
        foreach (var root in roots)
            foreach (var node in root.WalkSelfAndDescendants())
            {
                switch (node)
                {
                    case ModNodeVM mod when saved.Mods.Contains(mod.Owner):
                        mod.IsChecked = true; // 向下级联到已物化的服装行
                        break;
                    case OutfitNodeVM outfit when outfit.IsChecked != true
                        && outfit.Parent is ModNodeVM parent
                        && saved.Outfits.Contains((parent.Owner, outfit.OutfitName)):
                        outfit.IsChecked = true;
                        break;
                }
            }
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
            node.BadgeText = L10n.TrF("L.Tree_InGroupBadge", inGroup, visibleOutfits.Count);
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
                    // Text 恒为 BaseHeader：计数走后缀徽标（BadgeText），不再拼进名字里。
                    // 于是换语言只需重算徽标那一小段，模组名本身不用重拼。
                    if (node.Text != mod.BaseHeader)
                        node.Text = mod.BaseHeader;
                    node.BadgeText = inGroup > 0
                        ? L10n.TrF("L.Tree_InGroupBadge", inGroup, mod.Outfits.Count)
                        : "";
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
        // 这两种情况改走内联提示条而不是模态框：它们都是"你少做了一步"，用户看完要做的
        // 下一件事就是回到左边的树/右边的组列表继续操作，而模态框正好挡在那两块上面。
        if (Store.Current is null)
        {
            ShowBanner(BannerKind.Info, L10n.Tr("L.Msg_SelectGroupFirstSide"));
            return;
        }

        var names = CollectCheckedOutfitNames();
        if (names.Count == 0)
        {
            ShowBanner(BannerKind.Info, L10n.Tr("L.Msg_NothingChecked"));
            return;
        }

        // 操作真正生效了 → 收掉上一条提示（否则"先勾选服装"会一直挂在那里，
        // 而用户早就勾好并成功搬运过了）
        Banner = null;
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
        CanUndo = Store.CanUndo;
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
            // "没有可撤销的操作"是非破坏性的（什么都没发生），走内联提示，
            // 不打断用户；按钮本身此时也是禁用态，两处说法一致。
            ShowBanner(BannerKind.Info, error ?? L10n.Tr("L.Msg_NothingToUndo"));
            return;
        }
        Banner = null;
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
        // 就地重建，**不能**换成新集合：ListBox 绑的 GroupsView 是构造期建立的一次性投影，
        // 换集合会让它一直对着旧实例，列表永久空白（详见 Groups 属性注释）。
        Groups.Clear();
        foreach (var group in Store.Groups)
            Groups.Add(new GroupItem(group.Name, group.Members.Count));

        // 先落 null 再落目标：Clear() 之后 ListBox 那一侧的选中项是视图侧的事，VM 里可能还留着
        // 一个与新实例逐字段相等的旧记录；[ObservableProperty] 按值比较会判定"没变"而不发通知，
        // 列表就会停在一个已经不在集合里的实例上（看着像没有任何一行高亮）。
        SelectedGroup = null;
        // 选中行按组名回挂到**新**的 GroupItem 实例上（Clear 之后旧实例已经不在列表里了）。
        // 当前组被过滤掉时就是 null：列表没有高亮，但 Store.Current 保持原样——绝不为"总得有
        // 一行高亮"去选第一行，那等于把用户没点过的组悄悄变成当前组。
        SelectedGroup = selectedName is null
            ? null
            : Groups.FirstOrDefault(g => string.Equals(g.Name, selectedName, StringComparison.OrdinalIgnoreCase));
        UpdateGroupInfo();
        OnPropertyChanged(nameof(IsGroupsEmpty));
        CanUndo = Store.CanUndo;
        RefreshTransferState();
        UpdateTitle(); // 新建/重命名/删除组都是脏变更：不补这一下，标题栏与保存按钮还停在旧状态
    }

    /// <summary>应用勾选后调用：不清空选中位置。</summary>
    private void RefreshGroupsListPreserveSelection() => RefreshGroupsList();

    partial void OnSelectedGroupChanged(GroupItem? value)
    {
        RefreshTransferState(); // 目标组变了，搬运按钮的可用性与提示随之变
        if (value is null)
            return; // 列表就地重建期间的空选中不是用户意图，别动 Store.Current
        // 选中了组 → "先选一个组"这条提示的前提已经不成立，主动收掉。
        // 提示条不该靠用户点 × 才消失：条件一满足就走，才是"就近反馈"而不是"又一个要清理的窗口"。
        if (Banner is { Kind: BannerKind.Info })
            Banner = null;
        Store.SelectGroup(value.Name);
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
        // 删完哪一行算选中不由这里指定：DeleteGroup 已把 CurrentGroupName 挪到剩下的第一个组，
        // RefreshGroupsList 按那个名字回挂，列表与 GroupInfo 自然对上。
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
