using System.ComponentModel;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using BSGroupGenerator.Core;
using BSGroupGenerator.Wpf.Services;
using BSGroupGenerator.Wpf.ViewModels;

namespace BSGroupGenerator.Wpf.Views.Pages;

/// <summary>
/// 输出冲突与选择页：多个模组的服装写同一个 .nif 时，为每一组指定由谁来生成
/// （对应 BodySlide 批建时那个"以下 set 会覆盖同样的文件"的弹窗）。
/// <para>
/// 勾选即时存进本工具自己的设置；「写入 BodySlide…」把它们落成 BodySlide 认得的
/// BuildSelection.xml。不写也能用——只是批建时 BodySlide 仍会逐次问一遍。
/// </para>
/// 左侧列表随过滤重建，所以"哪一组被选中"记在 <see cref="_selectedPath"/> 上而不是列表项对象上。
/// <para>
/// 这一页常驻而不是每次现开，所以批量操作与缓存跨标签页切换都还在——换来的是"切走再切回"
/// 不必重新解码一遍网格。代价是数据得靠 VM 推：<see cref="MainViewModel.CurrentConflict"/> 一变就重建，
/// 视口的取景/取消则挂在 <see cref="OnTabVisibilityChanged"/> 上（隐藏期量不到尺寸）。
/// </para>
/// </summary>
public partial class OutputConflictPage : UserControl
{
    /// <summary>冲突组在左栏那一行。<see cref="Name"/> 是文件名（不含目录），
    /// 完整路径留在 <see cref="Path"/> 里给 ToolTip 与中栏标题用。</summary>
    private sealed record GroupRow(OutputConflictGroup Group, string Name, string Status, bool Resolved, string Path);

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
        _working = new Dictionary<string, string>(request.Choices, StringComparer.Ordinal);
        _textures = request.Assets is { } assets ? new PreviewTextureCache(assets) : null;

        IntroLabel.Text = L10n.TrF("L.Conflict_Intro",
            request.Groups.Count(g => g.CrossMod), request.Groups.Count);
        // 一组跨模组的都没有时别把列表筛成空的：这时默认显示全部
        OnlyCrossCheck.IsChecked = request.Groups.Any(g => g.CrossMod);
        OnlyCrossCheck.ToolTip = L10n.TrF("L.Conflict_OnlyCrossModTip",
            request.Groups.Count(g => g.CrossMod), request.Groups.Count);
        RescanButton.ToolTip = L10n.Tr("L.Conflict_RescanTip");
        AutoButton.ToolTip = L10n.Tr("L.Conflict_AutoAllTip");
        ClearButton.ToolTip = L10n.Tr("L.Conflict_ClearAllTip");
        ExportButton.ToolTip = L10n.Tr("L.Conflict_ExportTip");
        SetActionsEnabled(request.Groups.Count > 0);
        // 扫完一组冲突都没有时也要留着这句：那种情况下三栏同样是全空的，
        // 而 CurrentConflict 并不是 null（清单是空的，不是没送来）
        EmptyStateLabel.Visibility = request.Groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EnsureBodyOptions();

        RebuildGroups();
    }

    private void SetActionsEnabled(bool on) =>
        AutoButton.IsEnabled = ClearButton.IsEnabled = ExportButton.IsEnabled = on;

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

    /// <summary>当前过滤条件下看到的组（批量操作只作用于它们——筛出来一批"某某模组的"再点自动选择，
    /// 不该顺手改掉屏幕外的组）。</summary>
    private List<OutputConflictGroup> VisibleGroups() =>
        ((GroupList.ItemsSource as IEnumerable<GroupRow>) ?? Array.Empty<GroupRow>())
        .Select(r => r.Group)
        .ToList();

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

    private void RebuildGroups()
    {
        if (_request is not { } request)
            return;
        var filter = FilterBox.Text.Trim();
        // 先按"只看跨模组"筛出这一轮处理的范围，再在范围内套过滤词：计数说的是用户眼前这份清单
        var pool = OnlyCrossCheck.IsChecked == true
            ? request.Groups.Where(g => g.CrossMod).ToList()
            : request.Groups;
        var rows = new List<GroupRow>();
        foreach (var group in pool)
        {
            if (filter.Length > 0 && !MatchesFilter(group, filter))
                continue;
            var chosen = ChosenOf(group);
            rows.Add(new GroupRow(
                group,
                SplitTarget(group.TargetDisplay).Name,
                chosen is null
                    ? L10n.Tr("L.Conflict_StatusUnresolved")
                    : L10n.TrF("L.Conflict_StatusChosen", chosen),
                chosen is not null,
                group.TargetDisplay));
        }

        _rebuilding = true;
        GroupList.ItemsSource = rows;
        GroupList.SelectedItem =
            rows.Find(r => r.Group.OutputFilePath == _selectedPath) ?? rows.FirstOrDefault();
        _selectedPath = (GroupList.SelectedItem as GroupRow)?.Group.OutputFilePath;
        _rebuilding = false;

        GroupsCountLabel.Text = L10n.TrF("L.Conflict_GroupCount", rows.Count);
        BatchScopeLabel.Text = L10n.TrF("L.Conflict_BatchScope", rows.Count);
        var filtering = filter.Length > 0 || OnlyCrossCheck.IsChecked == true;
        FilterCountLabel.Text = filtering
            ? L10n.TrF("L.Conflict_FilterCount", rows.Count, pool.Count)
            : "";
        FilterCountLabel.Visibility = filtering ? Visibility.Visible : Visibility.Collapsed;

        UpdateProgress();
        RebuildCandidates();
    }

    private void OnlyCross_Changed(object sender, RoutedEventArgs e) => RebuildGroups();

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

    NifPreviewModel? BodyFor(BodyOption option)
    {
        if (option is BodyOption.None or BodyOption.Auto || _request?.Assets is not { } assets)
            return null;
        if (_bodies.TryGetValue(option, out var cached))
            return cached;

        NifPreviewModel? model = null;
        if (assets.TryRead(option == BodyOption.Female ? BodyFemaleNif : BodyMaleNif) is { } bytes)
            NifPreviewLoader.TryLoad(bytes, out model, out _);
        _bodies[option] = model;
        return model;
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
            // 命中时省掉的是解析 nif 与解码贴图，这才是贵的那部分
            var loaded = await Task.Run(() =>
            {
                if (!_meshCache.TryGetValue(path, out var outfit))
                {
                    var ok = NifPreviewLoader.TryLoad(path, out var model, out var error);
                    outfit = ok
                        ? (model, null, model?.Shapes.Select(s => _textures?.Get(s.TexturePath)).ToArray())
                        : (null, error, null);
                    if (_meshCache.Count > 12)
                        _meshCache.Clear();
                    _meshCache[path] = outfit;
                }

                var body = BodyFor(ResolveBody(wanted, outfit.Model));
                var bodyBrushes = body?.Shapes.Select(s => _textures?.Get(s.TexturePath)).ToArray();
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
            // 背面永远同材质：实测一半形状开了 DoubleSided，而没开的也只是游戏里靠背面剔除
            // 省开销，裙摆内侧、披风内衬在预览里画成纯黑只会让人以为模型坏了
            BackMaterial = material,
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
    /// 会把身体切得只剩一截。</summary>
    void Fit(NifPreviewModel model, NifPreviewModel? body)
    {
        var min = model.BoundsMin;
        var max = model.BoundsMax;
        if (body is not null)
        {
            min = Vector3.Min(min, body.BoundsMin);
            max = Vector3.Max(max, body.BoundsMax);
        }

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
            UpdateCamera();
            if (MeshVisual.Content is null && CandidateList.SelectedItem is CandidateRow row)
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
