using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// Persistent store for user-configurable output settings.
/// Stores data in &lt;data_dir&gt;/config/output-settings.json.
/// All operations are safe to call from any thread and never throw;
/// on failure they fall back to the built-in default directory.
/// </summary>
public static class OutputSettingsStore
{
    private const string ConfigDirName = "config";
    private const string FileName = "output-settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    /// <summary>
    /// Returns the effective default output directory.
    /// Order of precedence:
    /// 1. Persisted value in output-settings.json (if valid).
    /// 2. Built-in default directory (%USERPROFILE%\Videos\AgentRecorder or &lt;data_dir&gt;\Videos).
    /// </summary>
    public static string GetEffectiveDefaultOutputDir()
    {
        var stored = ReadStoredDirectory();
        if (!string.IsNullOrWhiteSpace(stored))
        {
            try
            {
                ValidateDirectory(stored);
                return Path.GetFullPath(stored);
            }
            catch
            {
                // Fall through to built-in default.
            }
        }
        return GetBuiltInDefaultOutputDir();
    }

    /// <summary>
    /// Saves the given directory as the new default output directory.
    /// Does nothing if the directory is null/empty/invalid.
    /// Returns true if the directory was actually persisted.
    /// </summary>
    public static bool SaveDefaultOutputDir(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return false;

        if (!Path.IsPathFullyQualified(directory))
            return false;

        try
        {
            var full = Path.GetFullPath(directory);
            ValidateDirectory(full);

            var path = GetSettingsPath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var data = new OutputSettingsDto { DefaultOutputDirectory = full };
            File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
            return true;
        }
        catch
        {
            // Best-effort persistence; failures are swallowed to avoid breaking the UI flow.
            return false;
        }
    }

    private static string? ReadStoredDirectory()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
                return null;

            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var data = JsonSerializer.Deserialize<OutputSettingsDto>(text, JsonOptions);
            return data?.DefaultOutputDirectory;
        }
        catch
        {
            return null;
        }
    }

    private static string GetSettingsPath() =>
        Path.Combine(DataDirResolver.Resolve(), ConfigDirName, FileName);

    /// <summary>
    /// Minimal directory validation to avoid a circular project reference on Security.
    /// Mirrors the essential rules from PolicyEngine.ValidateDirectory for output paths.
    /// </summary>
    private static void ValidateDirectory(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            throw new ArgumentException("Directory cannot be empty");

        if (!Path.IsPathFullyQualified(dir))
            throw new ArgumentException("Directory must be an absolute path");

        var lower = dir.ToLowerInvariant();

        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant();
        if (!string.IsNullOrEmpty(winDir) && lower.StartsWith(winDir))
            throw new ArgumentException("Writing into system directory is not allowed");

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).ToLowerInvariant();
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).ToLowerInvariant();
        if ((!string.IsNullOrEmpty(programFiles) && lower.StartsWith(programFiles))
            || (!string.IsNullOrEmpty(programFilesX86) && lower.StartsWith(programFilesX86)))
            throw new ArgumentException("Writing into Program Files is not allowed");

        string[] denied =
        {
            @"\program files\", @"\program files (x86)\", @"\windows\", @"\system32\",
            @"\users\public\", @"\users\all users\", @"\programdata\",
            @"\appdata\roaming\microsoft\credentials",
            @"\appdata\local\microsoft\credentials"
        };

        if (denied.Any(k => lower.Contains(k)))
            throw new ArgumentException("Writing into this directory is not allowed by security policy");
    }

    /// <summary>
    /// Built-in fallback default directory. Mirrors Paths.DefaultOutputDir
    /// to avoid a circular project reference.
    /// </summary>
    private static string GetBuiltInDefaultOutputDir()
    {
        if (DataDirResolver.HasOverride || DataDirResolver.UsingEnvOverride)
            return Path.Combine(DataDirResolver.Resolve(), "Videos");
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "AgentRecorder");
    }

    private sealed class OutputSettingsDto
    {
        [JsonPropertyName("default_output_directory")]
        public string? DefaultOutputDirectory { get; set; }
    }
}
