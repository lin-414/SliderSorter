using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SliderSorter.Wpf.Services;
using SliderSorter.Wpf.ViewModels;
using Microsoft.Win32;

namespace SliderSorter.Wpf.Views;

/// <summary>壳：菜单 + 标签条 + 状态栏 + 扫描遮罩。三页的内容与布局各自在 Views/Pages 下。</summary>
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
        // 目录/文件框也在这一层：设置页里的「选择实例目录」「浏览…」复用它们，
        // owner 才是真正有窗口身份的壳。
        _vm.FolderPicker = description => PickFolder(description);
        _vm.FilePicker = _ => PickImportFile();
        // 规则归组编辑器全窗口只开一个，却有两个入口（分组页的「规则归组」+「规则预设」窗口），
        // 所以它归壳管，页面走 VM 上这个注入委托——页面不该认识 MainWindow 这个类型。
        _vm.RuleEditorOpener = OpenRuleEditor;
        _vm.NewModsDetected += request => Dispatcher.Invoke(() => ShowNewMods(request));
        _vm.SaveCompleted += (dir, bsAppDir) => Dispatcher.Invoke(() => ShowSaveSuccess(dir, bsAppDir));
        // 更新提示不在这里订阅：CheckForUpdatesAsync 内部用 SuppressibleConfirmHandler 弹确认框并打开下载页
        // 日志区的 LogFlushed 订阅在分组页上：LogList 是那一页的控件。

        HookDragDrop();
        Loaded += (_, _) => _ = _vm.CheckForUpdatesAsync(reportUpToDate: false);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F1)
            {
                e.Handled = true;
                ShowManual();
            }
        };
    }

    /// <summary>F1 = 跳到设置页并把使用说明摊开。以前是弹 HelpWindow；说明内嵌进设置页之后，
    /// 只切页不展开的话，按 F1 看到的是又一个箭头，等于没响应。</summary>
    private void ShowManual()
    {
        _vm.SelectedTab = MainViewModel.TabSettings;
        _vm.IsManualExpanded = true;
    }

    /// <summary>把分组 XML 拖到窗口任意位置即可导入（等价于「导入现有组文件…」）。
    /// 挂在壳上而不是页上：拖到哪个页都该能导入。</summary>
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
            .Select(g => SliderSorter.Core.SliderGroupFile.FileNameForGroup(g.Name)));
        new SaveSuccessWindow(_vm.Store.Count, dir, examples, memberCount,
            _vm.ResolveTargetDescription(), bsAppDir,
            customNote: _vm.Settings.WriteMode == Core.WriteMode.Custom).ShowDialog();
    }

    private void ShowNewMods(NewModsRequest request)
    {
        var window = new NewModsWindow(request) { Owner = this };
        window.ShowDialog();
    }

    private void Conflicts_Click(object sender, MouseButtonEventArgs e) => _vm.OpenConflictsCommand.Execute(null);

    /// <summary>打开规则归组编辑器（非模态；规则预设页的「新建预设」「编辑所选」经
    /// <see cref="MainViewModel.RuleEditorOpener"/> 走这里）。
    /// 返回编辑器是否已就绪——调用方据此决定要不要留着自己是合理的。
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

    private void Output_Click(object sender, MouseButtonEventArgs e)
    {
        var dir = _vm.ResolveOutputDirectory();
        if (dir is null)
            Notify.Info(this, L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_OutputDirUndetermined"));
        else
            MainViewModel.OpenDirectory(dir);
    }

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
