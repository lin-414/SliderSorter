using Xunit;
using SliderSorter.Core;

namespace SliderSorter.Tests;

/// <summary>
/// Core 层的取词契约。Core 不依赖 UI，只能经 <see cref="CoreStrings.Localizer"/> 拿文案；
/// 这条边界是"en/ru/fr 界面下日志与诊断报告不再冒中文"的全部依据，所以要钉住。
/// </summary>
public class CoreStringsTests
{
    /// <summary>没有取词器时必须返回键名：日志里出现 L.Core_ScanParseFail 一眼能查，
    /// 返回空串或 null 会让"扫描出错了"变成一条空白行。</summary>
    [Fact]
    public void WithoutLocalizerReturnsKeySoProblemsStayVisible()
    {
        var saved = CoreStrings.Localizer;
        try
        {
            CoreStrings.Localizer = null;
            Assert.Equal("L.Core_GroupNothingToUndo", CoreStrings.Get("L.Core_GroupNothingToUndo"));
            Assert.Equal("L.Core_ScanParseFail",
                CoreStrings.Format("L.Core_ScanParseFail", "x.xml", "boom"));
        }
        finally
        {
            CoreStrings.Localizer = saved;
        }
    }

    /// <summary>有取词器时键与实参原样交给它，并且实参顺序就是语言文件里 {0}/{1} 的顺序。
    /// 顺序错了不会抛异常，只会静默把文件名和异常消息对调——最难发现的一类 bug。</summary>
    [Fact]
    public void LocalizerReceivesKeyAndArgumentsInOrder()
    {
        var saved = CoreStrings.Localizer;
        try
        {
            var seen = new List<(string Key, object?[] Args)>();
            CoreStrings.Localizer = (key, args) =>
            {
                seen.Add((key, args));
                return "无法解析 " + string.Join(" / ", args.Select(a => a?.ToString()));
            };

            var text = CoreStrings.Format("L.Core_ScanParseFail", "broken.xml", "unexpected end");

            Assert.Equal("无法解析 broken.xml / unexpected end", text);
            var call = Assert.Single(seen);
            Assert.Equal("L.Core_ScanParseFail", call.Key);
            Assert.Equal(["broken.xml", "unexpected end"], call.Args);
        }
        finally
        {
            CoreStrings.Localizer = saved;
        }
    }

    /// <summary>Get 不带实参：取词器必须收到空数组而不是 null，否则 L10n.TrF 侧的
    /// string.Format(..., args ?? []) 兜底就成了唯一防线。</summary>
    [Fact]
    public void GetPassesEmptyArgumentArray()
    {
        var saved = CoreStrings.Localizer;
        try
        {
            object?[]? captured = null;
            CoreStrings.Localizer = (_, args) =>
            {
                captured = args;
                return "ok";
            };

            Assert.Equal("ok", CoreStrings.Get("L.Core_InstancePortable"));
            Assert.NotNull(captured);
            Assert.Empty(captured!);
        }
        finally
        {
            CoreStrings.Localizer = saved;
        }
    }
}
