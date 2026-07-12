using System.Globalization;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public class UiLanguageTests
{
    [Theory]
    [InlineData(UiLanguage.ZhCn, "zh-CN")]
    [InlineData(UiLanguage.EnUs, "en-US")]
    public void ToCultureName_ReturnsStableIdentifier(UiLanguage language, string expected)
    {
        Assert.Equal(expected, language.ToCultureName());
    }

    [Theory]
    [InlineData(UiLanguage.ZhCn, "简体中文")]
    [InlineData(UiLanguage.EnUs, "English")]
    public void ToNativeName_ReturnsDisplayNameInOwnScript(UiLanguage language, string expected)
    {
        Assert.Equal(expected, language.ToNativeName());
    }

    [Theory]
    [InlineData("zh-CN", UiLanguage.ZhCn)]
    [InlineData("zh", UiLanguage.ZhCn)]
    [InlineData("zh-Hans", UiLanguage.ZhCn)]
    [InlineData("en-US", UiLanguage.EnUs)]
    [InlineData("en", UiLanguage.EnUs)]
    [InlineData("en-GB", UiLanguage.EnUs)]
    public void ParseLanguage_ValidValue_ReturnsLanguage(string value, UiLanguage expected)
    {
        Assert.Equal(expected, UiLanguageExtensions.ParseLanguage(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("fr-FR")]
    [InlineData("de")]
    public void ParseLanguage_UnsupportedValue_ReturnsNull(string? value)
    {
        Assert.Null(UiLanguageExtensions.ParseLanguage(value));
    }

    [Theory]
    [InlineData("zh-CN", UiLanguage.ZhCn)]
    [InlineData("en-US", UiLanguage.EnUs)]
    [InlineData("zh-TW", UiLanguage.ZhCn)]
    [InlineData("ja-JP", UiLanguage.EnUs)]
    public void DefaultFromCulture_SelectsChineseForZhOthersForEnglish(string cultureName, UiLanguage expected)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        Assert.Equal(expected, UiLanguageExtensions.DefaultFromCulture(culture));
    }
}
