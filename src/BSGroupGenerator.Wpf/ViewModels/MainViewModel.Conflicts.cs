using BSGroupGenerator.Core;
using BSGroupGenerator.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BSGroupGenerator.Wpf.ViewModels;

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

    /// <summary>该 set 的源网格（BodySlide 建它时读的 .nif）在本机的实际路径；解析不到为 null。</summary>
    public required Func<string, string?> SourceNifOf { get; init; }

    /// <summary>全量保存工作副本到本工具的设置（不碰 BodySlide）。</summary>
    public required Action<IReadOnlyDictionary<string, string>> Save { get; init; }

    /// <summary>选择要写去的位置（BodySlide 的 BuildSelection.xml 全路径）；还没选中 BodySlide 安装时为空串。
    /// 确认框由窗口来弹——它的宿主才是这个对话框。</summary>
    public required string BuildSelectionPath { get; init; }

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

    /// <summary>「工具 → 输出冲突与选择…」与状态栏的冲突计数。没东西可看时不切页——
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
            SourceNifOf = name => sourceNif.TryGetValue(name, out var path) ? path : null,
            Save = SaveConflictChoices,
            BuildSelectionPath = _bsAppDir is null ? "" : BuildSelectionFile.PathFor(_bsAppDir),
            Export = ExportConflictChoices,
            Assets = BuildAssetResolver(),
        };
    }

    /// <summary>记住「输出冲突」页左栏某个分组的折叠状态。改完立刻落盘——这个状态没有"保存"按钮，
    /// 用户点一下就是最终意图；攒着不写的话，下次启动看到的还是旧状态。
    /// <para>
    /// 先判重再写：分组头点开又点回都会走到这里，不判重就是每点一下重写一遍 settings.json。
    /// </para></summary>
    private void SetConflictGroupCollapsed(string groupName, bool collapsed)
    {
        var names = Settings.CollapsedConflictGroups;
        var at = names.FindIndex(n => string.Equals(n, groupName, StringComparison.Ordinal));
        if (collapsed == (at >= 0))
            return;
        if (collapsed)
            names.Add(groupName);
        else
            names.RemoveAt(at);
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

    /// <summary>只动本次扫描认得的冲突路径：其它 BodySlide 安装留下的、或对应模组已消失的条目都不碰。</summary>
    private void SaveConflictChoices(IReadOnlyDictionary<string, string> working)
    {
        if (Scan is not { } scan)
            return;

        var changed = false;
        foreach (var group in ConflictGroups)
        {
            var path = group.OutputFilePath;
            var chosen = working.TryGetValue(path, out var name) && !string.IsNullOrEmpty(name) ? name : null;
            // 候选里已经没有它了（模组被禁用/卸载）——别把死选择存回去
            if (chosen is not null && !group.Candidates.Any(c => c.Name == chosen))
                chosen = null;

            if (chosen is null)
                changed |= Settings.OutputChoices.Remove(path);
            else if (!Settings.OutputChoices.TryGetValue(path, out var old) || old != chosen)
            {
                Settings.OutputChoices[path] = chosen;
                changed = true;
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

    private (bool Ok, string? Message) ExportConflictChoices()
    {
        if (string.IsNullOrEmpty(_bsAppDir))
            return (false, L10n.Tr("L.Msg_NoBodySlideDir"));

        var path = BuildSelectionFile.PathFor(_bsAppDir);
        var managed = ConflictGroups.Select(g => g.OutputFilePath).ToList();
        var desired = Settings.OutputChoices
            .Where(p => managed.Contains(p.Key, StringComparer.Ordinal))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (desired.Count == 0)
            return (false, L10n.Tr("L.Msg_ConflictNothingToExport"));

        if (!BuildSelectionFile.TryExport(path, desired, managed, out var written, out var removed, out var error))
            return (false, error ?? L10n.Tr("L.Msg_ExportFail"));

        Log(L10n.TrF("L.Log_BuildSelWritten", written, removed, path));
        return (true, L10n.TrF("L.Msg_BuildSelWritten", written, removed));
    }
}
