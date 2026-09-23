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
        // Core 不引用 UI 层，取词函数得由这里注入：日志、诊断报告、分组校验错误才会跟着界面语言走
        Core.CoreStrings.Localizer = L10n.TrF;

        // 单实例：与 WinForms 版共用同一个名字，两个副本会互相覆盖 settings.json 与分组文件
        _mutex = new Mutex(initiallyOwned: true, @"Local\SliderSorter.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Notify.Info(null, L10n.Tr("L.Title_Tip"), L10n.Tr("L.Msg_AlreadyRunning"));
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            // 处理策略：**不无条件吞异常**。全部标记 Handled 会让应用在半坏状态下继续跑
            //（树刷新失败后仍能保存、设置写了一半……），问题被藏起来，用户只会觉得"点了没反应"。
            // 因此只吞确实可恢复的那几类（IO/权限/状态/XML/格式/参数），其余的记日志 + 弹窗后
            // 保持 Handled = false，让进程按正常路径快速失败（随后触发 AppDomain.UnhandledException
            // 把致命崩溃也记进 crash.log）。
            CrashLog.ShowError(args.Exception, isFatal: false);
            args.Handled = IsRecoverable(args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.ShowError(args.ExceptionObject as Exception, isFatal: args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write(args.Exception, isFatal: false);
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    /// <summary>可恢复异常白名单：这几类都是"这一次操作失败了，但进程状态仍然一致"
    /// （IO/权限失败、对象状态不允许、XML 损坏、格式/参数错误）。NullReferenceException 这类
    /// 逻辑缺陷不在其中——那说明内存里的状态已经不可信，继续跑下去只会写坏用户的分组文件。</summary>
    private static bool IsRecoverable(Exception? ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
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
