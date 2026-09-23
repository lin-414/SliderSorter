using System.IO;
using System.Text;
using SliderSorter.Wpf.Services;
using Xunit;

namespace SliderSorter.Wpf.Tests;

/// <summary>崩溃日志的落盘位置与截断逻辑。
/// 目录走 <see cref="CrashLog.DirectoryOverride"/>，用例只在临时目录里读写，不碰用户真实日志。</summary>
[Collection(WpfStaCollection.Name)]
public class CrashLogTests
{
    [Fact]
    public void WriteGoesToOverriddenDirectory()
    {
        using var scope = IsolatedUserState.Enter();

        CrashLog.Write(new InvalidOperationException("boom-marker"), isFatal: false);

        Assert.True(File.Exists(scope.CrashLogFile));
        var text = File.ReadAllText(scope.CrashLogFile);
        Assert.Contains("boom-marker", text);
        Assert.Contains("UI异常", text);
    }

    [Fact]
    public void OversizedLogIsTruncatedAtLineBoundary()
    {
        using var scope = IsolatedUserState.Enter();
        Directory.CreateDirectory(Path.GetDirectoryName(scope.CrashLogFile)!);

        // 每行 200 个 x + 换行：截断点必须落在行首，否则保留下来的开头会是一段
        // 没有时间戳、没有堆栈头的残行，看起来像日志损坏
        var line = new string('x', 200) + "\n";
        var builder = new StringBuilder();
        while (builder.Length <= 600 * 1024)
            builder.Append(line);
        File.WriteAllText(scope.CrashLogFile, builder.ToString());

        CrashLog.Write(new Exception("after-truncate"), isFatal: true);

        var text = File.ReadAllText(scope.CrashLogFile);
        Assert.True(text.Length < builder.Length, "日志应当被截断");
        Assert.StartsWith(new string('x', 200) + "\n", text);   // 以完整行开头
        Assert.Contains("after-truncate", text);
        Assert.EndsWith("\n\n", text);
    }
}