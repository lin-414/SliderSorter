using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using SliderSorter.Wpf.ViewModels;
using SliderSorter.Wpf.Views;
using SliderSorter.Wpf.Views.Pages;
using Xunit;
using Xunit.Abstractions;

namespace SliderSorter.Wpf.Tests;

/// <summary>STA 测试集合名。WPF 的 <c>Application</c> 是**进程级单例**，第二个实例的构造会抛
/// InvalidOperationException；两个 STA 用例并行时会撞车（都先判 null 再 new），所以凡是要
/// 构造真实窗口的测试类都挂到同一个集合上，彼此串行。</summary>
public static class WpfStaCollection
{
    public const string Name = "WpfSta";
}

/// <summary>真的把 MainWindow 造出来，再用**绑定表达式**核对命令：绑定路径写错时
/// <c>Button.Command</c> 是 null（按钮照样可点、可聚焦，点了什么都不发生）。
///
/// XamlCommandBindingTests 用反射对照类型，不需要 UI 线程；本用例补上「运行时真的解析出来了」
/// 这一层——反射对得上、但运行时解析失败的情况（DataContext 没设、绑到别的元素）只有这里能发现。
///
/// 窗口构造时会读用户设置（实例/Profile/写盘模式）并据此触发扫描，所以本用例先把设置目录与
/// MO2 全局实例根重定向到临时目录（见 <see cref="IsolatedUserState"/>）：既不再改写用户真实的
/// settings.json，结果也不再取决于跑测试那台机器装了什么 MO2/BodySlide。
///
/// 求值靠 <see cref="PumpOnce"/> 推一次消息队列，并带超时兜底；详见该方法的说明。</summary>
[Collection(WpfStaCollection.Name)]
public class MainWindowCommandTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryCommandBindingInMainWindowResolvesToACommand()
    {
        // 用户状态隔离：设置文件与 MO2 全局实例根都指向临时目录，
        // 本用例不再读写真实 %APPDATA%\SliderSorter\settings.json，也不枚举真实 MO2 安装。
        using var scope = IsolatedUserState.Enter();

        Exception? captured = null;
        var evidence = new List<string>();
        var deadBindings = new List<string>();

        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                if (app.Resources.MergedDictionaries.Count == 0)
                {
                    // MainWindow.xaml 用了 {StaticResource AppWindow}/{StaticResource RowLabel} 等，
                    // 解析时必须在 Application 资源里找到（pack URI 必须带程序集名）。
                    // 走 WpfHost 的共享清单：漏一份的表现是"页面里的 StaticResource 抛异常"，
                    // 看着像页面写错、实际是宿主没搭全资源环境。
                    WpfHost.AddAppDictionaries(app);
                }

                var window = new MainWindow();
                var vm = Assert.IsType<MainViewModel>(window.DataContext);
                PumpOnce(window, evidence);

                // 设置确实落在隔离目录里（没写进真实 %APPDATA%）：窗口构造期回写的设置
                // 与这条标记必须出现在同一个文件里
                var marker = scope.MarkSaved();
                evidence.Add($"[设置隔离] 设置目录={scope.Root} 文件存在={File.Exists(scope.SettingsFile)}"
                             + $" 含本次标记={File.Exists(scope.SettingsFile) && File.ReadAllText(scope.SettingsFile).Contains(marker)}"
                             + $" 真实 APPDATA={Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}");
                Assert.True(File.Exists(scope.SettingsFile),
                    "隔离目录下没有生成 settings.json —— 设置被写到真实 %APPDATA% 去了");
                Assert.Contains(marker, File.ReadAllText(scope.SettingsFile));

                // ① 底部「保存分组文件」——x:Name 生成的字段是 internal，跨程序集只能反射取。
                // 期望值也走反射取（而不是 vm.SaveCommand）：属性被改名时本用例要**失败**并给出可读原因，
                // 而不是让测试项目编译不过——编译错误指不到「绑定路径写错」这个真正的问题上。
                var saveButton = NamedButton(window, "SaveButton");
                var saveCommand = Resolve(vm, "SaveCommand");
                Assert.NotNull(saveCommand);
                evidence.Add($"[环境] ApplicationData={Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}"
                             + $" 窗口 DataContext={window.DataContext?.GetType().Name ?? "null"}"
                             + $" 内容 DataContext={(window.Content as FrameworkElement)?.DataContext?.GetType().Name ?? "null"}"
                             + $" 按钮 DataContext={saveButton.DataContext?.GetType().Name ?? "null"}"
                             + $" 按钮 Content={saveButton.Content as string ?? "null"}"
                             + $" 绑定状态={BindingOperations.GetBindingExpression(saveButton, ButtonBase.CommandProperty)?.Status}");
                evidence.Add($"[保存按钮] IsEnabled={saveButton.IsEnabled}"
                             + $" Command={Describe(saveButton.Command)}"
                             + $" 与 vm.SaveCommand 同一实例={ReferenceEquals(saveButton.Command, saveCommand)}");
                Assert.NotNull(saveButton.Command);
                Assert.Same(saveCommand, saveButton.Command);

                // ② Ctrl+S：和菜单项、按钮共用同一个命令，顺手一起钉住
                var ctrlS = window.InputBindings.OfType<KeyBinding>()
                    .Single(k => k.Key == Key.S && k.Modifiers == ModifierKeys.Control);
                evidence.Add($"[Ctrl+S] Command={Describe(ctrlS.Command)}");
                Assert.Same(saveCommand, ctrlS.Command);

                // ③ 「探测」按钮：先按绑定路径定位（路径本身就是断言的一部分），再断言真的解析出了命令
                var sinks = CommandSinks(window).ToList();
                var detect = sinks.FirstOrDefault(s => CommandPath(s) == "DetectBodySlideCommand");
                Assert.True(detect is not null,
                    "窗口里没有绑定到 DetectBodySlideCommand 的控件；实际存在的命令绑定："
                    + string.Join("、", sinks.Select(CommandPath)));
                // 期望值同样走反射取，理由见 ①
                var detectCommand = Resolve(vm, "DetectBodySlideCommand");
                evidence.Add($"[探测按钮] IsEnabled={detect!.Element.IsEnabled}"
                             + $" 控件侧 Command={Describe(detect.Source.Command)}"
                             + $" 反射侧={Describe(detectCommand)}");
                Assert.NotNull(detectCommand);
                Assert.NotNull(detect.Source.Command);
                Assert.Same(detectCommand, detect.Source.Command);

                // ④ 整窗所有 Command 绑定（菜单项、树/组的批量按钮……）：控件真的拿到命令了才算数。
                // 同时用反射独立再解一遍路径，两条路必须指向同一个命令实例。
                foreach (var sink in sinks)
                {
                    var path = CommandPath(sink);
                    var delivered = sink.Source.Command;
                    var reflected = Resolve(vm, path);
                    evidence.Add($"[全窗] {sink.Element.GetType().Name} {{{path}}} → 控件侧 {Describe(delivered)}"
                                 + $"，反射侧 {(reflected is null ? "null" : reflected.GetType().Name)}"
                                 + $"，同一实例={ReferenceEquals(delivered, reflected)}");
                    if (delivered is null || reflected is null || !ReferenceEquals(delivered, reflected))
                        deadBindings.Add($"{sink.Element.GetType().Name}{{{path}}}");
                }

                Assert.True(deadBindings.Count == 0,
                    "以下命令绑定没有解析出任何命令（按钮会静默失效）：" + string.Join("、", deadBindings));

                // 顺带钉住进度遮罩的语义属性确实在 VM 上（绑定路径由 XamlCommandBindingTests 逐条对照）
                Assert.NotNull(typeof(MainViewModel).GetProperty("IsDetecting"));
                Assert.NotNull(typeof(MainViewModel).GetProperty("IsBusy"));
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        foreach (var line in evidence)
            output.WriteLine(line);

        Assert.Null(captured);
        // 证据本身也要能被检出：光靠 NotNull 断言，绑定表达式解析失败时会看不出来是哪一步退化了
        Assert.Contains(evidence, e => e.StartsWith("[保存按钮]") && e.Contains("Command=AsyncRelayCommand"));
        Assert.Contains(evidence, e => e.StartsWith("[探测按钮]") && e.Contains("Command=AsyncRelayCommand"));
    }

    private static string Describe(object? command) => command is null ? "null（绑定失效）" : command.GetType().Name;

    /// <summary>把窗口的绑定「泵」一次，让它们真的完成求值——**这一步是必需的，不是取巧**。
    ///
    /// WPF 把绑定的首次挂接排在 Dispatcher 的 <c>DataBind</c> 优先级上。窗口没 <c>Show()</c> 过就没有消息循环，
    /// 于是绑定一直停在 <c>BindingStatus.Unattached</c>、<c>Button.Command</c>/<c>Button.Content</c> 恒为 null
    /// （实测 DataContext 已正确继承到按钮上，只是没人推动求值）。不泵就断言 null，等于断言了个假象。
    ///
    /// 为什么不用 <c>UpdateTarget()</c>：它不会让未挂接的绑定挂接起来，实测仍为 null。
    /// 为什么不 <c>Show()</c>：那会触发 <c>Loaded</c> → <c>CheckForUpdatesAsync</c>（联网 + 可能弹更新确认框）。
    ///
    /// 泵队列会连带执行优先级更高的残留回调——构造窗口时 VM 已经启动了一次扫描，若它判定「有新模组」，
    /// 就会经 Dispatcher 回调弹模态框。所以这里挂一个 Send 优先级的兜底定时器：3 秒内没跑完就把
    /// 除主窗口外的窗口统统关掉并结束泵送（定时器在模态框自己的嵌套消息循环里照样会触发，
    /// 因此不会永久死等）。超时说明真的弹了框，用例会带着这条证据失败，而不是挂住整个测试进程。</summary>
    private static void PumpOnce(Window window, List<string> evidence)
    {
        var dispatcher = window.Dispatcher;
        var frame = new DispatcherFrame();
        var timedOut = true;

        var guard = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Send, (_, _) =>
        {
            foreach (Window stray in Application.Current.Windows)
                if (!ReferenceEquals(stray, window))
                    stray.Close();
            frame.Continue = false;
        }, dispatcher);

        var start = Environment.TickCount64;
        guard.Start();
        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
            {
                timedOut = false;
                frame.Continue = false;
            }));
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            guard.Stop();
        }

        var elapsed = Environment.TickCount64 - start;
        evidence.Add($"[泵送] 用时 {elapsed} ms，超时={timedOut}"
                     + (timedOut ? "（队列里有东西在阻塞——多半是扫描任务弹了模态框）" : ""));
        Assert.False(timedOut, "推送消息队列时超时：构造期启动的扫描任务很可能弹出了模态框（见上一条证据）");
    }

    /// <summary>x:Name 生成的字段是 internal，跨程序集只能反射取；取不到说明 XAML 里的 x:Name 被改了。
    ///
    /// 保存按钮住在「分组生成」页上（壳只剩菜单、标签条、状态栏），所以先顺逻辑树找到那一页再反射。</summary>
    private static Button NamedButton(MainWindow window, string name)
    {
        var page = Descendants(window).OfType<GroupGenerationPage>().FirstOrDefault();
        Assert.True(page is not null, "壳的标签页里找不到 GroupGenerationPage——保存按钮失去覆盖");
        var field = page!.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.True(field is not null, $"GroupGenerationPage.xaml 里找不到 x:Name=\"{name}\" 的控件（改名会让本用例失去覆盖）");
        return Assert.IsType<Button>(field!.GetValue(page));
    }

    /// <summary>窗口逻辑树里所有命令绑定的落点（按钮、菜单项）。控制模板不会展开——主窗口这些按钮
    /// 都直接写在 XAML 里，构造完逻辑树里就有；模板内部的绑定不会混进来误报。</summary>
    private static IEnumerable<CommandSink> CommandSinks(DependencyObject root)
    {
        foreach (var element in Descendants(root))
        {
            var commandProperty = CommandPropertyOf(element);
            if (commandProperty is null)
                continue;
            if (BindingOperations.GetBindingExpression(element, commandProperty) is not { } expression)
                continue;

            var path = expression.ParentBinding?.Path?.Path;
            if (string.IsNullOrEmpty(path))
                continue;

            yield return new CommandSink((FrameworkElement)element, (ICommandSource)element, path);
        }
    }

    private static string CommandPath(CommandSink sink) =>
        BindingOperations.GetBindingExpression(sink.Element, CommandPropertyOf(sink.Element)!)
            ?.ParentBinding?.Path?.Path ?? "";

    private static DependencyProperty? CommandPropertyOf(DependencyObject element) => element switch
    {
        ButtonBase => ButtonBase.CommandProperty,
        MenuItem => MenuItem.CommandProperty,
        _ => null,
    };

    /// <summary>命令绑定的落点。ButtonBase/MenuItem 都既是 FrameworkElement（IsEnabled 在这里）
    /// 又实现 ICommandSource（Command 在这里），ICommandSource 本身没有 IsEnabled，所以要分开留。</summary>
    private sealed record CommandSink(FrameworkElement Element, ICommandSource Source, string Path);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        if (root is not FrameworkElement and not FrameworkContentElement)
            yield break;
        foreach (var child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependencyObject)
                foreach (var descendant in Descendants(dependencyObject))
                    yield return descendant;
    }

    /// <summary>在 DataContext 上按路径取属性，取不到返回 null（与 WPF 绑定自身的失败方式一致）。</summary>
    private static object? Resolve(object target, string path)
    {
        object? current = target;
        foreach (var segment in path.Split('.'))
        {
            if (current is null || segment.Length == 0 || segment.Contains('[') || segment.Contains('('))
                return null;
            var property = current.GetType().GetProperty(segment,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (property is null)
                return null;
            current = property.GetValue(current);
        }
        return current;
    }
}
