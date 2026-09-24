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


    /// <summary>@ByteArray 里装的是**字节**：Qt 把非 ASCII 写成 \xHH，一个中文字是三个字节（它的
    /// UTF-8）。逐字节 <c>(char)b</c> 等于按 Latin-1 解码，路径当场读成乱码 —— 于是
    /// <c>Directory.Exists</c> 全失败、模组一个也扫不到，而诊断里显示的还是那个错路径。</summary>
    [Fact]
    public void HexEscapesAreUtf8BytesNotLatin1()
    {
        Assert.Equal(@"E:\游戏\Skyrim", ValueOf(@"@ByteArray(E:\\\xe6\xb8\xb8\xe6\x88\x8f\\Skyrim)"));
        // 俄语目录同样：\xd0\xa9 = «Щ»
        Assert.Equal(@"D:\Щ\games", ValueOf(@"@ByteArray(D:\\\xd0\xa9\\games)"));
        // 纯 ASCII（真实 MO2 的 gamePath 基本如此）行为一字不变
        Assert.Equal(@"E:\Skyrim AE\Skyrim Special Edition",
            ValueOf(@"@ByteArray(E:\\Skyrim AE\\Skyrim Special Edition)"));
    }

    /// <summary>不是合法 UTF-8 的字节串（老设置里可能按本地 ANSI 代码页存的）退回逐字节直投：
    /// 与修复前一致，至少不把字符吃掉。</summary>
    [Fact]
    public void NonUtf8ByteRunsFallBackToBytePerChar() =>
        Assert.Equal(@"A:\éé", ValueOf(@"@ByteArray(A:\\\xe9\xe9)"));
}
