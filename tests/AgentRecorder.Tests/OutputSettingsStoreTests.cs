using System;
using System.IO;
using System.Text.Json;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Direct tests for <see cref="OutputSettingsStore"/>, including persistence,
/// fallback, and security validation.
/// </summary>
[Collection("NonParallel-AgentRecorderDataDir")]
public class OutputSettingsStoreTests : IDisposable
{
    private readonly TempDirectory _tmp = new();

    public OutputSettingsStoreTests()
    {
        DataDirResolver.SetOverride(_tmp.Path);
    }

    public void Dispose()
    {
        DataDirResolver.ClearOverride();
        _tmp.Dispose();
    }

    private static string SettingsPath(string dataDir) =>
        Path.Combine(dataDir, "config", "output-settings.json");

    [Fact]
    public void GetEffectiveDefaultOutputDir_NoConfig_ReturnsBuiltInDefault()
    {
        // The constructor sets a DataDirResolver override, so the built-in default
        // is rooted under the overridden data directory instead of the real user profile.
        var expected = Path.Combine(_tmp.Path, "Videos");

        var actual = OutputSettingsStore.GetEffectiveDefaultOutputDir();

        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(actual));
    }

    [Fact]
    public void SaveDefaultOutputDir_AndRead_RoundTrips()
    {
        var target = Path.Combine(_tmp.Path, "SavedClips");
        Directory.CreateDirectory(target);

        var saved = OutputSettingsStore.SaveDefaultOutputDir(target);
        Assert.True(saved);

        var actual = OutputSettingsStore.GetEffectiveDefaultOutputDir();
        Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(actual));

        var settingsPath = SettingsPath(_tmp.Path);
        Assert.True(File.Exists(settingsPath));
        var json = File.ReadAllText(settingsPath);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(
            Path.GetFullPath(target),
            Path.GetFullPath(doc.RootElement.GetProperty("default_output_directory").GetString()!));
    }

    [Fact]
    public void GetEffectiveDefaultOutputDir_CorruptConfig_FallsBackToBuiltIn()
    {
        var configDir = Path.Combine(_tmp.Path, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(SettingsPath(_tmp.Path), "not valid json {");

        var actual = OutputSettingsStore.GetEffectiveDefaultOutputDir();

        var expected = Path.Combine(_tmp.Path, "Videos");
        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(actual));
    }

    [Fact]
    public void SaveDefaultOutputDir_RelativePath_IsRejected()
    {
        var saved = OutputSettingsStore.SaveDefaultOutputDir("relative\\path");
        Assert.False(saved);

        var actual = OutputSettingsStore.GetEffectiveDefaultOutputDir();
        var expected = Path.Combine(_tmp.Path, "Videos");
        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(actual));
    }

    [Fact]
    public void SaveDefaultOutputDir_SystemDirectory_IsRejected()
    {
        var sysDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.True(Directory.Exists(sysDir), "Windows directory must exist for this test");

        var saved = OutputSettingsStore.SaveDefaultOutputDir(sysDir);
        Assert.False(saved);
    }

    [Fact]
    public void SaveDefaultOutputDir_ProgramFiles_IsRejected()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.True(Directory.Exists(pf), "Program Files directory must exist for this test");

        var saved = OutputSettingsStore.SaveDefaultOutputDir(pf);
        Assert.False(saved);
    }

    [Fact]
    public void SaveDefaultOutputDir_CredentialsDirectory_IsRejected()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var credentialsDir = Path.Combine(localAppData, "Microsoft", "Credentials");
        Directory.CreateDirectory(credentialsDir);

        var saved = OutputSettingsStore.SaveDefaultOutputDir(credentialsDir);
        Assert.False(saved);
    }

    [Fact]
    public void SaveDefaultOutputDir_EmptyOrNull_IsRejected()
    {
        Assert.False(OutputSettingsStore.SaveDefaultOutputDir(null));
        Assert.False(OutputSettingsStore.SaveDefaultOutputDir(""));
        Assert.False(OutputSettingsStore.SaveDefaultOutputDir("   "));
    }
}
