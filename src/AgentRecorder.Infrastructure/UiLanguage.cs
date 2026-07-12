using System;
using System.Globalization;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// Supported UI languages for Agent Recorder local user interface.
/// </summary>
public enum UiLanguage
{
    ZhCn,
    EnUs
}

public static class UiLanguageExtensions
{
    /// <summary>
    /// Returns the stable culture name used for persistence and interchange.
    /// </summary>
    public static string ToCultureName(this UiLanguage language) => language switch
    {
        UiLanguage.ZhCn => "zh-CN",
        UiLanguage.EnUs => "en-US",
        _ => "en-US"
    };

    /// <summary>
    /// Parses a persisted language value safely. Returns null for unknown or unsupported values.
    /// </summary>
    public static UiLanguage? ParseLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "zh-CN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "zh", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("zh-", StringComparison.OrdinalIgnoreCase))
        {
            return UiLanguage.ZhCn;
        }

        if (string.Equals(trimmed, "en-US", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "en", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
        {
            return UiLanguage.EnUs;
        }

        return null;
    }

    /// <summary>
    /// Returns the display name of the language in its own script.
    /// </summary>
    public static string ToNativeName(this UiLanguage language) => language switch
    {
        UiLanguage.ZhCn => "简体中文",
        UiLanguage.EnUs => "English",
        _ => "English"
    };

    /// <summary>
    /// Determines the default UI language from the given culture.
    /// Windows UI cultures starting with "zh" select Chinese; everything else falls back to English.
    /// </summary>
    public static UiLanguage DefaultFromCulture(CultureInfo? culture)
    {
        var name = culture?.Name ?? CultureInfo.CurrentUICulture.Name;
        return name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? UiLanguage.ZhCn : UiLanguage.EnUs;
    }
}
