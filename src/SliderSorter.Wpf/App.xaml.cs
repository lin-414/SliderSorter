using System.IO;
using System.Threading;
using System.Xml;
using SliderSorter.Wpf.Services;
using SliderSorter.Wpf.Views;
using System.Windows;

namespace SliderSorter.Wpf;

public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 按设置加载色板与语言。既须在 StartupUri 窗口创建前完成，也须早于下面的单实例提示
        //（提示文案走 L10n，语言尚未装载时会回落到键名）
        // 设置里的语言可能是 "system"（跟随系统）：L10n.Apply 在这里一次性解析成具体语言，
        // 之后全程序只认 L10n.Current，不必再各自判断一次。
        // 登记为进程共享实例：ViewModel 直接取用，不必再各自 Load 一遍（见 AppSettings.Shared）
        var settings = Core.AppSettings.Use(Core.AppSettings.Load());
        ThemeManager.Apply(settings.UiTheme);
        L10n.Apply(settings.UiLanguage);
        // 字号档同样要在首窗口创建前生效（改写的是 app 级资源键，晚了首屏就按 100% 画一遍）
        FontScaleManager.Apply(settings.UiFontScalePercent);
        // Core 不引用 UI 层，取词函数得由这里注入：日志、诊断报告、分组校验错误才会跟着界面语言走
        Core.CoreStrings.Localizer = L10n.TrF;

        // 崩溃处理器必须挂在**所有**可能抛异常的启动代码之前：设置装载、主题/语言应用、
        // 下面的单实例互斥体（权限不足时 UnauthorizedAccessException）都可能在启动早期抛——
        // 处理器若还没挂上，进程裸崩且不落 crash.log，
        // "程序打不开"恰恰发生在用户最需要诊断信息的时候。
        DispatcherUnhandledException += (_, args) =>
        {
            // 处理策略：**不无条件吞异常**。全部标记 Handled 会让应用在半坏状态下继续跑
            //（树刷新失败后仍能保存、设置写了一半……），问题被藏起来，用户只会觉得"点了没反应"。
            // 因此只吞确实可恢复的那几类（IO/权限/XML/格式/参数）：记日志、弹一个"非致命"框后继续跑。
            // 其余的保持 Handled = false，让进程按正常路径快速失败——终止路径会触发下面的
            // AppDomain.UnhandledException 记日志并弹"致命"框。这里若也弹，用户要连点两个框，
            // 第二个还在拆卸过程中，可能展示不完整。
            args.Handled = IsRecoverable(args.Exception);
            if (args.Handled)
                CrashLog.ShowError(args.Exception, isFatal: false);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.ShowError(args.ExceptionObject as Exception, isFatal: args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write(args.Exception, isFatal: false);
            args.SetObserved();
        };

        // 单实例：与 WinForms 版共用同一个名字，两个副本会互相覆盖 settings.json 与分组文件。
        // 构造不需要任何等待/重试：打开已存在的命名互斥体时既不等待也不取得所有权，
        // createdNew=false 就是"已有实例在跑"；AbandonedMutexException 只在 WaitOne 真正等到
        // 被弃互斥体时才抛，这条链路没有等待，不会遇到。上一实例崩溃/被强杀时 OS 随进程
        // 释放句柄、互斥体对象随之销毁，下一次启动自然 createdNew=true，不会留下残留。
        _mutex = new Mutex(initiallyOwned: true, @"Local\SliderSorter.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Notify.Info(null, L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_AlreadyRunning"));
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
    }

    /// <summary>可恢复异常白名单：这几类都是"这一次操作失败了，但进程状态仍然一致"
    /// （IO/权限失败、XML 损坏、格式/参数错误）。
    /// <para>
    /// 注意 <see cref="InvalidOperationException"/> **不在其中**。WPF 的两类经典编程错误——
    /// 跨线程访问 DispatcherObject、跨线程改已挂接 CollectionView 的 ObservableCollection——
    /// 抛的都是它，而那两类意味着内存里的 UI/集合状态已经不可信，继续跑只会写坏用户的分组文件；
    /// 把它当"可恢复"吞掉，表现是"弹一次框后带病继续跑"，正是这份白名单最想避免的事。
    /// NullReferenceException 这类逻辑缺陷同理不在其中。</para></summary>
    private static bool IsRecoverable(Exception? ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or XmlException
            or FormatException
            or ArgumentException;

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { // 非持有线程退出时忽略
        }
        base.OnExit(e);
    }
}
