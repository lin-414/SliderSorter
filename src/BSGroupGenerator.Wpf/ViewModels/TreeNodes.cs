using System.Collections.ObjectModel;
using BSGroupGenerator.Core;
using BSGroupGenerator.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BSGroupGenerator.Wpf.ViewModels;

public enum NodeKind { Separator, Mod, Outfit }

/// <summary>树节点 VM：分隔符(S)/模组(M)/服装(O) 三层。
/// 勾选语义：叶子 = true/false；容器（分隔符/模组）按子节点聚合——全选 true、全不选 false、
/// 部分选中 null（复选框显示半选态）。用户勾选容器向下级联；勾选/取消子节点向上聚合。</summary>
public partial class NodeVM : ObservableObject
{
    public NodeVM(NodeKind kind) => Kind = kind;

    public NodeKind Kind { get; }
    public NodeVM? Parent { get; set; }
    public ObservableCollection<NodeVM> Children { get; } = new();

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool? _isChecked;
    [ObservableProperty] private bool _isMember;
    [ObservableProperty] private bool _isSeparator;
    [ObservableProperty] private bool _isConflict;
    [ObservableProperty] private bool _isPlaceholder;

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value)
                OnExpanded();
        }
    }

    /// <summary>首次展开时物化子节点（模组懒加载）。</summary>
    protected virtual void OnExpanded() { }

    /// <summary>双击行标题展开/折叠（与 WinForms 版行为一致）。</summary>
    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    // ── 整棵树的展开/折叠（左侧列表右键菜单）──
    // 三个命令都挂在**节点**上而不是 MainViewModel 上：菜单项的 DataContext 就是被右键的那个节点
    // （模板与 ContextMenu 见 Themes/Controls.xaml 的 NodeTreeItemStyle），树级操作再经 Roots
    // 拿到整棵树——否则「全部展开」只会展开自己这棵子树。

    /// <summary>整棵树的根集合：由树的宿主挂在根节点上，子节点沿 Parent 链向上取。
    /// 与 CheckedChanged 同一套做法——只在根节点上接线，懒物化的服装行不必逐个接。</summary>
    private IReadOnlyList<NodeVM>? _roots;
    public IReadOnlyList<NodeVM>? Roots
    {
        get => _roots ?? Parent?.Roots;
        set => _roots = value;
    }

    /// <summary>整棵树的节点。宿主没提供根集合时（弹窗里的预览树等）退化成自己的子树。</summary>
    private IEnumerable<NodeVM> TreeScope() =>
        Roots is { Count: > 0 } roots
            ? roots.SelectMany(root => root.WalkSelfAndDescendants())
            : WalkSelfAndDescendants();

    [RelayCommand]
    private void ExpandAll() => SetExpandedEverywhere(true);

    [RelayCommand]
    private void CollapseAll() => SetExpandedEverywhere(false);

    /// <summary>折叠其他：保留「自己 → 根」这条链（连祖先一起折了就看不见自己了），其余全部折叠，
    /// 自己保持展开。右键选中的那一行由视图负责选中（见 Views/TreeSelection.cs）。</summary>
    [RelayCommand]
    private void CollapseOthers()
    {
        var keep = new HashSet<NodeVM>();
        for (var node = this; node is not null; node = node.Parent)
            keep.Add(node);

        foreach (var node in TreeScope())
            if (!keep.Contains(node))
                node.IsExpanded = false;

        IsExpanded = true; // 右键点的是折叠着的节点时也要"保持展开"（顺带物化出服装行）
    }

    /// <summary>展开是把节点自身先置 true（触发物化、建出服装行）再降入其子节点：
    /// 物化改的是**该节点自己**的 Children，而枚举此刻走的是它父节点的 Children，两个集合不同，安全。</summary>
    private void SetExpandedEverywhere(bool value)
    {
        foreach (var node in TreeScope())
            node.IsExpanded = value;
    }

    private bool _cascading;

    /// <summary>勾选态变化时通知视图模型（用于刷新搬运按钮上的数量）。
    /// 只挂在根节点上：子节点经 Parent 链向上取，懒物化的服装行不必逐个接线。
    /// 级联过程（_cascading）与聚合重算都不触发，避免一次点击引发上百次回调。</summary>
    private Action? _checkedChanged;
    public Action? CheckedChanged
    {
        get => _checkedChanged ?? Parent?.CheckedChanged;
        set => _checkedChanged = value;
    }

    partial void OnIsCheckedChanged(bool? value)
    {
        if (_cascading)
            return;
        CascadeDown(value == true);
        Parent?.RefreshAggregated();
        CheckedChanged?.Invoke();
    }

    /// <summary>用户勾选后向下级联：容器把整棵子树置为同一布尔值（占位节点跳过，null 不会来自用户点击）。</summary>
    private void CascadeDown(bool value)
    {
        foreach (var child in Children)
        {
            if (child.IsPlaceholder)
                continue;
            child._cascading = true;
            child.IsChecked = value;
            child.CascadeDown(value);
            child._cascading = false;
        }
    }

    /// <summary>子节点状态变化后，自底向上重算祖先的聚合勾选态。
    /// 公开给手动重建/剪枝子树的窗口（新装模组弹窗等）在操作后调用。</summary>
    public void RefreshAggregated()
    {
        _cascading = true;
        IsChecked = ComputeChecked();
        _cascading = false;
        Parent?.RefreshAggregated();
    }

    private bool? ComputeChecked()
    {
        var real = Children.Where(c => !c.IsPlaceholder).ToList();
        if (real.Count == 0)
            return IsChecked; // 未物化的模组等：子态未知，保持原值
        if (real.All(c => c.IsChecked == true))
            return true;
        if (real.All(c => c.IsChecked == false))
            return false;
        return null; // 混合（含半选的子容器）
    }

    public IEnumerable<NodeVM> WalkSelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var n in child.WalkSelfAndDescendants())
                yield return n;
    }
}

public sealed class SeparatorNodeVM : NodeVM
{
    public SeparatorNodeVM(string title) : base(NodeKind.Separator)
    {
        Text = title;
        IsSeparator = true;
        IsChecked = false;
    }
}

public sealed class OutfitNodeVM : NodeVM
{
    public OutfitNodeVM(string outfitName, bool hasConflict, bool isMember, bool isChecked,
        bool hasOutputConflict = false)
        : base(NodeKind.Outfit)
    {
        OutfitName = outfitName;
        HasConflict = hasConflict;
        HasOutputConflict = hasOutputConflict;
        // 「已在组内」不再拼成 "✔ " 文本前缀：它由视图里的独立徽标呈现（可单独设色/对齐），
        // 也就不会和「（同名冲突）」后缀混在同一个字符串里
        Text = FormatText(outfitName, hasConflict, hasOutputConflict);
        IsMember = isMember;
        IsConflict = hasConflict;
        IsChecked = isChecked;
    }

    public string OutfitName { get; }
    public bool HasConflict { get; }

    /// <summary>还有别的模组的服装写同一个输出文件（BodySlide 的输出文件冲突）。</summary>
    public bool HasOutputConflict { get; }

    /// <summary>树里服装行的文本：两种冲突各带一个后缀，措辞在 Lang.*.xaml 里。
    /// <see cref="MainViewModel"/> 刷新成员标记时也走这里，两处判定不会漂开。</summary>
    public static string FormatText(string outfitName, bool hasConflict, bool hasOutputConflict)
    {
        if (hasConflict)
            outfitName += L10n.Tr("L.Tree_ConflictSuffix");
        if (hasOutputConflict)
            outfitName += L10n.Tr("L.Tree_OutputConfSuffix");
        return outfitName;
    }
}

/// <summary>模组节点：懒加载——挂一个空占位子节点让展开箭头出现，首次展开才物化服装行。</summary>
public sealed class ModNodeVM : NodeVM
{
    private readonly Func<string, bool> _isMember;
    private bool _materialized;

    public ModNodeVM(string header, string owner, List<OutfitEntry> visibleOutfits,
        Func<string, bool> isMember, bool allMember) : base(NodeKind.Mod)
    {
        Owner = owner;
        Outfits = visibleOutfits;
        _isMember = isMember;
        BaseHeader = header;
        Text = header;
        IsMember = allMember && visibleOutfits.Count > 0;
        IsChecked = allMember && visibleOutfits.Count > 0;
        Children.Add(new NodeVM(NodeKind.Outfit) { IsPlaceholder = true }); // 占位
    }

    public string Owner { get; }
    public List<OutfitEntry> Outfits { get; }
    /// <summary>不含 [组内 x/y] 徽标的基础头部；由本属性还原，避免依赖当前语言的前缀做字符串剥离。</summary>
    public string BaseHeader { get; }

    /// <summary>子节点是手动构建（而非懒物化）时调用，防止首次展开被清空。</summary>
    public void MarkMaterialized() => _materialized = true;

    protected override void OnExpanded()
    {
        if (_materialized)
            return;
        _materialized = true;
        Children.Clear();
        foreach (var outfit in Outfits)
            Children.Add(new OutfitNodeVM(outfit.Name, outfit.HasConflict, _isMember(outfit.Name), IsChecked == true,
                outfit.HasOutputConflict)
            {
                Parent = this,
            });
    }
}
