using SliderSorter.Core;
using SliderSorter.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SliderSorter.Wpf.ViewModels;

/// <summary>打开「输出冲突」窗口所需的一切：数据 + 回写通道。</summary>
public sealed class ConflictRequest
{
    public required IReadOnlyList<OutputConflictGroup> Groups { get; init; }
    public required IReadOnlyDictionary<string, string> Choices { get; init; }

    /// <summary>该 slider set 是否已经被划进某个分组。</summary>
    public required Func<string, bool> IsGrouped { get; init; }

    /// <summary>用户自己的分组（「分组生成」页那批），按显示顺序。冲突页据此把清单按组分类
    /// （<see cref="OutputConflicts.CategorizeByUserGroup"/>），于是"这个分组里有哪些冲突"一眼可见。
    /// 是**活**的取值器而不是快照，理由同 <see cref="IsGrouped"/>：分组在另一页随时会改，
    /// 而本页只在 <see cref="MainViewModel.CurrentConflict"/> 变化时重建。</summary>
    public required Func<IReadOnlyList<SliderGroup>> UserGroups { get; init; }

    /// <summary>左栏那个分组当前是不是**收起**的（持久化在设置里）。与 <see cref="UserGroups"/> 一样
    /// 是活取值器：分组头是每次重建时现造的，而"收起"这个动作就发生在页面上、不经 VM。
    /// 键是分组名，空串 = 「未入组」那一桶。</summary>
    public required Func<string, bool> IsGroupCollapsed { get; init; }

    /// <summary>记下某个分组的折叠状态（true = 收起）。页面点一下分组头就会调它。</summary>
    public required Action<string, bool> SetGroupCollapsed { get; init; }

    /// <summary>批量记住折叠状态（左栏「全部展开/全部折叠」）。逐条走上面那个就是
    /// 一次点击重写几十遍 settings.json，这里改完整份清单只落盘一次。</summary>
    public required Action<IReadOnlyDictionary<string, bool>> SetGroupsCollapsed { get; init; }

    /// <summary>该 set 的源网格（BodySlide 建它时读的 .nif）在本机的实际路径；解析不到为 null。</summary>
    public required Func<string, string?> SourceNifOf { get; init; }

    /// <summary>全量保存工作副本到本工具的设置（不碰 BodySlide）。</summary>
    public required Action<IReadOnlyDictionary<string, string>> Save { get; init; }

    /// <summary>选择要写去的位置（BuildSelection.xml 全路径，落在当前输出位置里）；还没有写入目标时为空串。
    /// 与 <see cref="IsGrouped"/> 一样是**活**的取值器而不是快照：输出位置（模式 / 自定义目录 / 所选实例）
    /// 在设置页随时会改，而本页只在 <see cref="MainViewModel.CurrentConflict"/> 变化时重建——
    /// 快照会让确认框念出一个导出时并不使用的路径。
    /// 确认框由窗口来弹——它的宿主才是这个对话框。</summary>
    public required Func<string> BuildSelectionPath { get; init; }

    /// <summary>把已保存的选择写进 BodySlide 的 BuildSelection.xml（调用方须先取得用户同意）。
    /// Message = 该告诉用户的话（成功文案或错误原因）。</summary>
    public required Func<(bool Ok, string? Message)> Export { get; init; }

    /// <summary>按游戏口径解析贴图/网格的数据视图（跨覆盖层从强到弱，散文件优先于归档）。
    /// 没有扫描结果时为 null，预览就退回纯色。</summary>
    public required GameDataResolver? Assets { get; init; }
}

public partial class MainViewModel
{
    /// <summary>冲突页要展示的那一份请求。页面常驻（不再是模态窗口），所以"有没有东西可看"
    /// 得是一个可观察属性，而不是构造窗口时传进去就完事的参数。
    /// 每次赋值都是新建的实例，因此重复点「输出冲突」也会触发 PropertyChanged，页面才会重建。</summary>
    [ObservableProperty] private ConflictRequest? _currentConflict;

    [ObservableProperty] private int _outputConflictCount;

    /// <summary>当前扫描发现的全部输出文件冲突（含同一模组内部的），已套用用户的选择。
    /// 顺序稳定，窗口直接拿来渲染。</summary>
    public IReadOnlyList<OutputConflictGroup> ConflictGroups { get; private set; } = [];

    /// <summary>其中"多个模组改同一件衣服"的组数——状态栏与树里标注的是这个。</summary>
    public IReadOnlyList<OutputConflictGroup> CrossModConflicts { get; private set; } = [];

    public bool HasOutputConflicts => OutputConflictCount > 0;

    partial void OnOutputConflictCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasOutputConflicts));
        OnPropertyChanged(nameof(ConflictStatusText));
    }

    /// <summary>状态栏那一段可点击文字（0 组时为空，视图据此收起占位）。</summary>
    public string ConflictStatusText => OutputConflictCount > 0
        ? L10n.TrF("L.Vm_ConflictsInline", OutputConflictCount, CrossModConflicts.Count(g => g.Chosen is null))
        : "";

    /// <summary>扫描结束后重算冲突清单。</summary>
    private void RefreshConflictsFromScan()
    {
        ConflictGroups = Scan is { } scan
            ? OutputConflicts.Detect(scan, Settings.OutputChoices)
            : [];
        CrossModConflicts = ConflictGroups.Where(g => g.CrossMod).ToList();
        OutputConflictCount = CrossModConflicts.Count;
        OnPropertyChanged(nameof(ConflictStatusText));
        // 冲突页是一等入口（标签头常驻、还挂着计数徽标），不能等用户去点菜单才有内容：
        // 扫描完就把清单备好，切过去立刻看得到；扫完发现冲突没了也要推，否则页面停在上一轮的清单上。
        // 建这份请求本身不碰磁盘（GameDataResolver 只存层序，源网格索引是一次内存遍历）。
        CurrentConflict = BuildConflictRequest();
        if (ConflictGroups.Count == 0)
            return;
        Log(L10n.TrF("L.Log_OutputConflictsFound", OutputConflictCount, ConflictGroups.Count,
            CrossModConflicts.Count(g => g.Chosen is null)));
    }

    /// <summary>语言切换后重算状态栏文案（它是由代码拼的）。</summary>
    public void RefreshConflictText() => OnPropertyChanged(nameof(ConflictStatusText));

    /// <summary>set 名 → 源网格路径。同名 set 只会有一个进入清单（先见者胜），忽略大小写查是为了
    /// 界面传来的名字与扫描结果的大小写差异不该让预览莫名变成"没有源网格"。</summary>
    private Dictionary<string, string?> BuildSourceNifIndex()
    {
        var index = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var outfit in Scan?.Outfits ?? [])
            if (!string.IsNullOrEmpty(outfit.SourceNif))
                index.TryAdd(outfit.Name, outfit.SourceNif);
        return index;
    }

    /// <summary>状态栏那枚冲突计数：点了切到「输出归属」页。没东西可看时不切页——
    /// 跳到一个空白页比原地不动更让人困惑。</summary>
    [RelayCommand]
    private void OpenConflicts()
    {
        if (Scan is null)
        {
            NotifyUser(L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_ScanIncomplete"), warning: true);
            return;
        }
        if (ConflictGroups.Count == 0)
        {
            // 一组都没有才说"没有冲突"：只有同模组内部的共用输出文件时，页面照样有东西可选
            NotifyUser(L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_NoConflicts"), warning: true);
            return;
        }

        CurrentConflict = BuildConflictRequest();
        SelectedTab = TabConflicts;
    }

    /// <summary>给冲突页攒一份请求：数据 + 回写通道。每次都是新实例，页面据此判断"该重建了"。</summary>
    private ConflictRequest BuildConflictRequest()
    {
        var sourceNif = BuildSourceNifIndex();
        return new ConflictRequest
        {
            Groups = ConflictGroups,
            Choices = Settings.OutputChoices,
            IsGrouped = Store.IsInAnyGroup,
            UserGroups = () => Store.Groups,
            IsGroupCollapsed = name => Settings.CollapsedConflictGroups.Contains(name, StringComparer.Ordinal),
            SetGroupCollapsed = SetConflictGroupCollapsed,
            SetGroupsCollapsed = SetConflictGroupsCollapsed,
            SourceNifOf = name => sourceNif.TryGetValue(name, out var path) ? path : null,
            Save = SaveConflictChoices,
            BuildSelectionPath = () => ResolveBuildSelectionPath() ?? "",
            Export = ExportConflictChoices,
            Assets = BuildAssetResolver(),
        };
    }

    /// <summary>记住「输出冲突」页左栏某个分组的折叠状态。改完立刻落盘——这个状态没有"保存"按钮，
    /// 用户点一下就是最终意图；攒着不写的话，下次启动看到的还是旧状态。
    /// <para>
    /// 先判重再写：分组头点开又点回都会走到这里，不判重就是每点一下重写一遍 settings.json。
    /// </para></summary>
    private void SetConflictGroupCollapsed(string groupName, bool collapsed) =>
        SetConflictGroupsCollapsed(new Dictionary<string, bool> { [groupName] = collapsed });

    private void SetConflictGroupsCollapsed(IReadOnlyDictionary<string, bool> collapsedByName)
    {
        var names = Settings.CollapsedConflictGroups;
        var changed = false;
        foreach (var (groupName, collapsed) in collapsedByName)
        {
            var at = names.FindIndex(n => string.Equals(n, groupName, StringComparison.Ordinal));
            if (collapsed == (at >= 0))
                continue; // 点开又点回都会走到这里，不判重就是每点一下重写一遍设置
            if (collapsed)
                names.Add(groupName);
            else
                names.RemoveAt(at);
            changed = true;
        }
        if (changed)
            Settings.Save();
    }

    /// <summary>预览用的数据视图。层序必须和扫描时一模一样（模组从强到弱，最后才是真实的 Data），
    /// 否则"预览里的贴图"和"BodySlide 建出来的 nif 进游戏后实际引用的贴图"就不是同一份。</summary>
    private GameDataResolver? BuildAssetResolver()
    {
        var roots = Mods.Select(m => m.Dir).ToList();
        var gameData = Resolution?.GameDataPath;
        if (!string.IsNullOrWhiteSpace(gameData))
            roots.Add(gameData!);
        return roots.Count == 0 ? null : new GameDataResolver(roots);
    }

    /// <summary>BuildSelection.xml 的落点：当前输出位置里的 <c>CalienteTools\BodySlide</c> 目录
    /// （自定义模式就是所选目录）——与分组文件同处一个模组，见 <see cref="OutputTarget"/> 里那段
    /// 「MO2 下 AppDir 是虚拟路径」的说明。还没有写入目标（未选 BodySlide 安装 / 未完成扫描）时为 null。</summary>
    private string? ResolveBuildSelectionPath() =>
        ResolveWriteTarget() is { } target ? BuildSelectionFile.PathFor(target.BuildSelectionDir) : null;

    /// <summary>只动本次扫描认得的冲突路径：其它 BodySlide 安装留下的、或对应模组已消失的条目都不碰。
    /// <para>
    /// 一组冲突里成员声明的路径可能只差大小写（分组键忽略大小写，见 <see cref="OutputConflicts.Detect"/>），
    /// 而设置是按逐字节存的——所以清理时要把这组的<b>所有</b>拼写都清掉，否则留着另一拼写的老选择
    /// 会被忽略大小写的视图重新认成"已指定"，用户点了清除却清不干净。
    /// </para></summary>
    private void SaveConflictChoices(IReadOnlyDictionary<string, string> working)
    {
        if (Scan is not { } scan)
            return;

        var changed = false;
        foreach (var group in ConflictGroups)
        {
            var chosen = working.TryGetValue(group.OutputFilePath, out var name) && !string.IsNullOrEmpty(name)
                ? name
                : null;
            // 候选里已经没有它了（模组被禁用/卸载）——别把死选择存回去
            if (chosen is not null && !group.Candidates.Any(c => c.Name == chosen))
                chosen = null;

            foreach (var spelling in group.KeySpellings)
            {
                if (chosen is null)
                    changed |= Settings.OutputChoices.Remove(spelling);
                else if (spelling != group.OutputFilePath)
                    changed |= Settings.OutputChoices.Remove(spelling); // 统一存到规范拼写那条
                else if (!Settings.OutputChoices.TryGetValue(spelling, out var old) || old != chosen)
                {
                    Settings.OutputChoices[spelling] = chosen;
                    changed = true;
                }
            }
        }

        if (!changed)
            return;
        Settings.Save();
        // 重算 Chosen：下次打开窗口、诊断报告与导出都读到最新状态
        ConflictGroups = OutputConflicts.Detect(scan, Settings.OutputChoices);
        CrossModConflicts = ConflictGroups.Where(g => g.CrossMod).ToList();
        OutputConflictCount = CrossModConflicts.Count;
    }

    /// <summary>导出：展开成"每种拼写一条"由 Core 算（<see cref="OutputConflicts.ExportEntries"/>），
    /// 这里只负责落点与反馈。落点跟随输出位置（<see cref="ResolveBuildSelectionPath"/>），不再是
    /// 所选 BodySlide 安装的真实目录。</summary>
    private (bool Ok, string? Message) ExportConflictChoices()
    {
        if (ResolveBuildSelectionPath() is not { } path)
            return (false, L10n.Tr("L.Msg_NoExportTarget"));
        var managed = OutputConflicts.ManagedPaths(ConflictGroups);
        var desired = OutputConflicts.ExportEntries(ConflictGroups);
        if (desired.Count == 0)
            return (false, L10n.Tr("L.Msg_ConflictNothingToExport"));

        if (!BuildSelectionFile.TryExport(path, desired, managed, out var written, out var removed, out var error))
            return (false, error ?? L10n.Tr("L.Msg_ExportFail"));

        Log(L10n.TrF("L.Log_BuildSelWritten", written, removed, path));
        return (true, L10n.TrF("L.Msg_BuildSelWritten", written, removed, path));
    }
}
