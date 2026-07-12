using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public class UiTextProviderTests
{
    [Theory]
    [InlineData(UiLanguage.ZhCn, "RegionSelection_Button_Confirm", "确认 (Enter)")]
    [InlineData(UiLanguage.EnUs, "RegionSelection_Button_Confirm", "Confirm (Enter)")]
    [InlineData(UiLanguage.ZhCn, "Confirmation_Button_Approve", "✓ 确认")]
    [InlineData(UiLanguage.EnUs, "Confirmation_Button_Approve", "✓ Confirm")]
    [InlineData(UiLanguage.ZhCn, "Tray_Menu_Language", "语言 / Language")]
    [InlineData(UiLanguage.EnUs, "Tray_Menu_Language", "Language / 语言")]
    public void Get_KnownKey_ReturnsLocalizedText(UiLanguage language, string key, string expected)
    {
        var provider = new UiTextProvider(language);
        Assert.Equal(expected, provider.Get(key));
    }

    [Theory]
    [InlineData(UiLanguage.ZhCn)]
    [InlineData(UiLanguage.EnUs)]
    public void Get_MissingKey_ReturnsKeyAsFallback(UiLanguage language)
    {
        var provider = new UiTextProvider(language);
        Assert.Equal("Missing.Key.Not.Defined", provider.Get("Missing.Key.Not.Defined"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void Get_EmptyKey_ReturnsEmptyString(string? key, string expected)
    {
        var provider = new UiTextProvider(UiLanguage.EnUs);
        Assert.Equal(expected, provider.Get(key!));
    }

    [Theory]
    [InlineData(UiLanguage.ZhCn, "Confirmation_QueuePosition", new object[] { 1, 3 }, "队列位置：1 / 3")]
    [InlineData(UiLanguage.EnUs, "Confirmation_QueuePosition", new object[] { 1, 3 }, "Queue position: 1 / 3")]
    [InlineData(UiLanguage.ZhCn, "Tray_Status_Waiting", new object[] { 30 }, "状态：● 等待确认（30s 内请操作）")]
    [InlineData(UiLanguage.EnUs, "Tray_Status_Waiting", new object[] { 30 }, "Status: ● Pending confirmation (act within 30s)")]
    public void Format_KnownKey_SubstitutesArguments(UiLanguage language, string key, object[] args, string expected)
    {
        var provider = new UiTextProvider(language);
        Assert.Equal(expected, provider.Format(key, args));
    }

    [Fact]
    public void Format_MissingFormatArguments_ReturnsTemplateUnchanged()
    {
        var provider = new UiTextProvider(UiLanguage.EnUs);
        Assert.Equal("Queue position: {0} / {1}", provider.Format("Confirmation_QueuePosition"));
    }

    [Fact]
    public void Format_InvalidFormatString_ReturnsTemplateUnchanged()
    {
        var provider = new UiTextProvider(UiLanguage.EnUs);
        // Reuse a key whose template has no format placeholders and pass extra args.
        Assert.Equal("Language / 语言", provider.Format("Tray_Menu_Language", "extra"));
    }

    [Fact]
    public void LanguageProperty_ReflectsConstructorArgument()
    {
        Assert.Equal(UiLanguage.ZhCn, new UiTextProvider(UiLanguage.ZhCn).Language);
        Assert.Equal(UiLanguage.EnUs, new UiTextProvider(UiLanguage.EnUs).Language);
    }

    [Fact]
    public void BothLanguages_HaveIdenticalKeySets()
    {
        var zh = new UiTextProvider(UiLanguage.ZhCn);
        var en = new UiTextProvider(UiLanguage.EnUs);

        var zhKeys = GetKeys(zh);
        var enKeys = GetKeys(en);

        Assert.Subset(zhKeys, enKeys);
        Assert.Subset(enKeys, zhKeys);
    }

    [Fact]
    public void BothLanguages_ContainAllProductionKeys()
    {
        // Keys actively requested by AgentRecorder.App UI code. If a new key is
        // added to production code, it must also be added here to ensure both
        // dictionaries stay in sync and complete.
        var productionKeys = new[]
        {
            // Region selection form
            "RegionSelection_Button_Confirm", "RegionSelection_Button_Cancel",
            "RegionSelection_Info_Default", "RegionSelection_Coords_VirtualScreen",
            "RegionSelection_Display_Unknown", "RegionSelection_Input_X",
            "RegionSelection_Input_Y", "RegionSelection_Input_W", "RegionSelection_Input_H",
            "RegionSelection_Preset_1280x720", "RegionSelection_Preset_1600x900",
            "RegionSelection_Preset_1920x1080", "RegionSelection_Preset_Fit16x9",
            "RegionSelection_Info_TooSmall", "RegionSelection_Info_Selected",
            "RegionSelection_Coords_FormBounds", "RegionSelection_Display",
            "RegionSelection_Display_UnknownWithVirtual",

            // Recording stop control
            "StopControl_Button_Stop", "StopControl_Tooltip", "StopControl_Button_Stopping",

            // Confirmation form
            "Confirmation_FolderBrowser_Description", "Confirmation_Title",
            "Confirmation_Close_Expired", "Confirmation_Close_Rejected",
            "Confirmation_Close_QueueAdvanced", "Confirmation_RequestTitle",
            "Confirmation_QueuePosition", "Confirmation_Info_Source",
            "Confirmation_Info_SourceType", "Confirmation_Info_SourceTitle",
            "Confirmation_Info_Duration", "Confirmation_Info_Audio",
            "Confirmation_Info_NestedRole", "Confirmation_Info_RecordingId",
            "Confirmation_Info_ConfirmationId", "Confirmation_Info_Timeout",
            "Confirmation_Info_ExpiresAt", "Confirmation_Preview_NoBounds",
            "Confirmation_Preview_Fallback", "Confirmation_Output_Title",
            "Confirmation_Output_Change", "Confirmation_Output_Remember",
            "Confirmation_Timeout_Initializing", "Confirmation_Warning",
            "Confirmation_Button_Reject", "Confirmation_Button_Approve",
            "Confirmation_Output_AutoName", "Confirmation_Timeout_Expired",
            "Confirmation_Timeout_SecondsUrgent", "Confirmation_Timeout_Seconds",
            "Confirmation_Close_Approved",

            // Tray context
            "Tray_Status_Idle", "Tray_Menu_Stop", "Tray_Language_ZhCn",
            "Tray_Language_EnUs", "Tray_Menu_Language", "Tray_Menu_OpenOutputDir",
            "Tray_Menu_Exit", "Tray_Idle", "Tray_Menu_Confirm", "Tray_Menu_Reject",
            "Tray_Status_Waiting", "Tray_WaitingConfirmation", "Tray_Balloon_WaitingTitle",
            "Tray_Balloon_WaitingBody", "Tray_Balloon_RecordingTitle",
            "Tray_Balloon_RecordingBody", "Tray_Recording_WithCount", "Tray_Recording",
            "Tray_Stopping", "Tray_Status_RecordingWithCount", "Tray_Status_Recording",
            "Tray_Status_Stopping", "Tray_Menu_StopAll", "Tray_Balloon_ErrorTitle"
        };

        var zh = new UiTextProvider(UiLanguage.ZhCn);
        var en = new UiTextProvider(UiLanguage.EnUs);
        var zhKeys = GetKeys(zh);
        var enKeys = GetKeys(en);

        foreach (var key in productionKeys)
        {
            Assert.Contains(key, zhKeys);
            Assert.Contains(key, enKeys);
            Assert.NotEqual(key, zh.Get(key));
            Assert.NotEqual(key, en.Get(key));
        }
    }

    private static System.Collections.Generic.HashSet<string> GetKeys(UiTextProvider provider)
    {
        // The dictionary is private; use reflection to compare key coverage.
        var field = typeof(UiTextProvider).GetField("_texts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var dict = (System.Collections.Generic.Dictionary<string, string>)field!.GetValue(provider)!;
        return new System.Collections.Generic.HashSet<string>(dict.Keys);
    }
}
