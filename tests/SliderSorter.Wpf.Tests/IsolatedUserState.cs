using System.IO;
using SliderSorter.Core;
using SliderSorter.Wpf.Services;

namespace SliderSorter.Wpf.Tests;

/// <summary>
/// 把"用户状态"重定向到临时目录：设置文件目录（AppSettings.DirectoryOverride）、
/// MO2 全局实例根（Mo2Discovery.GlobalRootOverride）与崩溃日志目录（CrashLog.DirectoryOverride），
/// 用例结束时还原并删除临时目录。
///
/// 为什么必须这么做：MainViewModel 一构造就读设置、并按设置里记的实例目录去枚举**用户真实的**
/// MO2 安装并触发扫描；不重定向的话，跑一次单测就会把用户真实的
/// <c>%APPDATA%\SliderSorter\settings.json</c>（LastInstanceDir / LastProfile / WriteMode …）
/// 改写成测试现场的取值，结果取决于那台机器装了什么。
/// </summary>
public sealed class IsolatedUserState : IDisposable
{
    private readonly string? _previousSettingsDir;
    private readonly string? _previousGlobalRoot;
    private readonly string? _previousCrashLogDir;

    /// <summary>本次用例独享的临时根目录。</summary>
    public string Root { get; }

    /// <summary>隔离状态下崩溃日志的落盘位置（定向到临时目录，不写用户真实日志）。</summary>
    public string CrashLogFile => Path.Combine(Root, "logs", "crash.log");

    private IsolatedUserState(string root)
    {
        Root = root;
        _previousSettingsDir = AppSettings.DirectoryOverride;
        _previousGlobalRoot = Mo2Discovery.GlobalRootOverride;
        _previousCrashLogDir = CrashLog.DirectoryOverride;
    }

    /// <summary>进入隔离状态。用 <c>using var scope = IsolatedUserState.Enter();</c> 形状使用。</summary>
    public static IsolatedUserState Enter()
    {
        var root = Path.Combine(Path.GetTempPath(), "bsgg-wpf-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(root);

        var scope = new IsolatedUserState(root);
        AppSettings.DirectoryOverride = root;
        // 指向空目录：全局实例扫描什么都找不到，测试不再依赖机器上装了什么
        Mo2Discovery.GlobalRootOverride = Directory.CreateDirectory(Path.Combine(root, "ModOrganizer")).FullName;
        // 崩溃日志同样重定向：有回归用例会主动调用 Write，不能写进用户的真实日志目录
        CrashLog.DirectoryOverride = Path.Combine(root, "logs");
        return scope;
    }

    /// <summary>隔离状态下设置文件应该落在这里（断言"没碰真实 %APPDATA%"用）。</summary>
    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>往共享设置实例里写一条可识别的标记并落盘，返回该标记
    /// （ViewModel 用的就是这个共享实例，因此它的 Save() 也会落到隔离目录）。</summary>
    public string MarkSaved()
    {
        var marker = $"bsgg-test-{Guid.NewGuid():N}";
        var settings = AppSettings.Shared;
        settings.SuppressedPrompts.Add(marker);
        settings.Save();
        return marker;
    }

    public void Dispose()
    {
        AppSettings.DirectoryOverride = _previousSettingsDir;
        Mo2Discovery.GlobalRootOverride = _previousGlobalRoot;
        CrashLog.DirectoryOverride = _previousCrashLogDir;
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响用例结论
        }
    }
}
