using System.IO;

namespace SliderSorter.Wpf.Services;

/// <summary>崩溃日志：与 WinForms 版相同的 %APPDATA%\SliderSorter\crash.log 与格式。</summary>
public static class CrashLog
{
    /// <summary>日志目录覆盖（null = 真实 <c>%APPDATA%\SliderSorter</c>）。
    /// 单测用：用例若主动触发崩溃日志（如截断逻辑的回归），不能写进用户的真实日志目录。</summary>
    public static string? DirectoryOverride { get; set; }

    private static string Dir => DirectoryOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SliderSorter");
    private static string Path2 => System.IO.Path.Combine(Dir, "crash.log");

    // Write 有两个并发入口：UI 线程的各类调用，与 GC 终结器线程上的 UnobservedTaskException。
    // 无锁的话"读全文→截断→写回"与另一条的追加交错，轻则丢一条记录，重则写出半条——
    // 崩溃日志是排查时唯一的凭据，一把静态锁的成本几乎为零。
    private static readonly object WriteGate = new();

    public static void Write(Exception? ex, bool isFatal)
    {
        try
        {
            lock (WriteGate)
            {
                Directory.CreateDirectory(Dir);
                if (File.Exists(Path2) && new FileInfo(Path2).Length > 512 * 1024)
                {
                    // 超过 512KB 保留后半。必须按**行边界**截：从字符中点硬切会把一条异常记录劈成两半，
                    // 开头那半行既读不出时间也没有堆栈头，看着像日志损坏。找不到换行（单行超长）才退化成按字符切，
                    // 且不能落在代理对中间——切出孤立代理项会让后续读取直接抛编码异常。
                    var text = File.ReadAllText(Path2);
                    var cut = text.Length / 2;
                    var newline = text.IndexOf('\n', cut);
                    cut = newline >= 0 ? newline + 1 : cut;
                    if (cut > 0 && cut < text.Length && char.IsHighSurrogate(text[cut - 1]) && char.IsLowSurrogate(text[cut]))
                        cut--; // 回退一位，把这对代理项完整留下
                    File.WriteAllText(Path2, text[cut..]);
                }
                File.AppendAllText(Path2,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {(isFatal ? "致命" : "UI")}异常：\n{ex}\n\n");
            }
        }
        catch
        {
            // 日志失败不影响提示
        }
    }

    public static void ShowError(Exception? ex, bool isFatal)
    {
        Write(ex, isFatal);
        // 文案走 L10n：崩溃处理器可能在 Application 已拆解后才触发，
        // Tr/TrF 在无 Application 上下文时回落到键名，不会二次抛异常。
        // 异常消息直接作为 TrF 的实参传入即可，不要转义花括号——string.Format 只解析
        // 格式串（即资源值），实参里的花括号是字面量，转义反而会在弹框里显示成 {{ }}。
        // 真正的风险在资源值本身含不配对的括号，已由 wpf-l10n-verify 的格式串校验拦截。
        var message = L10n.TrF(isFatal ? "L.Crash_Fatal" : "L.Crash_NonFatal", ex?.Message ?? "");
        var title = L10n.Tr(isFatal ? "L.Title_Error" : "L.Title_Tip");
        try
        {
            // 与其它提示同一套外观（系统 MessageBox 在暗色主题下会弹白框）
            Views.Notify.Show(null, title, message, Views.NotifyKind.Error);
        }
        catch
        {
            // 崩溃处理期间 WPF 可能已不可用，退回系统弹框兜底——这里绝不能再抛异常
            System.Windows.MessageBox.Show(message, title,
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}
