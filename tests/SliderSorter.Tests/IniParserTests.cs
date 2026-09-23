using Xunit;
using SliderSorter.Core;

namespace SliderSorter.Tests;

/// <summary>QSettings 的 @ByteArray(...) 取值：外壳要剥掉，内部的 Qt 转义只在**确实有转义**时才解。
/// 判据是内容里出现 <c>\\</c>——普通的 <c>D:\Games\SSE</c> 里 <c>\G</c>、<c>\S</c> 只是普通字符，
/// 一律按转义处理会把这些路径改坏（这也是本用例组存在的理由）。</summary>
public class IniParserTests
{
    private static string ValueOf(string rawValue) =>
        IniParser.Parse($"[Settings]\nmod_directory={rawValue}\n")["Settings"]["mod_directory"];

    [Fact]
    public void EscapedByteArrayIsUnescaped()
    {
        // 原样（逐字符）就是 @ByteArray(D:\\Games\\SSE)，解完是 D:\Games\SSE
        Assert.Equal(@"D:\Games\SSE", ValueOf(@"@ByteArray(D:\\Games\\SSE)"));
    }

    [Fact]
    public void EscapedUncPathIsUnescaped()
    {
        // 原样就是 @ByteArray(\\\\srv\\share) → \\srv\share
        Assert.Equal(@"\\srv\share", ValueOf(@"@ByteArray(\\\\srv\\share)"));
    }

    [Fact]
    public void UnescapedByteArrayKeepsInnerTextAsIs()
    {
        // 没有 \\ 就只剥壳，内容原样：绝不能把 \G、\S 当成未知转义处理（丢反斜杠 = 路径被改坏）
        Assert.Equal(@"D:\Games\SSE", ValueOf(@"@ByteArray(D:\Games\SSE)"));
    }

    [Fact]
    public void PlainTextIsUnchanged()
    {
        Assert.Equal(@"D:\Games\SSE", ValueOf(@"D:\Games\SSE"));
        Assert.Equal("Skyrim Special Edition", ValueOf("Skyrim Special Edition"));
        // 不是 @ByteArray(...) 形式（缺右括号）时也不要动
        Assert.Equal(@"@ByteArray(D:\\x", ValueOf(@"@ByteArray(D:\\x"));
    }

    [Fact]
    public void UnknownEscapesKeepBothCharacters()
    {
        // \G 不在 Qt 的转义表里：原样保留两个字符，绝不丢反斜杠
        Assert.Equal(@"D:\Games\SSE", ValueOf(@"@ByteArray(D:\\Games\SSE)"));
        // 结尾孤立反斜杠同理
        Assert.Equal(@"D:\Games\", ValueOf(@"@ByteArray(D:\\Games\)"));
    }

    [Fact]
    public void ControlAndHexEscapesAreDecoded()
    {
        // 注意：控制符/十六进制转义只在"内容里出现过 \\"这条保守分支内才会被解——
        // 输入里都带上一个 \\，正是为了进入该分支（见类注释）。
        Assert.Equal("C:\\a\tb", ValueOf(@"@ByteArray(C:\\a\tb)"));
        Assert.Equal("C:\\a\r\nb", ValueOf(@"@ByteArray(C:\\a\r\nb)"));
        Assert.Equal("A\\B\0", ValueOf(@"@ByteArray(A\\B\0)"));
        Assert.Equal(@"C:\A", ValueOf(@"@ByteArray(C:\\\x41)"));
        // 十六进制位数不足 → 按未知转义原样保留，别吃掉字符
        Assert.Equal(@"C:\Games\x4Z", ValueOf(@"@ByteArray(C:\\Games\x4Z)"));
    }
}
