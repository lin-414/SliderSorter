using System.Collections.ObjectModel;
using BSGroupGenerator.Wpf.Services;
using System.Windows;
using BSGroupGenerator.Core;
using BSGroupGenerator.Wpf.ViewModels;

namespace BSGroupGenerator.Wpf.Views;

/// <summary>预览某个组内的全部服装：按 分隔符 → 模组 分层显示，可过滤、勾选批量移出。</summary>
public partial class GroupMembersWindow : Window
{
    private readonly SliderGroup _group;
    private readonly List<(string? Separator, string Owner, List<string> Outfits)> _structure;
    private readonly Action _beforeChange;
    private readonly Action _onChanged;

    public GroupMembersWindow(SliderGroup group,
        List<(string? Separator, string Owner, List<string> Outfits)> structure,
        Action beforeChange, Action onChanged)
    {
        InitializeComponent();
        _group = group;
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

        // 连续相同名称的分隔符共用同一个节点（与主界面一致）
        SeparatorNodeVM? currentSepNode = null;
        string? currentSepName = null;
        var started = false;

        foreach (var (separator, owner, outfits) in _structure)
        {
            var memberOutfits = outfits
                .Where(o => _group.Members.Contains(o, StringComparer.Ordinal))
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
        var selected = Walk(Tree.ItemsSource!.Cast<NodeVM>())
            .Where(n => n.IsChecked == true && n is OutfitNodeVM)
            .Select(n => ((OutfitNodeVM)n).OutfitName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (selected.Count == 0)
        {
            Notify.Info(this, L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_SelectOutfitsFirst"));
            return;
        }
        if (!Notify.Confirm(this, L10n.Tr("L.Title_Confirm"),
                L10n.TrF("L.Msg_ConfirmRemove", selected.Count, _group.Name), destructive: true))
            return;

        _beforeChange();
        foreach (var name in selected)
            _group.Members.RemoveAll(m => m == name);
        _onChanged();
        Rebuild();
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
