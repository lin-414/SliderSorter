using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SliderSorter.Core;

public class SliderGroup
{
    public string Name { get; set; } = "";
    public List<string> Members { get; set; } = new();

    public SliderGroup() { }

    public SliderGroup(string name) => Name = name;

    public SliderGroup(string name, IEnumerable<string> members) : this(name) => Members.AddRange(members);

    /// <summary>深拷贝（撤销快照用）。</summary>
    public SliderGroup Clone() => new(Name, Members);
}

/// <summary>
/// BodySlide 分组文件（&lt;SliderGroups&gt; → &lt;Group name&gt; → &lt;Member name&gt;）的读写。
/// 写出格式与 BodySlide 自身一致：UTF-8 带 BOM、XML 声明、缩进。
/// 注意：BodySlide 的成员匹配是大小写敏感的精确比较，这里原样保存字符串。
/// </summary>
public static class SliderGroupFile
{
    /// <summary>
    /// WinForms 版（本工具的前身，UI/MainForm.cs）写出的单文件分组文件名。
    /// 现役格式是"每组一个 &lt;组名&gt;.xml + 清单"，此常量只服务于旧格式的读取与清理：
    /// 老用户的 SliderGroups 目录里可能仍有这个文件，不迁移会让 BodySlide 同时读到新旧两份分组。
    /// 不是死代码，勿删。
    /// </summary>
    public const string DefaultFileName = "SliderSorter.xml";

    public const string ManifestFileName = "SliderSorter.files.txt";

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>组名转安全文件名（非法字符替换为下划线、Windows 保留名加前缀、自动补 .xml）。</summary>
    public static string FileNameForGroup(string groupName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in groupName.Trim())
            sb.Append(invalid.Contains(c) ? '_' : c);
        var name = sb.ToString().TrimEnd('.', ' ');
        if (name.Length == 0)
            name = CoreStrings.Get("L.Core_FileUnnamedGroup");
        var stem = name.Contains('.') ? name[..name.IndexOf('.')] : name;
        if (ReservedDeviceNames.Contains(stem))
            name = "_" + name; // Windows 保留设备名（CON/NUL/COM1…）连同任意扩展名都禁用
        return name + ".xml";
    }

    /// <summary>
    /// 清单条目是否是"裸文件名"，即 <c>Path.Combine(dir, entry)</c> 必定落在 dir 之内。
    /// 清单（<see cref="ManifestFileName"/>）是输出目录里的纯文本文件，用户或其它程序都可能改写它，
    /// 因此其中的字符串不能直接当路径用：<c>Path.Combine(dir, "D:\x")</c> 会**丢弃 dir**，
    /// <c>Path.Combine(dir, "..\..\x")</c> 能越出输出目录，<c>Path.Combine(dir, "D:x")</c>（驱动器相对
    /// 路径）则按 D 盘当前目录解析——三种都会让删除动作落到输出目录之外。
    /// 本工具自己写出的条目必然通过本校验（<see cref="FileNameForGroup"/> 已把 Windows 非法字符
    /// 换成下划线，其中含 \ / :），所以收紧不会影响正常清理。
    /// </summary>
    public static bool IsBareFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            return false;
        // 显式列举而不是用 Path.GetFileName(name) == name：后者的行为取决于运行平台的分隔符定义
        //（Unix 上反斜杠不是分隔符，"C:\x" 会被判成裸名）。清单是磁盘上的文件，只按最严的
        // Windows 规则判才安全，也让单测在任何平台上结论一致。
        return name.IndexOfAny(['\\', '/', ':']) < 0;
    }

    public static bool TryLoad(string path, out List<SliderGroup> groups, out string error)
    {
        groups = new List<SliderGroup>();
        try
        {
            groups = Load(path);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static List<SliderGroup> Load(string path)
    {
        var doc = XDocument.Load(path, LoadOptions.None);
        var root = doc.Root ?? throw new InvalidDataException(CoreStrings.Get("L.Core_FileEmpty"));
        if (root.Name.LocalName != "SliderGroups")
            throw new InvalidDataException(CoreStrings.Get("L.Core_FileNotSliderGroups"));

        var groups = new List<SliderGroup>();
        foreach (var groupElement in root.Elements("Group"))
        {
            var name = (string?)groupElement.Attribute("name");
            if (string.IsNullOrEmpty(name))
                continue;
            var group = new SliderGroup(name);
            foreach (var member in groupElement.Elements("Member"))
            {
                var memberName = (string?)member.Attribute("name");
                if (!string.IsNullOrEmpty(memberName) && !group.Members.Contains(memberName, StringComparer.Ordinal))
                    group.Members.Add(memberName);
            }
            groups.Add(group);
        }
        return groups;
    }

    public static void Save(string path, IEnumerable<SliderGroup> groups)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("SliderGroups",
                groups.Select(g => new XElement("Group",
                    new XAttribute("name", g.Name),
                    g.Members.Select(m => new XElement("Member", new XAttribute("name", m)))))));

        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "    ",
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            OmitXmlDeclaration = false,
        };
        // 先写临时文件再替换：写一半崩溃/断电不会留下损坏的分组 XML
        var temp = path + ".tmp";
        try
        {
            using (var writer = XmlWriter.Create(temp, settings))
                doc.Save(writer);
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }
        catch
        {
            // 失败时清掉半成品临时文件：否则输出目录里会留下一个来路不明的 .tmp，
            // 用户既不知道它是什么、也不知道能不能删。清理本身失败无所谓，别盖住原始异常。
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
                // 忽略
            }
            throw;
        }
    }

    /// <summary>合并导入（组名按不区分大小写匹配，保留先出现的写法；成员按精确去重）。</summary>
    public static void Merge(List<SliderGroup> target, IEnumerable<SliderGroup> source,
        out int addedGroups, out int addedMembers)
    {
        addedGroups = 0;
        addedMembers = 0;

        foreach (var incoming in source)
        {
            var existing = target.FirstOrDefault(g =>
                string.Equals(g.Name, incoming.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new SliderGroup(incoming.Name);
                target.Add(existing);
                addedGroups++;
            }

            foreach (var member in incoming.Members)
            {
                if (!existing.Members.Contains(member, StringComparer.Ordinal))
                {
                    existing.Members.Add(member);
                    addedMembers++;
                }
            }
        }
    }
}
