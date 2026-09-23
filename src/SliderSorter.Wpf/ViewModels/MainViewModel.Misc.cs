using SliderSorter.Core;
using SliderSorter.Wpf.Services;

namespace SliderSorter.Wpf.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// 生成诊断报告（工具 → 诊断信息，供排查问题时复制给开发者）。
    /// <para>
    /// 文案走 L10n，包括内嵌的 Core 层步骤说明（Core 经 CoreStrings.Localizer 取词）——
    /// 早先这里刻意不本地化，理由是"只译外层标题会变成中英混排"，代价却是英文/俄语界面下
    /// 整份报告都是中文，用户根本读不了。现在内外同源，混排问题自然消失。
    /// </para>
    /// <para>
    /// 像 <c>mods:</c> / <c>gameName:</c> / <c>Kind:</c> 这类本来就是英文的技术标签保持字面量，
    /// 不必为翻译而翻译；需要本地化的只是其中的中文措辞与「存在/不存在」这类状态词。
    /// </para>
    /// </summary>
    public string BuildDiagnostics()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(L10n.Tr("L.Diag_SecInstances"));
        foreach (var instance in Instances)
        {
            sb.AppendLine($"[{instance.DisplayName}]");
            sb.AppendLine(L10n.TrF("L.Diag_InstanceDir", instance.InstanceDir));
            sb.AppendLine($"  mods:     {instance.ModsDirectory} " +
                          $"({L10n.Tr(Directory.Exists(instance.ModsDirectory) ? "L.Core_Exists" : "L.Core_NotExists")})");
            sb.AppendLine($"  profiles: {instance.ProfilesDirectory}");
            sb.AppendLine($"  gameName: {instance.GameName}");
            sb.AppendLine($"  gamePath: {instance.GamePath}");
        }
        if (Instances.Count == 0)
            sb.AppendLine(L10n.Tr("L.Diag_None"));

        sb.AppendLine();
        sb.AppendLine(L10n.Tr("L.Diag_SecProfile"));
        sb.AppendLine(L10n.TrF("L.Diag_ProfileLine", SelectedProfile ?? L10n.Tr("L.Diag_None"), Mods.Count));
        foreach (var (entry, dir) in Mods.Take(200))
            sb.AppendLine($"  #{entry.Priority} {entry.Name} → " +
                          L10n.Tr(Directory.Exists(dir) ? "L.Core_Exists" : "L.Diag_ModDirMissing"));

        sb.AppendLine();
        sb.AppendLine(L10n.Tr("L.Diag_SecBodySlide"));
        sb.AppendLine(L10n.TrF("L.Diag_BsDir", _bsAppDir ?? L10n.Tr("L.Diag_NotSelected")));
        if (Resolution is not null)
        {
            sb.AppendLine($"Kind: {Resolution.Kind}");
            sb.AppendLine(L10n.TrF("L.Diag_EffectivePath", Resolution.EffectivePath));
            sb.AppendLine(L10n.TrF("L.Diag_GameDataPath", Resolution.GameDataPath,
                L10n.Tr(Resolution.GameDataPathFromMo2 ? "L.Btn_Yes" : "L.Btn_No")));
            sb.AppendLine(L10n.Tr("L.Diag_Steps"));
            foreach (var step in Resolution.Steps)
                sb.AppendLine($"  - {step}");
        }

        sb.AppendLine();
        sb.AppendLine(L10n.Tr("L.Diag_SecScan"));
        if (Scan is null)
        {
            sb.AppendLine(L10n.Tr("L.Diag_NotScanned"));
        }
        else
        {
            foreach (var note in Scan.LayerNotes)
                sb.AppendLine(note);
            sb.AppendLine(L10n.TrF("L.Diag_OutfitCount", Scan.Outfits.Count,
                Scan.Outfits.Count(o => o.HasConflict)));
            sb.AppendLine(L10n.TrF("L.Diag_OutputDir",
                ResolveWriteTarget()?.Dir ?? L10n.Tr("L.Diag_OutputUndetermined")));
        }

        sb.AppendLine();
        sb.AppendLine(L10n.Tr("L.Diag_SecConflicts"));
        if (OutputConflictCount == 0)
        {
            sb.AppendLine(L10n.Tr("L.Diag_NoConflicts"));
        }
        else
        {
            sb.AppendLine(L10n.TrF("L.Diag_ConflictLine", ConflictGroups.Count, OutputConflictCount,
                ConflictGroups.Count(g => g.Chosen is not null)));
            foreach (var group in ConflictGroups.Take(60))
            {
                var winner = group.Chosen?.Name ?? L10n.Tr("L.Conflict_StatusUnresolved");
                sb.AppendLine($"  {group.OutputFilePath} → {winner}" +
                              (group.CrossMod ? "" : $"  [{L10n.Tr("L.Conflict_SameModOnly")}]"));
                foreach (var candidate in group.Candidates)
                    sb.AppendLine($"    [{candidate.LayerIndex}] {candidate.Name} ← {candidate.OwnerLabel}");
            }
            if (OutputConflictCount > 60)
                sb.AppendLine(L10n.TrF("L.Diag_ConflictMore", OutputConflictCount - 60));
        }
        return sb.ToString();
    }
}
