using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BSGroupGenerator.Wpf.Services;
using BSGroupGenerator.Wpf.ViewModels;

namespace BSGroupGenerator.Wpf.Views.Pages;

/// <summary>设置页：单列四节——工作环境、输出设置、外观、帮助与关于。VM 由壳通过 DataContext 继承下来。</summary>
public partial class SettingsPage : UserControl
{
    private const string RepoUrl = "https://github.com/lin-414/BSGroupGenerator";

    private MainViewModel _vm = null!;

    /// <summary>说明正文与诊断报告都是代码拼的字符串，换语言不会自己变，所以各记一份"是按哪种语言建的"。</summary>
    private string? _manualLang;
    private string? _diagLang;

    public SettingsPage()
    {
        InitializeComponent();
        // VM 由壳经 DataContext 继承下来，构造期还没有（FrameworkElement 只有这个事件，
        // 没有可重写的 OnDataContextChanged）。
        DataContextChanged += (_, _) => AdoptViewModel();

        var version = typeof(SettingsPage).Assembly.GetName().Version;
        VersionLabel.Text = "v" + (version is null ? "?" : version.ToString(3));
    }

    private void AdoptViewModel()
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelChanged;
        _vm = (MainViewModel)DataContext;
        _vm.PropertyChanged += OnViewModelChanged;
    }

    /// <summary>展开态挂在 VM 上，所以"把说明展开"这件事也能从壳那边发起（F1）。
    /// 正文构建挂在这里而不是各个点击处，两条入口才共用同一条路径。</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsManualExpanded) when _vm.IsManualExpanded:
                ShowManual();
                // 单列之后帮助与关于落在长列底部：F1 从别的页过来时，展开的面板其实在视口之外。
                // 排到 Loaded 再滚——Visibility 刚变时这一节还没量出高度，此刻滚等于没滚。
                Dispatcher.BeginInvoke(() => ManualHost.BringIntoView(), DispatcherPriority.Loaded);
                break;
            case nameof(MainViewModel.IsDiagnosticsExpanded) when _vm.IsDiagnosticsExpanded && _diagLang != L10n.Current:
                RefreshDiagnostics();
                break;
        }
    }

    // 目录选择走 VM 上注入的 FolderPicker（宿主是壳窗口）：这里再 new 一个 OpenFolderDialog
    // 就会出现两套弹框、两套 owner，且页面没有窗口身份时第二套只能无主弹出。
    private void AddMo2_Click(object sender, RoutedEventArgs e)
    {
        var dir = _vm.FolderPicker?.Invoke(L10n.Tr("L.Pick_Mo2Dir"));
        if (dir is not null)
            _vm.AddMo2Directory(dir);
    }

    private void BrowseBodySlide_Click(object sender, RoutedEventArgs e)
    {
        var dir = _vm.FolderPicker?.Invoke(L10n.Tr("L.Pick_BodySlideDir"));
        if (dir is not null)
            _vm.UseBodySlideDirectory(dir);
    }

    // ── 使用说明 / 诊断信息：按钮只翻展开态，正文由上面的 OnViewModelChanged 备好 ──
    private void ManualToggle_Click(object sender, RoutedEventArgs e) =>
        _vm.IsManualExpanded = !_vm.IsManualExpanded;

    /// <summary>正文只在展开时构建（60+ 段，收起时白建），换过语言后重建一次。</summary>
    private void ShowManual()
    {
        if (ManualViewer.Document is not null && _manualLang == L10n.Current)
            return;
        ManualViewer.Document = HelpDocument.Build();
        _manualLang = L10n.Current;
    }

    private void DiagnosticsToggle_Click(object sender, RoutedEventArgs e) =>
        _vm.IsDiagnosticsExpanded = !_vm.IsDiagnosticsExpanded;

    private void DiagnosticsRefresh_Click(object sender, RoutedEventArgs e) => RefreshDiagnostics();

    private void RefreshDiagnostics()
    {
        ReportBox.Text = _vm.BuildDiagnostics();
        _diagLang = L10n.Current;
    }

    private void DiagnosticsCopy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(ReportBox.Text);
        Notify.Info(Window.GetWindow(this), L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_CopiedToClipboard"));
    }

    private void Repo_Click(object sender, RoutedEventArgs e) => MainViewModel.OpenUrl(RepoUrl);
}
