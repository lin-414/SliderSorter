using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using SliderSorter.Wpf.Services;
using SliderSorter.Wpf.ViewModels;

namespace SliderSorter.Wpf.Views.Pages;

/// <summary>分组生成页：树 → 搬运 → 分组 → 写文件。
/// VM 由壳通过 DataContext 继承下来，页面自己不造 VM。</summary>
public partial class GroupGenerationPage : UserControl
{
    private MainViewModel _vm = null!;

    public GroupGenerationPage()
    {
        InitializeComponent();
        // 右键点在某一行上时把那一行也选中——右键菜单就是在这一行上弹的，
        // 用户预期它同时被选中。实现放在视图侧，理由见 TreeSelection。
        OutfitTree.ContextMenuOpening += (_, e) =>
            TreeSelection.SelectRow(e.OriginalSource as DependencyObject, OutfitTree);
        // 空格勾选 / 左右展开折叠 / 上下移动行。不接这一套的话，纯键盘用户能选中行却勾不上它——
        // 详见 TreeKeyboard 的类注释。
        TreeKeyboard.Attach(OutfitTree);
        DataContextChanged += (_, _) => AdoptViewModel();
    }

    /// <summary>VM 由壳通过 DataContext 继承下来，构造期还是空的（FrameworkElement 没有可重写的
    /// OnDataContextChanged，只有这个事件）。日志列表是虚拟化的 ListBox，滚到底的订阅挂在这里。</summary>
    private void AdoptViewModel()
    {
        if (_vm is not null)
            _vm.LogFlushed -= OnLogFlushed;
        _vm = (MainViewModel)DataContext;
        _vm.LogFlushed += OnLogFlushed;
    }

    private void OnLogFlushed()
    {
        if (_vm.IsLogExpanded && _vm.LogLines.Count > 0)
            LogList.ScrollIntoView(_vm.LogLines[^1]);
    }

    /// <summary>对话框的宿主：页面自己没有窗口身份，取所在的壳窗口。</summary>
    private Window? Shell => Window.GetWindow(this);

    // ── 组管理（右侧「⋯」与组列表右键菜单共用同一份菜单）────────────────
    private void GroupMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || GroupList.ContextMenu is not { } menu)
            return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        var name = InputWindow.Show(Shell, L10n.Tr("L.Title_NewGroup"), L10n.Tr("L.Prompt_GroupName"));
        if (name is not null)
            _vm.NewGroupCommand.Execute(name);
    }

    private void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        var current = _vm.Store.Current?.Name;
        if (current is null)
            return;
        var name = InputWindow.Show(Shell, L10n.Tr("L.Title_RenameGroup"), L10n.Tr("L.Prompt_NewGroupName"), current);
        if (name is not null)
            _vm.RenameGroupCommand.Execute(name);
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = _vm.Store.Current;
        if (group is null)
            return;
        if (Notify.Confirm(Shell, L10n.Tr("L.Title_Confirm"),
                L10n.TrF("L.Msg_ConfirmDeleteGroup", group.Name, group.Members.Count), destructive: true))
            _vm.DeleteGroupCommand.Execute(null);
    }

    private void ViewMembers_Click(object sender, RoutedEventArgs e)
    {
        var group = _vm.Store.Current;
        if (group is null)
        {
            Notify.Info(Shell, L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_SelectGroupFirst"));
            return;
        }
        new GroupMembersWindow(group, _vm.GetTreeDisplayStructure(),
            beforeChange: () => _vm.Store.Snapshot(),
            onChanged: () =>
            {
                _vm.Store.MarkDirtyFromUi();
                _vm.RefreshGroupsList();
                _vm.RefreshTree();
            })
        { Owner = Shell }.ShowDialog();
    }

    /// <summary>规则编辑器归壳管（它同时服务「规则预设」窗口，且全窗口只能开一个），
    /// 所以这里走 VM 上注入的委托，而不是把页面转成 MainWindow 再调方法。</summary>
    private void Rules_Click(object sender, RoutedEventArgs e) => _vm.RuleEditorOpener?.Invoke(null);

    /// <summary>双击某一行 = 看它的成员。落在空白处不算——<see cref="ViewMembers_Click"/> 取的是
    /// <c>Store.Current</c>（当前选中的组）而不是被双击的那一行，所以不校验命中行的话，
    /// 双击列表空白区也会弹出当前组的成员窗口，看起来像"列表里有看不见的行"。</summary>
    private void Groups_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (GroupList.ContainerFromElement(e.OriginalSource as DependencyObject) is not ListBoxItem)
            return;
        ViewMembers_Click(sender, e);
    }

    // ── 日志区（默认折叠成一行摘要）────────────────────────────────────
    private void LogToggle_Click(object sender, RoutedEventArgs e)
    {
        _vm.IsLogExpanded = !_vm.IsLogExpanded;
        if (!_vm.IsLogExpanded || _vm.LogLines.Count == 0)
            return;
        // 等展开后的列表完成布局再滚，否则虚拟化面板还没有可视项
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            new Action(() => LogList.ScrollIntoView(_vm.LogLines[^1])));
    }

    private void LogCopy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_vm.LogTextAll);
        Notify.Info(Shell, L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_CopiedToClipboard"));
    }

    private void LogClear_Click(object sender, RoutedEventArgs e) => _vm.ClearLog();

    /// <summary>拖拽条在日志上方，向下拖 = 让日志变矮。</summary>
    private void LogResizeThumb_DragDelta(object sender, DragDeltaEventArgs e) =>
        _vm.LogPanelHeight = Math.Clamp(_vm.LogPanelHeight - e.VerticalChange, 80, 600);
}
