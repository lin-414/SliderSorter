namespace SliderSorter.Core;

/// <summary>
/// 发现 MO2 实例，方式与 MO2 自身一致（instancemanager.cpp）：
/// 全局实例 = %LOCALAPPDATA%\ModOrganizer 下每个包含 ModOrganizer.ini 的子目录；
/// 便携实例 = ModOrganizer.exe 旁的 ModOrganizer.ini。
/// </summary>
public static class Mo2Discovery
{
    /// <summary>测试可替换的全局实例根目录（null = 真实 <c>%LOCALAPPDATA%\ModOrganizer</c>）。
    /// 单测必须能挡住对用户真实 MO2 目录的枚举，否则用例结果取决于跑测试那台机器装了什么。</summary>
    public static string? GlobalRootOverride { get; set; }

    public static string GlobalRoot => GlobalRootOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer");

    public static List<Mo2Instance> Discover(IEnumerable<string> extraDirs)
    {
        var found = new List<Mo2Instance>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(GlobalRoot))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(GlobalRoot))
                {
                    var ini = Path.Combine(dir, "ModOrganizer.ini");
                    if (File.Exists(ini) && seen.Add(NormalizeOrRaw(dir)))
                        TryAdd(found, dir, ini, Mo2InstanceKind.Global);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // 枚举失败（权限不足、磁盘/设备错误）就跳过这个根目录：Discover 在 ViewModel 的
                // 构造函数链里被调用，这里抛出去等于"启动即崩"，而少列几个实例最多是重新指一次目录
            }
        }

        foreach (var dir in CommonPortableLocations())
        {
            TryAdd(found, seen, dir, Mo2InstanceKind.Portable);
        }

        foreach (var dir in extraDirs)
        {
            TryAdd(found, seen, dir, Mo2InstanceKind.Manual);
        }

        return found;
    }

    /// <summary>去重键：与 <see cref="Mo2Instance.InstanceDir"/> 同一种归一化。
    /// 用原始字符串去重的话，用户在设置里手工加的 <c>E:\MO2\Inst\</c>（带尾分隔符）
    /// 与自动发现给的 <c>E:\MO2\Inst</c> 会被当成两个实例，列表里出现两行，
    /// 选中哪一行决定了 <c>LastInstanceDir</c> 能不能在下次启动时对回去。</summary>
    private static string Normalize(string dir) => Path.GetFullPath(dir).TrimEnd('\\', '/');

    /// <summary>把用户浏览的目录转为实例（实例目录或便携安装根目录皆可）。</summary>
    public static Mo2Instance? CreateFromDirectory(string dir)
    {
        var list = new List<Mo2Instance>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TryAdd(list, seen, dir, Mo2InstanceKind.Manual);
        return list.FirstOrDefault();
    }

    /// <summary>该目录是否是 MO2 的**程序目录**（有 ModOrganizer.exe、但没有 ModOrganizer.ini）。
    ///
    /// 用途只有一条：把"选错目录"的提示说清楚。MO2 官方安装器把 exe 装在程序目录、实例数据放在
    /// %LOCALAPPDATA%\ModOrganizer\&lt;实例名&gt;，用户凭直觉去选程序目录是常事，而登记判据只有 ini，
    /// 于是必然被拒——这时该告诉他的不是"这里没有 exe"，而是"exe 在哪不影响本程序"。
    /// 本程序从不读取或启动 exe：实例的 mods / profiles / 游戏路径全部来自 ini，
    /// 所以这里只做提示分流，不参与登记判据。</summary>
    public static bool IsInstallRoot(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return false;
        return File.Exists(Path.Combine(dir, "ModOrganizer.exe"))
            && !File.Exists(Path.Combine(dir, "ModOrganizer.ini"));
    }

    private static void TryAdd(List<Mo2Instance> list, HashSet<string> seen, string dir, Mo2InstanceKind kind)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return;

        // 判据只有 ModOrganizer.ini。便携安装的 ini 就写在 exe 同级，与上面这条是**同一个路径**，
        // 所以"只有 ModOrganizer.exe 没有 ini"的目录不登记（MO2 自己也不认这种目录为实例）。
        var ini = Path.Combine(dir, "ModOrganizer.ini");
        if (!File.Exists(ini) || !seen.Add(NormalizeOrRaw(dir)))
            return;
        TryAdd(list, dir, ini, kind);
    }

    private static void TryAdd(List<Mo2Instance> list, string dir, string ini, Mo2InstanceKind kind)
    {
        try
        {
            list.Add(new Mo2Instance(dir, ini, kind));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or System.Security.SecurityException or PathTooLongException)
        {
            // 构造一个实例要读它的 ini、并把目录规范成全路径，两件都可能失败（ini 正被 MO2 占着、
            // 盘被拔掉、目录名里有非法字符）。Discover 是在 ViewModel 的构造链上调的，
            // 一个坏目录不该让整张实例列表列不出来——那等于启动即崩。
        }
    }

    /// <summary>归一化目录名，同时兜住 <c>Path.GetFullPath</c> 对非法字符的抛：
    /// 这个键既用于去重，也在坏路径时退回原始字符串（最坏结果是多列一行，不是崩）。</summary>
    private static string NormalizeOrRaw(string dir)
    {
        try
        {
            return Normalize(dir);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return dir;
        }
    }

    private static IEnumerable<string> CommonPortableLocations()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        yield return Path.Combine(programFiles, "Mod Organizer 2");
        yield return Path.Combine(programFilesX86, "Mod Organizer 2");
        yield return Path.Combine(documents, "Mod Organizer 2");
    }
}
