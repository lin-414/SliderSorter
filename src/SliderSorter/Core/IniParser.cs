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
    /// <para>
    /// 其余 <c>@Xxx(...)</c> 形式（括号配对的 <c>@</c> 打头值）是 QSettings 的**类型标记**
    /// （<c>@Invalid()</c>、<c>@Rect(...)</c>、<c>@Variant(...)</c> 等），不是字符串：
    /// <c>@Invalid()</c> 是"未设置/无效"的值，真实 MO2 ini 里实例尚未配置游戏时
    /// <c>gamePath</c>、<c>gameName</c> 就是它。原样透传会让所有
    /// <c>IsNullOrWhiteSpace</c> 守卫失效——<see cref="Mo2Instance.GamePath"/> 返回字面量
    /// <c>@Invalid()</c>，拼出 <c>@Invalid()\Data</c> 冒充真实游戏目录、跳过注册表回退，
    /// 诊断报告里也印出这行垃圾。归一为空串 = 按"未设置"走各处已有的回退。
    /// </para>
    /// </summary>
    private static string DecodeQSettingsValue(string value)
    {
        const string prefix = "@ByteArray(";
        if (value.StartsWith(prefix, StringComparison.Ordinal) && value.EndsWith(')'))
        {
            var inner = value[prefix.Length..^1];
            return inner.Contains(@"\\", StringComparison.Ordinal) ? UnescapeQtByteArray(inner) : inner;
        }

        // 其余 @ 打头、且括号配对的（@Invalid()、@Rect(...)、@Variant(...) 等）是 QSettings 的
        // **类型标记**，不是字符串：@Invalid() 是"未设置/无效"的值，真实 MO2 ini 里实例尚未配置
        // 游戏时 gamePath、gameName 就是它。原样透传会让所有 IsNullOrWhiteSpace 守卫失效——
        // Mo2Instance.GamePath 返回字面量 @Invalid()，拼出 "@Invalid()\Data" 冒充真实游戏目录、
        // 跳过注册表回退，诊断报告里也印出这行垃圾。归一为空串 = 按"未设置"走各处已有的回退。
        // 括号不配对的不算（截断的 "@ByteArray(" 那类坏值保持原样——宁可让用户看见，也不猜）。
        // 已知代价：真实字符串恰好以 @ 开头、末尾是 ')' 且括号配对时也会被当成标记（MO2 的
        // 路径实际长不成这样）——归一为空串走的是各键已有的"未设置"回退，不崩、不写坏数据；
        // 换来 Qt 任何标记（含未来新增的）一并兜住。按标记名白名单收窄反而会把没见过的标记漏成字面量。
        var open = value.IndexOf('(');
        if (value.StartsWith('@') && open > 1 && value.EndsWith(')'))
            return "";

        return value;
    }

    /// <summary>
    /// Qt byte array 转义解码（保守版）：只认 <c>\\</c>、<c>\0</c>、<c>\a \b \f \n \r \t \v</c>
    /// 与 <c>\xHH</c>；其余（含结尾孤立的反斜杠、位数不足的 <c>\x</c>）**原样保留两个字符**。
    /// 宁可留下一个多余的反斜杠让用户看得见，也不能把路径里的字符吃掉——丢字符是静默损坏。
    /// <para>
    /// 关键在于 <c>@ByteArray</c> 里装的是**字节**，而 Qt 把非 ASCII 一律写成 <c>\xHH</c>：
    /// 一个中文字是三个 <c>\xHH</c>（它的 UTF-8 字节）。逐字节直接 <c>(char)b</c> 等于按 Latin-1 解码，
    /// <c>\xe6\xb8\xb8</c> 就变成三个奇形字符，路径当场读成乱码 —— 于是 <c>Directory.Exists</c>
    /// 全失败、模组一个也扫不到，而诊断里显示的仍是这个错路径。所以整段先收成字节，末尾按 UTF-8 解。
    /// 真实 MO2 安装里的 gamePath 基本是纯 ASCII（<c>@ByteArray(E:\\Skyrim AE\\...)</c>），
    /// 那种情况按字节直投，结果与逐字符一致。
    /// </para>
    /// </summary>
    private static string UnescapeQtByteArray(string text)
    {
        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\\' || i + 1 >= text.Length)
            {
                AddUtf8(bytes, c); // 值里也可能本来就是没转义的非 ASCII 字符：按 UTF-8 收进同一个流
                continue;
            }

            var next = text[++i];
            switch (next)
            {
                case '\\': bytes.Add((byte)'\\'); break;
                case '0': bytes.Add(0); break;
                case 'a': bytes.Add((byte)'\a'); break;
                case 'b': bytes.Add((byte)'\b'); break;
                case 'f': bytes.Add((byte)'\f'); break;
                case 'n': bytes.Add((byte)'\n'); break;
                case 'r': bytes.Add((byte)'\r'); break;
                case 't': bytes.Add((byte)'\t'); break;
                case 'v': bytes.Add((byte)'\v'); break;
                case 'x' when i + 2 < text.Length && IsHexDigit(text[i + 1]) && IsHexDigit(text[i + 2]):
                    bytes.Add((byte)((HexValue(text[i + 1]) << 4) | HexValue(text[i + 2])));
                    i += 2;
                    break;
                default:
                    bytes.Add((byte)'\\');
                    AddUtf8(bytes, next);
                    break;
            }
        }

        return DecodeQtBytes(bytes);
    }

    private static void AddUtf8(List<byte> bytes, char c)
    {
        if (c <= 0x7F)
            bytes.Add((byte)c);
        else
            bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
    }

    /// <summary>只用来判"这段字节是不是合法 UTF-8"的解码器：非法序列抛
    /// <see cref="DecoderFallbackException"/>，而不是像 <c>Encoding.UTF8</c> 那样换成 U+FFFD 蒙混过关。</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>整段字节按 UTF-8 解。解不动（不是合法 UTF-8，例如早先按本地 ANSI 代码页存的值）时
    /// 退回逐字节直投，与修复前的行为一致——至少不吞字符。含非 ASCII 而 UTF-8 又解得开时，
    /// 得到的就是原始那个路径，这是本次修复的目的。</summary>
    private static string DecodeQtBytes(List<byte> bytes)
    {
        var ascii = true;
        foreach (var b in bytes)
            if (b > 0x7F)
            {
                ascii = false;
                break;
            }

        if (ascii)
        {
            var chars = new char[bytes.Count];
            for (var i = 0; i < chars.Length; i++)
                chars[i] = (char)bytes[i];
            return new string(chars);
        }

        var raw = bytes.ToArray();
        try
        {
            // 严格判定：非法序列要显式抛出来，而不是让 Encoding.UTF8 换成 U+FFFD 蒙混过关
            return StrictUtf8.GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            var chars = new char[raw.Length];
            for (var i = 0; i < chars.Length; i++)
                chars[i] = (char)raw[i];
            return new string(chars);
        }
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
