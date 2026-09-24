using System.Text.Json;
using System.Text.Json.Serialization;

namespace SliderSorter.Core;

public enum WriteMode
{
    /// <summary>按有效项目路径自动选择写入位置。</summary>
    Auto = 0,
    /// <summary>写到 BodySlide.exe 旁的 SliderGroups。</summary>
    BodySlideDir = 1,
    /// <summary>写到 MO2 专用小模组（mods\SliderSorter Output\CalienteTools\BodySlide\SliderGroups）。</summary>
    Mo2Mod = 2,
    /// <summary>写到游戏真实 Data（GameDataPath\CalienteTools\BodySlide\SliderGroups）。</summary>
    RealGameData = 3,
    /// <summary>写到用户通过"浏览…"指定的任意路径。</summary>
    Custom = 4,
}

public class AppSettings
{
    public List<string> ExtraMo2Dirs { get; set; } = new();
    public string? LastInstanceDir { get; set; }
    public string? LastProfile { get; set; }
    public string? LastBodySlideDir { get; set; }
    public string? CustomTargetDir { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WriteMode WriteMode { get; set; } = WriteMode.Auto;

    /// <summary>上次扫描时含服装的模组（OwnerLabel）基线。已改用 KnownOwnersByProfile，仅作旧数据迁移用。</summary>
    public List<string>? KnownOwners { get; set; }

    /// <summary>含服装模组基线，按 "实例目录|Profile" 隔离——切换 Profile 不会误报新模组。</summary>
    public Dictionary<string, List<string>> KnownOwnersByProfile { get; set; } = new();

    /// <summary>规则归组预设：可在新装模组弹窗里一键批量执行。</summary>
    public List<RulePreset> RulePresets { get; set; } = new();

    /// <summary>界面主题："boutique"（暗色，默认）或 "light"（上一版亮色）。</summary>
    public string UiTheme { get; set; } = "boutique";

    /// <summary>界面语言："system"（跟随系统，默认）、"zh"、"en"、"de"、"ru" 或 "fr"。
    /// 这里存的是**诉求**而不是算出来的语言码——"system" 每次启动按系统 UI 语言现算
    /// （系统语言不在受支持列表里时由 L10n 回落到 "en"），未知值与 null 回落到 "zh"。
    /// 存语言码就等于"跟随系统"只用一次，用户在系统里换了显示语言它也不会再跟。</summary>
    public string UiLanguage { get; set; } = "system";

    /// <summary>用户在提示框里勾过「不再提示」的项目（如更新提示）。键由调用方定义，仅做等值比较。</summary>
    public List<string> SuppressedPrompts { get; set; } = new();

    /// <summary>输出文件冲突的赢家选择：BodySlide 口径的输出路径（反斜杠、不含扩展名）→ slider set 名。
    /// 键与 BodySlide 的 BuildSelection.xml 同一套、存取都<b>逐字节区分大小写</b>——它的
    /// <c>BuildSelection::outputChoice</c> 用的就是默认比较器的 <c>std::map</c>（<c>BuildSelection.h:21</c>），
    /// 大小写错了它就查不到。也不按实例分区——BodySlide 自己就只有一份全局的 BuildSelection.xml。
    /// <para>
    /// 注意这与"哪些路径算同一组冲突"是两回事：分组那一层忽略大小写
    /// （<c>outFileCount</c> 的 <c>case_insensitive_compare</c>，见 <see cref="OutputConflicts.Detect"/>）。
    /// 所以同一组可能有多个拼写的键，规范键取首个成员的写法，导出时每种拼写各写一条。
    /// </para>
    /// 写回时只提交当前扫描仍然确认存在的冲突，
    /// 所以别的实例留下的条目既不会被误用、也不会被误删。</summary>
    public Dictionary<string, string> OutputChoices { get; set; } = new();

    /// <summary>「输出归属」左栏里被**收起**的分组名（空串 = 「未入组」那一桶，
    /// 与 <see cref="UserGroupConflicts.GroupName"/> 的哨兵同一口径）。
    /// <para>
    /// 存"收起的"而不是"展开的"：默认全展开，于是新分组、新用户、以及老设置文件
    /// 都不需要任何迁移就得到正确行为——多出来的一条只影响它自己那一组。
    /// </para>
    /// <para>
    /// 不按实例/Profile 分区（与 <see cref="OutputChoices"/> 的取舍不同）：那一位是数据，
    /// 这一位是纯显示偏好。两个实例恰好有同名分组时，最多是"另一个实例里那一组也默认收起"，
    /// 点一下分组头就复原——不值得为它引入一套分区键。
    /// </para></summary>
    public List<string> CollapsedConflictGroups { get; set; } = new();

    private static string? _directoryOverride;

    /// <summary>设置目录覆盖（null = 真实 <c>%APPDATA%\SliderSorter</c>）。
    /// 单测必须能避开用户真实的设置文件——否则跑一次测试就会改写用户的实例/Profile/写盘模式。
    /// 换目录等于换了一整套设置，所以这里顺带把共享实例作废，下一次 <see cref="Shared"/> 会重新加载。</summary>
    public static string? DirectoryOverride
    {
        get => _directoryOverride;
        set
        {
            _directoryOverride = value;
            _shared = null;
        }
    }

    private static string SettingsDir =>
        DirectoryOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SliderSorter");
    private static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    private static AppSettings? _shared;

    /// <summary>进程内共享的设置实例：App 启动时加载一次（<see cref="Use"/>）并登记，
    /// 界面层直接取用即可——早先 App 与 ViewModel 各自 <see cref="Load"/> 一遍，后加载的那份会把
    /// 先加载那份上刚改的设置覆盖回去。没有 App 上下文（单测）时惰性加载，行为与各自 Load 一致。</summary>
    public static AppSettings Shared => _shared ??= Load();

    /// <summary>登记共享实例（App 启动用），返回同一个实例便于链式取用。</summary>
    public static AppSettings Use(AppSettings instance) => _shared = instance;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // 两处麻烦都按"键 + 实参"的形式存着，取的时候才本地化：<c>Load()</c> 是在 App.OnStartup 里跑的，
    // 那时 CoreStrings.Localizer 还没注入（它要等语言字典装载完），当场拼文案只会得到键名。
    private static (string? Kept, string Reason)? _loadFailure;
    private static string? _saveFailureReason;

    /// <summary>上次 <see cref="Load"/> 遇到的麻烦：文件读不动或解不开（坏文件会留一份档）。
    /// 界面在启动日志里带一句，别让用户在"设置怎么全没了"里自己猜。每次 Load 重置。</summary>
    public static string? LoadNote => _loadFailure is not { } f
        ? null
        : f.Kept is null
            ? CoreStrings.Format("L.Core_SettingsBroken", f.Reason)
            : CoreStrings.Format("L.Core_SettingsBrokenKept", f.Kept, f.Reason);

    /// <summary>上次 <see cref="Save"/> 失败的原因（这次的改动就只存在于内存里了）。
    /// <c>Save()</c> 是从各处调的，就地弹框会打断操作，所以先记账，由界面在日志里说。</summary>
    public static string? SaveNote => _saveFailureReason is null
        ? null
        : CoreStrings.Format("L.Core_SettingsSaveFail", _saveFailureReason);

    public static AppSettings Load()
    {
        _loadFailure = null;
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(ReadShared(SettingsPath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            // 损坏的设置不能悄悄丢掉：里面存着用户在冲突页手选的赢家、实例与 Profile、写盘模式，
            // 一句"回到默认值"会让下次启动看着像程序自己忘了配过什么。把坏文件留下并说清楚去了哪。
            _loadFailure = (KeepBrokenCopy(), ex.Message);
        }
        return new AppSettings();
    }

    /// <summary>把读不动的设置文件改名留档（带时间戳，别覆盖上一份坏的），返回留下的文件名；
    /// 改名也失败时返回 null——原文件可能是只读或被别的实例占着，此时宁可不动它。</summary>
    private static string? KeepBrokenCopy()
    {
        var target = Path.Combine(SettingsDir,
            $"settings.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        try
        {
            Directory.CreateDirectory(SettingsDir);
            if (File.Exists(target))
                return null;
            File.Move(SettingsPath, target, overwrite: false);
            return Path.GetFileName(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>读成字符串。MO2/编辑器可能正持有 settings.json，与 ini、modlist 同一口径要允许共享读。</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public void Save()
    {
        _saveFailureReason = null;
        try
        {
            Directory.CreateDirectory(SettingsDir);
            // 先写临时文件再替换：WriteAllText 是直接截断原文件，写一半时断电/磁盘满/杀软打断，
            // 留下的就是半份 JSON——下次启动整份设置作废（见 Load 的分支）。替换是原子的，
            // 最坏结果是新内容没写进去，旧那份仍然完整可读。
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            try
            {
                if (File.Exists(SettingsPath))
                    File.Replace(tmp, SettingsPath, destinationBackupFileName: null);
                else
                    File.Move(tmp, SettingsPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // 目标在别的文件系统上、或被别的进程以不共享写的方式开着：
                // Replace/Move 都会抛。退回直接覆盖写，至少不让这次的改动丢掉。
                File.Move(tmp, SettingsPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            // 保存失败（目录权限、磁盘满）先记下来由界面说，不在这儿弹框：Save() 是从各处调的。
            _saveFailureReason = ex.Message;
        }
    }
}
