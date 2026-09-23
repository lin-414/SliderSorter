using BSGroupGenerator.Core;
using Xunit;

namespace BSGroupGenerator.Tests;

/// <summary>
/// 设置的落盘往返。这里只钉「输出冲突」页新加的那一项（左栏收起的那些分组），
/// 其余字段的行为不在本次改动范围内。
/// <para>
/// 用 <see cref="AppSettings.DirectoryOverride"/> 指到临时目录：默认路径是用户真实的
/// <c>%APPDATA%\BSGroupGenerator</c>，跑一次测试就会把人家真实的设置写掉。
/// </para>
/// </summary>
public class AppSettingsTests
{
    /// <summary>换到临时目录跑一段，结束后把覆盖撤掉（否则后续用例拿到的是这间临时目录）。</summary>
    private static void WithTempSettings(Action<AppSettings> body)
    {
        using var temp = new TempDir();
        try
        {
            AppSettings.DirectoryOverride = temp.Path;
            var settings = AppSettings.Load();
            body(settings);
        }
        finally
        {
            AppSettings.DirectoryOverride = null;
        }
    }

    [Fact]
    public void CollapsedGroupsStartEmptySoEverythingIsExpandedByDefault()
    {
        // 存"收起的"而不是"展开的"，全部意义就在这一条：默认值与"什么都没配过"是同一个状态。
        // 若改成存展开的分组，新分组会默认收起——那是把默认值和用户意图搞反了。
        var settings = new AppSettings();

        Assert.Empty(settings.CollapsedConflictGroups);
    }

    [Fact]
    public void CollapsedGroupsSurviveARoundTripThroughTheSettingsFile()
    {
        WithTempSettings(settings =>
        {
            settings.CollapsedConflictGroups.Add("护甲");
            settings.CollapsedConflictGroups.Add("随从");
            settings.Save();

            var reloaded = AppSettings.Load();

            Assert.Equal(["护甲", "随从"], reloaded.CollapsedConflictGroups);
        });
    }

    [Fact]
    public void TheUngroupedBucketRoundTripsAsAnEmptyString()
    {
        // 「未入组」那一桶的键是空串（与 UserGroupConflicts.GroupName 同一哨兵）。
        // 空串在 JSON 数组里完全合法，但正因为它"看着像没写"，必须有用例钉住它不会被
        // 顺手过滤掉——那样表现是"未入组那一组收起后，重启又自己展开了"。
        WithTempSettings(settings =>
        {
            settings.CollapsedConflictGroups.Add("");
            settings.Save();

            var reloaded = AppSettings.Load();

            Assert.Equal([""], reloaded.CollapsedConflictGroups);
        });
    }

    [Fact]
    public void ASettingsFileWrittenBeforeThisFieldExistedLoadsWithEverythingExpanded()
    {
        // 老用户的 settings.json 里没有这个键。反序列化后必须是空列表而不是 null——
        // 是 null 的话，页面上那句 Contains/Add 会当场抛 NullReferenceException。
        WithTempSettings(_ =>
        {
            File.WriteAllText(
                Path.Combine(AppSettings.DirectoryOverride!, "settings.json"),
                """{ "UiTheme": "light", "UiLanguage": "en" }""");

            var reloaded = AppSettings.Load();

            Assert.NotNull(reloaded.CollapsedConflictGroups);
            Assert.Empty(reloaded.CollapsedConflictGroups);
        });
    }
}
