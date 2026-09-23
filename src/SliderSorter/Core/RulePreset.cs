namespace SliderSorter.Core;

/// <summary>规则归组预设：一组可复用的归组条件（规则归组窗口里保存，新装模组弹窗可一键批量执行）。</summary>
public class RulePreset
{
    public string Name { get; set; } = "";
    public string GroupName { get; set; } = "";
    public bool Add { get; set; } = true;
    public string ModInclude { get; set; } = "";
    public string OutfitInclude { get; set; } = "";
    public string OutfitExclude { get; set; } = "";
    public bool UnassignedOnly { get; set; } = true;
}
