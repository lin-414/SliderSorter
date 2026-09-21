using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using BSGroupGenerator.Wpf.Services;
using BSGroupGenerator.Wpf.ViewModels;
using Microsoft.Win32;

namespace BSGroupGenerator.Wpf.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private RuleGroupWindow? _ruleWindow;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        // 视图注入：所有提示/确认都走自绘的 Notify，不再用系统 MessageBox
        //（后者在暗色主题下弹白框、图标是 Vista 位图、按钮语言跟随操作系统）
        _vm.NotifyHandler = (title, message, warning) =>
        {
            Notify.Show(this, title, message, warning ? NotifyKind.Warning : NotifyKind.Info);
            return false;
        };
        _vm.ConfirmHandler = (title, message) => Notify.Confirm(this, title, message);
        _vm.SuppressibleConfirmHandler = (title, message, suppressKey) =>
            Notify.Show(this, title, message, NotifyKind.Question, NotifyButtons.OkCancel,
                settings: _vm.Settings, suppressKey: suppressKey) == NotifyResult.Primary;
        _vm.FolderPicker = description => PickFolder(description);
        _vm.FilePicker = _ => PickImportFile();
        _vm.NewModsDetected += request => Dispatcher.Invoke(() => ShowNewMods(request));
        _vm.ConflictsRequested += request => Dispatcher.Invoke(() => ShowConflicts(request));
        _vm.SaveCompleted += (dir, bsAppDir) => Dispatcher.Invoke(() => ShowSaveSuccess(dir, bsAppDir));
        // 更新提示不在这里订阅：CheckForUpdatesAsync 内部用 SuppressibleConfirmHandler 弹确认框并打开下载页

        // 日志列表是虚拟化的 ListBox，新行只在展开时才需要滚到底
        _vm.LogFlushed += () =>
        {
            if (_vm.IsLogExpanded && _vm.LogLines.Count > 0)
                LogList.ScrollIntoView(_vm.LogLines[^1]);
        };

        HookDragDrop();
        HookTreeRowSelection();
        SyncThemeChecks();
        SyncLangChecks();
        Loaded += (_, _) => _ = _vm.CheckForUpdatesAsync(reportUpToDate: false);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F1)
            {
                e.Handled = true;
                ShowHelp();
            }
        };
    }

    /// <summary>右键点在某一行上时把那一行也选中——右键菜单就是在这一行上弹的，
    /// 用户预期它同时被选中。实现放在视图侧，理由见 TreeSelection。</summary>
    private void HookTreeRowSelection() =>
        OutfitTree.ContextMenuOpening += (_, e) =>
            TreeSelection.SelectRow(e.OriginalSource as DependencyObject, OutfitTree);

    /// <summary>把分组 XML 拖到窗口任意位置即可导入（等价于「导入现有组文件…」）。</summary>
    private void HookDragDrop()
    {
        AllowDrop = true;
        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effects = DragDropEffects.Copy;
        };
        Drop += (_, e) =>
        {
            var files = e.Data?.GetData(DataFormats.FileDrop) as string[];
            if (files is not null && files.Length > 0)
                _vm.ImportFiles(files.Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)));
        };
    }

    // ── 界面主题（菜单栏顶层「界面主题」）──────────────────────────────────
    private void SyncThemeChecks()
    {
        MiThemeBoutique.IsChecked = ThemeManager.Current == ThemeManager.Boutique;
        MiThemeLight.IsChecked = ThemeManager.Current == ThemeManager.Light;
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string theme } || theme == ThemeManager.Current)
            return;
        ThemeManager.Apply(theme);
        _vm.Settings.UiTheme = theme;
        _vm.Settings.Save();
        SyncThemeChecks();
    }

    // ── 界面语言（菜单栏顶层「语言」）────────────────────────────────────
    private void SyncLangChecks()
    {
        MiLangZh.IsChecked = L10n.Current == L10n.Zh;
        MiLangEn.IsChecked = L10n.Current == L10n.En;
        MiLangRu.IsChecked = L10n.Current == L10n.Ru;
        MiLangFr.IsChecked = L10n.Current == L10n.Fr;
    }

    private void Lang_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string lang } || lang == L10n.Current)
            return;
        L10n.Apply(lang);
        _vm.Settings.UiLanguage = lang;
        _vm.Settings.Save();
        SyncLangChecks();
        _vm.OnLanguageChanged();
    }

    private string? PickFolder(string description)
    {
        var dialog = new OpenFolderDialog { Title = description };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private string? PickImportFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = L10n.Tr("L.Pick_ImportGroups"),
            Filter = L10n.Tr("L.Pick_ImportGroupsFilter"),
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void ShowSaveSuccess(string dir, string? bsAppDir)
    {
        var memberCount = _vm.Store.Groups.Sum(g => g.Members.Count);
        var examples = string.Join("、", _vm.Store.Groups.Take(3)
            .Select(g => BSGroupGenerator.Core.SliderGroupFile.FileNameForGroup(g.Name)));
        new SaveSuccessWindow(_vm.Store.Count, dir, examples, memberCount,
            _vm.ResolveTargetDescription(), bsAppDir,
            customNote: _vm.Settings.WriteMode == Core.WriteMode.Custom).ShowDialog();
    }

    private void ShowNewMods(NewModsRequest request)
    {
        var window = new NewModsWindow(request) { Owner = this };
        window.ShowDialog();
    }

    private void ShowConflicts(ConflictRequest request) =>
        new OutputConflictWindow(request) { Owner = this }.ShowDialog();

    private void Conflicts_Click(object sender, MouseButtonEventArgs e) => _vm.OpenConflictsCommand.Execute(null);

    private void ShowHelp() => new HelpWindow { Owner = this }.ShowDialog();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Diagnostics_Click(object sender, RoutedEventArgs e) =>
        new DiagnosticsWindow(_vm.BuildDiagnostics()) { Owner = this }.ShowDialog();

    private void Help_Click(object sender, RoutedEventArgs e) => ShowHelp();

    private void About_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    private void CheckUpdate_Click(object sender, RoutedEventArgs e) =>
        _ = _vm.CheckForUpdatesAsync(reportUpToDate: true);

    private void AddMo2_Click(object sender, RoutedEventArgs e)
    {
        var dir = PickFolder(L10n.Tr("L.Pick_Mo2Dir"));
        if (dir is not null)
            _vm.AddMo2Directory(dir);
    }

    private void BrowseBodySlide_Click(object sender, RoutedEventArgs e)
    {
        var dir = PickFolder(L10n.Tr("L.Pick_BodySlideDir"));
        if (dir is not null)
            _vm.UseBodySlideDirectory(dir);
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var file = PickImportFile();
        if (file is not null)
            _vm.ImportFiles([file]);
    }

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
        var name = InputWindow.Show(this, L10n.Tr("L.Title_NewGroup"), L10n.Tr("L.Prompt_GroupName"));
        if (name is not null)
            _vm.NewGroupCommand.Execute(name);
    }

    private void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        var current = _vm.Store.Current?.Name;
        if (current is null)
            return;
        var name = InputWindow.Show(this, L10n.Tr("L.Title_RenameGroup"), L10n.Tr("L.Prompt_NewGroupName"), current);
        if (name is not null)
            _vm.RenameGroupCommand.Execute(name);
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = _vm.Store.Current;
        if (group is null)
            return;
        if (Notify.Confirm(this, L10n.Tr("L.Title_Confirm"),
                L10n.TrF("L.Msg_ConfirmDeleteGroup", group.Name, group.Members.Count), destructive: true))
            _vm.DeleteGroupCommand.Execute(null);
    }

    private void ViewMembers_Click(object sender, RoutedEventArgs e)
    {
        var group = _vm.Store.Current;
        if (group is null)
        {
            Notify.Info(this, L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_SelectGroupFirst"));
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
        { Owner = this }.ShowDialog();
    }

    private void Rules_Click(object sender, RoutedEventArgs e) => OpenRuleEditor();

    private void RulePresets_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RulePresetsWindow(_vm.Settings.RulePresets, OpenRuleEditor) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Changed)
        {
            _vm.Settings.Save();
            _vm.Log(L10n.TrF("L.Log_PresetsUpdated", _vm.Settings.RulePresets.Count));
        }
    }

    /// <summary>打开规则归组编辑器（非模态；规则预设窗口的「新建预设」「编辑所选」也走这里）。
    /// 返回编辑器是否已就绪——规则预设窗口据此决定要不要关掉自己，没就绪就得留着，免得点了没反应。
    /// <paramref name="presetToEdit"/> 非空时把该预设载入各控件，同名保存即覆盖。</summary>
    private bool OpenRuleEditor(Core.RulePreset? presetToEdit = null)
    {
        if (_ruleWindow is { } existing && existing.IsLoaded)
        {
            if (presetToEdit is not null)
                existing.LoadPreset(presetToEdit);
            existing.Activate(); // 已打开时不再叠加新窗口（两个窗口叠在一起会互相干扰点击）
            return true;
        }
        if (_vm.Store.Count == 0)
        {
            Notify.Info(this, L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_NeedGroupFirst"));
            return false;
        }
        if (_vm.Scan is null || _vm.Scan.Outfits.Count == 0)
        {
            Notify.Info(this, L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_NoOutfits"));
            return false;
        }
        var ownerMap = _vm.OwnerByOutfit();
        RuleGroupWindow? window = null;
        window = new RuleGroupWindow(_vm.Store.Groups, presetToEdit?.GroupName ?? _vm.Store.Current?.Name,
            _vm.GetTreeDisplayStructure(),
            (modInclude, outfitInclude, outfitExclude, unassignedOnly) =>
                _vm.RuleMatchPreview(ownerMap, modInclude, outfitInclude, outfitExclude, unassignedOnly),
            onApply: () =>
            {
                if (window is null)
                    return;
                var applied = _vm.RuleApply(window.GroupName, window.Add, window.ModInclude,
                    window.OutfitInclude, window.OutfitExclude, window.UnassignedOnly);
                if (applied < 0)
                    return;
                _vm.RefreshGroupsList();
                _vm.RefreshTree();
                window.UpdatePreview();
            },
            onSavePreset: preset => _vm.SaveRulePreset(preset),
            presetToEdit: presetToEdit)
        { Owner = this };
        _ruleWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_ruleWindow, window)) _ruleWindow = null; };
        window.Show();
        return true;
    }

    private void Groups_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        ViewMembers_Click(sender, e);

    private void Output_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var dir = _vm.ResolveOutputDirectory();
        if (dir is null)
            Notify.Info(this, L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_OutputDirUndetermined"));
        else
            MainViewModel.OpenDirectory(dir);
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
        Notify.Info(this, L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_CopiedToClipboard"));
    }

    private void LogClear_Click(object sender, RoutedEventArgs e) => _vm.ClearLog();

    /// <summary>拖拽条在日志上方，向下拖 = 让日志变矮。</summary>
    private void LogResizeThumb_DragDelta(object sender, DragDeltaEventArgs e) =>
        _vm.LogPanelHeight = Math.Clamp(_vm.LogPanelHeight - e.VerticalChange, 80, 600);

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.Store.Dirty)
        {
            // 三分支（保存并退出 / 不保存退出 / 留下）需要三值结果，不能压成"确定/取消"
            var choice = Notify.Show(this, L10n.Tr("L.Title_UnsavedChanges"), L10n.Tr("L.Msg_UnsavedOnExit"),
                NotifyKind.Warning, NotifyButtons.YesNoCancel);
            if (choice == NotifyResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            if (choice == NotifyResult.Primary && !_vm.TrySaveGroups(showSuccessDialog: false))
            {
                e.Cancel = true;
                return;
            }
        }
        _vm.OnViewClosed();
        base.OnClosing(e);
    }
}
