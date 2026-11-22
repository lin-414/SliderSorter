namespace SliderSorter.Core;

public enum ProjectPathKind
{
    /// <summary>Config.xml 的 ProjectPath 命中（用户自定义）。</summary>
    Custom,
    /// <summary>BodySlide.exe 旁存在 SliderSets 目录，使用 exe 所在目录。</summary>
    AppDir,
    /// <summary>回落到 &lt;GameDataPath&gt;\CalienteTools\BodySlide（MO2 启动时为虚拟目录）。</summary>
    GameDataCalienteTools,
    /// <summary>回落到 &lt;GameDataPath&gt;\Tools\BodySlide。</summary>
    GameDataTools,
    /// <summary>所有候选都不存在，按 BodySlide 行为返回 AppDir 或 ProjectPath。</summary>
    Fallback,
}

public record ProjectPathResolution
{
    public required string EffectivePath { get; init; }
    public required ProjectPathKind Kind { get; init; }
    public required string GameDataPath { get; init; }
    /// <summary>GameDataPath 是否来自 MO2（BodySlide 自己没配置时）。</summary>
    public bool GameDataPathFromMo2 { get; init; }
    public List<string> Steps { get; } = new();
}

public record BodySlideCandidate(string AppDir, string ExePath, string Source)
{
    public override string ToString() => AppDir;
}

/// <summary>
/// 定位 BodySlide 安装，并精确复刻 BodySlide 的 ProjectUtil::GetProjectPath() 解析逻辑：
/// 1) 若 AppDir\SliderSets 存在 → 直接返回 AppDir；
/// 2) 否则按顺序检查 [Config 的 ProjectPath, GameDataPath\CalienteTools\BodySlide, GameDataPath\Tools\BodySlide]；
/// 3) 都不存在 → 返回 ProjectPath（若配置了）否则 AppDir。
/// 对 GameDataPath 之下的路径做"虚拟存在"判断（真实磁盘 或 任一启用模组提供相同相对路径），
/// 以模拟 MO2 USVFS 启动 BodySlide 时看到的虚拟 Data。
/// </summary>
public static class BodySlideLocator
{
    public static List<BodySlideCandidate> FindCandidates(
        List<(ModEntry Entry, string Dir)> enabledMods, string? mo2GamePath, string? previousDir)
    {
        var result = new List<BodySlideCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string dir, string source)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return;
            var exe = FindExe(dir);
            if (exe is null || !File.Exists(Path.Combine(dir, "Config.xml")))
                return;
            var full = TryGetFullPath(dir);
            if (full is null || !seen.Add(full))
                return;
            result.Add(new BodySlideCandidate(full, exe, source));
        }

        if (!string.IsNullOrWhiteSpace(previousDir))
            Add(previousDir, CoreStrings.Get("L.Core_CandLastUsed"));

        foreach (var (_, dir) in enabledMods)
        {
            Add(dir, CoreStrings.Get("L.Core_CandModRoot"));
            Add(Path.Combine(dir, "Tools", "BodySlide"), CoreStrings.Get("L.Core_CandModTools"));
            Add(Path.Combine(dir, "CalienteTools", "BodySlide"), CoreStrings.Get("L.Core_CandModCaliente"));
        }

        if (!string.IsNullOrWhiteSpace(mo2GamePath))
        {
            Add(Path.Combine(mo2GamePath, "Data", "CalienteTools", "BodySlide"), CoreStrings.Get("L.Core_CandGameDataCaliente"));
            Add(Path.Combine(mo2GamePath, "Data", "Tools", "BodySlide"), CoreStrings.Get("L.Core_CandGameDataTools"));
        }

        return result;
    }

    private static string? FindExe(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "BodySlide*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>带防御的 GetFullPath。路径可能来自 Config.xml 的 ProjectPath / GameDataPath
    /// （用户可手改）或 settings.json 里的上次使用目录：含空字节等退化输入时 GetFullPath 会抛
    /// ArgumentException——不接的话异常从扫描链路漏进通用 catch，用户只看到一句"扫描失败"。
    /// 按"路径无效"处理返回 null，调用方当作目录不存在继续走其余候选。</summary>
    private static string? TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static ProjectPathResolution ResolveProjectPath(
        BodySlideConfig config, string appDir, List<(ModEntry Entry, string Dir)> enabledMods, string? mo2GamePath)
    {
        var resolution = new ProjectPathResolution
        {
            EffectivePath = appDir,
            Kind = ProjectPathKind.Fallback,
            GameDataPath = "",
        };
        var steps = resolution.Steps;

        var gameDataPath = config.GetGameDataPath();
        var fromMo2 = false;
        if (string.IsNullOrWhiteSpace(gameDataPath) && !string.IsNullOrWhiteSpace(mo2GamePath))
        {
            gameDataPath = Path.Combine(mo2GamePath, "Data");
            fromMo2 = true;
            steps.Add(CoreStrings.Get("L.Core_StepGameDataFromMo2"));
        }
        if (string.IsNullOrWhiteSpace(gameDataPath))
        {
            var reg = config.GetGamePathFromRegistry();
            if (!string.IsNullOrWhiteSpace(reg))
            {
                gameDataPath = Path.Combine(reg, "Data");
                steps.Add(CoreStrings.Get("L.Core_StepGameDataFromRegistry"));
            }
        }
        resolution = resolution with { GameDataPath = gameDataPath, GameDataPathFromMo2 = fromMo2 };
        steps.Add(CoreStrings.Format("L.Core_StepGameDataPath", gameDataPath,
            fromMo2 ? CoreStrings.Get("L.Core_FromMo2") : ""));

        // 0) 所选 BodySlide 目录是"模组托管"的标准布局（<模组>\CalienteTools\BodySlide /
        //    <模组>\Tools\BodySlide，或直接躺在真实 Data 下）且装载了 MO2 模组表时，BodySlide
        //    经 MO2（usvfs）启动后它的 AppDir 是虚拟 Data 下的同相对路径目录——那里是全部
        //    模组按优先级**合并**的视图，"AppDir\SliderSets 有没有滑块组文件"必须按合并视图问。
        //    只按所选目录的真实磁盘问的话：BodySlide 模组自带的 SliderSets 非空（整合包常把
        //    滑块组放在 BodySlide 旁边）会把有效项目路径钉在该模组的真实目录上、走单目录扫描，
        //    其余模组经虚拟 Data 汇聚的滑块组全体漏扫——BodySlide 实际读得到它们，清单却比它少。
        //    没有 MO2 模组表时不走这一步：那时虚拟视图就是真实磁盘，原有判断已经是同一答案。
        var hostedRel = enabledMods.Count == 0
            ? null
            : StandardBodySlideRelUnder(appDir, enabledMods) ?? StandardBodySlideRelUnder(appDir, gameDataPath);
        if (hostedRel is not null && !string.IsNullOrWhiteSpace(gameDataPath))
        {
            var virtualAppDir = Path.Combine(gameDataPath, hostedRel);
            var hostedKind = hostedRel.Equals(@"Tools\BodySlide", StringComparison.OrdinalIgnoreCase)
                ? ProjectPathKind.GameDataTools
                : ProjectPathKind.GameDataCalienteTools;
            if (VirtualHasSliderSetFiles(Path.Combine(virtualAppDir, "SliderSets"), gameDataPath, enabledMods))
            {
                steps.Add(CoreStrings.Format("L.Core_StepVirtualAppDirFound", virtualAppDir));
                return resolution with { EffectivePath = virtualAppDir, Kind = hostedKind };
            }
            steps.Add(CoreStrings.Get("L.Core_StepVirtualAppDirEmpty"));
        }

        // 1) AppDir\SliderSets 存在且确实包含滑块组文件 → AppDir（最高优先级，与 BodySlide 一致）。
        //    注意：部分安装（FOMOD 等）会留下一个空的 SliderSets 文件夹，此时不能视为命中，
        //    否则永远扫不到模组经虚拟 Data 汇聚的服装。走到这里的 appDir 必然不是上面那种
        //    模组托管布局（托管布局的虚拟视图为空 ⟹ 它自己的真实目录也为空，同一条路回落）。
        var appSliderSets = Path.Combine(appDir, "SliderSets");
        if (Directory.Exists(appSliderSets))
        {
            if (HasSliderSetFiles(appSliderSets))
            {
                steps.Add(CoreStrings.Format("L.Core_StepSliderSetsFound", appDir));
                return resolution with { EffectivePath = appDir, Kind = ProjectPathKind.AppDir };
            }
            steps.Add(CoreStrings.Get("L.Core_StepSliderSetsEmpty"));
        }
        else
        {
            steps.Add(CoreStrings.Get("L.Core_StepNoSliderSets"));
        }

        var projectPath = config.Get("ProjectPath");
        var candidates = new List<(string Path, ProjectPathKind Kind)>();
        if (!string.IsNullOrWhiteSpace(projectPath))
            candidates.Add((projectPath, ProjectPathKind.Custom));
        if (!string.IsNullOrWhiteSpace(gameDataPath))
        {
            candidates.Add((Path.Combine(gameDataPath, "CalienteTools", "BodySlide"), ProjectPathKind.GameDataCalienteTools));
            candidates.Add((Path.Combine(gameDataPath, "Tools", "BodySlide"), ProjectPathKind.GameDataTools));
        }

        foreach (var (path, kind) in candidates)
        {
            var exists = VirtualDirectoryExists(path, gameDataPath, enabledMods);
            steps.Add(CoreStrings.Format("L.Core_StepCandidate", kind, path,
                CoreStrings.Get(exists ? "L.Core_Exists" : "L.Core_NotExists")));
            if (exists)
                return resolution with { EffectivePath = path, Kind = kind };
        }

        var fallback = !string.IsNullOrWhiteSpace(projectPath) ? projectPath : appDir;
        steps.Add(CoreStrings.Format("L.Core_StepFallback", fallback));
        return resolution with { EffectivePath = fallback, Kind = ProjectPathKind.Fallback };
    }

    /// <summary>appDir 若位于 rootDir 之下、且相对路径恰是 BodySlide 的两种标准布局之一
    /// （<c>CalienteTools\BodySlide</c> / <c>Tools\BodySlide</c>），返回该相对路径；
    /// 其余情形（不在其下、exe 在模组根、或更深的自定义层级）返回 null——那些布局交回
    /// 既有的真实目录判断，不猜。</summary>
    private static string? StandardBodySlideRelUnder(string appDir, string rootDir)
    {
        var rel = GetSuffixUnder(appDir, rootDir);
        if (rel is null)
            return null;
        var normalized = rel.Replace('/', '\\');
        return normalized.Equals(@"CalienteTools\BodySlide", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(@"Tools\BodySlide", StringComparison.OrdinalIgnoreCase)
                ? normalized
                : null;
    }

    private static string? StandardBodySlideRelUnder(string appDir, List<(ModEntry Entry, string Dir)> enabledMods) =>
        enabledMods.Select(m => StandardBodySlideRelUnder(appDir, m.Dir))
            .FirstOrDefault(rel => rel is not null);

    /// <summary>虚拟 Data 视角下 dir（递归）是否有滑块组文件：真实磁盘有，或任一启用模组提供的
    /// 同相对路径目录里有。与 <see cref="VirtualDirectoryExists"/> 同一个口径，
    /// 只是问的内容从"目录存在"换成了"目录里有滑块组"——BodySlide 判 AppDir 命中的
    /// 正是后者（空目录不算，见 <see cref="HasSliderSetFiles"/>）。</summary>
    private static bool VirtualHasSliderSetFiles(string dir, string gameDataPath,
        List<(ModEntry Entry, string Dir)> enabledMods)
    {
        if (HasSliderSetFiles(dir))
            return true;
        var suffix = GetSuffixUnder(dir, gameDataPath);
        if (suffix is null)
            return false;
        foreach (var (_, modDir) in enabledMods)
        {
            var candidate = Path.Combine(modDir, suffix);
            // 先用 Exists 挡掉大多数模组（没有 CalienteTools\BodySlide 的）：HasSliderSetFiles
            // 对不存在的目录靠异常兜底，两千个模组就是两千次异常，纯浪费
            if (Directory.Exists(candidate) && HasSliderSetFiles(candidate))
                return true;
        }
        return false;
    }

    /// <summary>目录下（递归）是否有任何 *.xml / *.osp 滑块组文件。</summary>
    private static bool HasSliderSetFiles(string dir)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            };
            return Directory.EnumerateFiles(dir, "*.xml", options).Any()
                || Directory.EnumerateFiles(dir, "*.osp", options).Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 判断目录在"虚拟 Data"视角下是否存在：真实磁盘存在，或任一启用模组（按优先级）提供相同相对路径。
    /// </summary>
    public static bool VirtualDirectoryExists(string path, string gameDataPath, List<(ModEntry Entry, string Dir)> enabledMods)
    {
        var full = TryGetFullPath(path);
        if (full is null)
            return false;
        if (Directory.Exists(full))
            return true;

        var suffix = GetSuffixUnder(full, gameDataPath);
        if (suffix is null)
            return false;

        return enabledMods.Any(m => Directory.Exists(Path.Combine(m.Dir, suffix)));
    }

    /// <summary>取 path 相对 gameDataPath 的后缀（不区分大小写）；不位于其下则返回 null。</summary>
    public static string? GetSuffixUnder(string path, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(baseDir))
            return null;
        var fullBase = TryGetFullPath(baseDir);
        var fullPath = TryGetFullPath(path);
        if (fullBase is null || fullPath is null)
            return null;
        fullBase = fullBase.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var comparison = StringComparison.OrdinalIgnoreCase;
        if (!fullPath.StartsWith(fullBase, comparison))
            return null;
        return fullPath[fullBase.Length..].TrimEnd('\\', '/');
    }
}
