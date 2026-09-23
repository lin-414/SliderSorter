using System.Text;

namespace SliderSorter.Core;

/// <summary>极简 INI 解析器，覆盖 MO2 的 ModOrganizer.ini（QSettings 格式）所需的部分。</summary>
public static class IniParser
{
    public static Dictionary<string, Dictionary<string, string>> ParseFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(reader.ReadToEnd());
    }

    public static Dictionary<string, Dictionary<string, string>> Parse(string content)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        sections[""] = current;

        foreach (var raw in content.Split('\r', '\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                if (!sections.TryGetValue(name, out var section))
                {
                    section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sections[name] = section;
                }
                current = section;
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim();
            var value = DecodeQSettingsValue(line[(eq + 1)..].Trim());
            current[key] = value;
        }

        return sections;
    }

    /// <summary>
    /// QSettings 会把含空格、反斜杠等字符的字符串写成 <c>@ByteArray(...)</c> 包裹形式
    /// （MO2 的 <c>gamePath</c> 几乎必然如此）。解析层直接解掉：否则这个编码包裹会一路透传到
    /// 界面与诊断报告里，用户看到的是 <c>@ByteArray(E:\Skyrim AE\Skyrim Special Edition)</c> 这种东西。
    /// <para>
    /// 里面的内容是 Qt byte array 的转义写法（<c>\\</c> 表示一个反斜杠、<c>\xHH</c> 表示一个字节），
    /// 但**只有真的出现转义**（含 <c>\\</c>）才去解：未转义的写法 <c>@ByteArray(D:\Games\X)</c> 里
    /// <c>\G</c>、<c>\S</c> 都是普通字符，一律当转义处理会把这些路径改坏。
    /// </para>
    /// </summary>
    private static string DecodeQSettingsValue(string value)
    {
        const string prefix = "@ByteArray(";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || !value.EndsWith(')'))
            return value;

        var inner = value[prefix.Length..^1];
        return inner.Contains(@"\\", StringComparison.Ordinal) ? UnescapeQtByteArray(inner) : inner;
    }

    /// <summary>
    /// Qt byte array 转义解码（保守版）：只认 <c>\\</c>、<c>\0</c>、<c>\a \b \f \n \r \t \v</c>
    /// 与 <c>\xHH</c>；其余（含结尾孤立的反斜杠、位数不足的 <c>\x</c>）**原样保留两个字符**。
    /// 宁可留下一个多余的反斜杠让用户看得见，也不能把路径里的字符吃掉——丢字符是静默损坏。
    /// </summary>
    private static string UnescapeQtByteArray(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\\' || i + 1 >= text.Length)
            {
                sb.Append(c);
                continue;
            }

            var next = text[++i];
            switch (next)
            {
                case '\\': sb.Append('\\'); break;
                case '0': sb.Append('\0'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'v': sb.Append('\v'); break;
                case 'x' when i + 2 < text.Length && IsHexDigit(text[i + 1]) && IsHexDigit(text[i + 2]):
                    sb.Append((char)((HexValue(text[i + 1]) << 4) | HexValue(text[i + 2])));
                    i += 2;
                    break;
                default:
                    sb.Append('\\').Append(next);
                    break;
            }
        }
        return sb.ToString();
    }

    private static bool IsHexDigit(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => c - 'A' + 10,
    };
}
