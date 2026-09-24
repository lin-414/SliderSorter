using System.IO;
using System.Net.Http;
using System.Text;
using SliderSorter.Core;
using SliderSorter.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SliderSorter.Wpf.ViewModels;

public sealed class NewModsRequest
{
    public required IReadOnlyList<(string? Separator, string Owner, List<string> Outfits)> Entries { get; init; }
    public required IReadOnlyList<SliderGroup> Groups { get; init; }
    public required string? PreselectGroup { get; init; }
    public required IReadOnlyList<string> PresetNames { get; init; }
    public required Action<string, List<string>> ApplyBatch { get; init; }
    public required Func<List<string>> ApplyPresets { get; init; }
}

public partial class MainViewModel
{
    /// <summary>视图注入：目录选择器（OpenFolderDialog），返回所选目录或 null。</summary>
    public Func<string, string?>? FolderPicker { get; set; }
    public Func<string?, string?>? FilePicker { get; set; } // 导入分组文件

    /// <summary>扫描发现新模组时触发，视图弹出归组窗口。</summary>
    public event Action<NewModsRequest>? NewModsDetected;
    /// <summary>保存成功后触发，视图显示带"打开输出目录"的完成弹窗。</summary>
    public event Action<string, string?>? SaveCompleted;
    // 曾有一个 UpdateAvailable 事件，视图订阅了却从未被触发——更新提示实际走
    // CheckForUpdatesAsync 里的 ConfirmHandler。已删除该事件与视图侧的订阅。

    private OutputTarget? _lastTarget;

    /// <summary>状态栏输出目录点击：打开当前输出目录。</summary>
    public string? ResolveOutputDirectory() => ResolveWriteTarget()?.Dir;

    public string? ResolveTargetDescription() => ResolveWriteTarget()?.Description;

    /// <summary>当前写入目标（分组文件与 BuildSelection.xml 的落点，见 <see cref="OutputTarget"/>）。</summary>
    private OutputTarget? ResolveWriteTarget()
    {
        if (Resolution is null || _bsAppDir is null)
            return null;
        return OutputTarget.Resolve(Resolution, _bsAppDir, SelectedInstance, Settings.WriteMode, Settings.CustomTargetDir);
    }

    private void LogWriteTarget()
    {
        var target = ResolveWriteTarget();
        _lastTarget = target;
        OutputText = target is null ? L10n.Tr("L.Vm_OutputUndetermined") : L10n.TrF("L.Vm_OutputIs", target.Value.Description);
        OnPropertyChanged(nameof(OutputToolTip));
        if (target is not null)
            Log(L10n.TrF("L.Log_OutputDir", target.Value.Dir, target.Value.Description));
    }

    [RelayCommand]
    private void BrowseForTarget()
    {
        var dir = FolderPicker?.Invoke(L10n.Tr("L.Pick_CustomOutput"));
        if (dir is null)
            return;
        SetCustomTargetDir(dir);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var ok = TrySaveGroups(showSuccessDialog: true);
        await Task.CompletedTask;
        if (!ok)
            return;
    }

    public bool TrySaveGroups(bool showSuccessDialog)
    {
        var target = ResolveWriteTarget();
        if (target is null)
        {
            NotifyUser(L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_ScanIncomplete"), warning: true);
            return false;
        }
        if (Store.Count == 0)
        {
            NotifyUser(L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_NoGroupsYet"), warning: true);
            return false;
        }
        if (Store.Groups.Any(g => g.Name.Trim().Length == 0))
        {
            NotifyUser(L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_EmptyGroupName"), warning: true);
            return false;
        }

        // 组名 → 文件名，检查重名冲突
        var fileByGroup = new Dictionary<SliderGroup, string>();
        var groupByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Store.Groups)
        {
            var fileName = SliderGroupFile.FileNameForGroup(group.Name);
            if (groupByFile.TryGetValue(fileName, out var other))
            {
                NotifyUser(L10n.Tr("L.Title_FilenameConflict"),
                    L10n.TrF("L.Msg_FilenameConflict", group.Name, other, fileName), warning: true);
                return false;
            }
            groupByFile[fileName] = group.Name;
            fileByGroup[group] = fileName;
        }

        var dir = target.Value.Dir;
        try
        {
            Directory.CreateDirectory(dir);

            // 清理 WinForms 版遗留的单文件分组（见 SliderGroupFile.DefaultFileName 的说明）。
            // 该格式已废弃，但老用户的输出目录里可能仍有此文件——不删会让 BodySlide 读到重复分组。
            var legacy = Path.Combine(dir, SliderGroupFile.DefaultFileName);
            if (File.Exists(legacy))
            {
                File.Delete(legacy);
                Log(L10n.TrF("L.Log_LegacyRemoved", SliderGroupFile.DefaultFileName));
            }

            // 按清单清理已改名/已删除组留下的旧文件
            var manifestPath = Path.Combine(dir, SliderGroupFile.ManifestFileName);
            if (File.Exists(manifestPath))
            {
                foreach (var line in File.ReadAllLines(manifestPath))
                {
                    var old = line.Trim();
                    if (old.Length == 0 || groupByFile.ContainsKey(old))
                        continue;
                    // 清单是输出目录里的纯文本文件，可能被用户或其它程序改写，条目不能直接当路径用：
                    // Path.Combine(dir, 绝对路径) 会丢弃 dir，含 ..\ 或 C: 的条目能落到输出目录之外。
                    // 校验不通过就跳过并记日志——最坏结果是留下一个陈旧分组文件，代价远低于误删用户文件。
                    if (!SliderGroupFile.IsBareFileName(old))
                    {
                        LogWarning(L10n.TrF("L.Log_ManifestEntrySkipped", old));
                        continue;
                    }
                    try
                    {
                        File.Delete(Path.Combine(dir, old));
                        Log(L10n.TrF("L.Log_OldRemoved", old));
                    }
                    catch
                    {
                        // 删除失败不阻断保存
                    }
                }
            }

            // 每个组一个文件：<组名>.xml
            foreach (var group in Store.Groups)
                SliderGroupFile.Save(Path.Combine(dir, fileByGroup[group]), new[] { group });

            File.WriteAllLines(manifestPath,
                groupByFile.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            LogError(L10n.TrF("L.Log_WriteFail", ex.Message));
            NotifyUser(L10n.Tr("L.Title_Error"), L10n.TrF("L.Msg_WriteFail", ex.Message), warning: true);
            return false;
        }

        Store.MarkSaved();
        UpdateTitle();
        var memberCount = Store.Groups.Sum(g => g.Members.Count);
        var fileExamples = string.Join("、", Store.Groups.Take(3).Select(g => SliderGroupFile.FileNameForGroup(g.Name)));
        Log(L10n.TrF("L.Log_Saved", dir, Store.Count, memberCount, target.Value.Description));
        if (showSuccessDialog)
            SaveCompleted?.Invoke(dir, _bsAppDir);
        return true;
    }

    // ── 新模组提醒 ──
    /// <summary>扫描完成后对照上次基线找新装的服装模组：更新基线；首次建立基线不弹窗。</summary>
    private void CheckNewModsAfterScan()
    {
        if (Scan is null || Scan.Outfits.Count == 0)
            return;
        var currentOwners = Scan.Outfits.Select(o => o.OwnerLabel).Distinct().ToList();
        var baselineKey = BaselineKey();
        // 旧版的全局基线迁移到当前 实例|Profile 名下，避免升级后全量弹窗
        if (!Settings.KnownOwnersByProfile.TryGetValue(baselineKey, out var known)
            && Settings.KnownOwners is { Count: > 0 } legacy)
        {
            known = legacy;
            Settings.KnownOwners = null;
        }
        var hadBaseline = known is { Count: > 0 };
        var newOwners = KnownMods.Diff(known, currentOwners);
        Settings.KnownOwnersByProfile[baselineKey] = currentOwners;
        Settings.Save();
        if (!hadBaseline || newOwners.Count == 0)
            return;

        // 按树结构（MO2 顺序）排列并带分隔符上下文与服装明细；结构外的模组兜底追加
        var ordered = new List<(string? Separator, string Owner, List<string> Outfits)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (separator, owner, outfits) in GetTreeDisplayStructure())
        {
            if (!seen.Add(owner) || !newOwners.Contains(owner))
                continue;
            ordered.Add((separator, owner, outfits));
        }
        foreach (var owner in newOwners.Where(o => !seen.Contains(o)))
            ordered.Add((null, owner, Scan.Outfits
                .Where(o => o.OwnerLabel == owner)
                .Select(o => o.Name)
                .Distinct()
                .ToList()));
        if (ordered.Count == 0)
            return;

        Log(L10n.TrF("L.Log_NewModsFound", ordered.Count, string.Join("、", ordered.Select(e => e.Owner).Take(6)), ordered.Count > 6 ? "…" : ""));
        if (Store.Count == 0)
        {
            Log(L10n.Tr("L.Log_NoGroupsForNew"));
            return;
        }

        NewModsDetected?.Invoke(new NewModsRequest
        {
            Entries = ordered,
            Groups = Store.Groups,
            PreselectGroup = Store.Current?.Name,
            PresetNames = Settings.RulePresets.Select(p => p.Name).ToList(),
            ApplyBatch = (groupName, outfitsToAdd) =>
            {
                var applied = Store.ApplyToGroup(groupName, outfitsToAdd, true);
                if (applied < 0)
                {
                    // 组名来自弹窗里那份组列表，用户看不到"组不见了"这回事：预设或改名脚本
                    // 刚把目标组弄走时，这里必须说清楚，否则弹窗那边把项剔掉了而组里什么也没有
                    LogError(L10n.TrF("L.Log_GroupMissing", groupName));
                    return;
                }
                Log(L10n.TrF("L.Log_NewModsApplied", applied, groupName));
                RefreshGroupsList();
                RefreshTree();
            },
            ApplyPresets = ApplyRulePresets,
        });
    }

    /// <summary>基线键：实例目录 + Profile——切换 Profile 不误报新模组。</summary>
    private string BaselineKey() =>
        (SelectedInstance?.InstanceDir ?? L10n.Tr("L.Word_NoInstance")) + "|" + (Settings.LastProfile ?? L10n.Tr("L.Word_Default"));

    // ── 规则归组 / 预设 ──
    public List<string> RuleMatchPreview(Dictionary<string, string> ownerByOutfit,
        string modInclude, string outfitInclude, string outfitExclude, bool unassignedOnly)
    {
        if (Scan is null)
            return [];
        var outfitKw = GroupRules.SplitKeywords(outfitInclude);
        var excludeKw = GroupRules.SplitKeywords(outfitExclude);
        var modKw = GroupRules.SplitKeywords(modInclude);
        return Scan.Outfits
            .Where(o => GroupRules.MatchesOutfit(o.Name, OwnerNameOf(ownerByOutfit, o.Name),
                outfitKw, excludeKw, modKw, unassignedOnly, IsInAnyGroup(o.Name)))
            .Select(o => o.Name)
            .ToList();
    }

    public int RuleApply(string groupName, bool add, string modInclude, string outfitInclude,
        string outfitExclude, bool unassignedOnly)
    {
        if (Scan is null)
            return -1;
        var outfitKw = GroupRules.SplitKeywords(outfitInclude);
        var excludeKw = GroupRules.SplitKeywords(outfitExclude);
        var modKw = GroupRules.SplitKeywords(modInclude);
        var ownerByOutfit = OwnerByOutfit();

        var matched = Scan.Outfits
            .Where(o => GroupRules.MatchesOutfit(o.Name, OwnerNameOf(ownerByOutfit, o.Name),
                outfitKw, excludeKw, modKw, unassignedOnly, IsInAnyGroup(o.Name)))
            .Select(o => o.Name)
            .ToList();

        var applied = Store.ApplyToGroup(groupName, matched, add);
        if (applied < 0)
        {
            // 组不存在时不能只 return -1 就完事：调用方原先会照旧刷新一遍列表与树，
            // 用户看到的是一句"已应用 0 条"或者什么都没有，规则却一条没动
            var missing = L10n.TrF("L.Log_GroupMissing", groupName);
            LogError(missing);
            ShowBanner(BannerKind.Warn, missing);
            return applied;
        }
        Log(L10n.TrF("L.Log_RuleApplied", applied, L10n.Tr(add ? "L.Word_Add" : "L.Word_Remove"), groupName, Store.GetGroup(groupName)?.Members.Count ?? 0));
        return applied;
    }

    /// <summary>保存规则预设（重名覆盖）。覆盖时**原地替换**：改一条预设不该把它挪到列表末尾，
    /// 否则用户刚编辑过的那条会跳走，看着像被删了又新建了一条。</summary>
    public void SaveRulePreset(RulePreset preset)
    {
        var index = Settings.RulePresets.FindIndex(p => string.Equals(p.Name, preset.Name, StringComparison.Ordinal));
        if (index >= 0)
            Settings.RulePresets[index] = preset;
        else
            Settings.RulePresets.Add(preset);
        Settings.Save();
        Log(L10n.TrF("L.Log_PresetSaved", preset.Name, preset.GroupName.Length == 0 ? L10n.Tr("L.Word_Unselected") : preset.GroupName, Settings.RulePresets.Count));
    }

    /// <summary>新模组弹窗「按预设分组」：依次执行所有规则预设（作用于全部服装），
    /// 返回当前扫描中已入任意组的服装，供弹窗剔除已处理项。</summary>
    public List<string> ApplyRulePresets()
    {
        if (Scan is null || Settings.RulePresets.Count == 0)
            return [];
        var total = 0;
        var skipped = new List<string>();
        foreach (var preset in Settings.RulePresets)
        {
            var applied = RuleApply(preset.GroupName, preset.Add, preset.ModInclude,
                preset.OutfitInclude, preset.OutfitExclude, preset.UnassignedOnly);
            if (applied < 0)
                skipped.Add(preset.GroupName.Length == 0 ? L10n.Tr("L.Word_UnspecifiedGroup") : preset.GroupName);
            else
                total += applied;
        }
        Log(skipped.Count > 0
            ? L10n.TrF("L.Log_PresetsSkipped", total, string.Join("、", skipped))
            : L10n.TrF("L.Log_PresetsDone", total));
        RefreshGroupsList();
        RefreshTree();
        return Scan.Outfits.Where(o => IsInAnyGroup(o.Name)).Select(o => o.Name).ToList();
    }

    // ── 清理失效服装 ──
    [RelayCommand]
    private void CleanupStaleMembers()
    {
        if (Scan is null)
        {
            NotifyUser(L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_ScanIncompleteStale"));
            return;
        }
        var known = Scan.Outfits.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        var staleByGroup = new List<(SliderGroup Group, List<string> Stale)>();
        foreach (var group in Store.Groups)
        {
            var stale = group.Members.Where(m => !known.Contains(m)).Distinct(StringComparer.Ordinal).ToList();
            if (stale.Count > 0)
                staleByGroup.Add((group, stale));
        }
        var total = staleByGroup.Sum(x => x.Stale.Count);
        if (total == 0)
        {
            NotifyUser(L10n.Tr("L.Title_CleanupStale"), L10n.Tr("L.Msg_NoStale"));
            return;
        }
        var sample = string.Join("、", staleByGroup.SelectMany(x => x.Stale).Take(5));
        var go = ConfirmHandler?.Invoke(L10n.Tr("L.Title_CleanupStale"),
            L10n.TrF("L.Msg_CleanupConfirm", total, sample));
        if (go != true)
            return;
        Store.Snapshot();
        foreach (var (group, stale) in staleByGroup)
            group.Members.RemoveAll(m => stale.Contains(m));
        Store.MarkDirtyFromUi();
        Log(L10n.TrF("L.Log_Cleaned", total, staleByGroup.Count));
        RefreshGroupsList();
        RefreshTree();
    }

    public Func<string, string, bool?>? ConfirmHandler { get; set; } // 视图注入：确认框，true=确认

    /// <summary>视图注入：带「不再提示」记忆的确认框 (title, message, suppressKey) => 是否确认。
    /// 未注入时退回普通 ConfirmHandler（单测、无视图场景）。</summary>
    public Func<string, string, string, bool>? SuppressibleConfirmHandler { get; set; }

    // ── 选择实例目录 / BodySlide 浏览 ──
    public void SelectInstanceDirectory(string path)
    {
        var instance = Mo2Discovery.CreateFromDirectory(path);
        if (instance is null)
        {
            // 登记判据只有 ModOrganizer.ini。用户拿 MO2 的程序目录（有 exe、无 ini）来试是高频误操作，
            // 这种情形只说"没有 ini"帮不上忙——得点明程序目录不是实例目录，且实例多半已被自动检测到。
            var key = Mo2Discovery.IsInstallRoot(path) ? "L.Msg_Mo2InstallDirNotInstance" : "L.Msg_NotMo2Dir";
            NotifyUser(L10n.Tr("L.Title_Tip"), L10n.Tr(key), warning: true);
            return;
        }
        if (!Settings.ExtraMo2Dirs.Contains(instance.InstanceDir, StringComparer.OrdinalIgnoreCase))
        {
            Settings.ExtraMo2Dirs.Add(instance.InstanceDir);
            Settings.Save();
        }
        ReloadInstances();
        SelectedInstance = Instances.FirstOrDefault(i => i.InstanceDir == instance.InstanceDir) ?? SelectedInstance;
    }

    public void UseBodySlideDirectory(string path)
    {
        if (!File.Exists(Path.Combine(path, "Config.xml")))
        {
            NotifyUser(L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_NotBodySlideDir"), warning: true);
            return;
        }
        var candidate = new BodySlideCandidate(path, Path.Combine(path, "BodySlide x64.exe"), L10n.Tr("L.Word_Manual"));
        BodySlideDirs.Insert(0, candidate);
        // 走 SelectBodySlide 而不是直接赋值：手动挑的目录与当前那条逐字段相等时（同一个路径点两次）
        // setter 判定"没变"，挂在 changed 钩子上的扫描就不会跑（见 SelectBodySlide 的说明）
        SelectBodySlide(candidate);
    }

    /// <summary>「导入现有组文件…」：弹框，并把选中的那个文件真的导进来。
    /// 早先这里是 <c>_ = FilePicker?.Invoke(...)</c>——返回值直接丢掉，而 <see cref="ImportFiles"/>
    /// 只有拖拽那条路会调，于是这个按钮点了等于只弹了个框：选完文件什么也不发生，也没有日志。</summary>
    [RelayCommand]
    private void ImportGroups() => OpenImportDialog();

    public void OpenImportDialog()
    {
        if (FilePicker?.Invoke(L10n.Tr("L.Pick_ImportGroups")) is { } file)
            ImportFiles([file]);
    }

    // ── 更新检查 ──
    /// <summary>「发现新版本」提示的「不再提示」记忆键（存于 AppSettings.SuppressedPrompts）。</summary>
    private const string SuppressKeyUpdate = "update-available";

    /// <summary>设置页「检查更新」按钮。生成的命令属性去掉了 Async 后缀：CheckUpdateAsync → CheckUpdateCommand
    /// （与 SaveAsync → SaveCommand 同一条约定，写错后缀不会编译报错，只会让按钮静默失效）。
    /// AsyncRelayCommand 在跑完前 CanExecute 为 false，所以按钮执行期间自己会灰掉，不用再绑 IsEnabled。</summary>
    [RelayCommand]
    private Task CheckUpdateAsync() => CheckForUpdatesAsync(reportUpToDate: true);

    public async Task CheckForUpdatesAsync(bool reportUpToDate)
    {
        const string releasesUrl = "https://github.com/lin-414/SliderSorter/releases/latest";
        if (reportUpToDate)
            UpdateStatusText = L10n.Tr("L.Settings_UpdateChecking");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SliderSorter");
            var json = await http.GetStringAsync("https://api.github.com/repos/lin-414/SliderSorter/releases/latest");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var latest = Version.TryParse(tag.TrimStart('v', 'V'), out var v) ? v : null;
            var current = Version.TryParse(typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? "", out var cv) ? cv : null;
            if (latest is null || current is null || latest <= current)
            {
                // 「已是最新」「检查失败」只在用户主动点按钮时写进设置页：
                // 启动时的静默检查留一句话在那儿，等于每次开机都往设置页撒噪声。
                if (reportUpToDate)
                {
                    UpdateStatusText = L10n.TrF("L.Msg_UpToDate", current);
                    NotifyUser(L10n.Tr("L.Title_CheckUpdate"), L10n.TrF("L.Msg_UpToDate", current));
                }
                return;
            }
            // 有新版本这句话无论如何都留着——静默检查发现的，用户也该在设置页看得见
            UpdateStatusText = L10n.TrF("L.Msg_UpdateAvailable", tag, current);
            var go = SuppressibleConfirmHandler is not null
                ? SuppressibleConfirmHandler(L10n.Tr("L.Title_CheckUpdate"),
                    L10n.TrF("L.Msg_UpdateAvailable", tag, current), SuppressKeyUpdate)
                : ConfirmHandler?.Invoke(L10n.Tr("L.Title_CheckUpdate"),
                    L10n.TrF("L.Msg_UpdateAvailable", tag, current)) == true;
            if (go)
                OpenUrl(releasesUrl);
        }
        catch (Exception ex)
        {
            if (reportUpToDate)
            {
                UpdateStatusText = L10n.TrF("L.Msg_UpdateFailed", ex.Message);
                NotifyUser(L10n.Tr("L.Title_CheckUpdate"), L10n.TrF("L.Msg_UpdateFailed", ex.Message), warning: true);
            }
        }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 浏览器打开失败时忽略
        }
    }

    public static void OpenDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                // 走 ArgumentList 而不是把路径直接拼成参数字符串：explorer 按空格拆参数，
                // 装在 "Mod Organizer 2"、"Steam library" 这类目录下的输出位置会打开错的地方
                // （常见结果是退回「文档」），而用户只会觉得"点了没反应"。
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
                {
                    ArgumentList = { path },
                });
        }
        catch
        {
            // 打开失败不影响主流程
        }
    }
}
