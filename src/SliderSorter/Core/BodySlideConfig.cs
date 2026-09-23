using System.Xml.Linq;

namespace SliderSorter.Core;

/// <summary>
/// 读取 BodySlide 目录下的 Config.xml（根元素 &lt;Config&gt;，键即子元素名，值为元素文本）。
/// GameDataPaths / GameRegKey / GameRegVal 等分组键按 "父/子" 展平。
/// </summary>
public class BodySlideConfig
{
    /// <summary>与 BodySlide GameUtil::TargetGames 的顺序一致（Config.xml 注释：0=FO3 … 9=Starfield）。</summary>
    public static readonly string[] TargetGames =
    {
        "Fallout3", "FalloutNewVegas", "Skyrim", "Fallout4", "SkyrimSpecialEdition",
        "Fallout4VR", "SkyrimVR", "Fallout76", "Oblivion", "Starfield",
    };

    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string ConfigPath { get; }
    public int TargetGameIndex { get; private set; } = -1;

    public BodySlideConfig(string configPath)
    {
        ConfigPath = configPath;
        Load();
    }

    private void Load()
    {
        var doc = XDocument.Load(ConfigPath, LoadOptions.None);
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "Config")
            throw new InvalidDataException(CoreStrings.Get("L.Core_ConfigRootMissing"));

        foreach (var element in root.Elements())
        {
            var key = element.Name.LocalName;

            if (element.HasElements)
            {
                foreach (var child in element.Elements())
                {
                    var value = child.Value.Trim();
                    if (value.Length == 0)
                        value = (string?)child.Attribute("value") ?? (string?)child.Attribute("path") ?? "";
                    _values[$"{key}/{child.Name.LocalName}"] = value;
                }
            }

            var self = element.Value.Trim();
            if (self.Length == 0)
                self = (string?)element.Attribute("value") ?? "";
            _values[key] = self;
        }

        if (_values.TryGetValue("TargetGame", out var tg) && int.TryParse(tg, out var index))
            TargetGameIndex = index;
        else
            TargetGameIndex = -1;
    }

    public string TargetGameName =>
        TargetGameIndex >= 0 && TargetGameIndex < TargetGames.Length ? TargetGames[TargetGameIndex] : "";

    public string Get(string key) => _values.TryGetValue(key, out var value) ? value : "";

    /// <summary>Config.xml 中记录的游戏 Data 路径（全局 GameDataPath 优先，其次按游戏的 GameDataPaths/&lt;Game&gt;）。</summary>
    public string GetGameDataPath()
    {
        var global = Get("GameDataPath");
        if (!string.IsNullOrWhiteSpace(global))
            return global;

        var game = TargetGameName;
        if (game.Length > 0)
        {
            var perGame = Get($"GameDataPaths/{game}");
            if (!string.IsNullOrWhiteSpace(perGame))
                return perGame;
        }

        return "";
    }

    /// <summary>按 Config.xml 里的注册表键读取游戏安装目录（与 BodySlide 相同：HKLM 32 位视图）。</summary>
    public string GetGamePathFromRegistry()
    {
        var game = TargetGameName;
        if (game.Length == 0)
            return "";

        var regKey = Get($"GameRegKey/{game}");
        var regValue = Get($"GameRegVal/{game}");
        if (regKey.Length == 0 || regValue.Length == 0)
            return "";

        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32);
            using var key = baseKey.OpenSubKey(regKey);
            var path = key?.GetValue(regValue) as string;
            return path?.TrimEnd('\\', '/') ?? "";
        }
        catch
        {
            return "";
        }
    }
}
