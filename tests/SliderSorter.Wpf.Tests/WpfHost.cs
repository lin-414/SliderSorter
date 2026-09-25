using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace SliderSorter.Wpf.Tests;

/// <summary>离屏宿主：把任意元素装进一个真窗口、Show 一次并泵到 <c>ContextIdle</c>。
///
/// 为什么必须 Show：模板项只有在窗口连上视觉树并完成排版后才会生成，而"绑定路径写错"只有在
/// 这一步才会暴露成空文本（不 Show 时连 ListBox 都找不到）。挪到屏幕外 + 全透明，
/// 测试期间不会有任何东西闪出来。
/// <para>
/// 整个程序集共用**一条常驻 STA 线程**：WPF 的 <c>Application.Current</c> 是整个 AppDomain 共享的，
/// 一个用例的宿主线程退出后，后来的用例拿到的是"宿主线程已经不存在"的 Application，
/// 窗口虽然能 Show，模板项与绑定却不一定补得上——表现就是同一句断言单跑通过、连跑随机空。
/// 线程常驻 + 全部经 Dispatcher 投递，才是确定的。
/// </para>
/// 反过来这条线程上也**只能**用这里开窗口：在 xUnit 自己的线程上 Show 再 Close，
/// 最后一个窗口一关会触发 WPF 默认的关机流程，把共享的 Application 一起带走，
/// 后面每个碰 Application 的用例都遭殃（<c>HelpDocumentTests</c> 就踩过，所以它不建窗口）。</summary>
public static class WpfHost
{
    /// <summary>App.xaml 里合并的那套字典，**顺序一致**。
    ///
    /// 为什么要有这个常量：探针与各测试原先各自手写一份列表，加字典时漏改一处，
    /// 表现就是「页面里的 {StaticResource X} 抛 XamlParseException」——看着像页面写错了，
    /// 实际是宿主没把资源环境搭全。2026-09-22 加 Themes/Components.xaml 时正是如此：
    /// 探针、WpfHost、以及另外三个用例文件共五处硬编码列表，一次漏了四处的顺序与内容。
    /// 新加 app 级字典时**只改这里**（以及 App.xaml 与 layout-probe/Program.cs）。
    ///
    /// 顺序不能乱：Components.xaml 的样式 BasedOn 到 Controls.xaml 上，反过来解析期就抛。</summary>
    public static readonly string[] AppDictionaries =
    [
        "Themes/Controls.xaml",
        "Themes/Components.xaml",
        "Themes/TypeScale.xaml",
    ];

    /// <summary>把 App 级字典合并进 Application（幂等）。语言字典由调用方按需追加。</summary>
    public static void AddAppDictionaries(Application app)
    {
        foreach (var relative in AppDictionaries)
            AddDictionary(app, relative);
    }

    private static readonly Thread UiThread = new(RunUi);
    private static Dispatcher? _dispatcher;

    private static void RunUi()
    {
        var app = Application.Current ?? new Application();
        // 关掉窗口不许顺带关掉 Application：默认是 OnLastWindowClose，于是"最后一个宿主窗口一关"
        // 就开始关机流程，同一集合里的下一个用例撞上的是「应用程序对象正在关闭」——
        // 能不能碰上取决于别的并行集合此刻还开着几个窗口，表现为随机红一条（单独 --filter 一个类时几乎必中）。
        // 常驻线程上的窗口全在这里显式关闭，不需要靠它来停机（线程本身是 IsBackground）。
        // 只在 Application 归本线程所有时改：它可能是别的用例在别的线程上建的（MainWindowCommandTests 那几个），
        // 那种线程跑完就退出，连它的 Dispatcher 都不能再 Invoke——直接赋值当场抛「调用线程无法访问此对象」，
        // 把这条常驻线程一起带崩，后面每个 WithWindow 都进不来。
        if (app.Dispatcher == Dispatcher.CurrentDispatcher)
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        AddAppDictionaries(app);
        AddDictionary(app, "Strings/Lang.zh.xaml");
        _dispatcher = Dispatcher.CurrentDispatcher;
        Dispatcher.Run();
    }

    private static Dispatcher Ui => _dispatcher ?? throw new InvalidOperationException("只能从 WpfHost.WithWindow 进入");

    /// <summary>在常驻 UI 线程上建窗口、排版一遍，然后把内容元素交给调用方检查，最后关掉窗口。
    /// 内容用工厂传进来而不是直接传实例：参数在调用方线程上求值，而 WPF 控件只能在 STA 线程构造
    /// （传实例会得到「调用线程必须为 STA」——构造就已经炸了，根本没走到这里）。
    /// <para>
    /// 尺寸可传：响应式布局的用例要按真实窗口宽度量（DIP，与 XAML 里的 Width/Height 同一单位）。
    /// </para></summary>
    public static T WithWindow<T>(Func<FrameworkElement> createContent, Func<FrameworkElement, T> inspect,
        double width = 1180, double height = 680)
    {
        if (_dispatcher is null)
        {
            UiThread.SetApartmentState(ApartmentState.STA);
            UiThread.IsBackground = true;
            UiThread.Start();
            SpinWait.SpinUntil(() => _dispatcher is not null, TimeSpan.FromSeconds(10));
        }
        return Ui.Invoke(() =>
        {
            var content = createContent();
            var window = new Window
            {
                Content = content,
                Width = width,
                Height = height,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowActivated = false,
                Opacity = 0,
            };
            window.Show();
            Pump();
            window.UpdateLayout();
            try
            {
                return inspect(content);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>推一次消息队列：绑定的首次挂接排在 DataBind 优先级上，模板项的生成排在更低的
    /// ItemContainerGenerator/ContextIdle 上——不泵就会拿到"列表有 3 项、里面一个容器都没有"的假象。
    /// 只在 UI 线程上调用（<see cref="WithWindow"/> 的回调里就是）。</summary>
    public static void Pump() => Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.ContextIdle);

    /// <summary>用 pack URI 而不是文件路径：文件路径加载时 XAML 里的 <c>clr-namespace</c> 前缀
    /// 会拿调用方程序集（测试程序集）去解析，Controls.xaml 里的 <c>Setter Property="ui:Watermark.Text"</c>
    /// 当场解析失败。
    /// 公开是因为几个自己开 STA 线程的用例（MainWindowCommandTests / NewModsFilterTests 等）
    /// 也要往同一个 Application 上合并字典——它们必须与 <see cref="AppDictionaries"/> 保持一致。</summary>
    public static void AddDictionary(Application app, string relative)
    {
        var uri = new Uri($"pack://application:,,,/SliderSorter;component/{relative}");
        if (app.Resources.MergedDictionaries.Any(d => d.Source == uri))
            return;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
    }
}
