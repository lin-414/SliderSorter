using System.IO;
using System.Reflection;
using BSGroupGenerator.Wpf.ViewModels;
using BSGroupGenerator.Wpf.Views;
using Xunit;

namespace BSGroupGenerator.Wpf.Tests;

/// <summary>XAML 里 <c>{Binding XxxCommand}</c> 的路径必须真的存在于对应 ViewModel 上。
///
/// 起因是一次真实故障：MainWindow.xaml 的保存（Ctrl+S / 菜单 / 底部按钮）与「探测」按钮绑的是
/// <c>SaveAsyncCommand</c> / <c>DetectBodySlideCommand</c>，而 CommunityToolkit.Mvvm 生成的属性叫
/// <c>SaveCommand</c>（生成命令时**去掉 Async 后缀**）/ <c>DetectBodySlideCoreCommand</c>。
/// WPF 绑定走反射，路径写错不报错也不警告：按钮照常可点、可聚焦，点下去毫无反应，
/// 编译期与现有测试全绿——只能靠这类「绑定路径 ↔ 属性」的对照测试兜住。
///
/// 本用例不构造窗口、不碰磁盘数据（Txt/ini/xml 一律不读），只用反射对照类型，
/// 因此在没有真实 BodySlide 路径 / 分组文件 / MO2 实例的机器上同样能跑。
/// 「真的能构造出窗口」这一层由 MainWindowCommandTests 负责。</summary>
public class XamlCommandBindingTests
{
    /// <summary>视图（相对 WPF 项目根，分隔符一律 <c>/</c>）→ 该文件里 <c>{Binding}</c> 的求值对象类型。
    ///
    /// DataContext 不在 XAML 里声明（代码里 <c>DataContext = ...</c> 赋的），所以只能写死。
    /// 少了登记项时下面的 <see cref="UnregisteredViewsMustNotBindCommands"/> 会失败，逼作者补表，
    /// 不然一个新增的 ViewModel 窗口就能悄悄漏过本用例。
    /// Views/Pages 下的页面同样要登记：壳把 DataContext 传下去，页面里的 {Binding} 也落在 MainViewModel 上。</summary>
    private static readonly Dictionary<string, Type> BoundTarget = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Views/MainWindow.xaml"] = typeof(MainViewModel),
        ["Views/Pages/GroupGenerationPage.xaml"] = typeof(MainViewModel),
        ["Views/Pages/OutputConflictPage.xaml"] = typeof(MainViewModel),
        ["Views/Pages/RulePresetsPage.xaml"] = typeof(MainViewModel),
        ["Views/Pages/SettingsPage.xaml"] = typeof(MainViewModel),
        // 这两个窗口的 DataContext 就是自己（DataContext = this），绑定路径即窗口自身的公开属性
        ["Views/InputWindow.xaml"] = typeof(InputWindow),
        ["Views/NotifyDialog.xaml"] = typeof(NotifyDialog),
        // 控件模板里的绑定作用于「被模板化的那个项」，节点树的模板项类型是 NodeVM（ToggleExpandCommand）
        ["Themes/Controls.xaml"] = typeof(NodeVM),
        // Components.xaml 里的行模板（NodeRowText / NodeRowCheckBox）绑在 NodeVM 的
        // IsConflict / IsSeparator / IsPlaceholder / IsMember / IsChecked 上，同样按模板项求值。
        // 它是 app 级字典，漏登记的话这里的绑定路径没人对照——而「静默失效」正是本用例要防的。
        ["Themes/Components.xaml"] = typeof(NodeVM),
    };

    /// <summary>整份路径都要对照的视图。其余视图（如 MainWindow）的绑定里混着 DataTemplate 内部的
    /// 路径（Display / Level / IsMember…），它们是绑到模板项而不是 DataContext 上的，
    /// 全量对照会误报；这类文件只对照 <c>*Command</c> 路径——而死按钮正是本用例要防的东西。</summary>
    private static readonly HashSet<string> FullPathCheck = new(StringComparer.OrdinalIgnoreCase)
    {
        "Views/InputWindow.xaml",
        "Views/NotifyDialog.xaml",
    };

    [Fact]
    public void EveryCommandBindingPathExistsOnItsViewModel()
    {
        var checkedPaths = new List<string>();

        foreach (var (relative, target) in BoundTarget)
        {
            var file = Path.Combine(WpfProjectDir(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), $"找不到 XAML：{relative}");

            foreach (var binding in ParseBindings(File.ReadAllText(file)))
            {
                // 绑到别的元素（ElementName=… / RelativeSource Self）的路径不在 DataContext 上求值
                if (binding.TargetsAnotherElement)
                    continue;

                var path = binding.Path;
                if (path.Length == 0)
                    continue;

                var isCommand = path.Split('.')[^1].EndsWith("Command", StringComparison.Ordinal);
                if (!isCommand && !FullPathCheck.Contains(relative))
                    continue;

                checkedPaths.Add($"{relative}: {path}");
                AssertPathResolves(target, path, relative);
            }
        }

        // 兜底：命令绑定是真实存在的一批路径，别因为解析器写崩而「零断言通过」。
        // 逐条钉住"哪个视图上的哪个命令"，这样某个命令随布局搬家时（比如探测按钮从主界面搬进设置页）
        // 是这条断言失败提醒作者改指向，而不是那批绑定悄悄失去对照。
        Assert.Contains("Views/MainWindow.xaml: SaveCommand", checkedPaths);
        Assert.Contains("Views/MainWindow.xaml: UndoCommand", checkedPaths);
        Assert.Contains("Views/Pages/SettingsPage.xaml: DetectBodySlideCommand", checkedPaths);
        Assert.Contains("Views/Pages/GroupGenerationPage.xaml: ApplyCheckedToCurrentGroupCommand", checkedPaths);
    }

    /// <summary>没登记 DataContext 类型的视图不该出现命令绑定：出现就说明它是某个 ViewModel 的宿主，
    /// 必须登记进 <see cref="BoundTarget"/>，否则它的绑定路径没人对照（本用例的漏洞）。
    ///
    /// 必须递归扫：Views/Pages 下的页面同样绑命令，非递归 glob 会让它们**静默**逃过本用例——
    /// 不报错比报错危险。</summary>
    [Fact]
    public void UnregisteredViewsMustNotBindCommands()
    {
        var offenders = new List<string>();
        var viewsDir = Path.Combine(WpfProjectDir(), "Views");

        foreach (var file in Directory.EnumerateFiles(viewsDir, "*.xaml", SearchOption.AllDirectories))
        {
            var relative = "Views/" + Path.GetRelativePath(viewsDir, file).Replace(Path.DirectorySeparatorChar, '/');
            if (BoundTarget.ContainsKey(relative))
                continue;

            foreach (var binding in ParseBindings(File.ReadAllText(file)))
                if (binding.Path.Split('.')[^1].EndsWith("Command", StringComparison.Ordinal))
                    offenders.Add($"{relative}: {binding.Raw}");
        }

        Assert.True(offenders.Count == 0,
            "以下视图绑定了命令，但没有登记 DataContext 类型，绑定路径无人对照——请加入 BoundTarget：\n"
            + string.Join("\n", offenders));
    }

    /// <summary>两个曾经写错、静默失效的命令名，单独钉住：生成命令时 Async 后缀会被去掉。
    /// （ViewModels 里改名会让本用例失败，属于预期——XAML 必须同步改。）</summary>
    [Fact]
    public void GeneratedCommandNamesMatchTheDocumentedConvention()
    {
        Assert.NotNull(typeof(MainViewModel).GetProperty("SaveCommand", PublicInstance));
        Assert.NotNull(typeof(MainViewModel).GetProperty("DetectBodySlideCommand", PublicInstance));
        Assert.NotNull(typeof(NodeVM).GetProperty("ToggleExpandCommand", PublicInstance));
    }

    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

    private static void AssertPathResolves(Type target, string path, string file)
    {
        var current = target;
        foreach (var segment in path.Split('.'))
        {
            if (segment.Length == 0 || segment.Contains('[') || segment.Contains('('))
                return; // 索引器 / 方法调用路径交给运行期，不在这里猜

            var property = current.GetProperty(segment, PublicInstance);
            Assert.True(property is not null,
                $"{file} 绑定了 \"{path}\"，但 {current.Name} 上没有公开属性 {segment}。" +
                "WPF 绑定路径写错不会报错，只会让控件静默失效。");
            current = property!.PropertyType;
        }
    }

    /// <summary>WPF 项目的源码目录。从测试程序集往上找仓库根，避免写死 bin 的层级深度。</summary>
    private static string WpfProjectDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var probe = Path.Combine(dir.FullName, "src", "BSGroupGenerator.Wpf", "Views", "MainWindow.xaml");
            if (File.Exists(probe))
                return Path.Combine(dir.FullName, "src", "BSGroupGenerator.Wpf");
        }
        throw new InvalidOperationException(
            $"从 {AppContext.BaseDirectory} 向上找不到 src/BSGroupGenerator.Wpf；本用例需要仓库源码在测试程序集附近。");
    }

    private sealed record BindingRef(string Raw, string Path, bool TargetsAnotherElement);

    /// <summary>把文本里所有 <c>{Binding …}</c> 摘出来。花括号按嵌套计数配对，
    /// 因为 <c>Converter={StaticResource BoolToVis}</c> 这类参数自带一层花括号。</summary>
    private static IEnumerable<BindingRef> ParseBindings(string xaml)
    {
        for (var i = 0; i < xaml.Length; i++)
        {
            if (xaml[i] != '{' || !xaml.AsSpan(i).StartsWith("{Binding"))
                continue;

            var depth = 0;
            var end = -1;
            for (var j = i; j < xaml.Length; j++)
            {
                if (xaml[j] == '{')
                    depth++;
                else if (xaml[j] == '}' && --depth == 0)
                {
                    end = j;
                    break;
                }
            }
            if (end < 0)
                break;

            var args = xaml[(i + "{Binding".Length)..end];
            i = end;

            var segments = SplitTopLevel(args);
            var path = "";
            var targetsAnotherElement = false;
            foreach (var (index, segment) in segments.Select((s, idx) => (idx, s.Trim())))
            {
                var eq = segment.IndexOf('=');
                if (eq < 0)
                {
                    if (index == 0)
                        path = segment; // {Binding SaveCommand}
                    continue;
                }

                var key = segment[..eq].Trim();
                var value = segment[(eq + 1)..].Trim();
                switch (key)
                {
                    case "Path":
                        path = value;
                        break;
                    // RelativeSource/ElementName 让路径在别的元素上求值；Self 是在自身类型上求值，
                    // 都与 DataContext 无关，跳过。
                    case "RelativeSource":
                        targetsAnotherElement = true;
                        break;
                    case "ElementName":
                        targetsAnotherElement = true;
                        break;
                }
            }

            yield return new BindingRef($"{{Binding{args}}}", path, targetsAnotherElement);
        }
    }

    /// <summary>按顶层逗号切分（跳过花括号内的逗号）。</summary>
    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(text[start..i]);
                    start = i + 1;
                    break;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }
}
