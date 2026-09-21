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
}

public partial class MainViewModel
{
    /// <summary>「工具 → 输出冲突…」与状态栏的冲突计数触发，视图负责弹窗。</summary>
    public event Action<ConflictRequest>? ConflictsRequested;

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
            // 一组都没有才说"没有冲突"：只有同模组内部的共用输出文件时，窗口照样有东西可选
            NotifyUser(L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_NoConflicts"), warning: true);
            return;
        }

        var sourceNif = BuildSourceNifIndex();
        ConflictsRequested?.Invoke(new ConflictRequest
        {
            Groups = ConflictGroups,
            Choices = Settings.OutputChoices,
            IsGrouped = Store.IsInAnyGroup,
            SourceNifOf = name => sourceNif.TryGetValue(name, out var path) ? path : null,
            Save = SaveConflictChoices,
            BuildSelectionPath = _bsAppDir is null ? "" : BuildSelectionFile.PathFor(_bsAppDir),
            Export = ExportConflictChoices,
        });
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
