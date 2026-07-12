using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// Persistent store for the user's UI language preference.
/// Stores data in &lt;data_dir&gt;/config/ui-settings.json.
/// All operations are safe to call from any thread and never throw.
/// </summary>
public static class UiLanguageStore
{
    private const string ConfigDirName = "config";
    private const string FileName = "ui-settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    private static readonly object FileLock = new();

    /// <summary>
    /// Loads the persisted language or falls back to the system UI culture default.
    /// Thread-safe; never throws.
    /// </summary>
    public static UiLanguage LoadOrDefault(CultureInfo? culture = null)
    {
        try
        {
            var path = GetSettingsPath();
            string? text;
            lock (FileLock)
            {
                if (!File.Exists(path))
                    return UiLanguageExtensions.DefaultFromCulture(culture);

                text = File.ReadAllText(path);
            }

            if (string.IsNullOrWhiteSpace(text))
                return UiLanguageExtensions.DefaultFromCulture(culture);

            var data = JsonSerializer.Deserialize<UiSettingsDto>(text, JsonOptions);
            var parsed = UiLanguageExtensions.ParseLanguage(data?.Language);
            return parsed ?? UiLanguageExtensions.DefaultFromCulture(culture);
        }
        catch
        {
            return UiLanguageExtensions.DefaultFromCulture(culture);
        }
    }

    /// <summary>
    /// Saves the language preference atomically using a temporary file and rename.
    /// Thread-safe; failures are swallowed to avoid breaking the UI flow and never
    /// corrupt an existing valid configuration file.
    /// </summary>
    public static void Save(UiLanguage language)
    {
        try
        {
            var path = GetSettingsPath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var data = new UiSettingsDto { Language = language.ToCultureName() };
            var json = JsonSerializer.Serialize(data, JsonOptions);
            var tempPath = path + ".tmp";

            lock (FileLock)
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, path, overwrite: true);
            }
        }
        catch
        {
            // Best-effort persistence; leave the previous valid file in place.
        }
    }

    private static string GetSettingsPath() =>
        Path.Combine(DataDirResolver.Resolve(), ConfigDirName, FileName);

    private sealed class UiSettingsDto
    {
        [JsonPropertyName("language")]
        public string? Language { get; set; }
    }
}
