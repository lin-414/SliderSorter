namespace SliderSorter.Wpf.ViewModels;

/// <summary>重建树时保住「哪些节点是展开的」。
///
/// 重建（改过滤条件、加入分组、载入组……）会换掉整棵树的节点对象，而展开状态是视图状态、
/// 不存在扫描结果里——重建前后得对得上号：先在旧树上记下每个展开节点的**路径**，
/// 重建后在新树上按同一路径恢复，其余保持默认（折叠）。
///
/// 路径 = 根到自己的每一层「节点类型 + 在父节点里的下标」。</summary>
public static class TreeExpandState
{
    /// <summary>记下当前展开的节点（路径集合）。</summary>
    public static HashSet<string> Capture(IEnumerable<NodeVM> roots)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var rootIndex = 0;
        foreach (var root in roots)
        {
            foreach (var node in root.WalkSelfAndDescendants())
                if (node.IsExpanded)
                    keys.Add(KeyOf(node, rootIndex));
            rootIndex++;
        }
        return keys;
    }

    /// <summary>把 <paramref name="keys"/> 里的节点在新树上重新展开。新树默认全折叠，
    /// 所以这里只置 true 就能完整还原（包括"用户手动折叠过"的节点保持折叠）。
    /// <para>
    /// 还原是**全树**逐节点的事（折叠的节点也要被问到才能保持折叠），成本必须摊平：
    /// 路径键沿 DFS 增量拼接、子节点下标直接用枚举序号。不能对每个节点现算
    /// <c>KeyOf</c>——那要递归重走整条祖先链、每层再 <c>Children.IndexOf</c>（O(兄弟数)），
    /// 单个模组几千件服装时一次重建就是几百万次比较，而重建在每次过滤防抖后都会跑。
    /// </para></summary>
    public static void Restore(IEnumerable<NodeVM> roots, HashSet<string> keys)
    {
        var rootIndex = 0;
        foreach (var root in roots)
        {
            RestoreNode(root, rootIndex, keys, parentPrefix: "");
            rootIndex++;
        }
    }

    /// <summary>节点的路径键：根到自己的每层「类型 + 在父节点里的下标」，
    /// 与 <see cref="KeyOf"/> 同一格式（<c>"Mod2/Outfit5/"</c> 这类），只是改成了沿路递增拼接。
    /// 根节点的下标是它在根集合里的位置——⚠️ 不能让所有根节点算出同一个键（见 <see cref="KeyOf"/> 的注）。</summary>
    private static void RestoreNode(NodeVM node, int indexInParent, HashSet<string> keys, string parentPrefix)
    {
        var key = $"{parentPrefix}{node.Kind}{indexInParent}/";
        if (keys.Contains(key))
            // 先置 true 再降入子节点：ModNodeVM 展开时才物化出服装行，
            // 物化改的是该节点自己的 Children，此刻还没开始枚举它，安全
            node.IsExpanded = true;
        var childIndex = 0;
        foreach (var child in node.Children)
            RestoreNode(child, childIndex++, keys, key);
    }

    /// <summary>节点的路径键：根到自己的每层「类型 + 在父节点里的下标」。
    ///
    /// ⚠️ 根节点没有父节点，下标只能用它在根集合里的位置。<c>parent?.Children.IndexOf(node) ?? -1</c>
    /// 这种写法（本类的前身）让**所有**根节点算出同一个键 <c>"Mod-1/"</c>：只要有一个根节点是展开的，
    /// 重建后每个同类型的根节点都会被恢复成展开——正是「加入分组后左侧列表整片展开」。
    /// <see cref="Restore"/> 走增量拼接后只有 <see cref="Capture"/> 还在用本方法（只对展开节点算，便宜）。</summary>
    private static string KeyOf(NodeVM node, int rootIndex)
    {
        var parent = node.Parent;
        var index = parent?.Children.IndexOf(node) ?? rootIndex;
        return (parent is null ? "" : KeyOf(parent, rootIndex)) + $"{node.Kind}{index}/";
    }
}
