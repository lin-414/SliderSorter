using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LayoutProbe;

/// <summary>
/// 译文/主题布局溢出探针。
///
/// 用法（仓库根目录下执行）：
///   dotnet run --project tools/layout-probe -c Release
///   dotnet run --project tools/layout-probe -c Release -- &lt;仓库根目录&gt;
/// 退出码 0 = 无溢出/裁切，1 = 有命中（可作为门禁）。
///
/// 做法：加载**真实**资源字典与**真实**窗口/页面 XAML，离屏 Measure/Arrange 后读 ActualWidth，
/// 按三种失效模式判定：
///   ① 自己被压窄     ActualWidth &lt; 文本所需宽（不可折行、无省略号）
///   ② 溢出父容器     ActualWidth &gt; 父容器可用宽
///   ③ 溢出 Grid 单元格  ActualWidth &gt; 所在列宽（固定像素列不约束子元素）
/// 刻意用真实控件而不是把逻辑抄进探针：容器尺寸、内边距、布局舍入都算在内，
/// 抄一遍逻辑就会漏掉控件自身的成本。
///
/// 扫描递归进 Views/Pages：页面根不是 Window，会被套进一个只当继承宿主的临时窗口，
/// 因为 ViewModels/隐式 Window 样式提供的字体不量进去，测出的宽度和真实渲染对不上。
/// </summary>
internal static class Program
{
    // 程序集名：WPF 工程 csproj 里 AssemblyName=BSGroupGenerator（不是 BSGroupGenerator.Wpf），
    // Core 才是 BSGroupGenerator.Core。写错会报"找不到程序集"而不是"找不到资源"。
    private const string AppAssembly = "BSGroupGenerator";

    private static readonly string[] Langs = ["zh", "en", "ru", "fr"];
    private static readonly string[] Themes = ["Boutique", "Light"];

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private const int SM_CYCAPTION = 4;
    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CXPADDEDBORDER = 92;

    /// <summary>代码后台不在，这些事件属性的值（处理函数名）解析不到，必须剥掉。</summary>
    private static readonly string[] EventAttrs =
    [
        "Click", "MouseDoubleClick", "MouseLeftButtonUp", "MouseLeftButtonDown", "MouseRightButtonUp",
        "MouseRightButtonDown", "MouseRightClick",
        "MouseWheel", "MouseDown", "MouseUp", "MouseMove", "DragDelta", "DragStarted", "DragCompleted",
        "DragOver", "DragEnter", "DragLeave", "Drop", "Checked", "Unchecked", "Indeterminate",
        "SelectionChanged", "TextChanged", "KeyDown", "KeyUp", "PreviewKeyDown", "GotFocus",
        "LostFocus", "Loaded", "Unloaded", "Closing", "Closed", "SizeChanged", "IsVisibleChanged",
        "ValueChanged", "Expanded", "Collapsed", "ScrollChanged",
    ];

    [STAThread]
    private static int Main(string[] args)
    {
        // 输出含中文：Windows 上重定向到管道/文件时按本地代码页编码会抛异常。
        // 设失败也不该让门禁崩掉，静默降级即可。
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch (Exception) { /* 重定向环境下不允许改编码 */ }

        var repo = args.Length > 0 ? Path.GetFullPath(args[0]) : FindRepoRoot();
        if (repo is null)
        {
            Console.WriteLine("找不到仓库根目录（预期存在 src/BSGroupGenerator.Wpf/Views）。");
            Console.WriteLine("请显式传入：dotnet run --project tools/layout-probe -c Release -- <仓库根目录>");
            return 2;
        }

        var viewsDir = Path.Combine(repo, "src", "BSGroupGenerator.Wpf", "Views");
        if (!Directory.Exists(viewsDir))
        {
            Console.WriteLine($"找不到窗口目录：{viewsDir}");
            return 2;
        }

        var app = new Application();
        var findings = new List<string>();
        var configs = 0;

        foreach (var lang in Langs)
        {
            foreach (var theme in Themes)
            {
                LoadResources(app, lang, theme);
                var winStyle = app.TryFindResource(typeof(Window)) as Style;

                foreach (var file in Directory.EnumerateFiles(viewsDir, "*.xaml", SearchOption.AllDirectories).OrderBy(f => f))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var text = File.ReadAllText(file, Encoding.UTF8);

                    foreach (var (w, h) in SizesFor(name, text))
                    {
                        configs++;
                        var hs = h > 0 ? h.ToString(CultureInfo.InvariantCulture) : "auto";
                        var label = $"{lang}/{theme}/{name} {w}x{hs}";
                        try
                        {
                            foreach (var hit in ProbeOne(winStyle, text, w, h))
                                findings.Add($"{label}: {hit}");
                        }
                        catch (Exception ex)
                        {
                            findings.Add($"{label}: [探针异常] {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
            }
        }

        Console.WriteLine($"共检查 {configs} 个「语言×主题×视图×尺寸」组合（视图含 Views 下递归到的窗口与页面）。");
        if (findings.Count == 0)
        {
            Console.WriteLine("结果：0 处溢出/裁切。");
            return 0;
        }

        Console.WriteLine($"结果：{findings.Count} 处溢出/裁切：");
        foreach (var f in findings)
            Console.WriteLine("  " + f);
        return 1;
    }

    /// <summary>从可执行文件位置向上找到含 src/BSGroupGenerator.Wpf/Views 的目录。</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "BSGroupGenerator.Wpf", "Views")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static void LoadResources(Application app, string lang, string theme)
    {
        var dicts = app.Resources.MergedDictionaries;
        dicts.Clear();
        dicts.Add(Load($"Themes/Palette.{theme}.xaml"));
        dicts.Add(Load("Themes/Controls.xaml"));
        dicts.Add(Load($"Strings/Lang.{lang}.xaml"));
    }

    // 资源字典要用 Application.LoadComponent 而不是 XamlReader.Load：前者能解析 clr-namespace 前缀
    // （BAML 里类型已解析），后者会去调用方程序集里找类型而失败。且 LoadComponent 只接受相对 URI。
    private static ResourceDictionary Load(string relative) =>
        (ResourceDictionary)Application.LoadComponent(
            new Uri($"/{AppAssembly};component/" + relative, UriKind.Relative));

    private static List<(double W, double H)> SizesFor(string view, string xaml)
    {
        // 主窗口是评审里出问题的那一个：从 MinWidth×MinHeight 起三种尺寸都要过。
        // 1000x720 = MainWindow 的 MinWidth/MinHeight；改这两个值时必须同步改 ShellSizes，
        // 否则「最小尺寸」这一档就失去了意义。
        if (view == "MainWindow")
            return [.. ShellSizes];

        // 页面活在壳的标签页里，可用宽度与壳一致（切标签不改变内容宽），所以照抄壳的三档；
        // 壳层竖向占用（菜单/标签头/状态栏）在 ProbeOne 里扣，那里才分得清窗口与页面。
        if (view.EndsWith("Page", StringComparison.Ordinal))
            return [.. ShellSizes];

        var w = ParseAttr(xaml, "Width");
        var h = ParseAttr(xaml, "Height");
        if (xaml.Contains("SizeToContent=\"Height\"", StringComparison.Ordinal))
            return [(w ?? 520, 0)]; // 0 = 高度按内容，ProbeOne 里再定
        return [(w ?? 600, h ?? 600)];
    }

    private static readonly (double W, double H)[] ShellSizes = [(1000, 720), (1240, 820), (1440, 900)];

    /// <summary>壳层竖向占用：标签头 + 状态栏 ≈ 64 DIP（实测量 32/30 加余量；菜单栏取消前是 82）。
    /// 壳改这两处任何一样式时必须同步改这里，否则页面量到的可用高与真实值脱节——
    /// 扣多了会漏报（真实排版比测的更挤），扣少了会误报。</summary>
    private const double PageChromeAllowance = 64;

    private static double? ParseAttr(string xaml, string attr)
    {
        var m = Regex.Match(xaml, $@"\b{attr}=""([0-9.]+)""");
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static string StripForParse(string xaml)
    {
        // ① x:Class 指向不存在的代码后台
        var s = Regex.Replace(xaml, @"\sx:Class=""[^""]*""", "");
        // ② 事件属性：值为处理函数名，解析不到
        foreach (var ev in EventAttrs)
            s = Regex.Replace(s, $@"\s{ev}=""[^""]*""", "");
        // ③ 自定义命名空间补 ;assembly=：XamlReader 只在调用方程序集里找类型，
        //    不补的话 ui:Watermark.Text 会报"无法设置未知成员"
        s = Regex.Replace(s, @"xmlns:(\w+)=""clr-namespace:([^;""]+)""",
            $@"xmlns:$1=""clr-namespace:$2;assembly={AppAssembly}""");
        return s;
    }

    private static List<string> ProbeOne(Style? winStyle, string xaml, double winW, double winH)
    {
        var parsed = System.Windows.Markup.XamlReader.Parse(StripForParse(xaml));
        Window? host = null;
        FrameworkElement root;
        double clientW, clientH;

        if (parsed is Window win)
        {
            // 窗口样式必须由 XAML 自己声明（根元素 Style="{StaticResource AppWindow}"）：隐式样式按元素
            // **确切类型**匹配，对 x:Class 生成的派生窗口不生效，漏声明就退回系统默认的白底黑字。
            // 这里只做兜底并留痕——以前无条件强挂样式，恰好把「XAML 漏声明」这类问题掩盖住了。
            var declaredStyle = win.Style is not null;
            if (winStyle is not null)
                win.Style = winStyle;
            if (winStyle is not null && !declaredStyle)
                return ["根 <Window> 未声明 Style=\"{StaticResource AppWindow}\"，字体/前景/渲染选项在真实运行时不会生效"];

            if (win.Content is not FrameworkElement content)
                return [$"根内容不是 FrameworkElement（{win.Content?.GetType().Name}）"];

            root = content;
            (clientW, clientH) = ClientSize(winW, winH, win.ResizeMode != ResizeMode.NoResize);
        }
        else
        {
            if (parsed is not FrameworkElement page)
                return [$"根元素既不是 Window 也不是 FrameworkElement（{parsed?.GetType().Name}）"];

            // 页面（Views/Pages/*.xaml）不自己开窗，套进一个真 Window 才拿得到 AppWindow 样式
            // 往下继承的字体/字号/文字色——直接量裸 UserControl 会按系统默认字体排版，宽度与真实渲染不符。
            // 窗口不 Show，只当继承宿主用；可用高还要再扣掉壳层的标签头与状态栏。
            host = new Window { Style = winStyle, Content = page };
            root = page;
            (clientW, clientH) = ClientSize(winW,
                winH > 0 ? winH - PageChromeAllowance : winH, resizable: true);
        }

        root.Width = clientW;
        if (!double.IsInfinity(clientH))
            root.Height = clientH;

        root.Measure(new Size(clientW, double.IsInfinity(clientH) ? double.PositiveInfinity : clientH));
        if (double.IsInfinity(clientH))
        {
            var desired = root.DesiredSize.Height;
            root.Height = desired;
            root.Measure(new Size(clientW, desired));
            root.Arrange(new Rect(0, 0, clientW, desired));
        }
        else
        {
            root.Arrange(new Rect(0, 0, clientW, clientH));
        }
        root.UpdateLayout();

        var hits = new List<string>();
        Walk(root, root, hits);
        GC.KeepAlive(host); // 页面靠它提供继承上下文，量完之前不能被回收
        return hits;
    }

    /// <summary>整窗尺寸 → 客户区尺寸。窗口边框宽度与标题栏高度按当前机器的系统度量算，
    /// 不可调大小的窗口（ResizeMode.NoResize）边框薄得多，不能一律按可拖拽算。</summary>
    private static (double W, double H) ClientSize(double winW, double winH, bool resizable)
    {
        var frame = resizable
            ? GetSystemMetrics(SM_CXSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER)
            : 3;
        var clientW = Math.Max(120, winW - 2 * frame);
        var clientH = winH > 0
            ? Math.Max(120, winH - 2 * frame - GetSystemMetrics(SM_CYCAPTION))
            : double.PositiveInfinity;
        return (clientW, clientH);
    }

    private static void Walk(DependencyObject element, FrameworkElement root, List<string> hits)
    {
        foreach (var child in Children(element))
        {
            if (child is FrameworkElement fe &&
                fe.Visibility == Visibility.Visible && !InsideScrollable(fe, root))
            {
                Check(fe, root, hits);
            }
            Walk(child, root, hits);
        }
    }

    private static void Check(FrameworkElement e, FrameworkElement root, List<string> hits)
    {
        var width = e.ActualWidth;
        if (width <= 0 || double.IsNaN(width))
            return;

        var where = Describe(e, root);

        // ① 自己被压窄：只针对"文字不会自己让路"的控件——可折行或有省略号的都是有意的
        var needed = NeededTextWidth(e);
        if (needed is double n && n > width + TextSlack)
            hits.Add($"[压窄] {where} 实得 {width:F1} < 文本需 {n:F1}");

        // ②③ 溢出父容器 / 溢出 Grid 单元格
        var avail = AvailableWidth(e);
        if (!double.IsNaN(avail) && width > avail + 0.5)
            hits.Add($"[溢出] {where} 实得 {width:F1} > 可用 {avail:F1}");
    }

    /// <summary>可容忍的差值：Display 模式把每个字形推进取整到设备像素，
    /// 与 FormattedText 的估算有亚像素级偏差；差不到 1.5 DIP 时肉眼与渲染都无差别。</summary>
    private const double TextSlack = 1.5;

    private static double? NeededTextWidth(FrameworkElement e)
    {
        string? text;
        bool wrap, trim;

        switch (e)
        {
            case TextBlock tb:
                text = tb.Text;
                wrap = tb.TextWrapping != TextWrapping.NoWrap;
                trim = tb.TextTrimming != TextTrimming.None;
                break;
            case ContentControl { Content: string s } cc when cc is Button or CheckBox or RadioButton or Label:
                text = s;
                wrap = false;
                // 按钮模板给 ContentPresenter/AccessText 设了 CharacterEllipsis —— 那是有意省略
                trim = HasTrimming(cc);
                break;
            default:
                return null;
        }

        if (string.IsNullOrEmpty(text) || wrap || trim)
            return null;

        // 字体从附加属性读：FrameworkElement 没有 FontSize，而 TextElement 的这几个是继承属性，
        // 元素上没本地值时返回的是从 Window 继承下来的真实值
        var size = TextElement.GetFontSize(e);
        if (size <= 0)
            size = 12.0;
        var family = TextElement.GetFontFamily(e) ?? new FontFamily("Segoe UI");
        var typeface = new Typeface(family, TextElement.GetFontStyle(e),
            TextElement.GetFontWeight(e), TextElement.GetFontStretch(e));
        // 格式化模式必须与真实控件一致：窗口隐式样式设的是 Display（按像素对齐），
        // 而 FormattedText 默认 Ideal，两者差 1-2%（实测 64.8 vs 66.1），
        // 用 Ideal 量会把所有紧贴边界的标签误报成"被压窄"。
        var mode = TextOptions.GetTextFormattingMode(e);
        var dpi = VisualTreeHelper.GetDpi(e).PixelsPerDip;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, size, Brushes.Black, null, mode, dpi);
        return ft.Width;
    }

    private static bool HasTrimming(ContentControl cc)
    {
        if (cc.Template is null)
            return true; // 模板未套用，量不出真实文本宽，保守跳过
        foreach (var presenter in FindDescendants<ContentPresenter>(cc))
            if (presenter.Resources.Contains(typeof(AccessText)) || presenter.Resources.Contains(typeof(TextBlock)))
                return true;
        return false;
    }

    /// <summary>可用宽度：必须按父容器类型分情况，否则三种失效模式查不全。</summary>
    private static double AvailableWidth(FrameworkElement child)
    {
        if (ParentOf(child) is not FrameworkElement parent)
            return double.NaN;
        var m = child.Margin;
        return parent switch
        {
            // 固定像素列不约束子元素 → 必须按列宽判（DockPanel 同理按期望宽排列）。
            // 必须累加 ColumnSpan：跨列的 TextBlock/TreeView 实得宽度就是几列之和，
            // 只取起始列宽会把它全部误报成"溢出"。
            Grid g when g.ColumnDefinitions.Count > 0 => SpanWidth(g, child) - m.Left - m.Right,
            // 横向堆叠顺序排列，不约束子元素
            StackPanel { Orientation: Orientation.Horizontal } => double.NaN,
            _ => parent.ActualWidth - m.Left - m.Right,
        };
    }

    /// <summary>跨列元素的可用宽度 = 它跨过的所有列宽之和。</summary>
    private static double SpanWidth(Grid g, FrameworkElement child)
    {
        var start = Math.Clamp(Grid.GetColumn(child), 0, g.ColumnDefinitions.Count - 1);
        var span = Math.Max(1, Grid.GetColumnSpan(child));
        var end = Math.Min(g.ColumnDefinitions.Count, start + span);
        var sum = 0.0;
        for (var i = start; i < end; i++)
            sum += g.ColumnDefinitions[i].ActualWidth;
        return sum;
    }

    private static bool InsideScrollable(FrameworkElement e, FrameworkElement root)
    {
        DependencyObject? cur = e;
        while (cur is not null)
        {
            if (cur is ScrollViewer sv && sv.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled)
                return true;
            if (ReferenceEquals(cur, root))
                return false;
            cur = ParentOf(cur);
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or Visual3D
            ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d)
            : LogicalTreeHelper.GetParent(d);

    private static string Describe(FrameworkElement e, FrameworkElement root)
    {
        var sb = new StringBuilder(e.GetType().Name);
        var label = e switch
        {
            TextBlock tb => tb.Text,
            ContentControl { Content: string s } => s,
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(label))
            sb.Append(" \"").Append(label.Length > 28 ? label[..28] + "…" : label).Append('"');
        if (e.Name.Length > 0)
            sb.Append(" #").Append(e.Name);
        sb.Append(" @").Append(PathOf(e, root));
        return sb.ToString();
    }

    private static string PathOf(FrameworkElement e, FrameworkElement root)
    {
        var parts = new List<string>();
        DependencyObject? cur = e;
        while (cur is FrameworkElement && !ReferenceEquals(cur, root))
        {
            var parent = ParentOf(cur);
            if (parent is null)
                break;
            parts.Add($"{parent.GetType().Name}[{IndexOf(parent, cur)}]");
            cur = parent;
        }
        parts.Reverse();
        return parts.Count == 0 ? "root" : string.Join(">", parts);
    }

    private static int IndexOf(DependencyObject parent, DependencyObject child)
    {
        var i = 0;
        foreach (var c in Children(parent))
        {
            if (ReferenceEquals(c, child))
                return i;
            i++;
        }
        return -1;
    }

    /// <summary>
    /// 优先走可视化树（含模板生成的部分），非 Visual 时退回逻辑树。
    /// 两个必须的过滤：
    ///   · 逻辑子元素可能是字符串（ContentControl 的文本内容），不能当 DependencyObject 用；
    ///   · Grid 的逻辑子元素包含 RowDefinition/ColumnDefinition（DefinitionBase），
    ///     对它调 VisualTreeHelper 会抛"不是 Visual 或 Visual3D"。
    /// </summary>
    private static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {
        if (parent is Visual or Visual3D)
        {
            var n = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < n; i++)
                yield return VisualTreeHelper.GetChild(parent, i);
            yield break;
        }
        foreach (var c in LogicalTreeHelper.GetChildren(parent))
            if (c is DependencyObject d and not DefinitionBase)
                yield return d;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var c in Children(root))
        {
            if (c is T t)
                yield return t;
            foreach (var d in FindDescendants<T>(c))
                yield return d;
        }
    }
}
