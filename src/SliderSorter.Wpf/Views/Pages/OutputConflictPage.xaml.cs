using System.Collections;
using System.ComponentModel;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using SliderSorter.Core;
using SliderSorter.Wpf.Services;
using SliderSorter.Wpf.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SliderSorter.Wpf.Views.Pages;

/// <summary>
/// 输出归属页：多个模组的服装写同一个 .nif 时，为每一组指定由谁来生成
/// （对应 BodySlide 批建时那个"以下 set 会覆盖同样的文件"的弹窗）。
/// <para>
/// 勾选即时存进本工具自己的设置；「写入 BodySlide…」把它们落成 BodySlide 认得的
/// BuildSelection.xml。不写也能用——只是批建时 BodySlide 仍会逐次问一遍。
/// </para>
/// 左侧列表随过滤重建，所以"哪一组被选中"记在 <see cref="_selectedPath"/> 上而不是列表项对象上。
/// <para>
/// 左栏还会按用户自己的分组（「分组生成」页那批）分类：每个分组头下面是"有该组成员卷入"的冲突，
/// 组内互撞的另挂一枚徽标。分类规则在 <see cref="OutputConflicts.CategorizeByUserGroup"/>，
/// 这里只负责把它渲染成两级列表（<see cref="ListCollectionView"/> + <c>GroupStyle</c>）。
/// </para>
/// <para>
/// 这一页常驻而不是每次现开，所以批量操作与缓存跨标签页切换都还在——换来的是"切走再切回"
/// 不必重新解码一遍网格。代价是数据得靠 VM 推：<see cref="MainViewModel.CurrentConflict"/> 一变就重建，
/// 视口的取景/取消则挂在 <see cref="OnTabVisibilityChanged"/> 上（隐藏期量不到尺寸）。
/// </para>
/// </summary>
public partial class OutputConflictPage : UserControl
{
    /// <summary>冲突组在左栏那一行。<see cref="Name"/> 是文件名（不含目录），
    /// 完整路径留在 <see cref="Path"/> 里给 ToolTip 与中栏标题用。
    /// <para>
    /// <see cref="GroupKey"/> / <see cref="IntraCollision"/> 只在"按分组分类"生效时才有意义：
    /// 前者让 WPF 把这一行归到某个分组头下面，后者决定要不要挂「组内互撞」徽标。
    /// 一条冲突可能同时挂在多个分组下，那时它是**多个** <see cref="GroupRow"/>（各一份），
    /// 所以"当前列出了几组冲突"必须按 <see cref="Group"/> 去重算，不能数行。
    /// </para></summary>
    private sealed record GroupRow(OutputConflictGroup Group, string Name, string Status, bool Resolved,
        string Path, ConflictGroupKey? GroupKey = null, bool IntraCollision = false)
    {
        public Visibility IntraVisibility => IntraCollision ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>左栏分组头的键，同时是 <c>CollectionViewGroup.Name</c>——分组头模板的 DataContext
    /// 是那个 group 对象，不是行，所以文案与计数必须在这里就拼好：模板拿不到页面里的行集合，
    /// 而"该组有几处冲突"必须跟着过滤词走（显示 3 处、下面只挂 1 行会自相矛盾）。
    /// <para>
    /// 计数由整份行集合预先算出，因此同一个分组内必然一致——这是必需的：
    /// <see cref="PropertyGroupDescription"/> 靠相等合并，而这是个**引用相等**的类，
    /// 同一个分组必须共用**一个实例**，否则会被拆成两个头。
    /// </para>
    /// <para>
    /// 它还是个可观察对象，因为折叠条的 <c>IsChecked</c> 双向绑在 <see cref="IsExpanded"/> 上：
    /// 展开态是"这个分组头"自己的状态，挂在键上最自然——挂到页面上就得按组名查表，
    /// 而每次重建都会换一批键，查表还得自己清理。
    /// </para></summary>
    private sealed class ConflictGroupKey : ObservableObject
    {
        private readonly Action<string, bool> _persist;
        private bool _isExpanded;

        public ConflictGroupKey(string name, string label, string countText, string intraText,
            bool expanded, Action<string, bool> persist)
        {
            Name = name;
            Label = label;
            CountText = countText;
            IntraText = intraText;
            _persist = persist;
            // 直接赋字段而不是走属性：构造期设的是初始状态，不该被当成用户动作写回设置
            _isExpanded = expanded;
        }

        public string Name { get; }
        public string Label { get; }
        public string CountText { get; }
        public string IntraText { get; }
        public bool HasIntra => IntraText.Length > 0;

        /// <summary>这一组是否展开。写回设置的是**收起**，所以这里取反——
        /// 设置里存的是"收起的那些分组"，空 = 全展开。</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (SetProperty(ref _isExpanded, value))
                    _persist(Name, !value);
            }
        }
    }

    /// <summary>「只看某组」下拉的一项。<see cref="Value"/> 为 null = 全部分组，空串 = 未入组
    /// （与 <see cref="UserGroupConflicts.GroupName"/> 的哨兵一致）。</summary>
    private sealed record GroupFilterItem(string? Value, string Label);

    private sealed record CandidateRow(
        string? SetName, string Label, string Info, bool IsWinner, bool Grouped, bool Strongest, string? SourceNif,
        bool IsOptOut = false)
    {
        public Visibility GroupedVisibility => Grouped ? Visibility.Visible : Visibility.Collapsed;
        public Visibility StrongestVisibility => Strongest ? Visibility.Visible : Visibility.Collapsed;
    }

    private MainViewModel? _vm;
    private ConflictRequest? _request; // 还没被喂过之前是空的——第一次扫描前这一页就是空的
    private Dictionary<string, string> _working = new(StringComparer.Ordinal);
    private string? _selectedPath;
    private bool _rebuilding;
    /// <summary>非空 = 正在批量改展开态。分组键手上的回写通道会把每次变更投到这里，
    /// 而不是逐个把自己那份写进设置（几十个分组头就是几十次 settings.json 重写）。</summary>
    private Dictionary<string, bool>? _pendingCollapsed;

    /// <summary>「只看某组」当前选中的组名；null = 全部分组。存值而不是存下拉的选中项：
    /// 选项每次重建都是新对象，按值还原才稳。</summary>
    private string? _groupFilter;

    /// <summary>上一次算分类时的分组指纹（见 <see cref="GroupingSignature"/>）。</summary>
    private string _groupingSignature = "";

    /// <summary>上一次取词时的语言（<see cref="ApplyTexts"/> 写下）。本页大半文案是代码拼的而不是
    /// DynamicResource，换语言不会自动跟上；切回本页时按这个记号补一次，见
    /// <see cref="OnTabVisibilityChanged"/>。<see cref="_bodyOptionsLang"/> 管的是身体下拉那一处。
    /// 初值为 null = 「还没取过词」。</summary>
    private string? _textsLang;

    /// <summary>本次重建出的全部分组头（按当前过滤）。右键菜单的「全部展开 / 全部折叠 / 折叠其他」
    /// 按它作用——与批量动作同一口径：只落在当前列出的那些分组上。收起的分组本来就都在这张表里
    /// （折叠只影响显示，不从列表里移除），所以"全部展开"能把它们一起放出来。
    /// <para>
    /// 从列表的视觉树里找分组头是不行的：分组开了虚拟化，收起的分组连容器都没生成过。
    /// </para></summary>
    private IReadOnlyList<ConflictGroupKey> _groupKeys = [];

    /// <summary>对话框的宿主：页面自己没有窗口身份，取所在的壳窗口。</summary>
    private Window? Shell => Window.GetWindow(this);

    public OutputConflictPage()
    {
        InitializeComponent();
        AddLights();
        FilterBox.TextChanged += (_, _) => RebuildGroups();
        IsVisibleChanged += (_, _) => OnTabVisibilityChanged();
        DataContextChanged += (_, _) => AdoptViewModel();
        EnsureBodyOptions();
        // 还没被喂过清单（第一次扫描之前）：三栏都是空的，空状态那句话由本页自己说了算，
        // 批量与导出也不该是可点的——按下去什么都不会发生，比置灰更让人困惑。
        SetActionsEnabled(false);
        EmptyStateLabel.Visibility = Visibility.Visible;
    }

    private void AdoptViewModel()
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelChanged;
        // 认不出 VM 时不报错：这一页渲染只依赖 ShowRequest 喂进来的那份请求，
        // 挂上 DataContext 只是多一条"VM 推新清单"的通道（单测就只走 ShowRequest）。
        _vm = DataContext as MainViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 每次都是 VM 新建的实例，所以重复点「输出冲突」也会走到这里，列表与预览跟着重建
        if (e.PropertyName == nameof(MainViewModel.CurrentConflict) && _vm!.CurrentConflict is { } request)
            ShowRequest(request);
    }

    /// <summary>装上一份冲突清单。壳那边由 <see cref="MainViewModel.CurrentConflict"/> 推过来；
    /// 公开是为了让宿主之外的调用方（含单测）也能直接喂一份，不必造整个 VM。</summary>
    public void ShowRequest(ConflictRequest request)
    {
        _request = request;
        // 键先归到规范拼写上（分组忽略大小写之后，设置里可能存的是同一组的另一种写法）：
        // 不归一，这一组会显示成"未指定"，而"未指定"在保存时是要清掉整组的——等于打开一次页面
        // 就把用户做过的决定抹了。见 OutputConflicts.NormalizeKeys。
        _working = OutputConflicts.NormalizeKeys(request.Groups, request.Choices);
        _textures = request.Assets is { } assets ? new PreviewTextureCache(assets) : null;
        // 新清单：上一轮的"只看某组"未必还在（组名改了、那个组没冲突了），交给 SyncGroupFilter 重新定
        _groupFilter = null;

        ApplyTexts(request);
        // 一组跨模组的都没有时别把列表筛成空的：这时默认显示全部
        OnlyCrossCheck.IsChecked = request.Groups.Any(g => g.CrossMod);
        SetActionsEnabled(request.Groups.Count > 0);
        // 扫完一组冲突都没有时也要留着这句：那种情况下三栏同样是全空的，
        // 而 CurrentConflict 并不是 null（清单是空的，不是没送来）
        EmptyStateLabel.Visibility = request.Groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EnsureBodyOptions();

        RebuildGroups();
    }

    private void SetActionsEnabled(bool on) =>
        AutoButton.IsEnabled = ClearButton.IsEnabled = ExportButton.IsEnabled = on;

    /// <summary>本页那些只能由代码拼出的固定文案：说明段与各控件的悬停提示。
    /// <para>
    /// 与 <see cref="ShowRequest"/> 里"顺带把状态摆正"的赋值（「只看跨模组」的勾选、按钮可点性、
    /// 空状态）分开，是因为换语言时只该重跑这一段——把整段 <see cref="ShowRequest"/> 再来一遍，
    /// 用户特意勾掉的「只看跨模组」会被重新勾上，「只看某组」的筛选也会被清掉。
    /// 列表里的文案（进度、分组头、状态行、候选行、批量范围）不在这里，它们由
    /// <see cref="RebuildGroups"/> 现取现拼，见 <see cref="OnTabVisibilityChanged"/>。
    /// </para></summary>
    private void ApplyTexts(ConflictRequest request)
    {
        IntroLabel.Text = L10n.TrF("L.Conflict_Intro",
            request.Groups.Count(g => g.CrossMod), request.Groups.Count);
        OnlyCrossCheck.ToolTip = L10n.TrF("L.Conflict_OnlyCrossModTip",
            request.Groups.Count(g => g.CrossMod), request.Groups.Count);
        RescanButton.ToolTip = L10n.Tr("L.Conflict_RescanTip");
        AutoButton.ToolTip = L10n.Tr("L.Conflict_AutoAllTip");
        ClearButton.ToolTip = L10n.Tr("L.Conflict_ClearAllTip");
        ExportButton.ToolTip = L10n.Tr("L.Conflict_ExportTip");
        _textsLang = L10n.Current;
    }

    private OutputConflictGroup? SelectedGroup => (GroupList.SelectedItem as GroupRow)?.Group;

    /// <summary>一盏环境光让背光面不糊成一团，方向光拉出体积感。光源颜色固定白：
    /// 这是渲染参数而不是界面配色，跟着主题变会让同一件衣服在两套主题下看着不像同一件。</summary>
    void AddLights()
    {
        // 主光跟相机走（在 UpdateCamera 里改方向）：预览的唯一用途就是转着看，
        // 光固定的话转到背面就只剩一团黑，看不出衣服本身长什么样
        _keyLight = new DirectionalLight(Colors.White, new Vector3D(0, 0, -1));
        var lights = new Model3DGroup
        {
            Children =
            {
                // 环境光压到五成：满白会把方向光的明暗差洗平，网格看起来像一张剪纸
                new AmbientLight(Color.FromRgb(0x8C, 0x8C, 0x8C)),
                _keyLight,
                new DirectionalLight(Color.FromRgb(0x9A, 0x9A, 0x9A), new Vector3D(-0.55, 0.3, 0.4)),
                // 从下往上的补光：裙摆下沿和靴底在只有上方光源时整片死黑，看着像模型缺面
                new DirectionalLight(Color.FromRgb(0x5C, 0x5C, 0x5C), new Vector3D(0.1, -0.75, -0.35)),
            },
        };
        LightVisual.Content = lights;
    }

    private DirectionalLight? _keyLight;

    /// <summary>当前过滤条件下看到的冲突（批量操作只作用于它们——筛出来一批"某某模组的"再点自动选择，
    /// 不该顺手改掉屏幕外的组）。一条冲突可能挂在多个分组下（界面上就是多行），故按 Group 去重：
    /// <see cref="OutputConflicts.AutoPick"/> 对同一个输出路径重复应用是幂等的，多算几遍不出错，
    /// 但「对当前列出的 N 组」里的 N 必须是冲突数而不是行数。</summary>
    private List<OutputConflictGroup> VisibleGroups()
    {
        if (GroupList.ItemsSource is not IEnumerable source)
            return [];
        return source.OfType<GroupRow>().Select(r => r.Group).Distinct().ToList();
    }

    /// <summary>生效中的选择。设置里可能留着一条指向已消失 set 的死选择，那种情况按"未指定"处理。</summary>
    private string? ChosenOf(OutputConflictGroup group) =>
        _working.TryGetValue(group.OutputFilePath, out var name) &&
        group.Candidates.Any(c => c.Name == name)
            ? name
            : null;

    private static bool MatchesFilter(OutputConflictGroup group, string filter) =>
        TextFilter.Matches(group.OutputFilePath, filter) ||
        group.Candidates.Any(c => TextFilter.Matches(c.Name, filter) || TextFilter.Matches(c.OwnerLabel, filter));

    /// <summary>把 <see cref="OutputConflictGroup.TargetDisplay"/> 拆成目录与文件名。
    /// 它是"&lt;完整输出路径&gt;_0.nif（以及 _1.nif）"：末段分隔符之前是目录、之后是文件名，
    /// 而那个后缀里不会有分隔符，所以从最后一个分隔符切是可靠的。</summary>
    private static (string Dir, string Name) SplitTarget(string display)
    {
        var cut = display.LastIndexOf('\\');
        return cut <= 0 || cut == display.Length - 1
            ? ("", display)
            : (display[..cut], display[(cut + 1)..]);
    }

    /// <summary>页标题右侧的进度："已指定 M / N 组"，还有没指定的就再补一句警示色的。
    /// M / N 数的是整份清单（不受过滤影响）：这是"还剩多少活"，不是"屏幕上有几行"。</summary>
    private void UpdateProgress()
    {
        if (_request is not { } request)
        {
            ProgressLabel.Text = "";
            UnresolvedLabel.Text = "";
            UnresolvedLabel.Visibility = Visibility.Collapsed;
            return;
        }

        var total = request.Groups.Count;
        var chosen = request.Groups.Count(g => ChosenOf(g) is not null);
        ProgressLabel.Text = L10n.TrF("L.Conflict_Progress", chosen, total);
        var rest = total - chosen;
        UnresolvedLabel.Text = rest > 0 ? L10n.TrF("L.Conflict_UnresolvedSuffix", rest) : "";
        UnresolvedLabel.Visibility = rest > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>左栏列表的滚动宿主：ListBox 模板里那块固定叫 <c>PART_ScrollViewer</c>。</summary>
    private ScrollViewer? GroupScroller =>
        GroupList.Template?.FindName("PART_ScrollViewer", GroupList) as ScrollViewer;

    private void RebuildGroups()
    {
        if (_request is not { } request)
            return;

        // 分类算在**未过滤**的全量清单上，行本身再按各道过滤筛：下拉的选项不该边打字边变，
        // 而分组头里的计数按筛完的行算（见 ConflictGroupKey）。
        var buckets = OutputConflicts.CategorizeByUserGroup(request.Groups, request.UserGroups());
        // 没有任何分组卷入（没建组，或建了但一件都没卷进来）时不做分类：那时全部行会挂在
        // 一个「未入组」头下面，那条头不带任何信息，只是把列表整体下移一行。
        var grouping = buckets.Any(b => !b.IsUngrouped);
        SyncGroupFilter(buckets, grouping);

        var filter = FilterBox.Text.Trim();
        // "只看跨模组"定的是这一轮的范围，用集合查比逐行 Any 便宜，也让下面计数的分母现成
        var pool = OnlyCrossCheck.IsChecked == true
            ? request.Groups.Where(g => g.CrossMod).ToHashSet()
            : null;

        // 按**桶**而不是按冲突遍历：WPF 按首次出现建立分组，所以行按桶排下来，
        // 分组头的顺序就等于桶的顺序（组列表顺序 → 未入组），与「分组生成」页的组列表一致。
        var kept = new List<(OutputConflictGroup Group, string GroupName, bool Intra)>();
        foreach (var bucket in buckets)
            foreach (var row in bucket.Rows)
            {
                if (pool is not null && !pool.Contains(row.Conflict))
                    continue;
                if (filter.Length > 0 && !MatchesFilter(row.Conflict, filter))
                    continue;
                if (_groupFilter is not null && bucket.GroupName != _groupFilter)
                    continue;
                kept.Add((row.Conflict, bucket.GroupName, row.IntraCollision));
            }

        // 「只看某组」筛到单个分组时，那一组一律展开：用户刚点名要看它，却因为上次收起过
        // 而只看到一条光杆标题，会以为这一组没冲突。此时也不把展开态写回设置（见 BuildGroupKeys）。
        var forceExpand = _groupFilter is not null;
        var keys = BuildGroupKeys(request, kept, forceExpand);
        _groupKeys = keys.Values.ToList();
        var rows = new List<GroupRow>(kept.Count);
        foreach (var (group, groupName, intra) in kept)
        {
            var chosen = ChosenOf(group);
            rows.Add(new GroupRow(
                group,
                SplitTarget(group.TargetDisplay).Name,
                chosen is null
                    ? L10n.Tr("L.Conflict_StatusUnresolved")
                    : L10n.TrF("L.Conflict_StatusChosen", chosen),
                chosen is not null,
                group.TargetDisplay,
                keys[groupName],
                intra));
        }

        // 换 ItemsSource 会把左栏的滚动位置归零：改一个赢家要整表重建（行是不可变记录），
        // 但用户正在翻的那一屏不该被弹回顶部。记下偏移，重建后原样放回。
        var offset = GroupScroller?.VerticalOffset ?? 0;
        _rebuilding = true;
        GroupList.ItemsSource = BuildGroupedView(rows, grouping);
        GroupList.SelectedItem =
            rows.Find(r => r.Group.OutputFilePath == _selectedPath) ?? rows.FirstOrDefault();
        _selectedPath = (GroupList.SelectedItem as GroupRow)?.Group.OutputFilePath;
        _rebuilding = false;
        if (offset > 0)
        {
            // 新 ItemsSource 还没排版时 ScrollToVerticalOffset 会被当成超出范围而夹住，
            // 所以先补一次布局；拿不到滚动宿主（换了模板）就退回把选中行滚进视野
            GroupList.UpdateLayout();
            if (GroupScroller is { } scroller)
                scroller.ScrollToVerticalOffset(offset);
            else
                GroupList.ScrollIntoView(GroupList.SelectedItem);
        }

        // 一条冲突可能挂在多个分组下，行数因此可能大于冲突数。这两个标签说的都是"有几组冲突"
        //（批量动作也是按冲突作用），所以按 Group 去重。
        var listed = rows.Select(r => r.Group).Distinct().Count();
        GroupsCountLabel.Text = L10n.TrF("L.Conflict_GroupCount", listed);
        BatchScopeLabel.Text = L10n.TrF("L.Conflict_BatchScope", listed);
        var filtering = filter.Length > 0 || pool is not null || _groupFilter is not null;
        FilterCountLabel.Text = filtering
            ? L10n.TrF("L.Conflict_FilterCount", listed, pool?.Count ?? request.Groups.Count)
            : "";
        FilterCountLabel.Visibility = filtering ? Visibility.Visible : Visibility.Collapsed;

        _groupingSignature = GroupingSignature();
        UpdateProgress();
        RebuildCandidates();
    }

    /// <summary>把行集合交给列表渲染。有分组时套一层分组描述，WPF 就会按 <c>GroupKey</c>
    /// 把分组头插在行前面。
    /// <para>
    /// 刻意不设 <see cref="PropertyGroupDescription.CustomSort"/>：分组按**首次出现**的顺序建立，
    /// 而行集合已经按桶排好，那正是我们要的顺序（组列表顺序 → 未入组）。设了反而危险——
    /// CustomSort 的语义（排"组"还是排"组内元素"）在文档里含糊，万一是后者，
    /// 比较器会收到 <see cref="GroupRow"/> 而不是键。
    /// </para></summary>
    private static ListCollectionView BuildGroupedView(List<GroupRow> rows, bool grouping)
    {
        var view = new ListCollectionView(rows);
        if (grouping)
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GroupRow.GroupKey)));
        return view;
    }

    /// <summary>按已筛出的行造分组头，同一分组共用一个实例。计数在这里算，所以它天然是"过滤后的"。
    /// 顺序无关：分组头的顺序由行的顺序决定，字典的枚举顺序不参与。
    /// <para>
    /// <paramref name="forceExpand"/> 为真（「只看某组」筛到了单个分组）时一律展开、且**不写设置**：
    /// 那一屏本来就是"我要看这一组"，若它的展开态落了盘，用户在"全部分组"下特意收起的意图
    /// 会被这一次查看悄悄抹掉。
    /// </para></summary>
    private Dictionary<string, ConflictGroupKey> BuildGroupKeys(
        ConflictRequest request,
        IReadOnlyList<(OutputConflictGroup Group, string GroupName, bool Intra)> kept,
        bool forceExpand)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var intras = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, name, intra) in kept)
        {
            counts[name] = counts.GetValueOrDefault(name) + 1;
            if (intra)
                intras[name] = intras.GetValueOrDefault(name) + 1;
        }

        // 「只看某组」那一屏不许写回设置（见方法注释），其余走 PersistCollapsed：
        // 它认得批量折叠的暂存态，逐个点折叠条时仍然即时落盘
        Action<string, bool> persist = forceExpand ? static (_, _) => { } : PersistCollapsed;
        var keys = new Dictionary<string, ConflictGroupKey>(StringComparer.Ordinal);
        foreach (var (name, count) in counts)
            keys[name] = new ConflictGroupKey(
                name,
                name.Length == 0 ? L10n.Tr("L.Conflict_Ungrouped") : name,
                L10n.TrF("L.Conflict_GroupBucketCount", count),
                intras.TryGetValue(name, out var n) && n > 0
                    ? L10n.TrF("L.Conflict_IntraGroupCount", n)
                    : "",
                expanded: forceExpand || !request.IsGroupCollapsed(name),
                persist);
        return keys;
    }

    // ── 分组头的右键菜单：整列的展开 / 折叠（2026-09-23）──
    //
    // 与「分组生成」页左侧模组树的同名菜单一致，只是那里的"节点"这里换成了"分组头"：
    // 树里菜单挂在每一行上，而这一页只有分组头会折叠，冲突行是叶子。
    //
    // 菜单项走 Click 而不是命令：菜单是资源里的共享实例（见 OutputConflictPage.xaml 的
    // ConflictGroupMenu），DataContext 不在逻辑树上继承得到，而三个动作又都是整列的、
    // 不属于任何一个分组头对象。锚点因此只能从 PlacementTarget 上现取——WPF 在弹出前
    // 会把它设成被右键的那个元素。

    /// <summary>被右键的那个分组头。菜单挂在分组头的 ToggleButton 上，而它的 DataContext 是
    /// <see cref="CollectionViewGroup"/>（分组头模板的 DataContext 不是行对象），分组键在 <c>Name</c> 上。
    /// <para>
    /// 取不到时只有「折叠其他」什么都不做（另两项本来就作用于整列，不需要锚点）。这条兜底不是
    /// 为"用户会遇到"准备的——菜单只挂在分组头上，PlacementTarget 必然是其中一个；它挡的是
    /// 模板被改坏之后"右键哪一组都把别的全折了"这种更难查的错法。
    /// </para></summary>
    private static ConflictGroupKey? MenuAnchor(object sender)
    {
        if (sender is not MenuItem item)
            return null;
        var menu = ItemsControl.ItemsControlFromItemContainer(item) as ContextMenu
                   ?? item.Parent as ContextMenu;
        return menu?.PlacementTarget is FrameworkElement { DataContext: CollectionViewGroup group }
            ? group.Name as ConflictGroupKey
            : null;
    }

    /// <summary>「全部展开」：当前列出的每个分组头都展开。</summary>
    private void ExpandAllGroups_Click(object sender, RoutedEventArgs e) =>
        BatchCollapse(keys =>
        {
            foreach (var key in keys)
                key.IsExpanded = true;
        });

    /// <summary>「全部折叠」：当前列出的每个分组头都收起。</summary>
    private void CollapseAllGroups_Click(object sender, RoutedEventArgs e) =>
        BatchCollapse(keys =>
        {
            foreach (var key in keys)
                key.IsExpanded = false;
        });

    /// <summary>把一批展开态变更收进一次落盘。见 <see cref="_pendingCollapsed"/>。</summary>
    private void BatchCollapse(Action<IReadOnlyList<ConflictGroupKey>> apply)
    {
        _pendingCollapsed = new Dictionary<string, bool>(StringComparer.Ordinal);
        try
        {
            apply(_groupKeys);
        }
        finally
        {
            var pending = _pendingCollapsed;
            _pendingCollapsed = null;
            if (pending.Count > 0)
                _request?.SetGroupsCollapsed(pending);
        }
    }

    /// <summary>分组键的回写通道：批量时先攒着，平时即时落盘。</summary>
    private void PersistCollapsed(string name, bool collapsed)
    {
        if (_pendingCollapsed is { } pending)
        {
            pending[name] = collapsed;
            return;
        }
        _request?.SetGroupCollapsed(name, collapsed);
    }

    /// <summary>批量改展开态。写回设置是 <see cref="ConflictGroupKey.IsExpanded"/> 的 setter 干的，
    /// 所以批量动作与逐个点折叠条落盘的是同一样东西；「只看某组」那一屏的键拿到的是空回写通道，
    /// 于是"只是看一眼"依旧不会抹掉用户原先的收起意图。</summary>
    private void SetAllGroupsExpanded(bool expanded)
    {
        foreach (var key in _groupKeys)
            key.IsExpanded = expanded;
    }

    /// <summary>「折叠其他」：右键的那一组保持展开（连它一起折了就看不见自己点的是谁），其余全部收起。</summary>
    private void CollapseOtherGroups_Click(object sender, RoutedEventArgs e)
    {
        if (MenuAnchor(sender) is not { } anchor)
            return;
        BatchCollapse(keys =>
        {
            foreach (var key in keys)
                if (!ReferenceEquals(key, anchor))
                    key.IsExpanded = false;
            anchor.IsExpanded = true;
        });
    }

    /// <summary>重建「只看某组」的选项，并按值还原选中项（组名稳定、选项对象每次都换）。
    /// 只列**真的有冲突**的分组：一个三十组的用户不该在选项里翻三十行才找到那两组，
    /// 而左栏的分组头本来也只出现在有冲突的组上，两处口径一致。
    /// 可选项不足两个时整颗收起（那时它与"全部分组"等价，只是噪声），并把筛选清掉——
    /// 留着一个既选不中也看不见的筛选值，表现是"列表莫名其妙地少东西"。</summary>
    private void SyncGroupFilter(IReadOnlyList<UserGroupConflicts> buckets, bool grouping)
    {
        var items = new List<GroupFilterItem>();
        if (grouping)
        {
            items.Add(new GroupFilterItem(null, L10n.Tr("L.Conflict_GroupFilterAll")));
            items.AddRange(buckets.Select(b => new GroupFilterItem(
                b.GroupName, b.IsUngrouped ? L10n.Tr("L.Conflict_Ungrouped") : b.GroupName)));
        }

        var show = items.Count > 1;
        if (!show || (_groupFilter is not null && items.All(i => i.Value != _groupFilter)))
            _groupFilter = null;

        // 换 ItemsSource 会触发 SelectionChanged，而它会回调 RebuildGroups —— 这里必须挡住，
        // 否则"重建选项 → 重建列表 → 重建选项"直接递归。_rebuilding 本就在管 GroupList 那一路。
        var restore = _rebuilding;
        _rebuilding = true;
        GroupFilterBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        GroupFilterBox.DisplayMemberPath = nameof(GroupFilterItem.Label);
        GroupFilterBox.ItemsSource = items;
        GroupFilterBox.SelectedItem = items.FirstOrDefault(i => i.Value == _groupFilter) ?? items.FirstOrDefault();
        _rebuilding = restore;
    }

    private void OnlyCross_Changed(object sender, RoutedEventArgs e) => RebuildGroups();

    private void GroupFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuilding)
            return;
        _groupFilter = (GroupFilterBox.SelectedItem as GroupFilterItem)?.Value;
        RebuildGroups();
    }

    /// <summary>分组数据的指纹，用来判断"切回本页时要不要重算分类"：分组在「分组生成」页随时会改，
    /// 而本页只在 <see cref="MainViewModel.CurrentConflict"/> 变化时重建——没有这道检查，
    /// 切回来看到的会是上一轮的分组头与「已在组内」徽标。
    /// <para>
    /// 拼成字符串而不是取哈希：一次标签页切换只算一次，而哈希碰撞的代价是"界面上静默显示旧数据"，
    /// 那正是这套门禁最不想放过去的一类问题。分隔符用不可能出现在组名/成员名里的控制字符，
    /// 免得 {"ab","c"} 与 {"a","bc"} 拼出同一个指纹。
    /// </para></summary>
    private string GroupingSignature() => _request is null
        ? ""
        : string.Join("\n", _request.UserGroups().Select(g => g.Name + "\u0001" + string.Join("\u0002", g.Members)));

    private void RebuildCandidates()
    {
        if (SelectedGroup is not { } group || _request is not { } request)
        {
            CandidateList.ItemsSource = Array.Empty<CandidateRow>();
            TargetLabel.Text = L10n.Tr("L.Conflict_NoneSelected");
            CandidateCountLabel.Text = "";
            ShowPreview(null);
            return;
        }

        // 只写文件名，整条路径进悬停提示：从尾截断的长路径在最小窗口下会正好切掉文件名，
        // 而那是唯一有区分度的一段（目录前缀在实战里人人相同）
        TargetLabel.Text = SplitTarget(group.TargetDisplay).Name;
        TargetLabel.ToolTip = L10n.TrF("L.Conflict_Target", group.TargetDisplay);
        CandidateCountLabel.Text = L10n.TrF("L.Conflict_CandidateCount", group.Candidates.Count);
        var chosen = ChosenOf(group);
        var rows = new List<CandidateRow>();
        for (var i = 0; i < group.Candidates.Count; i++)
        {
            var candidate = group.Candidates[i];
            rows.Add(new CandidateRow(
                candidate.Name,
                candidate.Name,
                L10n.TrF("L.Conflict_RowInfo", candidate.OwnerLabel, candidate.LayerIndex + 1),
                candidate.Name == chosen,
                request.IsGrouped(candidate.Name),
                Strongest: i == 0,
                request.SourceNifOf(candidate.Name)));
        }
        rows.Add(new CandidateRow(null, L10n.Tr("L.Conflict_AskMeAgain"), "", chosen is null, false, false, null,
            IsOptOut: true));
        CandidateList.ItemsSource = rows;

        // 选中"要看的那一行"：已定的赢家优先，否则最强层——预览跟着选中的行走
        CandidateList.SelectedItem = rows.FirstOrDefault(r => r.IsWinner && r.SetName is not null) ?? rows[0];
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuilding)
            return;
        _selectedPath = SelectedGroup?.OutputFilePath;
        RebuildCandidates();
    }

    private void Winner_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: CandidateRow row })
            SetWinner(row);
    }

    /// <summary>右键整行 = 把这一行设为赢家。指针本来就停在意的那一行上，不该逼用户去瞄准那个小圆圈。</summary>
    private void Row_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CandidateRow row })
            return;
        CandidateList.SelectedItem = row;
        SetWinner(row);
        e.Handled = true;
    }

    private void SetWinner(CandidateRow row)
    {
        if (SelectedGroup is not { } group)
            return;

        if (row.SetName is null)
            _working.Remove(group.OutputFilePath);
        else
            _working[group.OutputFilePath] = row.SetName;

        SaveAndRefresh();
    }

    private void CandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowPreview(CandidateList.SelectedItem as CandidateRow);

    // ── 3D 预览：读这个 set 的源网格（BodySlide 建它时读的同一个 .nif），每个形状各自上自己的贴图 ──

    /// <summary>缓存连失败结果一起存：同一个坏文件被反复选中时不该反复重试。
    /// 存的是"衣服本身"（几何 + 已解码的贴图），身体不在里面——它由界面上的选择决定，会单独变。</summary>
    private readonly Dictionary<string, (NifPreviewModel? Model, string? Error, IReadOnlyList<ImageSource?>? Brushes)>
        _meshCache = new(StringComparer.Ordinal);

    /// <summary>罩住 <see cref="_meshCache"/> 与 <see cref="_bodies"/>。两个字典都只在预览任务里
    /// 读写，而预览任务**可以并发**：取消只能让上一份的续体不落地（见 ShowPreview 里的说明），
    /// 已经开跑的解码停不掉，于是快速连点两行就是两条线程池线程同时 Add/Clear 同一个字典。
    /// 里面的 <c>NifPreviewLoader.TryLoad</c> 在锁外，慢活不互相排队。</summary>
    private readonly object _cacheGate = new();

    private PreviewTextureCache? _textures;

    private CancellationTokenSource? _previewCts;
    private Point3D _center;
    private Vector3D _half = new(50, 50, 50);
    /// <summary>Creation Engine 的模型朝 +Y（nif 是 Z 朝上的右手系），映射到 WPF 后正面落在 -Z，
    /// 所以机位从 π 起——0 会一上来就给人看屁股，而且镜像换轴会把左右翻反，不能那么干。</summary>
    private const double FrontYaw = Math.PI;

    private double _yaw = FrontYaw;
    private double _pitch;
    private double _zoom = 1;
    private MouseButton? _dragButton;
    private Point _dragLast;

    /// <summary>用户右键拖出来的视野偏移（世界坐标）。换一件衣服必须清零，
    /// 否则上一件拖到边角的内容会把新衣服推出画面。</summary>
    private Vector3D _pan;

    /// <summary>UpdateCamera 算出来的相机基向量与距离，平移要按"屏幕上 1 像素等于多少世界单位"
    /// 换算，就得同时用到这三个，而它们每次取景都会变。</summary>
    private Vector3D _camRight = new(1, 0, 0);
    private Vector3D _camUp = new(0, 1, 0);
    private double _distance = 100;

    /// <summary>拿不到贴图时的调色板：饱和度低、明度接近，所以既能把同一件衣服的几个形状分开，
    /// 又不会把"这件衣服本来什么颜色"的直觉带跑。按形状名哈希取，同一件衣服每次预览都是同一套色。</summary>
    static readonly Color[] PlainPalette =
    [
        Color.FromRgb(0xB8, 0xA8, 0x98), Color.FromRgb(0x9F, 0xB0, 0xA4),
        Color.FromRgb(0xA2, 0xAA, 0xB8), Color.FromRgb(0xB8, 0xA2, 0xAC),
        Color.FromRgb(0xB0, 0xAC, 0xA0), Color.FromRgb(0x9C, 0xB0, 0xB4),
        Color.FromRgb(0xB4, 0xAC, 0xBC), Color.FromRgb(0xAA, 0xB4, 0x9E),
    ];

    /// <summary>要不要在衣服下面垫一具身体。「自动」= 这件自带身体就不垫，没带就补一具女体。</summary>
    private enum BodyOption { Auto, Female, Male, None }

    private sealed record BodyOptionItem(BodyOption Option, string Label);

    /// <summary>打底身体的 nif（BodySlide 和游戏都认这一对名字）。它走的是同一套跨层解析，
    /// 所以装了体型 mod 时拿到的是 mod 那具身体，而不是官方模型——这点比 boutique 只用 vanilla 更准。</summary>
    private const string BodyFemaleNif = @"meshes\actors\character\character assets\femalebody_0.nif";
    private const string BodyMaleNif = @"meshes\actors\character\character assets\malebody_0.nif";

    /// <summary>身体模型按性别各缓存一份（连"加载失败"一起缓存，免得每件衣服都重试一遍）。</summary>
    private readonly Dictionary<BodyOption, NifPreviewModel?> _bodies = new();

    private BodyOption _body = BodyOption.Auto;

    /// <summary>身体下拉的标签是本地化的，而这一页不会因为换语言而重建，所以按语言记一笔，
    /// 切回这一页时补一次。选中的那一项按 <see cref="_body"/> 还原，不按列表下标。</summary>
    private string? _bodyOptionsLang;

    private void EnsureBodyOptions()
    {
        if (_bodyOptionsLang == L10n.Current)
            return;
        _bodyOptionsLang = L10n.Current;
        BodyChoice.ItemsSource = new[]
        {
            new BodyOptionItem(BodyOption.Auto, L10n.Tr("L.Preview_BodyAuto")),
            new BodyOptionItem(BodyOption.Female, L10n.Tr("L.Preview_BodyFemale")),
            new BodyOptionItem(BodyOption.Male, L10n.Tr("L.Preview_BodyMale")),
            new BodyOptionItem(BodyOption.None, L10n.Tr("L.Preview_BodyNone")),
        };
        BodyChoice.DisplayMemberPath = nameof(BodyOptionItem.Label);
        BodyChoice.SelectedIndex = (int)_body;
    }

    /// <param name="assets">调用方（UI 线程）抓好的数据视图快照：预览任务可能跑在
    /// <see cref="ShowRequest"/> 换掉 <see cref="_request"/> 之后，任务里不许再去读那个字段。</param>
    NifPreviewModel? BodyFor(BodyOption option, GameDataResolver? assets)
    {
        if (option is BodyOption.None or BodyOption.Auto || assets is null)
            return null;
        lock (_cacheGate)
            if (_bodies.TryGetValue(option, out var cached))
                return cached;

        NifPreviewModel? model = null;
        if (assets.TryRead(option == BodyOption.Female ? BodyFemaleNif : BodyMaleNif) is { } bytes)
            NifPreviewLoader.TryLoad(bytes, out model, out _);
        lock (_cacheGate)
            _bodies[option] = model; // 两条预览线程同时解到同一具身体时，后写的覆盖前写的，值等价
        return model;
    }

    /// <summary>这件衣服本身（网格 + 已解好的贴图笔刷），连"读不出来"一起缓存。
    /// 解码在锁外做：那是贵的一步，两份预览不该互相排队；只有动字典的那一下要互斥。</summary>
    private (NifPreviewModel? Model, string? Error, IReadOnlyList<ImageSource?>? Brushes) OutfitOf(
        string path, PreviewTextureCache? textures)
    {
        lock (_cacheGate)
            if (_meshCache.TryGetValue(path, out var hit))
                return hit;

        var ok = NifPreviewLoader.TryLoad(path, out var model, out var error);
        (NifPreviewModel? Model, string? Error, IReadOnlyList<ImageSource?>? Brushes) entry = ok
            ? (model, null, model?.Shapes.Select(s => textures?.Get(s.TexturePath)).ToArray())
            : (null, error, null);
        lock (_cacheGate)
        {
            if (_meshCache.Count > 12)
                _meshCache.Clear();
            _meshCache[path] = entry;
        }
        return entry;
    }

    /// <summary>把界面上的选择折算成这一件实际要不要垫、垫哪具：自带身体的就不垫。</summary>
    static BodyOption ResolveBody(BodyOption chosen, NifPreviewModel? outfit) => chosen switch
    {
        BodyOption.Auto => outfit is { CarriesOwnBody: false } ? BodyOption.Female : BodyOption.None,
        _ => chosen,
    };

    /// <summary>预览里"没有东西可渲染"的那部分状态：收起渲染面、清掉旧网格、把只对网格有意义的控件置灰。
    /// 渲染面收起来后露出的是同一块几何上的 B.Panel 托底，所以切候选时控制行不会上下跳。
    /// 两层的底色都跟着主题走（XAML 里是 DynamicResource），这里只切可见性与 IsEnabled、不碰颜色。</summary>
    private void SetPreviewSurface(bool hasMesh)
    {
        ViewportSurface.Visibility = hasMesh ? Visibility.Visible : Visibility.Collapsed;
        // 没网格时复位视角、换身体都没有对象可作用，置灰比按下去没反应清楚
        ResetButton.IsEnabled = BodyChoice.IsEnabled = hasMesh;
        if (!hasMesh)
            MeshVisual.Content = null;
    }

    private void SetPreviewStatus(string text)
    {
        PreviewStatus.Text = text;
        PreviewStatusBubble.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>统计文字没网格时是空的，但一行 13px + 边距照样占 27px——
    /// 收起它，别让预览卡底部多出一条看不见的空行。</summary>
    private void SetMeshStats(string text)
    {
        MeshStats.Text = text;
        MeshStats.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void ShowPreview(CandidateRow? row)
    {
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        PreviewTitle.Text = row?.Label ?? "";
        SetMeshStats("");
        SetPreviewSurface(false);
        if (row is null)
        {
            SetPreviewStatus("");
            return;
        }
        if (string.IsNullOrEmpty(row.SourceNif))
        {
            SetPreviewStatus(L10n.Tr("L.Preview_NoSource"));
            return;
        }

        var path = row.SourceNif!;
        SetPreviewStatus(L10n.Tr("L.Preview_Loading"));
        var wanted = _body;
        try
        {
            // 缓存命中也照样走这条后台任务：身体要按界面上的选择重新配，而那两步都可能是文件 IO。
            // 命中时省掉的是解析 nif 与解码贴图，这才是贵的那部分。
            // 两份快照都必须在 UI 线程上取：Task.Run 的令牌只能拦住"还没开跑"的任务，
            // 已经开跑的解码停不下来，于是上一份预览的续体会活到下一轮的数据视图里。
            var textures = _textures;
            var assets = _request?.Assets;
            var loaded = await Task.Run(() =>
            {
                cts.Token.ThrowIfCancellationRequested();
                var outfit = OutfitOf(path, textures);
                // 整件网格解完之后再看有没有人等它：身体那一步也是 IO，没人等就别做
                cts.Token.ThrowIfCancellationRequested();
                var body = BodyFor(ResolveBody(wanted, outfit.Model), assets);
                var bodyBrushes = body?.Shapes.Select(s => textures?.Get(s.TexturePath)).ToArray();
                return (outfit.Model, outfit.Error, outfit.Brushes, body, bodyBrushes);
            }, cts.Token);

            // 排队期间又点了别的行：这一份已经没人等了，丢掉
            if (!ReferenceEquals(_previewCts, cts))
                return;

            Render(loaded.Item1, loaded.Item2, loaded.Item3, loaded.Item4, loaded.Item5);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {
            // 被更新的预览取代，安静地丢掉
        }
    }

    void Render(NifPreviewModel? model, string? error, IReadOnlyList<ImageSource?>? brushes = null,
        NifPreviewModel? body = null, IReadOnlyList<ImageSource?>? bodyBrushes = null)
    {
        if (model is null)
        {
            SetPreviewSurface(false);
            SetPreviewStatus(error ?? L10n.Tr("L.Preview_NoSource"));
            return;
        }

        SetPreviewSurface(true);
        SetPreviewStatus("");
        MeshVisual.Content = BuildModel(model, brushes, body, bodyBrushes);
        SetMeshStats(MeshStatsFor(model, body));
        Fit(model, body);
        UpdateCamera();
    }

    /// <summary>统计说的是"画在屏幕上的东西"，所以垫进去的身体要算进来——只报衣服那 1 个形状、
    /// 眼前却站着一整个人，会让人以为那具身体是渲染出错多出来的。</summary>
    string MeshStatsFor(NifPreviewModel model, NifPreviewModel? body)
    {
        var stats = L10n.TrF("L.Preview_Stats",
            model.TriangleCount + (body?.TriangleCount ?? 0),
            model.ShapeCount + (body?.ShapeCount ?? 0),
            model.TexturedShapeCount + (body?.TexturedShapeCount ?? 0));
        // 超预算被丢掉的形状必须说出来：不然用户以为这件衣服本来就只有画出来的这几块
        return model.SkippedShapeCount > 0
            ? stats + " · " + L10n.TrF("L.Preview_Truncated", model.SkippedShapeCount)
            : stats;
    }

    /// <summary>nif 是 Z 朝上的右手系（单位厘米），WPF 世界坐标 Y 朝上：绕 X 轴转 90° 即可，
    /// 长度不变，所以按包围球取景时不需要额外缩放。</summary>
    static Point3D ToWpf(Vector3 v) => new(v.X, v.Z, -v.Y);

    /// <summary>每个形状一份几何 + 一份材质。合并成一份网格就得共用一个材质，
    /// 而一件衣服的衣身、袖子、腰带各自引用不同的贴图——合并等于放弃颜色。
    /// 打底的身体先画，让衣服在深度测试里盖住它。</summary>
    Model3D BuildModel(NifPreviewModel model, IReadOnlyList<ImageSource?>? brushes,
        NifPreviewModel? body, IReadOnlyList<ImageSource?>? bodyBrushes)
    {
        var group = new Model3DGroup();
        if (body is not null)
            for (var i = 0; i < body.Shapes.Count; i++)
                AddShape(group, body.Shapes[i], i < (bodyBrushes?.Count ?? 0) ? bodyBrushes![i] : null);
        for (var i = 0; i < model.Shapes.Count; i++)
            AddShape(group, model.Shapes[i], i < (brushes?.Count ?? 0) ? brushes![i] : null);
        return group;
    }

    static void AddShape(Model3DGroup group, NifShape shape, ImageSource? texture)
    {
        var geometry = new MeshGeometry3D();
        foreach (var v in shape.Positions)
            geometry.Positions.Add(ToWpf(v));
        foreach (var n in shape.Normals)
            geometry.Normals.Add(new Vector3D(n.X, n.Z, -n.Y));
        foreach (var index in shape.Indices)
            geometry.TriangleIndices.Add(index);

        Material material;
        if (texture is not null)
        {
            foreach (var uv in shape.Uvs)
                // 不翻 V 轴：WPF 的 TextureCoordinates 里 V=0 就是位图首行，和 NIF/DirectX 同一个约定
                //（拿一张上半红下半蓝的位图打靶回读确认过，翻了会让每件衣服上下镜像）
                geometry.TextureCoordinates.Add(new Point(uv.X, uv.Y));

            var diffuse = TextureBrush(texture);
            // 同一张贴图再按三成强度当自发光叠一层：Viewport3D 是顶点级光照，
            // 背光面会被压成死黑，而真实衣服在阴影里仍有花纹。叠一层"自己带的亮度"
            // 把暗面抬起来，又不至于像调高环境光那样把明暗差一起洗平。
            var emissive = TextureBrush(texture);
            emissive.Opacity = 0.3;
            material = new MaterialGroup
            {
                Children = { new DiffuseMaterial(diffuse), new EmissiveMaterial(emissive) },
            };
        }
        else
        {
            material = new DiffuseMaterial(new SolidColorBrush(PlainColorFor(shape.Name)));
        }

        group.Children.Add(new GeometryModel3D(geometry, material)
        {
            // 只有网格自己声明了双面才补背面材质，否则 WPF 按绕序把背面整个剔掉——这正是 BodySlide
            // 的口径（GLSurface.cpp:1439-1458：剔 GL_BACK，除非 shader 双面或 stencil DRAW_BOTH）。
            // 实测本机 53% 的形状开了双面；剩下那些是游戏里的单面裙摆/披风，透过去看内衬另一侧
            // 才是进游戏所见的样子。不设 BackMaterial 时 WPF 是"整面消失"（离屏回读覆盖 0 像素），
            // 不是画成纯黑，所以这里不存在"怕它黑所以干脆画满"的取舍。
            BackMaterial = shape.DoubleSided ? material : null,
        });
    }

    /// <summary>贴图的画刷。Stretch 必须是 Fill 而不是默认的 Uniform：UV 已经铺满 0..1，
    /// Uniform 会按图片宽高比留边，留出来的边在 TileMode.None 下采样到的是边缘那一列像素，
    /// 衣服边缘就会拖出拉丝。</summary>
    static ImageBrush TextureBrush(ImageSource texture) => new(texture)
    {
        Stretch = Stretch.Fill,
        TileMode = TileMode.None,
    };

    /// <summary>形状名 → 调色板下标。用 FNV-1a 而不是 string.GetHashCode：后者的种子每个进程都换，
    /// 同一件衣服今天蓝明天绿，而"记住那件是啥颜色"恰恰是这个回退色唯一的用处。</summary>
    static Color PlainColorFor(string name)
    {
        uint hash = 2166136261;
        foreach (var c in name)
        {
            hash ^= char.ToLowerInvariant(c);
            hash *= 16777619;
        }
        return PlainPalette[hash % (uint)PlainPalette.Length];
    }

    /// <summary>取景要连垫进去的身体一起算：身体通常比一片披风大得多，只按衣服的包围盒摆相机
    /// 会把身体切得只剩一截；而离身体一大截的散落碎片（蒙皮件被留在绑定空间那一类）不参与取景，
    /// 见 <see cref="NifPreviewModel.FramingBounds"/>。</summary>
    void Fit(NifPreviewModel model, NifPreviewModel? body)
    {
        var (min, max) = NifPreviewModel.FramingBounds(model, body);

        _center = ToWpf((min + max) * 0.5f);
        _pan = new Vector3D();
        var lo = ToWpf(min);
        var hi = ToWpf(max);
        _half = new Vector3D((hi.X - lo.X) / 2, (hi.Y - lo.Y) / 2, (hi.Z - lo.Z) / 2);
    }

    void UpdateCamera()
    {
        var cos = Math.Cos(_pitch);
        var direction = new Vector3D(cos * Math.Sin(_yaw), Math.Sin(_pitch), cos * Math.Cos(_yaw));
        direction.Normalize();

        // 相机右/上基向量：取景要按衣服在这两个方向上的投影来算，而不是按包围球半径。
        // 人形体是细高的，包围球会把半径撑到身高的 0.55 倍，而竖直视场角只有 32°——
        // 固定 2.6×半径的旧算法在窄面板里必然切掉头和小腿。
        // 屏幕基向量。WPF 3D 是左手系：在 yaw=π（正对人物）处算下来，屏幕上 = +Y、屏幕右 = -X。
        // 用 Cross(视线方向, 世界上方) 才给出屏幕右；早先写成 Cross(视线, 世界上) 的变体会得到屏幕左，
        // 于是"往右拖"把模型推向左边——这是拿真实右键拖动对比前后截图发现的。
        var forward = direction * -1;
        var up = new Vector3D(0, 1, 0);
        up -= forward * Vector3D.DotProduct(forward, up);
        if (up.LengthSquared < 1e-9)
            up = new Vector3D(0, 0, 1);
        up.Normalize();
        var right = Vector3D.CrossProduct(forward, up);
        if (right.LengthSquared < 1e-9)
            right = new Vector3D(1, 0, 0);
        right.Normalize();

        // 轴对齐包围盒在单位轴 a 上的投影半长 = Σ|a·eᵢ|·hᵢ（比遍历 8 个角点便宜且同样精确）
        static float Extent(Vector3D a, Vector3D h) =>
            (float)(Math.Abs(a.X) * h.X + Math.Abs(a.Y) * h.Y + Math.Abs(a.Z) * h.Z);

        var halfVertical = Camera.FieldOfView * Math.PI / 360;
        // PerspectiveCamera.FieldOfView 是**竖直**角，水平角由视口宽高比推出来：
        // 面板被拖扁时受限的是横向，只看竖直角就还是会裁
        var aspect = Preview.ActualHeight > 0 ? Preview.ActualWidth / Preview.ActualHeight : 1.0;
        var halfHorizontal = Math.Atan(Math.Tan(halfVertical) * Math.Max(aspect, 0.05));

        var distance = Math.Max(
            Extent(up, _half) / Math.Tan(halfVertical),
            Extent(right, _half) / Math.Tan(halfHorizontal)) * 1.12 * _zoom;
        distance = Math.Max(distance, 0.01);

        _camRight = right;
        _camUp = up;
        _distance = distance;

        var target = _center + _pan;
        Camera.Position = target + direction * distance;
        Camera.LookDirection = direction * -distance;
        Camera.UpDirection = new Vector3D(0, 1, 0);
        if (_keyLight is { } key)
            key.Direction = direction * -1;
    }

    /// <summary>视口尺寸一变就要重新取景：距离是按宽高比算的，面板被拖窄还沿用旧距离就会裁掉衣服。
    /// 首次渲染发生在构造期，那时 ActualHeight 还是 0，也靠这一次回调补上正确的取景。</summary>
    void Viewport_Resized(object sender, SizeChangedEventArgs e) => UpdateCamera();

    /// <summary>切走再切回这一页时，视口先是量不到尺寸（标签页只把选中的那页接进视觉树），
    /// 回来时必须补一次取景；否则看到的还是按 aspect=1 算出来的旧距离，衣服会被裁。
    /// 反过来切走时要收掉鼠标捕获与在跑的预览：模态窗口时代这些由 <c>OnClosed</c> 兜，
    /// 现在窗口一直活着，不收就会把捕获与取消令牌漏到别的标签页上。
    /// （UIElement 没有可重写的 OnIsVisibleChanged，只有这个事件。）</summary>
    private void OnTabVisibilityChanged()
    {
        if (IsVisible)
        {
            EnsureBodyOptions();
            // 语言是在设置页换的，而这一页常驻、不会因换语言而重建：说明段、进度、分组头、状态行、
            // 候选行、批量范围全是代码拼的，DynamicResource 那半（列头、按钮、徽标）当场就换了，
            // 于是切回来看到的是半中半英。按 _textsLang 补一次全量重取词——
            // 与 RulePresetsPage「切回标签页就 Reload」同一口径。
            var relang = _textsLang != L10n.Current;
            // 分组在「分组生成」页随时会改，而本页只在 CurrentConflict 变化时重建：变了就重算分类
            //（分组头、下拉选项、「已在组内」徽标都是按它算的）。
            var rebuild = relang || GroupingSignature() != _groupingSignature;
            if (rebuild)
            {
                // 重算分类会把中栏与预览一起重建，所以下面那次"补一次预览"要跳过，
                // 否则同一行会被解析两遍。
                if (relang && _request is { } request)
                    ApplyTexts(request);
                RebuildGroups();
            }
            UpdateCamera();
            if (!rebuild && MeshVisual.Content is null && CandidateList.SelectedItem is CandidateRow row)
                ShowPreview(row);
        }
        else
        {
            _dragButton = null;
            ViewportHost.ReleaseMouseCapture();
            _previewCts?.Cancel();
        }
    }

    void Viewport_DragStart(object sender, MouseButtonEventArgs e) => BeginDrag(e, MouseButton.Left);

    void Viewport_RightDragStart(object sender, MouseButtonEventArgs e) => BeginDrag(e, MouseButton.Right);

    void BeginDrag(MouseButtonEventArgs e, MouseButton button)
    {
        _dragButton = button;
        _dragLast = e.GetPosition(ViewportHost);
        ViewportHost.CaptureMouse();
        e.Handled = true;
    }

    void Viewport_Drag(object sender, MouseEventArgs e)
    {
        if (_dragButton is not { } button)
            return;
        var now = e.GetPosition(ViewportHost);
        var dx = now.X - _dragLast.X;
        var dy = now.Y - _dragLast.Y;
        _dragLast = now;

        if (button == MouseButton.Right)
        {
            // 让内容跟着光标走：往右下拖 = 相机往左上移。按当前距离换算成世界单位，
            // 这样放大后平移得慢、缩小后平移得快，指针底下的那个点不会跑掉
            var worldPerPixel = 2 * Math.Tan(Camera.FieldOfView * Math.PI / 360) * _distance /
                                Math.Max(ViewportHost.ActualHeight, 1);
            _pan -= _camRight * (dx * worldPerPixel);
            _pan += _camUp * (dy * worldPerPixel);
        }
        else
        {
            _yaw -= dx * 0.011;
            _pitch = Math.Clamp(_pitch + dy * 0.011, -1.45, 1.45);
        }
        UpdateCamera();
    }

    void Viewport_DragEnd(object sender, MouseButtonEventArgs e)
    {
        _dragButton = null;
        ViewportHost.ReleaseMouseCapture();
    }

    void Viewport_Zoom(object sender, MouseWheelEventArgs e)
    {
        _zoom = Math.Clamp(_zoom * (e.Delta < 0 ? 1.12 : 0.89), 0.12, 9);
        UpdateCamera();
    }

    /// <summary>换身体就重出一次当前这一行：衣服的几何还在缓存里，重跑的只是配对与取景。</summary>
    void BodyChoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (BodyChoice.SelectedItem is not BodyOptionItem item)
            return;
        _body = item.Option;
        ShowPreview(CandidateList.SelectedItem as CandidateRow);
    }

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        _yaw = FrontYaw;
        _pitch = 0;
        _zoom = 1;
        _pan = new Vector3D();
        UpdateCamera();
    }

    private void Auto_Click(object sender, RoutedEventArgs e)
    {
        if (_request is null)
            return;
        _working = OutputConflicts.AutoPick(VisibleGroups(), _working);
        SaveAndRefresh();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_request is null)
            return;
        _working = OutputConflicts.Clear(VisibleGroups(), _working);
        SaveAndRefresh();
    }

    private void SaveAndRefresh()
    {
        _request?.Save(_working);
        RebuildGroups();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_request is not { } request)
            return;
        var chosen = request.Groups.Count(g => ChosenOf(g) is not null);
        if (chosen == 0)
        {
            Notify.Warn(Shell, L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_ConflictNothingToExport"));
            return;
        }
        if (!Notify.Confirm(Shell, L10n.Tr("L.Conflict_Export"),
                L10n.TrF("L.Confirm_ExportBuildSel", chosen, request.BuildSelectionPath)))
            return;

        var (ok, message) = request.Export();
        if (message is null)
            return;
        if (ok)
            Notify.Info(Shell, L10n.Tr("L.Conflict_Export"), message);
        else
            Notify.Warn(Shell, L10n.Tr("L.Conflict_Export"), message);
    }
}
