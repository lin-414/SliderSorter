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

namespace BSGroupGenerator.Wpf.Views;

/// <summary>
/// 输出文件冲突：多个模组的服装写同一个 .nif 时，为每一组指定由谁来生成
/// （对应 BodySlide 批建时那个"以下 set 会覆盖同样的文件"的弹窗）。
/// <para>
/// 勾选即时存进本工具自己的设置；「写入 BodySlide…」把它们落成 BodySlide 认得的
/// BuildSelection.xml。不写也能用——只是批建时 BodySlide 仍会逐次问一遍。
/// </para>
/// 左侧列表随过滤重建，所以"哪一组被选中"记在 <see cref="_selectedPath"/> 上而不是列表项对象上。
/// </summary>
public partial class OutputConflictWindow : Window
{
    private sealed record GroupRow(OutputConflictGroup Group, string Target, string Status, bool Resolved);

    private sealed record CandidateRow(
        string? SetName, string Label, string Info, bool IsWinner, bool Grouped, bool Strongest, string? SourceNif)
    {
        public Visibility GroupedVisibility => Grouped ? Visibility.Visible : Visibility.Collapsed;
        public Visibility StrongestVisibility => Strongest ? Visibility.Visible : Visibility.Collapsed;
    }

    private readonly ConflictRequest _request;
    private Dictionary<string, string> _working; // 批量操作会整体换成 Core 算出来的新字典
    private string? _selectedPath;
    private bool _rebuilding;

    public OutputConflictWindow(ConflictRequest request)
    {
        InitializeComponent();
        _request = request;
        _working = new Dictionary<string, string>(request.Choices, StringComparer.Ordinal);

        IntroLabel.Text = L10n.TrF("L.Conflict_Intro",
            request.Groups.Count(g => g.CrossMod), request.Groups.Count);
        // 一组跨模组的都没有时别把列表筛成空的：这时默认显示全部
        OnlyCrossCheck.IsChecked = request.Groups.Any(g => g.CrossMod);
        AutoButton.ToolTip = L10n.Tr("L.Conflict_AutoAllTip");
        ClearButton.ToolTip = L10n.Tr("L.Conflict_ClearAllTip");
        ExportButton.ToolTip = L10n.Tr("L.Conflict_ExportTip");
        FilterBox.TextChanged += (_, _) => RebuildGroups();
        AddLights();

        RebuildGroups();
    }

    private OutputConflictGroup? SelectedGroup => (GroupList.SelectedItem as GroupRow)?.Group;

    /// <summary>一盏环境光让背光面不糊成一团，两盏方向光拉出体积感。光源颜色固定白：
    /// 这是渲染参数而不是界面配色，跟着主题变会让同一件衣服在两套主题下看着不像同一件。</summary>
    void AddLights()
    {
        var lights = new Model3DGroup
        {
            Children =
            {
                // 环境光压暗一档：满白会把方向光的明暗差洗平，网格看起来像一张剪纸
                new AmbientLight(Color.FromRgb(0x6E, 0x6E, 0x6E)),
                new DirectionalLight(Colors.White, new Vector3D(0.3, 0.5, 0.85)),
                new DirectionalLight(Color.FromRgb(0x80, 0x80, 0x80), new Vector3D(-0.4, -0.25, -0.35)),
            },
        };
        LightVisual.Content = lights;
    }

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

    private void RebuildGroups()
    {
        var filter = FilterBox.Text.Trim();
        // 先按"只看跨模组"筛出这一轮处理的范围，再在范围内套过滤词：计数说的是用户眼前这份清单
        var pool = OnlyCrossCheck.IsChecked == true
            ? _request.Groups.Where(g => g.CrossMod).ToList()
            : _request.Groups;
        var rows = new List<GroupRow>();
        foreach (var group in pool)
        {
            if (filter.Length > 0 && !MatchesFilter(group, filter))
                continue;
            var chosen = ChosenOf(group);
            rows.Add(new GroupRow(
                group,
                group.TargetDisplay,
                (chosen is null
                    ? L10n.Tr("L.Conflict_StatusUnresolved")
                    : L10n.TrF("L.Conflict_StatusChosen", chosen))
                + " · " + L10n.TrF("L.Conflict_CandidateCount", group.Candidates.Count),
                chosen is not null));
        }

        _rebuilding = true;
        GroupList.ItemsSource = rows;
        GroupList.SelectedItem =
            rows.Find(r => r.Group.OutputFilePath == _selectedPath) ?? rows.FirstOrDefault();
        _selectedPath = (GroupList.SelectedItem as GroupRow)?.Group.OutputFilePath;
        _rebuilding = false;

        var filtering = filter.Length > 0 || OnlyCrossCheck.IsChecked == true;
        FilterCountLabel.Text = filtering
            ? L10n.TrF("L.Conflict_FilterCount", rows.Count, pool.Count)
            : "";
        FilterCountLabel.Visibility = filtering ? Visibility.Visible : Visibility.Collapsed;

        RebuildCandidates();
    }

    private void OnlyCross_Changed(object sender, RoutedEventArgs e) => RebuildGroups();

    private void RebuildCandidates()
    {
        if (SelectedGroup is not { } group)
        {
            CandidateList.ItemsSource = Array.Empty<CandidateRow>();
            TargetLabel.Text = L10n.Tr("L.Conflict_NoneSelected");
            ShowPreview(null);
            return;
        }

        TargetLabel.Text = L10n.TrF("L.Conflict_Target", group.TargetDisplay);
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
                _request.IsGrouped(candidate.Name),
                Strongest: i == 0,
                _request.SourceNifOf(candidate.Name)));
        }
        rows.Add(new CandidateRow(null, L10n.Tr("L.Conflict_AskMeAgain"), "", chosen is null, false, false, null));
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

    // ── 3D 预览：读这个 set 的源网格，也就是 BodySlide 建它时读的同一个 .nif ──

    /// <summary>缓存连失败结果一起存：同一个坏文件被反复选中时不该反复重试。</summary>
    private readonly Dictionary<string, (NifMesh? Mesh, string? Error)> _meshCache = new(StringComparer.Ordinal);

    private CancellationTokenSource? _previewCts;
    private Point3D _center;
    private double _radius = 100;
    /// <summary>Creation Engine 的模型朝 +Y（nif 是 Z 朝上的右手系），映射到 WPF 后正面落在 -Z，
    /// 所以机位从 π 起——0 会一上来就给人看屁股，而且镜像换轴会把左右翻反，不能那么干。</summary>
    private const double FrontYaw = Math.PI;

    private double _yaw = FrontYaw;
    private double _pitch;
    private double _zoom = 1;
    private bool _dragging;
    private Point _dragLast;

    async void ShowPreview(CandidateRow? row)
    {
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        PreviewTitle.Text = row?.Label ?? "";
        MeshStats.Text = "";
        if (row is null)
        {
            MeshVisual.Content = null;
            PreviewStatus.Text = "";
            return;
        }
        if (string.IsNullOrEmpty(row.SourceNif))
        {
            MeshVisual.Content = null;
            PreviewStatus.Text = L10n.Tr("L.Preview_NoSource");
            return;
        }

        var path = row.SourceNif!;
        if (_meshCache.TryGetValue(path, out var cached))
        {
            Render(cached.Mesh, cached.Error);
            return;
        }

        PreviewStatus.Text = L10n.Tr("L.Preview_Loading");
        try
        {
            var loaded = await Task.Run(() =>
            {
                var ok = NifMeshLoader.TryLoad(path, out var mesh, out var error);
                return (Mesh: ok ? mesh : null, Error: ok ? null : error);
            }, cts.Token);

            // 排队期间又点了别的行：这一份已经没人等了，丢掉（连缓存都不写，免得把 UI 状态带乱）
            if (!ReferenceEquals(_previewCts, cts))
                return;

            if (_meshCache.Count > 12)
                _meshCache.Clear();
            _meshCache[path] = loaded;
            Render(loaded.Mesh, loaded.Error);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {
            // 被更新的预览取代，安静地丢掉
        }
    }

    void Render(NifMesh? mesh, string? error)
    {
        if (mesh is null)
        {
            MeshVisual.Content = null;
            PreviewStatus.Text = error ?? L10n.Tr("L.Preview_NoSource");
            return;
        }

        PreviewStatus.Text = "";
        MeshVisual.Content = BuildModel(mesh);
        MeshStats.Text = L10n.TrF("L.Preview_Stats", mesh.TriangleCount, mesh.ShapeCount);
        Fit(mesh);
        UpdateCamera();
    }

    /// <summary>nif 是 Z 朝上的右手系（单位厘米），WPF 世界坐标 Y 朝上：绕 X 轴转 90° 即可，
    /// 长度不变，所以按包围球取景时不需要额外缩放。</summary>
    static Point3D ToWpf(Vector3 v) => new(v.X, v.Z, -v.Y);

    Model3D BuildModel(NifMesh mesh)
    {
        var geometry = new MeshGeometry3D();
        foreach (var v in mesh.Positions)
            geometry.Positions.Add(ToWpf(v));
        foreach (var n in mesh.Normals)
            geometry.Normals.Add(new Vector3D(n.X, n.Z, -n.Y));
        foreach (var index in mesh.Indices)
            geometry.TriangleIndices.Add(index);

        // 网格颜色取主题的文字色：暗色下是浅沙色、亮色下是深蓝黑，两种主题里都能从卡片底色上跳出来。
        // 光源颜色是渲染参数而不是界面配色，保持固定白。
        var brush = (SolidColorBrush)TryFindResource("B.Text")!;
        var material = new DiffuseMaterial(brush);
        return new GeometryModel3D(geometry, material) { BackMaterial = material };
    }

    void Fit(NifMesh mesh)
    {
        _center = ToWpf((mesh.BoundsMin + mesh.BoundsMax) * 0.5f);
        _radius = Math.Max((mesh.BoundsMax - mesh.BoundsMin).Length() * 0.5f, 1f);
    }

    void UpdateCamera()
    {
        var distance = Math.Max(_radius * 2.6 * _zoom, 0.01);
        var cos = Math.Cos(_pitch);
        var direction = new Vector3D(cos * Math.Sin(_yaw), Math.Sin(_pitch), cos * Math.Cos(_yaw));
        direction.Normalize();
        Camera.Position = _center + direction * distance;
        Camera.LookDirection = direction * -distance;
        Camera.UpDirection = new Vector3D(0, 1, 0);
    }

    void Viewport_DragStart(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragLast = e.GetPosition(ViewportHost);
        ViewportHost.CaptureMouse();
    }

    void Viewport_Drag(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;
        var now = e.GetPosition(ViewportHost);
        _yaw -= (now.X - _dragLast.X) * 0.011;
        _pitch = Math.Clamp(_pitch + (now.Y - _dragLast.Y) * 0.011, -1.45, 1.45);
        _dragLast = now;
        UpdateCamera();
    }

    void Viewport_DragEnd(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        ViewportHost.ReleaseMouseCapture();
    }

    void Viewport_Zoom(object sender, MouseWheelEventArgs e)
    {
        _zoom = Math.Clamp(_zoom * (e.Delta < 0 ? 1.12 : 0.89), 0.12, 9);
        UpdateCamera();
    }

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        _yaw = FrontYaw;
        _pitch = 0;
        _zoom = 1;
        UpdateCamera();
    }

    protected override void OnClosed(EventArgs e)
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        base.OnClosed(e);
    }

    private void Auto_Click(object sender, RoutedEventArgs e)
    {
        _working = OutputConflicts.AutoPick(VisibleGroups(), _working);
        SaveAndRefresh();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _working = OutputConflicts.Clear(VisibleGroups(), _working);
        SaveAndRefresh();
    }

    private void SaveAndRefresh()
    {
        _request.Save(_working);
        RebuildGroups();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _request.Groups.Count(g => ChosenOf(g) is not null);
        if (chosen == 0)
        {
            Notify.Warn(this, L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_ConflictNothingToExport"));
            return;
        }
        if (!Notify.Confirm(this, L10n.Tr("L.Conflict_Export"),
                L10n.TrF("L.Confirm_ExportBuildSel", chosen, _request.BuildSelectionPath)))
            return;

        var (ok, message) = _request.Export();
        if (message is null)
            return;
        if (ok)
            Notify.Info(this, L10n.Tr("L.Conflict_Export"), message);
        else
            Notify.Warn(this, L10n.Tr("L.Conflict_Export"), message);
    }
}
