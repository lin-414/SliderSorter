namespace SliderSorter.Core;

/// <summary>
/// 分组集合的模型与操作：新建/重命名/删除、按勾选应用、规则应用、导入、撤销栈（30 步）。
/// 纯逻辑无 UI；错误以字符串返回，由界面层展示。所有变更先入撤销快照、置脏并触发 Changed。
/// </summary>
public class GroupStore
{
    private readonly List<SliderGroup> _groups = new();
    // 撤销栈必须能从两头动：新的压到**末尾**，超容量时从**开头**丢最老的。
    // 不能用 Stack<T>——它只有 Pop（弹栈顶），"超过 30 条丢最老"写出来会把刚压入的最新快照弹掉：
    // 栈一旦攒满 30 份就被冻结，之后每次快照都是 push+立即 pop 抵消，一次撤销会静默回跳几十步。
    private readonly List<List<SliderGroup>> _undoStack = new();
    private HashSet<string>? _membershipCache;

    public bool Dirty { get; private set; }
    public string? CurrentGroupName { get; private set; }
    public int Count => _groups.Count;
    public IReadOnlyList<SliderGroup> Groups => _groups;

    public event Action? Changed;

    // Current 用忽略大小写匹配：CurrentGroupName 可以来自界面上用户敲的名字（或外部传入的组名），
    // 与 GetGroup / GroupNameExists 的大小写策略保持一致，避免"选中的组名大小写不同 → Current 变 null"
    public SliderGroup? Current =>
        _groups.FirstOrDefault(g => string.Equals(g.Name, CurrentGroupName, StringComparison.OrdinalIgnoreCase));

    public SliderGroup? GetGroup(string name) =>
        _groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool IsInAnyGroup(string outfit)
    {
        EnsureMembershipCache();
        return _membershipCache!.Contains(outfit);
    }

    private void EnsureMembershipCache()
    {
        if (_membershipCache is not null)
            return;
        _membershipCache = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in _groups)
            foreach (var member in group.Members)
                _membershipCache.Add(member);
    }

    private void InvalidateMembershipCache() => _membershipCache = null;

    public bool GroupNameExists(string name, SliderGroup? except = null) =>
        _groups.Any(g => !ReferenceEquals(g, except) &&
                         string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>全量替换（扫描后载入上次生成的组）：视为干净状态。</summary>
    public void Load(IEnumerable<SliderGroup> groups)
    {
        _groups.Clear();
        _groups.AddRange(groups);
        CurrentGroupName = _groups.Count > 0 ? _groups[0].Name : null;
        Dirty = false;
        // 上一轮数据（甚至上一份扫描结果）的快照不能留着：留着的话，新清单上按一次撤销
        // 会把旧扫描的组整个恢复回来并置脏——状态穿越且无提示
        _undoStack.Clear();
        InvalidateMembershipCache();
        Changed?.Invoke();
    }

    public void SelectGroup(string? name)
    {
        if (string.Equals(CurrentGroupName, name, StringComparison.OrdinalIgnoreCase))
            return;
        CurrentGroupName = name;
        Changed?.Invoke();
    }

    public (bool Ok, string? Error) NewGroup(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            return (false, CoreStrings.Get("L.Core_GroupEmptyName"));
        if (GroupNameExists(name))
            return (false, CoreStrings.Get("L.Core_GroupDuplicate"));
        Snapshot();
        _groups.Add(new SliderGroup(name));
        CurrentGroupName = name;
        MarkDirty();
        return (true, null);
    }

    public (bool Ok, string? Error) RenameGroup(string oldName, string newName)
    {
        var group = _groups.FirstOrDefault(g => string.Equals(g.Name, oldName, StringComparison.OrdinalIgnoreCase));
        if (group is null)
            return (false, CoreStrings.Get("L.Core_GroupRenameMissing"));
        newName = newName.Trim();
        if (newName.Length == 0 || newName == group.Name)
            return (true, null);
        if (GroupNameExists(newName, group))
            return (false, CoreStrings.Get("L.Core_GroupDuplicate"));
        Snapshot();
        group.Name = newName;
        if (string.Equals(CurrentGroupName, oldName, StringComparison.OrdinalIgnoreCase))
            CurrentGroupName = newName;
        MarkDirty();
        return (true, null);
    }

    public bool DeleteGroup(string name)
    {
        var group = _groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        if (group is null)
            return false;
        Snapshot();
        _groups.Remove(group);
        if (string.Equals(CurrentGroupName, name, StringComparison.OrdinalIgnoreCase))
            CurrentGroupName = _groups.FirstOrDefault()?.Name;
        MarkDirty();
        return true;
    }

    public (bool Ok, string? Error) ApplyToCurrent(string outfit, bool add)
    {
        var group = Current;
        if (group is null)
            return (false, CoreStrings.Get("L.Core_GroupNeedSelection"));
        Snapshot();
        ApplyMembership(group, outfit, add);
        MarkDirty();
        return (true, null);
    }

    public (bool Ok, string? Error) ApplyToCurrent(IEnumerable<string> outfits, bool add)
    {
        var group = Current;
        if (group is null)
            return (false, CoreStrings.Get("L.Core_GroupNeedSelection"));
        Snapshot();
        foreach (var outfit in outfits.Distinct(StringComparer.Ordinal))
            ApplyMembership(group, outfit, add);
        MarkDirty();
        return (true, null);
    }

    /// <summary>把一批服装加入/移出指定名称的组（规则归组用）。
    /// 组名一律按忽略大小写找：<c>GetGroup</c>/<c>GroupNameExists</c>/<c>Current</c> 都是这个口径，
    /// 这里若按 Ordinal 找，把组 <c>Armor</c> 重命名成 <c>ARMOR</c> 之后预设里那句
    /// <c>Group="Armor"</c> 就会静默匹配不上——规则看起来执行了（调用方还按 GetGroup 打出成员数），
    /// 实际一条没动。找不到组返回 -1，调用方必须说给用户听。</summary>
    public int ApplyToGroup(string groupName, IEnumerable<string> outfits, bool add)
    {
        var group = _groups.FirstOrDefault(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase));
        if (group is null)
            return -1;
        Snapshot();
        var applied = 0;
        foreach (var outfit in outfits.Distinct(StringComparer.Ordinal))
        {
            if (add)
            {
                if (!group.Members.Contains(outfit, StringComparer.Ordinal))
                {
                    group.Members.Add(outfit);
                    applied++;
                }
            }
            else if (group.Members.RemoveAll(m => m == outfit) > 0)
            {
                applied++;
            }
        }
        if (applied > 0)
            MarkDirty();
        return applied;
    }

    public (bool Ok, string? Error) RemoveMembers(string groupName, IEnumerable<string> names)
    {
        var group = GetGroup(groupName);
        if (group is null)
            return (false, CoreStrings.Get("L.Core_GroupTargetMissing"));
        Snapshot();
        foreach (var name in names)
            group.Members.RemoveAll(m => m == name);
        MarkDirty();
        return (true, null);
    }

    /// <summary>把一批服装从 from 组搬到 to 组（成员预览窗口的「移动到…」）。
    /// 目标里已有的只从 from 移除、不重复添加；from 与 to 是同一个组时不动（菜单侧已按引用排除）。
    /// 组名沿用 <see cref="GetGroup"/> 的忽略大小写口径。任一组找不到返回 -1，调用方须告知用户。</summary>
    public int MoveMembers(string fromName, string toName, IEnumerable<string> names)
    {
        var from = GetGroup(fromName);
        var to = GetGroup(toName);
        if (from is null || to is null || ReferenceEquals(from, to))
            return -1;
        Snapshot();
        var moved = 0;
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            if (!to.Members.Contains(name, StringComparer.Ordinal))
                to.Members.Add(name);
            if (from.Members.RemoveAll(m => m == name) > 0)
                moved++;
        }
        if (moved > 0)
            MarkDirty();
        return moved;
    }

    public (int AddedGroups, int AddedMembers) Import(IEnumerable<SliderGroup> imported)
    {
        Snapshot();
        int addedGroups = 0, addedMembers = 0;
        foreach (var incoming in imported)
        {
            var existing = _groups.FirstOrDefault(g =>
                string.Equals(g.Name, incoming.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new SliderGroup(incoming.Name);
                _groups.Add(existing);
                addedGroups++;
            }
            foreach (var member in incoming.Members)
            {
                if (!existing.Members.Contains(member, StringComparer.Ordinal))
                {
                    existing.Members.Add(member);
                    addedMembers++;
                }
            }
        }
        if (addedGroups + addedMembers > 0)
            MarkDirty();
        return (addedGroups, addedMembers);
    }

    public bool CanUndo => _undoStack.Count > 0;

    public (bool Ok, string? Error) Undo()
    {
        if (_undoStack.Count == 0)
            return (false, CoreStrings.Get("L.Core_GroupNothingToUndo"));
        // 撤销弹的是最新一次快照（列表末尾），还原后把它从栈里拿掉
        var at = _undoStack.Count - 1;
        var restored = _undoStack[at];
        _undoStack.RemoveAt(at);
        _groups.Clear();
        _groups.AddRange(restored);
        if (CurrentGroupName is not null && _groups.All(g => !string.Equals(g.Name, CurrentGroupName, StringComparison.OrdinalIgnoreCase)))
            CurrentGroupName = _groups.FirstOrDefault()?.Name;
        Dirty = true;
        InvalidateMembershipCache();
        Changed?.Invoke();
        return (true, null);
    }

    /// <summary>手动入撤销快照（特殊变更路径用）。</summary>
    public void Snapshot()
    {
        _undoStack.Add(_groups.Select(g => g.Clone()).ToList());
        // 超容量丢的是**最老**的（列表开头），最新快照必须留得住——否则栈满之后
        // 一次撤销会跳到几十步之前，中间的修改既回不去也没法重做
        if (_undoStack.Count > UndoCapacity)
            _undoStack.RemoveRange(0, _undoStack.Count - UndoCapacity);
    }

    /// <summary>撤销栈深度。快照是整份组列表的克隆，30 步封顶防止大组表把内存吃穿。</summary>
    private const int UndoCapacity = 30;

    /// <summary>保存成功后清除脏标记。</summary>
    public void MarkSaved() => Dirty = false;

    private void ApplyMembership(SliderGroup group, string outfit, bool add)
    {
        if (add)
        {
            if (!group.Members.Contains(outfit, StringComparer.Ordinal))
                group.Members.Add(outfit);
        }
        else
        {
            group.Members.RemoveAll(m => m == outfit);
        }
    }

    /// <summary>界面侧直接变更组数据后（如成员预览移除）标记脏并通知。
    /// 成员集合被就地改过，必须一并失效成员缓存——否则 <see cref="IsInAnyGroup"/> 会一直用旧集合
    /// 回答，界面上表现为"刚移除的服装仍显示在组内"。</summary>
    public void MarkDirtyFromUi()
    {
        Dirty = true;
        InvalidateMembershipCache();
        Changed?.Invoke();
    }

    private void MarkDirty()
    {
        Dirty = true;
        InvalidateMembershipCache();
        Changed?.Invoke();
    }
}
