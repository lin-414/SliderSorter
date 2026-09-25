using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SliderSorter.Wpf.Services;
using SliderSorter.Wpf.ViewModels;

namespace SliderSorter.Wpf.Views.Pages;

/// <summary>设置页：单列五节——工作环境、输出设置、外观、维护、帮助。VM 由壳通过 DataContext 继承下来。</summary>
public partial class SettingsPage : UserControl
{
    private MainViewModel _vm = null!;

    /// <summary>说明正文与诊断报告都是代码拼的字符串，换语言不会自己变，所以各记一份"是按哪种语言建的"。
    /// 说明正文还带字号（FlowDocument 不吃 DynamicResource，见 HelpDocument.Build），字号档也要一起记。</summary>
    private string? _manualLang;
    private string? _diagLang;
    private int _manualFontScale = FontScaleManager.CurrentPercent;

    public SettingsPage()
    {
        InitializeComponent();
        // VM 由壳经 DataContext 继承下来，构造期还没有（FrameworkElement 只有这个事件，
        // 没有可重写的 OnDataContextChanged）。
        DataContextChanged += (_, _) => AdoptViewModel();
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
                // 只在**不是用户自己点的**时候滚：F1 从别的页过来时，展开的面板在视口之外，
                // 不滚过去等于没响应；但用户亲手点开折叠条时，那颗控件本来就在眼前，
                // 再替他把页面滚一下是多余的跳动。
                // ToggleButton 的双向绑定让两条入口汇到同一个 PropertyChanged，只能靠标志位区分。
                // 排到 Loaded 再滚——Visibility 刚变时这一节还没量出高度，此刻滚等于没滚。
                if (!_manualToggledByUser)
                    Dispatcher.BeginInvoke(() => ManualHost.BringIntoView(), DispatcherPriority.Loaded);
                _manualToggledByUser = false;
                break;
            case nameof(MainViewModel.IsDiagnosticsExpanded) when _vm.IsDiagnosticsExpanded && _diagLang != L10n.Current:
                RefreshDiagnostics();
                break;
            // 字号换档时说明正文若正开着就重建一次：FontSize 是构建期写死的，资源键帮不了它。
            // 收起状态不用管——下次展开时 ShowManual 的缓存键对不上，自然会重造。
            case nameof(MainViewModel.SelectedFontScaleOption) when _vm.IsManualExpanded:
                ShowManual();
                break;
        }
    }

    /// <summary>本次「使用说明」的展开是否由用户点折叠条发起（决定要不要自动滚到面板）。
    ///
    /// 为什么不用 ToggleButton.Checked 事件置位：那个事件的触发时机与 TwoWay 绑定回写 VM
    /// 的先后**没有保证**。若回写先到，PropertyChanged 先跑、标志还是 false，就会白滚一次。
    /// 改在 ToggleButton 的鼠标/键盘入口上记录意图，一定早于绑定回写，时序上不可能反过来。
    /// 用 PreviewMouseLeftButtonDown + PreviewKeyDown 而不是 Click：Click 同样晚于绑定回写。</summary>
    private bool _manualToggledByUser;

    private void ManualToggle_UserInput(object sender, System.Windows.Input.InputEventArgs e)
    {
        // 只在"即将展开"时记录：收起时不需要滚动，标志留给下一次展开判断
        if (ManualToggle.IsChecked != true)
            _manualToggledByUser = true;
    }

    // 目录选择走 VM 上注入的 FolderPicker（宿主是壳窗口）：这里再 new 一个 OpenFolderDialog
    // 就会出现两套弹框、两套 owner，且页面没有窗口身份时第二套只能无主弹出。
    private void SelectInstanceDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = _vm.FolderPicker?.Invoke(L10n.Tr("L.Pick_Mo2Dir"));
        if (dir is not null)
            _vm.SelectInstanceDirectory(dir);
    }

    private void BrowseBodySlide_Click(object sender, RoutedEventArgs e)
    {
        var dir = _vm.FolderPicker?.Invoke(L10n.Tr("L.Pick_BodySlideDir"));
        if (dir is not null)
            _vm.UseBodySlideDirectory(dir);
    }

    // ── 使用说明 / 诊断信息：展开态由 DisclosureToggle（ToggleButton）双向绑到 VM，
    //    这里不再有 Click 处理函数。正文由上面的 OnViewModelChanged 备好。 ──

    /// <summary>正文只在展开时构建（60+ 段，收起时白建），换过语言或字号档后重建一次。</summary>
    private void ShowManual()
    {
        if (ManualViewer.Document is not null && _manualLang == L10n.Current
            && _manualFontScale == FontScaleManager.CurrentPercent)
            return;
        ManualViewer.Document = HelpDocument.Build();
        _manualLang = L10n.Current;
        _manualFontScale = FontScaleManager.CurrentPercent;
    }

    private void DiagnosticsRefresh_Click(object sender, RoutedEventArgs e) => RefreshDiagnostics();

    private void RefreshDiagnostics()
    {
        ReportBox.Text = _vm.BuildDiagnostics();
        _diagLang = L10n.Current;
    }

    private void DiagnosticsCopy_Click(object sender, RoutedEventArgs e)
    {
        Notify.CopyText(Window.GetWindow(this), L10n.Tr("L.Title_Tip"), ReportBox.Text);
    }
}
