using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SliderSorter.Core;

/// <summary>
/// BodySlide 的 <c>BuildSelection.xml</c>：只认 <c>Config["AppDir"] + PathSep + "BuildSelection.xml"</c>
/// （BodySlideApp.cpp:1196），而 AppDir 就是 BodySlide.exe 自己所在的目录，<c>Config.xml</c> 改不了它
/// （<c>SetDefaultValue</c> 标为 isDefault，<c>SaveConfig</c> 不写 isDefault 条目）。
/// <para>
/// MO2 启动时那个"exe 所在目录"是<b>虚拟</b>的：usvfs 把 <c>GetModuleFileNameW</c> 的返回值按反向映射改写成
/// 虚拟路径（<c>usvfs/src/usvfs_dll/hooks/kernel32.cpp</c> 的 <c>hook_GetModuleFileNameW</c>），于是 AppDir 是
/// <c>&lt;游戏&gt;\Data\CalienteTools\BodySlide</c>，读写这个文件都经虚拟 Data 落到某个模组上
/// （用户机上的 usvfs 日志：<c>mapping file in vfs: …\Data\CalienteTools\BodySlide\BuildSelection.xml →
/// …\mods\&lt;模组&gt;\CalienteTools\BodySlide\BuildSelection.xml</c>）。
/// </para>
/// <para>
/// 所以落点与分组文件是同一个模组里的兄弟（界面层的 <c>OutputTarget</c> 把这条关系算给上层）：分组读
/// <c>ProjectUtil::GetProjectPath() + "/SliderGroups"</c>（同文件 :3338），
/// 这个文件读 <c>&lt;AppDir&gt;\BuildSelection.xml</c>。写在别处——包括所选 BodySlide 安装的真实目录——
/// 会被优先级更高的模组挡住，BodySlide 读到的仍是旧的那一份。
/// </para>
/// 记录"同一个输出文件由哪个 slider set 来建"，BodySlide 读到就不再弹窗询问。
/// 本工具只维护自己认得的那些 <c>&lt;OutputChoice&gt;</c>，其余节点（含 <c>&lt;ZapChoice&gt;</c>）原样保留。
/// </summary>
public static class BuildSelectionFile
{
    public const string FileName = "BuildSelection.xml";

    /// <summary>覆盖已有文件前，原件的备份后缀。</summary>
    public const string BackupSuffix = ".bak";

    /// <summary>拼出落点：传 BodySlide 眼里的程序目录（虚拟 Data 下的 <c>CalienteTools\BodySlide</c>，
    /// 在 MO2 里由本工具自己的输出模组供给）。见类型注释。</summary>
    public static string PathFor(string bodySlideFolder) => Path.Combine(bodySlideFolder, FileName);

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
    /// <paramref name="managedPaths"/> 划出本工具负责的范围：范围内的旧条目原位改成 desired 里的值、
    /// desired 里没有（或值为空）的就删掉，范围外的条目以及注释、其它节点和原有的先后顺序一律不动。
    /// 写前备份成 <c>.bak</c>，再经临时文件整体替换。
    /// <para>
    /// 原位改而不是"删光重建"：tinyxml2 的 <c>NextSiblingElement(name)</c> 会跳过异名兄弟一路找下去
    /// （<c>lib/TinyXML-2/tinyxml2.cpp:1053-1062</c>），所以 <c>OutputChoice</c> 与 <c>ZapChoice</c>
    /// 交错排列它照样读得到——早先"碰到第一个异名兄弟就停、故必须连续排在最前"的说法是 pugixml 的语义，
    /// 照它做只会白改用户的文件（连带把注释和原有的先后顺序一起丢掉）。
    /// </para>
    /// <para>
    /// 匹配一律按<b>逐字节</b>：BodySlide 更新条目时用的是 <c>choice.first.compare(attrPath) == 0</c>
    /// （<c>BuildSelection.cpp:206-235</c>），存进去的选择又由区分大小写的 <c>std::map</c> 查回
    /// （<c>BuildSelection.h:21</c>）。同一路径的重复条目合并成一条，是为了让它"改第一个匹配元素"
    /// 与"读取时最后一条覆盖"落在同一个元素上。
    /// </para>
    /// </summary>
    /// <param name="written">新增或改动的条目数。</param>
    /// <param name="removed">被删除的条目数（用户在工具里取消了选择，或同一路径的重复条目被合并）。</param>
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
        var byPath = new Dictionary<string, List<XElement>>(StringComparer.Ordinal);
        foreach (var element in root.Elements("OutputChoice"))
        {
            var key = element.Attribute("path")?.Value;
            if (string.IsNullOrEmpty(key) || !managed.Contains(key))
                continue; // 路径缺失的、以及不归我们管的（别的安装/别的实例留下的）都不碰
            if (!byPath.TryGetValue(key!, out var list))
                byPath[key!] = list = [];
            list.Add(element);
        }

        foreach (var (key, elements) in byPath)
        {
            var choice = desired.TryGetValue(key, out var value) ? value : "";
            if (choice.Length == 0)
            {
                // 空 = 未决定：不写条目，让 BodySlide 继续问
                foreach (var element in elements)
                {
                    element.Remove();
                    removed++;
                }
                continue;
            }

            for (var i = 1; i < elements.Count; i++)
            {
                elements[i].Remove();
                removed++;
            }
            var keeping = elements[0];
            if (!string.Equals(keeping.Attribute("choice")?.Value, choice, StringComparison.Ordinal))
            {
                keeping.SetAttributeValue("choice", choice);
                written++;
            }
        }

        // 文件里原本没有的键：按 Ordinal 顺序追加在末尾，于是同一份选择导出两次会得到完全相同的内容
        foreach (var (key, choice) in desired
                     .Where(p => managed.Contains(p.Key) && p.Value.Length > 0 && !byPath.ContainsKey(p.Key))
                     .OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            root.Add(new XElement("OutputChoice", new XAttribute("path", key), new XAttribute("choice", choice)));
            written++;
        }

        if (written == 0 && removed == 0 && File.Exists(path))
            return true; // 没有可写的改动，也就不必碰用户的文件

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
