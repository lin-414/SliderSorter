using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Threading;
using System.Xml;
using SliderSorter.Core;
using SliderSorter.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SliderSorter.Wpf.ViewModels;

/// <summary>状态栏写出模式下拉项。</summary>
public sealed record WriteModeItem(WriteMode Mode, string Label);

/// <summary>设置页「界面主题 / 界面语言」下拉项：Value 是 ThemeManager/L10n 认的键，Label 随语言变。
/// 语言那四项（中文 / English / Русский / Français）的 Label 恒为各语言自己的写法——
/// 把俄语界面翻坏了时，那是唯一还能认出「这里能切回中文」的线索，所以它们不进语言文件。
/// 「跟随系统」是唯一的例外：它描述的是行为不是语言名，法/俄用户该看到自己的说法，走 L.Settings_LanguageSystem。</summary>
public sealed record OptionItem(string Value, string Label);

/// <summary>内联提示条的级别。与日志级别分开：日志是"发生了什么"的流水账，
/// 提示条是"你现在该做什么"的一句话——同一件事可能只进提示条不进日志，或者反过来。</summary>
public enum BannerKind
{
    /// <summary>一般提示（"先勾选要搬运的服装"）。中性底色。</summary>
    Info,

    /// <summary>警告（"配置指向的目录不存在"）。琥珀底色。</summary>
    Warn,

    /// <summary>错误（"写入失败"）。红底色。</summary>
    Danger,
}

/// <summary>内联提示条。取代了一批"说了等于没说、但刚好挡在窗口正中间"的模态弹窗——
/// 那些提示（先选组、先勾选、没有可撤销的）都是**非破坏性**的，用户看完要做的
/// 永远是"回到刚才那块控件继续操作"。模态框强制先点确定、还会挡住刚看的位置，
/// 是这个流程里最没必要的打断。破坏性确认（删除组、清理失效服装）仍走模态框。</summary>
public sealed record BannerMessage(BannerKind Kind, string Text);

/// <summary>组列表项。CountText 单独成串：组名右对齐的数字用等宽字体单独一列，
/// 长组名截断时不会把计数一起吃掉（原来拼在 Display 里，截断点落在数字上就看不见成员数了）。</summary>
public sealed record GroupItem(string Name, int Count)
{
    public string Display => Name;
    public string CountText => Count.ToString();
}

public partial class MainViewModel : ObservableObject
{
    public static string AppTitle => L10n.Tr("L.App_Title");
    public const string DedicatedModName = "SliderSorter Output";

    // 共享实例：App 启动时加载并登记（AppSettings.Use）。没有 App 上下文（单测）时 Shared 惰性加载，
    // 效果与原来的各自 Load 一致，但不会出现"两处各加载一份、互相覆盖"的问题。
    public AppSettings Settings { get; } = AppSettings.Shared;
    public GroupStore Store { get; } = new();

    public List<ModEntry> Entries { get; private set; } = [];
    public List<(ModEntry Entry, string Dir)> Mods { get; private set; } = [];
    public ProjectPathResolution? Resolution { get; private set; }
    public ScanResult? Scan { get; private set; }

    private bool _scanning;
    private bool _localizing;
    private bool _scanQueued;
    private bool _closed;
    private string? _bsAppDir;

    // 视图交互统一走「视图注入委托」这一套：下面的 NotifyHandler，以及 Save.cs 里的
    // ConfirmHandler / FolderPicker / FilePicker，由 MainWindow 构造时赋值。
    // 曾并存一组 Notify / Confirm / RequestClose 事件，从未被触发（CS0067 可证），已删除，
    // 免得两套机制并存时看错哪套在生效。
    public Func<string, string, bool, bool>? NotifyHandler { get; set; } // 视图注入：MessageBox 包装 (title, message, warning)

    public void NotifyUser(string title, string message, bool warning = false) =>
        NotifyHandler?.Invoke(title, message, warning);

    // ── 内联提示条（非破坏性提示的出口）──────────────────────────────
    // 分工：破坏性/需要用户抉择的一律走 NotifyUser 的模态框（删除组、清理失效服装、
    // 未保存就退出）；"你少做了一步"这类纯提示走 ShowBanner，就近显示在触发它的控件旁边。
    [ObservableProperty] private BannerMessage? _banner;

    /// <summary>提示条的可见性。用「有没有内容」而不是另一个 bool：两者分开会出现
    /// "bool 已复位、内容还在"或反过来的中间态，那正是闪一下又冒出来的成因。</summary>
    public bool HasBanner => Banner is not null;

    partial void OnBannerChanged(BannerMessage? value) => OnPropertyChanged(nameof(HasBanner));

    /// <summary>显示一条内联提示。同一时刻只留一条：连续两次失败（例如连点两下"加入组"）
    /// 叠成两条会顶掉布局，而两条说的是同一件事。</summary>
    public void ShowBanner(BannerKind kind, string text) => Banner = new BannerMessage(kind, text);

    [RelayCommand]
    private void DismissBanner() => Banner = null;

    /// <summary>视图注入：打开规则归组编辑器（非模态，同一个编辑器不叠加第二个窗口）。
    /// 返回是否已就绪——「规则预设」窗口据此决定要不要关掉自己。
    /// 走注入而不是让分组页去转强类型找壳：壳换成了三页之后，页面不该认识 MainWindow。</summary>
    public Func<RulePreset?, bool>? RuleEditorOpener { get; set; }

    // ── 标签页（壳的 TabControl 选中项）────────────────────────────────
    // 状态放在 VM 上：状态栏的冲突计数、「工具 → 输出冲突…」、F1 都要能主动切页。
    // 用事件转给壳的话，壳还得反过来告诉 VM 当前是哪页，绕一圈仍是同一份状态。
    public const int TabGenerate = 0;
    public const int TabConflicts = 1;
    public const int TabRulePresets = 2;
    public const int TabSettings = 3;

    [ObservableProperty] private int _selectedTab = TabGenerate;

    [RelayCommand]
    private void OpenGenerate() => SelectedTab = TabGenerate;

    [RelayCommand]
    private void OpenRulePresets() => SelectedTab = TabRulePresets;

    [RelayCommand]
    private void OpenSettings() => SelectedTab = TabSettings;

    // ── 绑定状态 ──
    [ObservableProperty] private string _windowTitle = AppTitle;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private ObservableCollection<Mo2Instance> _instances = [];
    [ObservableProperty] private Mo2Instance? _selectedInstance;
    [ObservableProperty] private ObservableCollection<string> _profiles = [];
    [ObservableProperty] private string? _selectedProfile;
    [ObservableProperty] private ObservableCollection<BodySlideCandidate> _bodySlideDirs = [];
    [ObservableProperty] private BodySlideCandidate? _selectedBodySlide;
    [ObservableProperty] private ObservableCollection<WriteModeItem> _writeModes = [];

    // ── 设置页：外观与帮助 ──
    [ObservableProperty] private ObservableCollection<OptionItem> _themeOptions = [];
    [ObservableProperty] private OptionItem? _selectedThemeOption;
    [ObservableProperty] private ObservableCollection<OptionItem> _languageOptions = [];
    [ObservableProperty] private OptionItem? _selectedLanguageOption;

    /// <summary>「检查更新」旁边的内联结果。空串 = 这次运行还没查过。</summary>
    [ObservableProperty] private string _updateStatusText = "";

    /// <summary>使用说明 / 诊断信息两块的展开态。正文有 60+ 段，收起时干脆不建（见 SettingsPage）。</summary>
    [ObservableProperty] private bool _isManualExpanded;
    [ObservableProperty] private bool _isDiagnosticsExpanded;
    [ObservableProperty] private WriteModeItem? _selectedWriteMode;
    [ObservableProperty] private string _infoLine = L10n.Tr("L.Vm_NotScanned");
    [ObservableProperty] private string _statusCounts = L10n.Tr("L.Vm_NotScanned");
    [ObservableProperty] private string _outputText = L10n.Tr("L.Vm_OutputNone");
    /// <summary>组列表。<b>永远是同一个实例，只就地增删，不换集合</b>——
    /// 见 <see cref="GroupsView"/>：ListBox 绑的是那个构造期一次性建立的投影，
    /// 一旦把本属性指向新集合，投影就永远停在旧实例上，列表从此一片空白
    /// （Store 里明明有组，双击空白还能弹出当前组，看着像"组丢了"）。
    /// 因此这里刻意不给 setter：换集合这件事在编译期就被挡住。</summary>
    public ObservableCollection<GroupItem> Groups { get; } = [];

    [ObservableProperty] private int _selectedGroupIndex = -1;
    [ObservableProperty] private string _groupInfo = L10n.Tr("L.Vm_NoGroupSelected");

    /// <summary>组列表过滤词。与左树的时装/模组过滤互不影响——那两个筛的是「可加入的服装」，
    /// 这个筛的是「已有的组」，组多到几十个时才有用。</summary>
    [ObservableProperty] private string _groupFilterText = "";

    /// <summary>过滤后的组列表视图。<see cref="Groups"/> 始终是完整列表（SelectedGroupIndex 的语义、
    /// 保存顺序、右键菜单都依赖它），过滤只影响这里绑给 ListBox 的投影。
    /// <para>
    /// ⚠️ 本属性**没有变更通知**，所以它绑定的那个集合实例必须长命——与 <see cref="Groups"/> 是同生共死的关系。
    /// </para></summary>
    public System.ComponentModel.ICollectionView GroupsView { get; }

    partial void OnGroupFilterTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsGroupFilterActive));
        GroupsView.Refresh();
    }

    /// <summary>过滤是否处于激活状态（过滤框非空）——空结果提示据此区分「一个组都没有」和「都被滤掉了」。</summary>
    public bool IsGroupFilterActive => GroupFilterText.Trim().Length > 0;

    /// <summary>「撤销」按钮的可用性。Store.CanUndo 的变化没有通知源，
    /// 每次分组操作后由 RefreshGroupsList / RefreshTransferState 统一推一次。</summary>
    [ObservableProperty] private bool _canUndo;

    // ── 扫描状态指示器 ──────────────────────────────────────────────
    /// <summary>上一次扫描完成的时刻。null = 本次运行还没成功扫描过。
    /// 扫描是这两份成果的共同前置，而"上次扫描是什么时候"原先只藏在折叠日志里——
    /// 用户看到一份清单无从判断它是不是已经过期（装了新模组之后尤其危险）。</summary>
    [ObservableProperty] private DateTime? _lastScanAt;

    /// <summary>扫描状态一行摘要（"上次扫描：14:32 · 模组 120 · 服装 840"）。
    /// 时刻是绝对值而不是"3 分钟前"：这句只在扫描收尾那一刻算一次，没有计时器再去重算，
    /// 相对时长会一直冻在"0 秒前"——一个永远不动的读数会被读成这栏坏了。绝对时刻不随时间走样。
    /// 未扫描时给引导文案，而不是留空——空白同样会被读成"这栏坏了"。
    /// 注意别和 <see cref="ScanStatusText"/> 混：那个是扫描中遮罩上的进度文字。</summary>
    public string ScanSummaryText => LastScanAt is null
        ? L10n.Tr("L.Scan_StatusNever")
        : L10n.TrF("L.Scan_StatusAt", LastScanAt.Value.ToShortTimeString(), StatusCountsShort);

    /// <summary>扫描结果是否可能已过期（超过 10 分钟）。只用来调颜色，不做任何拦截——
    /// 判定"真的过期了"需要重扫，而那正是用户点「重新扫描」要做的事，不需要工具替他下结论。</summary>
    public bool IsScanStale => LastScanAt is not null && DateTime.Now - LastScanAt.Value > StaleAfter;

    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    private DispatcherTimer? _staleTimer;

    partial void OnLastScanAtChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(ScanSummaryText));
        OnPropertyChanged(nameof(IsScanStale));
        ArmStaleWarning(value);
    }

    /// <summary>到过期那一刻补发一次 <see cref="IsScanStale"/> 通知，让警告色真的亮起来。
    /// 这条线只有一次 false→true 的翻转，所以不挂个每秒重绘的常转计时器，而是一次性定时器到点停表。
    /// 上面 <see cref="OnLastScanAtChanged"/> 里那次通知顺手覆盖了"设进来时就已经过期"的情况。</summary>
    private void ArmStaleWarning(DateTime? scannedAt)
    {
        _staleTimer?.Stop();
        if (scannedAt is not { } at)
            return;
        var remaining = StaleAfter - (DateTime.Now - at);
        if (remaining <= TimeSpan.Zero)
            return;
        if (_staleTimer is null)
        {
            // 表只造一次，处理器也就只挂一次：重扫时反复 += 会让一次 Tick 叫醒好几个副本
            var timer = new DispatcherTimer(DispatcherPriority.Background);
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                OnPropertyChanged(nameof(IsScanStale));
            };
            _staleTimer = timer;
        }
        _staleTimer.Interval = remaining;
        _staleTimer.Start();
    }

    /// <summary>状态栏那句的简版（"模组 120 · 服装 840"），供扫描指示器复用。
    /// 不复用 StatusCounts 是因为那句还带"已分配/未分配"，对"上次扫描"这个语境是噪音。</summary>
    private string StatusCountsShort => Scan is null
        ? L10n.Tr("L.Word_None")
        : L10n.TrF("L.Scan_CountsShort", WalkRoots().Count(n => n.Kind == NodeKind.Mod), Scan.Outfits.Count);

    /// <summary>环境是否还没配好（缺实例或缺 BodySlide 目录）。设置页据此在最上方挂引导条。
    /// 两个条件缺一不可：只选实例不选 BodySlide 仍然扫不了，反之亦然。</summary>
    public bool ShowsUnconfiguredBanner => SelectedInstance is null || SelectedBodySlide is null;

    /// <summary>引导条的内容。用 BannerMessage 复用 InlineBannerTemplate，不另做一套样式。</summary>
    public BannerMessage? UnconfiguredBanner =>
        ShowsUnconfiguredBanner ? new BannerMessage(BannerKind.Info, L10n.Tr("L.Settings_Unconfigured")) : null;

    /// <summary>「重新扫描」是否可用：至少要选中 BodySlide 目录才有东西可扫。
    /// 不给它绑 IsBusy——扫描中遮罩会盖住整页，那期间点不到它，绑了只是多一个状态。</summary>
    public bool CanRescan => _bsAppDir is not null;

    /// <summary>环境配置状态变了就通知这两个派生属性。
    /// 由 OnSelectedInstanceChanged / OnSelectedBodySlideChanged 调用——
    /// CommunityToolkit 的 partial 钩子每个属性只能有一个，所以统一走这个私有方法。</summary>
    private void RaiseConfigState()
    {
        OnPropertyChanged(nameof(ShowsUnconfiguredBanner));
        OnPropertyChanged(nameof(UnconfiguredBanner));
        OnPropertyChanged(nameof(CanRescan));
    }
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isDetecting;
    [ObservableProperty] private string _scanStatusText = "";
    [ObservableProperty] private string _scanProgressDetail = "";
    [ObservableProperty] private double _scanProgressValue;

    /// <summary>BodySlide 探测或服装扫描进行中 → 主窗口盖进度遮罩（拦截点击，避免被误认为卡死）。</summary>
    public bool IsBusy => IsDetecting || IsScanning;
    partial void OnIsDetectingChanged(bool value) => OnPropertyChanged(nameof(IsBusy));
    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(IsBusy));

    /// <summary>保存按钮文案：未保存时补「（未保存）」后缀，与底色变化构成第二通道，
    /// 色盲用户也能分辨当前有没有未落盘的改动。</summary>
    public string SaveButtonLabel => IsDirty
        ? L10n.Tr("L.Main_Save") + L10n.Tr("L.Vm_UnsavedSuffix")
        : L10n.Tr("L.Main_Save");

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(SaveButtonLabel));

    /// <summary>状态栏输出链接的悬停提示：显示完整目录（状态栏本身只放得下描述）。</summary>
    public string OutputToolTip => ResolveOutputDirectory() ?? L10n.Tr("L.Vm_OutputUndetermined");

    private readonly List<LogLine> _pendingLines = [];

    /// <summary>日志行上限。超过后丢最旧的——大整合包一次扫描可达上千行，
    /// 保留全部会一直涨内存，而用户真正要看的永远是最近这一段。</summary>
    private const int MaxLogLines = 2000;

    /// <summary>结构化日志：级别决定展开后的着色，也让折叠摘要能报出警告/错误数。</summary>
    public ObservableCollection<LogLine> LogLines { get; } = [];

    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private bool _isLogExpanded;
    [ObservableProperty] private double _logPanelHeight = 160;

    public bool HasLogIssues => WarningCount + ErrorCount > 0;
    public int LogIssueCount => WarningCount + ErrorCount;

    public string LogSummary =>
        WarningCount == 0 && ErrorCount == 0 ? L10n.Tr("L.Log_SummaryClean")
        : WarningCount > 0 && ErrorCount > 0 ? L10n.TrF("L.Log_SummaryBoth", WarningCount, ErrorCount)
        : ErrorCount > 0 ? L10n.TrF("L.Log_SummaryErrors", ErrorCount)
        : L10n.TrF("L.Log_SummaryWarnings", WarningCount);

    partial void OnWarningCountChanged(int value) => RaiseLogSummary();
    partial void OnErrorCountChanged(int value) => RaiseLogSummary();

    private void RaiseLogSummary()
    {
        OnPropertyChanged(nameof(HasLogIssues));
        OnPropertyChanged(nameof(LogIssueCount));
        OnPropertyChanged(nameof(LogSummary));
    }

    /// <summary>日志刷新的最后一步：视图据此把日志列表滚到底。</summary>
    public event Action? LogFlushed;

    public void Log(string message) => AppendLog(LogLevel.Info, message);
    public void LogWarning(string message) => AppendLog(LogLevel.Warning, message);
    public void LogError(string message) => AppendLog(LogLevel.Error, message);

    private void AppendLog(LogLevel level, string message)
    {
        if (_closed)
            return;
        _pendingLines.Add(new LogLine($"[{DateTime.Now:HH:mm:ss}] {message}", level));
        if (level == LogLevel.Warning)
            WarningCount++;
        else if (level == LogLevel.Error)
            ErrorCount++;
        if (_pendingLines.Count > MaxLogLines)
            _pendingLines.RemoveRange(0, _pendingLines.Count - MaxLogLines);
        QueueLogFlush();
    }

    /// <summary>全部日志的纯文本（复制到剪贴板用）。</summary>
    public string LogTextAll => string.Join(Environment.NewLine, LogLines.Select(l => l.Text));

    public void ClearLog()
    {
        _pendingLines.Clear();
        LogLines.Clear();
        WarningCount = 0;
        ErrorCount = 0;
        LogFlushed?.Invoke();
    }

    /// <summary>
    /// 合并同一轮的日志刷新。逐条设置会让绑定的控件反复重排**整段**文本，
    /// 代价是 O(行数²)：实测 801 行要 10.6 秒，而批量刷新只需 21 ms（差 500 倍）。
    /// 扫描结束时会按覆盖层逐层写日志（层数 = 启用模组数 + 1，大整合包可达上千行），
    /// 正是这种爆发式写入，所以把刷新排进 Dispatcher 队列：一批 Log 只更新一次列表。
    /// 日志内容本身仍逐条进缓冲区，不丢信息，只是显示时机合并到本轮 UI 更新之后。
    /// </summary>
    private void QueueLogFlush()
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            FlushLog(); // 无 Dispatcher（单测、设计器）：退化成同步刷新
            return;
        }
        if (_logFlushQueued)
            return;
        _logFlushQueued = true;
        // Background 优先级：低于 Render，等本轮布局/渲染排完后刷一次即可
        app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(FlushLog));
    }

    private void FlushLog()
    {
        _logFlushQueued = false;
        if (_closed || _pendingLines.Count == 0)
            return;
        // 一次回调里连续 Add：WPF 布局是延迟的，整批只触发一次重排
        foreach (var line in _pendingLines)
            LogLines.Add(line);
        _pendingLines.Clear();
        while (LogLines.Count > MaxLogLines)
            LogLines.RemoveAt(0);
        LogFlushed?.Invoke();
    }

    public MainViewModel()
    {
        // 组列表的过滤投影：CollectionViewSource 绑在 Groups 上，Filter 读 GroupFilterText，
        // GroupFilterText 一变就 Refresh()。用投影而不是每次过滤都重建一个 ObservableCollection，
        // 是因为 ListBox 的 SelectedItem 要跨过滤保持——重建集合会把选中项丢掉。
        // 推论：这里拿到的是**Groups 那一个实例**的投影，所以 Groups 只能就地更新（见其属性注释）。
        GroupsView = System.Windows.Data.CollectionViewSource.GetDefaultView(Groups);
        GroupsView.Filter = o => o is GroupItem g &&
            TextFilter.Matches(g.Name, GroupFilterText);

        WriteModes = BuildWriteModes();
        _selectedWriteMode = WriteModes.FirstOrDefault(m => m.Mode == Settings.WriteMode) ?? WriteModes[0];

        // 回填走后备字段：属性 setter 会连带触发 OnChanged 里的 Apply + Settings.Save()，构造期不需要
        ThemeOptions = BuildThemeOptions();
        _selectedThemeOption = ThemeOptions.FirstOrDefault(o => o.Value == ThemeManager.Current) ?? ThemeOptions[0];
        LanguageOptions = BuildLanguageOptions();
        // 回填按**设置里选的那一项**（L10n.Normalize(Settings.UiLanguage)）而不是生效语言（L10n.Current）：
        // 选了「跟随系统」时两者不等，按 Current 回填会让下拉显示成系统恰好命中的那种语言，
        // "跟随系统"看起来像是没选上。走 Normalize 而不是直接比 Settings.UiLanguage，
        // 是因为设置文件里可能有手改出来的未知值（那些值生效语言已回落到 zh）。
        _selectedLanguageOption = LanguageOptions.FirstOrDefault(o => o.Value == L10n.Normalize(Settings.UiLanguage))
            ?? LanguageOptions[0];

        ReloadInstances();
    }

    private static string KindText(ProjectPathKind kind) => kind switch
    {
        ProjectPathKind.Custom => L10n.Tr("L.Kind_Custom"),
        ProjectPathKind.AppDir => L10n.Tr("L.Kind_AppDir"),
        ProjectPathKind.GameDataCalienteTools => L10n.Tr("L.Kind_GameDataCalienteTools"),
        ProjectPathKind.GameDataTools => L10n.Tr("L.Kind_GameDataTools"),
        _ => L10n.Tr("L.Kind_Fallback"),
    };

    private ObservableCollection<WriteModeItem> BuildWriteModes() => new(
    [
        new WriteModeItem(WriteMode.Auto, L10n.Tr("L.Wm_Auto")),
        new WriteModeItem(WriteMode.BodySlideDir, L10n.Tr("L.Wm_BsDir")),
        new WriteModeItem(WriteMode.Mo2Mod, L10n.Tr("L.Wm_Mo2Mod")),
        new WriteModeItem(WriteMode.RealGameData, L10n.Tr("L.Wm_GameData")),
        new WriteModeItem(WriteMode.Custom, L10n.Tr("L.Wm_Custom")),
    ]);

    private static ObservableCollection<OptionItem> BuildThemeOptions() => new(
    [
        new OptionItem(ThemeManager.Boutique, L10n.Tr("L.Theme_Dark")),
        new OptionItem(ThemeManager.Light, L10n.Tr("L.Theme_Light")),
    ]);

    /// <summary>语言下拉项。「跟随系统」排第一（新装默认就是它，见 AppSettings.UiLanguage）：
    /// 它是"我要什么行为"，后四项是"我要哪种语言"——两种意图混排在一个列表里，
    /// 放在最前才读得通，也让绝大多数非中文用户一进来就落在正确的那一项上。</summary>
    private static ObservableCollection<OptionItem> BuildLanguageOptions() => new(
    [
        new OptionItem(L10n.System, L10n.Tr("L.Settings_LanguageSystem")),
        new OptionItem(L10n.Zh, "中文"),
        new OptionItem(L10n.En, "English"),
        new OptionItem(L10n.Ru, "Русский"),
        new OptionItem(L10n.Fr, "Français"),
    ]);

    /// <summary>主题下拉：换项即热切换并落盘。ThemeManager 换的是 app 级色板字典，
    /// 所有开着的窗口一起变，不需要重建谁。</summary>
    partial void OnSelectedThemeOptionChanged(OptionItem? value)
    {
        if (value is null || _localizing || value.Value == ThemeManager.Current)
            return;
        ThemeManager.Apply(value.Value);
        Settings.UiTheme = value.Value;
        Settings.Save();
    }

    /// <summary>语言下拉：换项即热切换并落盘。落盘的是**设置值本身**（可能是 "system"），
    /// 不是算出来的语言码——存语言码等于"跟随系统"只用一次，之后用户在 Windows 里换了显示语言也不会再跟。
    /// 去重比的是设置里选的那一项（Normalize 之后）而非生效语言（L10n.Current）：
    /// 英文系统上用户手动点一下「English」，生效语言恰好也是 en，比 Current 就会把它当成"没变化"
    /// 直接忽略，设置里那句 "system" 永远清不掉。</summary>
    partial void OnSelectedLanguageOptionChanged(OptionItem? value)
    {
        if (value is null || _localizing || value.Value == L10n.Normalize(Settings.UiLanguage))
            return;
        L10n.Apply(value.Value);
        Settings.UiLanguage = value.Value;
        Settings.Save();
        OnLanguageChanged();
    }

    /// <summary>语言切换后重算所有由代码拼出的绑定串。</summary>
    public void OnLanguageChanged()
    {
        _localizing = true;
        var currentMode = SelectedWriteMode?.Mode ?? Settings.WriteMode;
        WriteModes = BuildWriteModes();
        SelectedWriteMode = WriteModes.FirstOrDefault(m => m.Mode == currentMode);
        // 换 ItemsSource 会把 ComboBox 的选中项清成 null，所以按「当前生效值」重新回填；
        // _localizing 挡掉回填触发的 OnChanged，免得换语言顺带把主题/语言又 Apply 一遍。
        // 语言这一项回填的是设置里选的那一项（Normalize 之后）而不是生效语言，理由见
        // OnSelectedLanguageOptionChanged。
        var currentTheme = ThemeManager.Current;
        var currentLang = L10n.Normalize(Settings.UiLanguage);
        ThemeOptions = BuildThemeOptions();
        SelectedThemeOption = ThemeOptions.FirstOrDefault(o => o.Value == currentTheme);
        LanguageOptions = BuildLanguageOptions();
        SelectedLanguageOption = LanguageOptions.FirstOrDefault(o => o.Value == currentLang);
        _localizing = false;

        if (Resolution is not null)
        {
            var game = string.IsNullOrEmpty(SelectedInstance?.GameName) ? L10n.Tr("L.Word_Unknown") : SelectedInstance!.GameName;
            InfoLine = L10n.TrF("L.Info_Line", game, Resolution.EffectivePath, KindText(Resolution.Kind));
        }
        else
        {
            InfoLine = L10n.Tr("L.Vm_NotScanned");
        }
        LogWriteTarget();
        OnPropertyChanged(nameof(SaveButtonLabel));
        OnPropertyChanged(nameof(OutputToolTip));
        // 扫描状态行与未配置引导条同样是代码拼的，且没有别的变更源（LastScanAt 扫完就定下来了），
        // 不在这儿补一句就会一直留着旧语言。
        OnPropertyChanged(nameof(ScanSummaryText));
        RaiseConfigState();
        RaiseLogSummary();
        RefreshTransferState();
        UpdateMembershipMarks(); // 树节点文本也是代码拼的（同名冲突 / [组内 x/y]），一并换语言
        RefreshConflictText();
        UpdateStatusText = ""; // 检查更新的结果是拼好的句子，就地重译不了——宁可空着也别留半句别的语言
        UpdateCounts();
        UpdateGroupInfo();
        UpdateTitle();
    }

    public void OnViewClosed()
    {
        _closed = true;
        DisposeDebounce(); // 关窗后不会再有输入变更，防抖计时器与它的 CTS 一并收掉
        _staleTimer?.Stop(); // 同理：不会再有过期翻转，别留一颗挂着 VM 的表
        Settings.Save();
    }

    private bool _logFlushQueued;

    partial void OnSelectedWriteModeChanged(WriteModeItem? value)
    {
        if (value is null || _localizing)
            return;
        Settings.WriteMode = value.Mode;
        Settings.Save();
        // 视图未挂接目录选择器时（构造阶段）不弹框
        if (value.Mode == WriteMode.Custom && string.IsNullOrWhiteSpace(Settings.CustomTargetDir) && FolderPicker is not null)
            BrowseForTarget();
        else
            LogWriteTarget();
    }

    /// <summary>视图弹目录框后回填。</summary>
    public void SetCustomTargetDir(string? dir)
    {
        Settings.CustomTargetDir = dir;
        Settings.Save();
        LogWriteTarget();
    }

    // ── MO2 实例链 ──
    [RelayCommand]
    private void RefreshInstances() => ReloadInstances();

    private void ReloadInstances()
    {
        var found = Mo2Discovery.Discover(Settings.ExtraMo2Dirs);
        Instances = new ObservableCollection<Mo2Instance>(found);
        var selected = found.FirstOrDefault(i => i.InstanceDir == Settings.LastInstanceDir) ?? found.FirstOrDefault();
        SelectedInstance = selected;
        Log(L10n.TrF("L.Log_InstancesFound", found.Count));
        foreach (var inst in found.Take(3))
            Log(L10n.TrF("L.Log_Instance", inst.DisplayName, inst.InstanceDir));
    }

    partial void OnSelectedInstanceChanged(Mo2Instance? value)
    {
        InvalidateModDirCache(); // 换了实例 = 换了 mods 根目录，探测结果整体作废
        RaiseConfigState(); // 选/取消选实例 → 未配置引导条的显隐跟着变
        if (value is null)
        {
            Profiles = [];
            return;
        }
        Settings.LastInstanceDir = value.InstanceDir;
        Settings.Save();
        var profiles = value.GetProfiles();
        Profiles = new ObservableCollection<string>(profiles);
        // List<string>.Contains 只接受非空 string；LastProfile 是 string?，须先判空
        //（原写法 profiles.Contains(null) 恒为 false，行为不变，但会报 CS8604）
        var last = Settings.LastProfile;
        var preferred = last is not null && profiles.Contains(last) ? last : profiles.FirstOrDefault();
        SelectedProfile = preferred;
    }

    partial void OnSelectedProfileChanged(string? value)
    {
        if (value is null || SelectedInstance is null)
            return;
        Settings.LastProfile = value;
        Settings.Save();
        LoadProfileMods(value);
    }

    private void LoadProfileMods(string profile)
    {
        var instance = SelectedInstance;
        if (instance is null)
            return;
        try
        {
            Entries = ModListParser.Parse(instance.GetModListPath(profile));
            Mods = ModListParser.GetEnabledModDirectories(Entries, instance.ModsDirectory);
            Log(L10n.TrF("L.Log_ProfileMods", profile, Mods.Count));
            _ = DetectBodySlideAsync();
        }
        catch (Exception ex)
        {
            LogError(L10n.TrF("L.Log_ModlistFail", ex.Message));
        }
    }

    // ── BodySlide 链 ──
    // 命令特性必须打在**包装方法**上：XAML 绑的是 DetectBodySlideCommand，
    // 而特性打在 Core 方法上会生成 DetectBodySlideCoreCommand——名字不匹配时按钮静默失效，
    // 编译期毫无提示。遮罩语义（IsDetecting）本来就在包装方法里，移到这里不改变任何行为。
    [RelayCommand]
    private async Task DetectBodySlideAsync()
    {
        IsDetecting = true;
        ScanStatusText = L10n.Tr("L.Scan_Detecting");
        ScanProgressDetail = "";
        try
        {
            await DetectBodySlideCoreAsync();
        }
        finally
        {
            IsDetecting = false;
        }
    }

    private async Task DetectBodySlideCoreAsync()
    {
        var modsSnapshot = Mods;
        var gamePath = SelectedInstance?.GamePath;
        var previous = SelectedBodySlide?.AppDir;
        var last = Settings.LastBodySlideDir;
        var candidates = await Task.Run(() => BodySlideLocator.FindCandidates(modsSnapshot, gamePath, previous));
        BodySlideDirs = new ObservableCollection<BodySlideCandidate>(candidates);
        var pick = candidates.FirstOrDefault(c => c.AppDir == previous)
                   ?? candidates.FirstOrDefault(c => c.AppDir == last)
                   ?? candidates.FirstOrDefault();
        SelectedBodySlide = pick;
        if (pick is not null)
            Settings.LastBodySlideDir = pick.AppDir;
    }

    partial void OnSelectedBodySlideChanged(BodySlideCandidate? value)
    {
        _bsAppDir = value?.AppDir;
        if (value is not null)
        {
            Settings.LastBodySlideDir = value.AppDir;
            Settings.Save();
        }
        RaiseConfigState(); // BodySlide 目录决定 CanRescan 与"未配置"引导条的显隐
        _ = RunScanAsync();
    }

    // ── 扫描 ──
    private sealed record ScanOutcome(
        ProjectPathResolution? Resolution,
        ScanResult? Result,
        List<SliderGroup> ExistingGroups,
        string? TargetDir,
        string TargetDescription,
        List<string> Errors);

    [RelayCommand]
    private async Task RunScanAsync()
    {
        if (_scanning)
        {
            _scanQueued = true; // 扫描进行中又收到新请求：完成后补一次，避免丢扫描
            return;
        }
        var bsDir = _bsAppDir;
        if (bsDir is null)
        {
            Resolution = null;
            Scan = null;
            InfoLine = L10n.Tr("L.Vm_NotScanned");
            RefreshConflictsFromScan(); // 没选中 BodySlide = 没有扫描结果，冲突清单与状态栏计数一并清空
            RebuildTree();
            return;
        }

        _scanning = true;
        IsScanning = true;
        ScanStatusText = L10n.Tr("L.Scan_Preparing");
        ScanProgressDetail = "";
        ScanProgressValue = 0;
        ScanOutcome? outcome = null;
        try
        {
            var modsSnapshot = Mods;
            var instanceSnapshot = SelectedInstance;
            var writeMode = Settings.WriteMode;
            var customDir = Settings.CustomTargetDir;
            var progress = new Progress<ScanProgress>(OnScanProgress);
            outcome = await Task.Run(() => ComputeScan(bsDir, modsSnapshot, instanceSnapshot, writeMode, customDir, progress));
            ScanStatusText = L10n.Tr("L.Scan_Render");
            ScanProgressValue = 100;
            ScanProgressDetail = "";
            ApplyScanOutcome(outcome);
        }
        catch (Exception ex)
        {
            LogError(L10n.TrF("L.Log_ScanFail", ex));
        }
        finally
        {
            _scanning = false;
            IsScanning = false;
            if (_scanQueued)
            {
                _scanQueued = false;
                _ = RunScanAsync();
            }
        }

        // 新模组弹窗是模态的：放在遮罩收起之后弹，否则弹窗开着时遮罩会一直挂在底下
        if (outcome is not null && !_closed)
            CheckNewModsAfterScan();
    }

    /// <summary>扫描进度回调（后台线程经 Progress 同步回 UI 线程）。
    /// 枚举目录远快于解析文件，进度条按 0–20% / 20–100% 加权，避免先冲顶又归零。</summary>
    private void OnScanProgress(ScanProgress p)
    {
        if (p.Phase == ScanPhase.Enumerating)
        {
            ScanProgressValue = p.Total == 0 ? 0 : 20.0 * p.Current / p.Total;
            ScanStatusText = L10n.TrF("L.Scan_Enum", p.Current, p.Total);
            ScanProgressDetail = p.FilesFound > 0 ? L10n.TrF("L.Scan_Files", p.FilesFound) : "";
        }
        else
        {
            ScanProgressValue = p.Total == 0 ? 100 : 20.0 + 80.0 * p.Current / p.Total;
            ScanStatusText = L10n.TrF("L.Scan_Parse", p.Current, p.Total);
        }
    }

    private static ScanOutcome ComputeScan(
        string bsDir, List<(ModEntry Entry, string Dir)> mods, Mo2Instance? instance,
        WriteMode writeMode, string? customDir, IProgress<ScanProgress>? progress)
    {
        var errors = new List<string>();
        var configPath = Path.Combine(bsDir, "Config.xml");
        if (!File.Exists(configPath))
            return new ScanOutcome(null, null, [], null, "", [L10n.TrF("L.Log_ConfigMissing", configPath)]);

        // Config.xml 存在不等于能读：损坏的 XML、被截断/半写的文件、权限不足都会在构造时抛。
        // 这些异常如果漏到 RunScanAsync 的通用 catch，用户只会看到一句"扫描失败"，无从下手；
        // 这里就地转成带路径与具体原因的扫描结果错误项。
        BodySlideConfig config;
        try
        {
            config = new BodySlideConfig(configPath);
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException or UnauthorizedAccessException or FormatException)
        {
            return new ScanOutcome(null, null, [], null, "", [L10n.TrF("L.Log_ConfigInvalid", configPath, ex.Message)]);
        }

        var resolution = BodySlideLocator.ResolveProjectPath(config, bsDir, mods, instance?.GamePath);
        var scan = SliderSetScanner.Scan(resolution, mods, progress);
        var target = ResolveWriteTargetCore(resolution, bsDir, instance, writeMode, customDir);
        var existing = new List<SliderGroup>();
        if (target?.Dir is { } targetDir)
        {
            // 载入上次生成的组：优先按清单，兼容旧版单文件
            var manifestPath = Path.Combine(targetDir, SliderGroupFile.ManifestFileName);
            var filesToLoad = new List<string>();
            if (File.Exists(manifestPath))
            {
                filesToLoad.AddRange(File.ReadAllLines(manifestPath)
                    .Select(line => line.Trim())
                    // 清单内容不可信（用户/其它程序可改写），只接受裸文件名——与保存侧的删除守卫
                    // 用同一个谓词，避免读写两侧对"合法条目"的定义漂移
                    .Where(SliderGroupFile.IsBareFileName)
                    .Select(line => Path.Combine(targetDir, line))
                    .Where(File.Exists));
            }
            else
            {
                // 还没有清单 = 输出目录可能来自 WinForms 版：尝试读入它写出的单文件分组，
                // 让老用户已有的组能带进新版继续编辑（保存后该文件会被清理）
                var legacy = Path.Combine(targetDir, SliderGroupFile.DefaultFileName);
                if (File.Exists(legacy))
                    filesToLoad.Add(legacy);
            }

            foreach (var file in filesToLoad)
            {
                if (!SliderGroupFile.TryLoad(file, out var groups, out var error))
                    errors.Add(L10n.TrF("L.Log_ExistingLoadFail", Path.GetFileName(file), error));
                else
                    SliderGroupFile.Merge(existing, groups, out _, out _);
            }
        }

        return new ScanOutcome(resolution, scan, existing, target?.Dir, target?.Description ?? "", errors);
    }

    private void ApplyScanOutcome(ScanOutcome outcome)
    {
        if (_closed)
            return;
        InvalidateModDirCache(); // 扫描结束前先作废探测缓存：本次磁盘状态可能已经变了（新装的模组）
        foreach (var error in outcome.Errors)
            LogError(L10n.TrF("L.Log_Error", error));

        Resolution = outcome.Resolution;
        Scan = outcome.Result;

        if (outcome.Resolution is null)
        {
            InfoLine = L10n.Tr("L.Info_Unresolved");
            RefreshConflictsFromScan();
            RebuildTree();
            return;
        }

        // 扫描真正成功了才记时间戳。上面那条提前返回的路径（有效路径解析不出）
        // 不能算一次成功扫描——否则指示器会说"刚刚扫描过"，而实际上什么都没读到。
        LastScanAt = DateTime.Now;

        var kindText = KindText(outcome.Resolution.Kind);
        var game = string.IsNullOrEmpty(SelectedInstance?.GameName) ? L10n.Tr("L.Word_Unknown") : SelectedInstance!.GameName;
        InfoLine = L10n.TrF("L.Info_Line", game, outcome.Resolution.EffectivePath, kindText);
        Log(L10n.TrF("L.Log_EffectivePath", outcome.Resolution.EffectivePath, kindText));
        foreach (var note in outcome.Result?.LayerNotes ?? [])
            Log($"  {note}");

        if (Scan is { } scan)
        {
            Log(L10n.TrF("L.Log_ScanDone", scan.WinnerFileCount, scan.Outfits.Count, scan.Warnings.Count));
            foreach (var warning in scan.Warnings.Take(20))
                LogWarning(L10n.TrF("L.Log_Warning", warning));
        }

        // 只在内存中还没有组时才载入上次写出的文件，避免覆盖未保存的修改
        if (Store.Count == 0 && !Store.Dirty && outcome.ExistingGroups.Count > 0)
        {
            Store.Load(outcome.ExistingGroups);
            Log(L10n.TrF("L.Log_LoadedGroups", outcome.TargetDir, Store.Count));
        }

        RefreshGroupsList();
        RefreshTree();
        RefreshConflictsFromScan();
        LogWriteTarget();
    }

    private bool IsVirtualScan =>
        Resolution is not null
        && (Resolution.Kind is ProjectPathKind.GameDataCalienteTools or ProjectPathKind.GameDataTools)
        && Entries.Count > 0;

    public Dictionary<string, string> OwnerByOutfit()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, owner, outfits) in GetTreeDisplayStructure())
            foreach (var outfit in outfits)
                map.TryAdd(outfit, owner);
        return map;
    }

    private static string OwnerNameOf(Dictionary<string, string> ownerByOutfit, string outfit) =>
        ownerByOutfit.TryGetValue(outfit, out var owner) ? owner : "";

    /// <summary>按树结构返回 分隔符 → 模组 → 服装名（供弹窗使用）。</summary>
    public List<(string? Separator, string Owner, List<string> Outfits)> GetTreeDisplayStructure()
    {
        var result = new List<(string?, string, List<string>)>();
        if (Scan is null)
            return result;

        var outfitsByOwner = new Dictionary<string, List<OutfitEntry>>();
        foreach (var outfit in Scan.Outfits)
        {
            if (!outfitsByOwner.TryGetValue(outfit.OwnerLabel, out var list))
                outfitsByOwner[outfit.OwnerLabel] = list = [];
            list.Add(outfit);
        }

        if (IsVirtualScan)
        {
            var consumed = new HashSet<string>(StringComparer.Ordinal);
            string? separator = null;
            for (var i = Entries.Count - 1; i >= 0; i--)
            {
                var entry = Entries[i];
                if (entry.IsForeign)
                    continue;

                if (entry.IsSeparator)
                {
                    separator = entry.Name;
                    continue;
                }

                var onDisk = SelectedInstance is not null &&
                             ModDirExists(Path.Combine(SelectedInstance.ModsDirectory, entry.Name));
                outfitsByOwner.TryGetValue(entry.Name, out var outfits);
                outfits ??= [];
                if (outfits.Count > 0)
                    consumed.Add(entry.Name);

                if (!entry.Enabled || !onDisk)
                    continue;
                result.Add((SeparatorTitle(separator), entry.Name, outfits.Select(o => o.Name).ToList()));
            }

            foreach (var (owner, outfits) in outfitsByOwner)
            {
                if (consumed.Contains(owner))
                    continue;
                result.Add((null, owner, outfits.Select(o => o.Name).ToList()));
            }
        }
        else
        {
            foreach (var (owner, outfits) in outfitsByOwner)
                result.Add((null, owner, outfits.Select(o => o.Name).ToList()));
        }

        return result;
    }

    private static string? SeparatorTitle(string? name) => name is null
        ? null
        : name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase)
            ? name[..^"_separator".Length]
            : name;

    public string SeparatorDisplayName(string name) =>
        name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase)
            ? name[..^"_separator".Length]
            : name;

    private bool IsInAnyGroup(string outfit) => Store.IsInAnyGroup(outfit);

    /// <summary>逐模组"目录是否在磁盘上"的探测缓存（键 = 模组目录全路径）。
    /// 树、结构弹窗与规则预览会反复问同一批模组，而每次 Directory.Exists 都要走一次文件系统。
    /// 只用于减少重复探测，判定语义与直接调用完全一致；缓存内容只在扫描完成/切换实例时失效
    ///（这两处是模组目录集合可能变化的时刻）。只在 UI 线程访问，无需加锁。</summary>
    private readonly Dictionary<string, bool> _modDirExistsCache = new(StringComparer.OrdinalIgnoreCase);

    private bool ModDirExists(string dir)
    {
        if (!_modDirExistsCache.TryGetValue(dir, out var exists))
        {
            exists = Directory.Exists(dir);
            _modDirExistsCache[dir] = exists;
        }
        return exists;
    }

    private void InvalidateModDirCache() => _modDirExistsCache.Clear();
}
