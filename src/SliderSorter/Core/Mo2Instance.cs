namespace SliderSorter.Core;

public enum Mo2InstanceKind
{
    Global,
    Portable,
    Manual,
}

/// <summary>
/// 一个 MO2 实例（全局实例目录或便携安装）。
/// ini 键名与 MO2 2.4.x / 2.5.x 源码一致：[Settings] base_directory / mod_directory / profiles_directory，
/// [General] gameName / gamePath / selected_profile。
/// </summary>
public class Mo2Instance
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections;

    public string Name { get; }
    public string InstanceDir { get; }
    public string IniPath { get; }
    public Mo2InstanceKind Kind { get; }

    public Mo2Instance(string instanceDir, string iniPath, Mo2InstanceKind kind, string? name = null)
    {
        InstanceDir = Path.GetFullPath(instanceDir).TrimEnd('\\', '/');
        IniPath = iniPath;
        Kind = kind;
        Name = string.IsNullOrEmpty(name) ? Path.GetFileName(InstanceDir.TrimEnd('\\', '/')) : name;
        _sections = File.Exists(iniPath)
            ? IniParser.ParseFile(iniPath)
            : new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    }

    public string DisplayName
    {
        get
        {
            var kind = Kind switch
            {
                Mo2InstanceKind.Portable => CoreStrings.Get("L.Core_InstancePortable"),
                Mo2InstanceKind.Global => CoreStrings.Get("L.Core_InstanceGlobal"),
                _ => CoreStrings.Get("L.Core_InstanceManual"),
            };
            var game = string.IsNullOrEmpty(GameName) ? CoreStrings.Get("L.Core_InstanceNoGame") : GameName;
            return $"{Name}（{kind} · {game}）";
        }
    }

    public string BaseDirectory
    {
        get
        {
            var value = Get("Settings", "base_directory");
            if (string.IsNullOrWhiteSpace(value))
                return InstanceDir;
            return ResolveAgainst(value, InstanceDir);
        }
    }

    public string ModsDirectory
    {
        get
        {
            var value = Get("Settings", "mod_directory");
            return ResolveDir(value, "mods");
        }
    }

    public string ProfilesDirectory
    {
        get
        {
            var value = Get("Settings", "profiles_directory");
            return ResolveDir(value, "profiles");
        }
    }

    public string OverwriteDirectory
    {
        get
        {
            var value = Get("Settings", "overwrite_directory");
            return ResolveDir(value, "overwrite");
        }
    }

    public string GameName => Get("General", "gameName");
    public string GamePath => Get("General", "gamePath");
    public string SelectedProfile => Get("General", "selected_profile");

    /// <summary>实例内所有 profile 名（目录下含 modlist.txt）。枚举失败时返回已收集的部分，不抛。</summary>
    public List<string> GetProfiles()
    {
        var result = new List<string>();
        var dir = ProfilesDirectory;
        if (!Directory.Exists(dir))
            return result;
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (File.Exists(Path.Combine(sub, "modlist.txt")))
                    result.Add(Path.GetFileName(sub));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // 个别子目录无权限/设备故障时只丢这一批（枚举是惰性的，异常可能出现在中途）：
            // 这条链路在 ViewModel 构造期被调用，抛出去就是启动即崩
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    public string GetModListPath(string profile) => Path.Combine(ProfilesDirectory, profile, "modlist.txt");

    public bool IsValid => File.Exists(IniPath) || Directory.Exists(ModsDirectory);

    private string ResolveDir(string? configured, string defaultFolder)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return Path.Combine(BaseDirectory, defaultFolder);
        return ResolveAgainst(configured, BaseDirectory);
    }

    /// <summary>把配置里的路径解析成绝对路径：先展开 %BASE_DIR%，相对路径一律相对 baseDir。
    /// Path.GetFullPath 对相对路径是按**当前工作目录**解析的，直接调用会让结果取决于进程从哪儿启动——
    /// 配置里的相对路径只有"相对基准目录"这一种说得通的语义（base_directory 相对实例目录、
    /// mod/profiles 相对 base_directory）。</summary>
    private static string ResolveAgainst(string path, string baseDir)
    {
        var resolved = path.Replace("%BASE_DIR%", baseDir);
        return Path.IsPathFullyQualified(resolved)
            ? Path.GetFullPath(resolved)
            : Path.GetFullPath(Path.Combine(baseDir, resolved));
    }

    private string Get(string section, string key)
    {
        if (_sections.TryGetValue(section, out var entries) && entries.TryGetValue(key, out var value))
            return value;
        return "";
    }
}
