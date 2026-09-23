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
            if (seen.Add(Path.GetFullPath(dir)))
                result.Add(new BodySlideCandidate(Path.GetFullPath(dir), exe, source));
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

        // 1) AppDir\SliderSets 存在且确实包含滑块组文件 → AppDir（最高优先级，与 BodySlide 一致）。
        //    注意：部分安装（FOMOD 等）会留下一个空的 SliderSets 文件夹，此时不能视为命中，
        //    否则永远扫不到模组经虚拟 Data 汇聚的服装。
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
        var full = Path.GetFullPath(path);
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
        var fullBase = Path.GetFullPath(baseDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        var comparison = StringComparison.OrdinalIgnoreCase;
        if (!fullPath.StartsWith(fullBase, comparison))
            return null;
        return fullPath[fullBase.Length..].TrimEnd('\\', '/');
    }
}
