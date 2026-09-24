using SliderSorter.Core;
using SliderSorter.Wpf.Services;

namespace SliderSorter.Wpf.ViewModels;

/// <summary>
/// 本工具两份产出各写到哪里：分组文件（<c>SliderGroups\*.xml</c>）与输出归属选择（<c>BuildSelection.xml</c>）。
/// <para>
/// 两者的目录关系是 BodySlide 自己定的：分组读 <c>ProjectUtil::GetProjectPath() + "/SliderGroups"</c>
/// （<c>BodySlideApp.cpp:3338</c>），BuildSelection.xml 读 <c>Config["AppDir"] + PathSep + "BuildSelection.xml"</c>
/// （同文件 :1196）。标准布局下这是同一个 BodySlide 目录里的兄弟——<c>&lt;BodySlide 目录&gt;\SliderGroups\</c>
/// 与 <c>&lt;BodySlide 目录&gt;\BuildSelection.xml</c>，所以「输出归属写哪儿」就是分组落点的上一级，
/// 而不是另一个可以各自配置的位置（<see cref="BuildSelectionDir"/> 的取值即由此而来）。
/// </para>
/// <para>
/// MO2 下 <c>AppDir</c> 并不是 exe 的真实路径：usvfs 会把 <c>GetModuleFileNameW</c> 的返回值按反向映射改写成
/// 虚拟路径（<c>usvfs/src/usvfs_dll/hooks/kernel32.cpp</c> 的 <c>hook_GetModuleFileNameW</c>），于是 BodySlide
/// 眼里的程序目录是 <c>&lt;游戏&gt;\Data\CalienteTools\BodySlide</c>。这一层是虚拟的，落到哪个模组由 MO2 的
/// 优先级决定——用户机上的 usvfs 日志里就是
/// <c>mapping file in vfs: …\Data\CalienteTools\BodySlide\BuildSelection.xml → …\mods\&lt;模组&gt;\CalienteTools\BodySlide\BuildSelection.xml</c>。
/// 因此把它写进<b>输出位置所属的那个模组</b>（分组文件也写在那儿）才是 BodySlide 读得到的那一份；
/// 反过来写进所选 BodySlide 安装的真实目录，会被优先级更高的模组挡住。
/// </para>
/// </summary>
/// <param name="Dir">分组文件的落点（各模式下的 SliderGroups 目录）。</param>
/// <param name="BuildSelectionDir">BuildSelection.xml 的落点。标准模式 = <paramref name="Dir"/> 的上一级
/// （BodySlide 目录）；自定义模式没有可推断的 BodySlide 目录，就写在所选目录本身里，与分组文件同处。</param>
/// <param name="Description">状态栏与保存提示里给用户看的一句描述。</param>
public readonly record struct OutputTarget(string Dir, string BuildSelectionDir, string Description)
{
    /// <summary>MO2 专用模组名：不碰任何别人的模组，重装 BodySlide 也不丢。</summary>
    public const string DedicatedModName = "SliderSorter Output";

    /// <summary>按写入模式算出落点；确定不了（缺 MO2 实例、自定义模式还没选目录）时为 null。</summary>
    public static OutputTarget? Resolve(ProjectPathResolution resolution, string bsAppDir,
        Mo2Instance? instance, WriteMode mode, string? customDir)
    {
        // 有效项目路径落在虚拟 Data 下（MO2 启动的常见情形）时，"自动"选专用模组：
        // 写进真实 Data 会被 MO2 的覆盖层挡住，写进 BodySlide 安装目录又会被别的模组挡住。
        var virtualKind = resolution.Kind is ProjectPathKind.GameDataCalienteTools or ProjectPathKind.GameDataTools;

        OutputTarget? Mo2Mod()
        {
            if (instance is null || !Directory.Exists(instance.ModsDirectory))
                return null;
            var bsFolder = Path.Combine(instance.ModsDirectory, DedicatedModName, "CalienteTools", "BodySlide");
            return new OutputTarget(Path.Combine(bsFolder, "SliderGroups"), bsFolder,
                L10n.TrF("L.Target_Mo2Mod", DedicatedModName));
        }

        OutputTarget? RealData()
        {
            if (string.IsNullOrWhiteSpace(resolution.GameDataPath))
                return null;
            var bsFolder = Path.Combine(resolution.GameDataPath, "CalienteTools", "BodySlide");
            return new OutputTarget(Path.Combine(bsFolder, "SliderGroups"), bsFolder, L10n.Tr("L.Wm_GameData"));
        }

        return mode switch
        {
            WriteMode.BodySlideDir => new OutputTarget(Path.Combine(bsAppDir, "SliderGroups"), bsAppDir,
                L10n.Tr("L.Wm_BsDir")),
            WriteMode.Mo2Mod => Mo2Mod() ?? RealData(),
            WriteMode.RealGameData => RealData() ?? Mo2Mod(),
            WriteMode.Custom => string.IsNullOrWhiteSpace(customDir)
                ? null
                : new OutputTarget(customDir, customDir, L10n.Tr("L.Target_CustomDir")),
            _ => virtualKind
                ? Mo2Mod() ?? RealData()
                : new OutputTarget(Path.Combine(bsAppDir, "SliderGroups"), bsAppDir, L10n.Tr("L.Wm_BsDir")),
        };
    }
}
