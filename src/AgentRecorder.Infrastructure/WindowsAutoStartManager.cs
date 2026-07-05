using System;
using System.IO;
using Microsoft.Win32;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// Result of an autostart status query.
/// </summary>
public sealed class AutoStartStatus
{
    public bool Enabled { get; set; }
    public bool MatchesCurrentApp { get; set; }
    public string ValueName { get; set; } = "";
    public string RunKey { get; set; } = "";
    public string AppPath { get; set; } = "";
    public string? ConfiguredCommand { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Result of an autostart enable/disable operation.
/// </summary>
public sealed class AutoStartOperationResult
{
    public bool Ok { get; set; }
    public bool Enabled { get; set; }
    public bool MatchesCurrentApp { get; set; }
    public string ValueName { get; set; } = "";
    public string RunKey { get; set; } = "";
    public string AppPath { get; set; } = "";
    public string? ConfiguredCommand { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? SuggestedAction { get; set; }
}

/// <summary>
/// Abstraction over registry operations so unit tests can avoid touching real HKCU.
/// </summary>
public interface IRegistryRunKey
{
    string? GetValue(string valueName);
    void SetValue(string valueName, string command);
    void DeleteValue(string valueName);
    bool ValueExists(string valueName);
}

/// <summary>
/// Real implementation backed by HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// </summary>
public sealed class RegistryRunKey : IRegistryRunKey
{
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? GetValue(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunSubKey, writable: false);
        return key?.GetValue(valueName) as string;
    }

    public void SetValue(string valueName, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunSubKey, writable: true);
        key?.SetValue(valueName, command, RegistryValueKind.String);
    }

    public void DeleteValue(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunSubKey, writable: true);
        if (key == null) return;
        if (key.GetValue(valueName) != null)
            key.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public bool ValueExists(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunSubKey, writable: false);
        return key?.GetValue(valueName) != null;
    }
}

/// <summary>
/// Manages per-user autostart registration via the HKCU Run registry key.
/// Safe for use from both CLI and the running service; never auto-enables.
/// </summary>
public sealed class WindowsAutoStartManager
{
    public const string DefaultValueName = "Agent Recorder";
    public const string RunKeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly IRegistryRunKey _registry;
    private readonly string _valueName;
    private readonly string _appPath;

    public WindowsAutoStartManager(string appPath, string valueName = DefaultValueName)
        : this(new RegistryRunKey(), appPath, valueName) { }

    internal WindowsAutoStartManager(IRegistryRunKey registry, string appPath, string valueName)
    {
        _registry = registry;
        _valueName = valueName;
        _appPath = appPath;
    }

    public string ValueName => _valueName;
    public string AppPath => _appPath;

    /// <summary>
    /// Query current autostart status. Does not modify the registry.
    /// </summary>
    public AutoStartStatus GetStatus()
    {
        try
        {
            var command = _registry.GetValue(_valueName);
            var enabled = command != null;
            var matches = enabled && CommandMatchesApp(command!, _appPath);

            return new AutoStartStatus
            {
                Enabled = enabled,
                MatchesCurrentApp = matches,
                ValueName = _valueName,
                RunKey = RunKeyPath,
                AppPath = _appPath,
                ConfiguredCommand = command,
                Code = enabled ? (matches ? "enabled" : "enabled_mismatch") : "disabled",
                Message = enabled
                    ? (matches ? "Autostart is enabled and matches the current app." : "Autostart is enabled but points to a different location.")
                    : "Autostart is disabled."
            };
        }
        catch (Exception ex)
        {
            return new AutoStartStatus
            {
                Enabled = false,
                MatchesCurrentApp = false,
                ValueName = _valueName,
                RunKey = RunKeyPath,
                AppPath = _appPath,
                ConfiguredCommand = null,
                Code = "unavailable",
                Message = $"Failed to read autostart status: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Enable autostart by writing the current app path to the Run key.
    /// </summary>
    public AutoStartOperationResult Enable()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_appPath) || !File.Exists(_appPath))
            {
                return new AutoStartOperationResult
                {
                    Ok = false,
                    Enabled = false,
                    MatchesCurrentApp = false,
                    ValueName = _valueName,
                    RunKey = RunKeyPath,
                    AppPath = _appPath,
                    ConfiguredCommand = null,
                    Code = "app_not_found",
                    Message = $"App executable not found at '{_appPath}'.",
                    SuggestedAction = "Provide a valid --app path pointing to AgentRecorder.App.exe"
                };
            }

            var command = BuildCommand(_appPath);
            _registry.SetValue(_valueName, command);

            return new AutoStartOperationResult
            {
                Ok = true,
                Enabled = true,
                MatchesCurrentApp = true,
                ValueName = _valueName,
                RunKey = RunKeyPath,
                AppPath = _appPath,
                ConfiguredCommand = command,
                Code = "enabled",
                Message = "Autostart enabled successfully."
            };
        }
        catch (Exception ex)
        {
            var status = GetStatus();
            return new AutoStartOperationResult
            {
                Ok = false,
                Enabled = status.Enabled,
                MatchesCurrentApp = status.MatchesCurrentApp,
                ValueName = _valueName,
                RunKey = RunKeyPath,
                AppPath = _appPath,
                ConfiguredCommand = status.ConfiguredCommand,
                Code = "enable_failed",
                Message = $"Failed to enable autostart: {ex.Message}",
                SuggestedAction = "Check registry permissions or try running as the current user"
            };
        }
    }

    /// <summary>
    /// Disable autostart by removing the value from the Run key.
    /// Only removes our own value name; never touches other entries.
    /// </summary>
    public AutoStartOperationResult Disable()
    {
        try
        {
            _registry.DeleteValue(_valueName);

            return new AutoStartOperationResult
            {
                Ok = true,
                Enabled = false,
                MatchesCurrentApp = false,
                ValueName = _valueName,
                RunKey = RunKeyPath,
                AppPath = _appPath,
                ConfiguredCommand = null,
                Code = "disabled",
                Message = "Autostart disabled successfully."
            };
        }
        catch (Exception ex)
        {
            var status = GetStatus();
            return new AutoStartOperationResult
            {
                Ok = false,
                Enabled = status.Enabled,
                MatchesCurrentApp = status.MatchesCurrentApp,
                ValueName = _valueName,
                RunKey = RunKeyPath,
                AppPath = _appPath,
                ConfiguredCommand = status.ConfiguredCommand,
                Code = "disable_failed",
                Message = $"Failed to disable autostart: {ex.Message}",
                SuggestedAction = "Check registry permissions or manually remove the entry from HKCU Run"
            };
        }
    }

    private static string BuildCommand(string appPath)
    {
        return $"\"{appPath}\"";
    }

    internal static bool CommandMatchesApp(string command, string appPath)
    {
        try
        {
            var cleaned = command.Trim();
            if (cleaned.StartsWith('"'))
            {
                var endQuote = cleaned.IndexOf('"', 1);
                if (endQuote > 1)
                    cleaned = cleaned[1..endQuote];
            }
            else
            {
                var spaceIdx = cleaned.IndexOf(' ');
                if (spaceIdx > 0)
                    cleaned = cleaned[..spaceIdx];
            }

            return PathsEqual(cleaned, appPath);
        }
        catch
        {
            return false;
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\'),
                Path.GetFullPath(b).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
