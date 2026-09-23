using Xunit;
using System.Text;
using SliderSorter.Core;

namespace SliderSorter.Tests;

public class SliderGroupFileTests
{
    [Fact]
    public void SaveProducesBomAndRoundtrips()
    {
        using var temp = new TempDir();
        var path = System.IO.Path.Combine(temp.Path, "groups.xml");

        var groups = new List<SliderGroup>
        {
            new("armor"),
            new("clothing"),
        };
        groups[0].Members.Add("CBBE");
        groups[0].Members.Add("Some Outfit v2");
        groups[1].Members.Add("Dress");

        SliderGroupFile.Save(path, groups);

        var bytes = System.IO.File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);

        var text = System.IO.File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("<SliderGroups>", text);
        Assert.Contains("<?xml version=\"1.0\" encoding=\"utf-8\"?>", text);

        Assert.True(SliderGroupFile.TryLoad(path, out var loaded, out var error));
        Assert.Empty(error);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("armor", loaded[0].Name);
        Assert.Equal(new[] { "CBBE", "Some Outfit v2" }, loaded[0].Members);
    }

    [InlineData("CON", "_CON.xml")]
    [InlineData("NUL", "_NUL.xml")]
    [InlineData("CON.x", "_CON.x.xml")]
    [Theory]
    [InlineData("UBE", "UBE.xml")]
    [InlineData("服装/护甲", "服装_护甲.xml")]
    [InlineData("  UBE  ", "UBE.xml")]
    [InlineData("a:b<c>d", "a_b_c_d.xml")]
    public void FileNameForGroupSanitizes(string name, string expected)
        => Assert.Equal(expected, SliderGroupFile.FileNameForGroup(name));

    [Fact]
    public void LoadRejectsNonGroupXml()
    {
        using var temp = new TempDir();
        var path = temp.File("other.xml", "<?xml version=\"1.0\"?><SomethingElse/>");

        Assert.False(SliderGroupFile.TryLoad(path, out _, out var error));
        Assert.Contains("SliderGroups", error);
    }

    [Fact]
    public void MergeIsCaseInsensitiveOnGroupNamesAndExactOnMembers()
    {
        var target = new List<SliderGroup> { new("Armor") };
        target[0].Members.Add("CBBE");

        var incoming = new List<SliderGroup> { new("ARMOR"), new("New Group") };
        incoming[0].Members.Add("CBBE");       // 重复，忽略
        incoming[0].Members.Add("OtherOutfit"); // 新增
        incoming[1].Members.Add("Dress");

        SliderGroupFile.Merge(target, incoming, out var addedGroups, out var addedMembers);

        Assert.Equal(1, addedGroups);
        Assert.Equal(2, addedMembers);
        Assert.Equal(2, target.Count);
        Assert.Equal("Armor", target[0].Name); // 保留先出现的写法
        Assert.Equal(new[] { "CBBE", "OtherOutfit" }, target[0].Members);
    }

    [Theory]
    [InlineData("armor.xml", true)]
    [InlineData("服装_护甲.xml", true)]
    [InlineData("UBE v2.xml", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("sub\\armor.xml", false)]
    [InlineData("../armor.xml", false)]
    [InlineData("C:armor.xml", false)]
    public void IsBareFileNameAcceptsOnlyDirectChildren(string entry, bool expected)
        => Assert.Equal(expected, SliderGroupFile.IsBareFileName(entry));

    /// <summary>
    /// 清单是输出目录里的纯文本文件，用户或其它程序都能改写它；保存时对每个陈旧条目执行
    /// <c>File.Delete(Path.Combine(dir, entry))</c>，而 IsBareFileName 是这一步唯一的守卫。
    /// 本测试锁住威胁模型：下面每一条都会让 Path.Combine 落到输出目录之外，守卫必须全部拒绝。
    /// 若哪天有人放宽了谓词，这里会先红。
    /// </summary>
    [Theory]
    [InlineData("..\\..\\secret.txt")]         // ..\ 越出输出目录
    [InlineData("sub\\armor.xml")]             // 子目录
    [InlineData("C:\\Windows\\notepad.exe")]   // 绝对路径：Path.Combine 直接丢弃 dir
    [InlineData("C:armor.xml")]                // 驱动器相对路径：按 C 盘当前目录解析
    [InlineData("\\\\server\\share\\x.xml")]   // UNC 路径
    public void IsBareFileNameRejectsPathsThatEscapeTheOutputDir(string entry)
    {
        Assert.False(SliderGroupFile.IsBareFileName(entry));

        // 反证：这些条目确实不是 dir 的直接子项（上面那条断言不是凭空写的）。
        // 只在 Windows 上验证——本工具是 WPF 应用，Path.Combine 的分隔符语义以 Windows 为准。
        if (System.IO.Path.DirectorySeparatorChar != '\\')
            return;
        var dir = @"D:\out";
        Assert.NotEqual(dir, System.IO.Path.GetDirectoryName(System.IO.Path.Combine(dir, entry)));
    }

    /// <summary>
    /// 反向保证：本工具自己写出的条目必须全部通过删除守卫，否则正常清理会被静默跳过、
    /// 旧分组文件永久残留（BodySlide 会读到重复分组）。
    /// </summary>
    [Theory]
    [InlineData("UBE")]
    [InlineData("服装/护甲")]
    [InlineData("a:b<c>d")]
    [InlineData("CON")]
    [InlineData("...")]
    [InlineData("   ")]
    public void FileNameForGroupAlwaysProducesBareFileName(string groupName)
        => Assert.True(SliderGroupFile.IsBareFileName(SliderGroupFile.FileNameForGroup(groupName)));

    /// <summary>写盘失败时不能把半成品 .tmp 留在输出目录里（用户既不知它是什么、也不敢删）。
    /// 触发方式：目标文件只读 → File.Replace 失败。</summary>
    [Fact]
    public void FailedSaveRemovesTempFile()
    {
        using var temp = new TempDir();
        var path = System.IO.Path.Combine(temp.Path, "groups.xml");
        System.IO.File.WriteAllText(path, "<SliderGroups />");

        var original = System.IO.File.GetAttributes(path);
        System.IO.File.SetAttributes(path, original | System.IO.FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsAny<Exception>(() =>
                SliderGroupFile.Save(path, new[] { new SliderGroup("A", new[] { "x" }) }));

            Assert.False(System.IO.File.Exists(path + ".tmp"));
            // 原文件保持原样（没被半成品覆盖）
            Assert.Equal("<SliderGroups />", System.IO.File.ReadAllText(path));
        }
        finally
        {
            System.IO.File.SetAttributes(path, original);
        }
    }
}
