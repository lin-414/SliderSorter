using Xunit;
using BSGroupGenerator.Core;

namespace BSGroupGenerator.Tests;

/// <summary>实例发现：全局实例（根目录下含 ModOrganizer.ini 的子目录）+ 用户指定目录。
/// 用 <see cref="Mo2Discovery.GlobalRootOverride"/> 指到临时目录，结果不依赖本机装了什么。</summary>
public class Mo2DiscoveryTests
{
    private static string IniOf(string dir) => System.IO.Path.Combine(dir, "ModOrganizer.ini");

    [Fact]
    public void DiscoversGlobalAndUserSpecifiedInstances()
    {
        using var temp = new TempDir();
        var globalRoot = temp.Sub("ModOrganizer");
        var globalInstance = temp.Sub("ModOrganizer", "MyInstance");
        System.IO.File.WriteAllText(IniOf(globalInstance), "[General]\ngameName=Skyrim Special Edition\n");
        var portable = temp.Sub("PortableMO2");
        System.IO.File.WriteAllText(IniOf(portable), "[General]\ngameName=Fallout 4\n");

        var previous = Mo2Discovery.GlobalRootOverride;
        try
        {
            Mo2Discovery.GlobalRootOverride = globalRoot;
            var found = Mo2Discovery.Discover(new[] { portable });

            Assert.Contains(found, i => i.InstanceDir == globalInstance && i.Kind == Mo2InstanceKind.Global);
            Assert.Contains(found, i => i.InstanceDir == portable && i.Kind == Mo2InstanceKind.Manual);
        }
        finally
        {
            Mo2Discovery.GlobalRootOverride = previous;
        }
    }

    /// <summary>只有 ModOrganizer.exe、没有 ModOrganizer.ini 的目录不登记为实例
    ///（判定依据只有 ini；可执行文件本身不构成实例）。</summary>
    [Fact]
    public void ExeWithoutIniIsNotAnInstance()
    {
        using var temp = new TempDir();
        var exeOnly = temp.Sub("ExeOnly");
        System.IO.File.WriteAllText(System.IO.Path.Combine(exeOnly, "ModOrganizer.exe"), "");

        var previous = Mo2Discovery.GlobalRootOverride;
        try
        {
            Mo2Discovery.GlobalRootOverride = temp.Sub("NoGlobalInstances"); // 空根目录
            var found = Mo2Discovery.Discover(new[] { exeOnly });
            Assert.DoesNotContain(found, i => i.InstanceDir == exeOnly);
        }
        finally
        {
            Mo2Discovery.GlobalRootOverride = previous;
        }
    }

    /// <summary>程序目录（有 ModOrganizer.exe、无 ini）被认出来是"安装根目录"，
    /// 但**不登记为实例**——这条判据只用于把提示分流到「这是程序目录，不是实例目录」，
    /// 不参与登记（登记判据始终只有 ModOrganizer.ini）。
    ///
    /// 非便携安装（MO2 官方安装器）就是这种布局：exe 在程序目录，实例数据在
    /// %LOCALAPPDATA%\ModOrganizer\&lt;实例名&gt;。用户凭直觉选程序目录是高频误操作。</summary>
    [Fact]
    public void InstallRootIsRecognizedButNotRegistered()
    {
        using var temp = new TempDir();
        var installRoot = temp.Sub("MO2");
        temp.File("MO2", "ModOrganizer.exe", "");

        Assert.True(Mo2Discovery.IsInstallRoot(installRoot));
        Assert.Null(Mo2Discovery.CreateFromDirectory(installRoot));

        // 便携安装（exe 与 ini 同目录）不是"程序目录"：它本身就是合法实例，
        // 提示不该把它引到 %LOCALAPPDATA% 去
        var portable = temp.Sub("PortableMO2");
        temp.File("PortableMO2", "ModOrganizer.exe", "");
        System.IO.File.WriteAllText(IniOf(portable), "[General]\ngameName=Fallout 4\n");

        Assert.False(Mo2Discovery.IsInstallRoot(portable));
        Assert.NotNull(Mo2Discovery.CreateFromDirectory(portable));

        // 既无 exe 也无 ini 的普通目录，以及不存在的路径，都不是程序目录
        Assert.False(Mo2Discovery.IsInstallRoot(temp.Sub("Empty")));
        Assert.False(Mo2Discovery.IsInstallRoot(System.IO.Path.Combine(temp.Path, "Missing")));
    }
}
