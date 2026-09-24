using System.Text.RegularExpressions;

namespace SliderSorter.Core;

public record ModEntry(string Name, bool Enabled, bool IsForeign, bool IsSeparator, int Priority);

/// <summary>
/// 解析 profile 的 modlist.txt（与 MO2 profile.cpp 语义一致）：
/// + 启用，- 禁用，* 外来（DLC/CC），# 注释。
/// 文件中第一行数据 = 最高优先级（MO2 左侧栏最底部，文件冲突时获胜）；
/// 因此 MO2 左侧栏的自上而下显示顺序 = 文件行序的倒序。
/// 分隔符（*_separator）保留（用于重建 MO2 的分组视图）；备份（*backup[0-9]*）、Overwrite 伪模组跳过。
/// </summary>
public static class ModListParser
{
    /// <summary>MO2 里"备份"模组的写法是名字**结尾**跟一段 backup（可带序号、可带括号），
    /// 例如 <c>Foo backup</c>、<c>Foo (Backup 1)</c>。锚在结尾是为了不把只是含这个词的正经模组吃掉：
    /// <c>MCM设置备份与恢复 MCM Memory - Settings Backup and Restore</c> 这类必须留着。
    /// backup 前面得有个分隔符（空格/括号/连字符/下划线），否则 <c>MyBackup</c> 也会被当成备份。</summary>
    private static readonly Regex BackupRegex =
        new(@"^(?:.*[\s(\[-])?backup[ _-]?[0-9]*\)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static List<ModEntry> Parse(string modListPath)
    {
        // 与 IniParser 同一口径：MO2 运行中就会持有 modlist.txt，FileShare.Read 会在它写文件的
        // 那个瞬间把这里顶成 IOException（README 承诺 MO2 开着也能用，这一步是唯一的入口）。
        using var stream = new FileStream(modListPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, bufferSize: 4096, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true);
        return ParseContent(reader.ReadToEnd());
    }

    public static List<ModEntry> ParseContent(string content)
    {
        var result = new List<ModEntry>();
        var priority = 0;

        foreach (var raw in content.Split('\r', '\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            bool enabled = true;
            bool foreign = false;
            var name = line;

            var first = line[0];
            if (first is '+' or '-' or '*')
            {
                enabled = first != '-';
                foreign = first == '*';
                name = line[1..].Trim();
            }

            if (name.Length == 0)
                continue;
            if (name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new ModEntry(name, enabled, foreign, true, priority++));
                continue;
            }
            if (name.Equals("Overwrite", StringComparison.OrdinalIgnoreCase))
                continue;
            if (BackupRegex.IsMatch(name))
                continue;

            result.Add(new ModEntry(name, enabled, foreign, false, priority++));
        }

        return result;
    }

    /// <summary>启用且真实存在于 mods 目录的模组，按优先级从高到低（与 modlist 行序一致）。</summary>
    public static List<(ModEntry Entry, string Dir)> GetEnabledModDirectories(List<ModEntry> entries, string modsDirectory)
    {
        var result = new List<(ModEntry, string)>();
        foreach (var entry in entries.Where(e => e.Enabled && !e.IsForeign && !e.IsSeparator))
        {
            var dir = Path.Combine(modsDirectory, entry.Name);
            if (Directory.Exists(dir))
                result.Add((entry, dir));
        }
        return result;
    }
}
