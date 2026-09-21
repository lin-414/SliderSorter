using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace BSGroupGenerator.Core;

/// <summary>
/// BodySlide 的 <c>BuildSelection.xml</c>（BodySlide 程序目录下的 <c>Config.xml</c> 同级）：
/// 记录"同一个输出文件由哪个 slider set 来建"，BodySlide 读到就不再弹窗询问。
/// 本工具只维护自己认得的那些 <c>&lt;OutputChoice&gt;</c>，其余节点（含 <c>&lt;ZapChoice&gt;</c>）原样保留。
/// </summary>
public static class BuildSelectionFile
{
    public const string FileName = "BuildSelection.xml";

    /// <summary>覆盖已有文件前，原件的备份后缀。</summary>
    public const string BackupSuffix = ".bak";

    public static string PathFor(string bodySlideAppDir) => Path.Combine(bodySlideAppDir, FileName);

    /// <summary>读出现有的输出选择（path → choice）。文件不存在算成功且结果为空——首次导出就是这种情况。</summary>
    public static bool TryRead(string path, out Dictionary<string, string> outputChoices, out string? error)
    {
        outputChoices = new Dictionary<string, string>(StringComparer.Ordinal);
        error = null;
        if (!File.Exists(path))
            return true;

        if (!TryLoad(path, out var doc, out error))
            return false;

        outputChoices = ReadChoices(doc!.Root!);
        return true;
    }

    /// <summary>
    /// 把 <paramref name="desired"/> 写进 <paramref name="path"/>。
    /// <paramref name="managedPaths"/> 划出本工具负责的范围：范围内的旧条目按 desired 覆盖或删除，
    /// 范围外的条目以及所有非 OutputChoice 节点都不动。写前备份成 <c>.bak</c>，再经临时文件整体替换。
    /// </summary>
    /// <param name="written">新增或改动的条目数。</param>
    /// <param name="removed">被删除的条目数（用户在工具里取消了选择）。</param>
    public static bool TryExport(string path, IReadOnlyDictionary<string, string> desired,
        IEnumerable<string> managedPaths, out int written, out int removed, out string? error)
    {
        written = 0;
        removed = 0;
        error = null;

        var managed = new HashSet<string>(managedPaths, StringComparer.Ordinal);
        XDocument? doc;
        if (File.Exists(path))
        {
            if (!TryLoad(path, out doc, out error))
                return false;
        }
        else
        {
            doc = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("BuildSelection"));
        }

        var root = doc!.Root!;
        var before = ReadChoices(root);
        var others = root.Elements().Where(e => e.Name.LocalName != "OutputChoice").ToList();

        // 范围外的既有选择保持不变
        var after = before.Where(p => !managed.Contains(p.Key))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var (key, choice) in desired)
        {
            if (choice.Length == 0)
                after.Remove(key); // 空串 = 未决定：不写条目，让 BodySlide 继续问
            else
                after[key] = choice;
        }

        written = after.Count(p => !before.TryGetValue(p.Key, out var old) || old != p.Value);
        removed = before.Count(p => !after.ContainsKey(p.Key));
        if (written == 0 && removed == 0 && File.Exists(path))
            return true; // 没有可写的改动，也就不必碰用户的文件

        // BodySlide 用 NextSiblingElement("OutputChoice") 遍历，碰到第一个异名兄弟就停
        //（BuildSelection.cpp:14-27），所以这些条目必须连续排在其它元素之前。
        root.RemoveNodes();
        foreach (var (key, choice) in after.OrderBy(p => p.Key, StringComparer.Ordinal))
            root.Add(new XElement("OutputChoice", new XAttribute("path", key), new XAttribute("choice", choice)));
        foreach (var element in others)
            root.Add(element);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

            var temp = path + ".tmp";
            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "    ",
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), // BodySlide 自己存盘也带 BOM
                OmitXmlDeclaration = false,
            };
            try
            {
                using (var writer = XmlWriter.Create(temp, settings))
                    doc.Save(writer);

                if (File.Exists(path))
                {
                    File.Copy(path, path + BackupSuffix, overwrite: true);
                    File.Replace(temp, path, null);
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            catch
            {
                try
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                }
                catch
                {
                    // 清不掉半成品不该盖住真正的失败原因
                }
                throw;
            }
            return true;
        }
        catch (Exception ex)
        {
            // BodySlide 正在运行时文件会被它占住；写权限问题同理。
            error = CoreStrings.Format("L.Core_BuildSelWriteFail", ex.Message);
            return false;
        }
    }

    /// <summary>解析文件；根元素不是 BuildSelection 或 XML 读不懂时返回 false 并给出原因。</summary>
    private static bool TryLoad(string path, out XDocument? doc, out string? error)
    {
        doc = null;
        try
        {
            doc = XDocument.Load(path);
        }
        catch (Exception ex)
        {
            error = CoreStrings.Format("L.Core_BuildSelParseFail", Path.GetFileName(path), ex.Message);
            return false;
        }
        if (doc.Root is null || doc.Root.Name.LocalName != "BuildSelection")
        {
            error = CoreStrings.Format("L.Core_BuildSelBadRoot", doc.Root?.Name.LocalName ?? "");
            doc = null;
            return false;
        }
        error = null;
        return true;
    }

    private static Dictionary<string, string> ReadChoices(XElement root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in root.Elements("OutputChoice"))
        {
            var key = element.Attribute("path")?.Value;
            var choice = element.Attribute("choice")?.Value;
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(choice))
                continue; // 无 choice 或 choice 为空，BodySlide 都当作未决定（BuildSelection.cpp:21-25）
            result[key] = choice;
        }
        return result;
    }
}
