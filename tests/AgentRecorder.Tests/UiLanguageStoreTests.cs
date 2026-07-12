using System;
using System.IO;
using System.Text.Json;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public class UiLanguageStoreTests : IDisposable
{
    private readonly TempDirectory _tmp = new();

    public UiLanguageStoreTests()
    {
        DataDirResolver.SetOverride(_tmp.Path);
    }

    public void Dispose()
    {
        DataDirResolver.ClearOverride();
        _tmp.Dispose();
    }

    private static string SettingsPath(string dataDir) =>
        Path.Combine(dataDir, "config", "ui-settings.json");

    [Fact]
    public void LoadOrDefault_NoConfig_ReturnsDefaultFromCulture()
    {
        var expected = UiLanguageExtensions.DefaultFromCulture(null);
        Assert.Equal(expected, UiLanguageStore.LoadOrDefault());
    }

    [Fact]
    public void Save_AndLoad_RoundTrips()
    {
        UiLanguageStore.Save(UiLanguage.EnUs);

        Assert.Equal(UiLanguage.EnUs, UiLanguageStore.LoadOrDefault());

        var path = SettingsPath(_tmp.Path);
        Assert.True(File.Exists(path));
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("en-US", doc.RootElement.GetProperty("language").GetString());
    }

    [Theory]
    [InlineData(UiLanguage.ZhCn, "zh-CN")]
    [InlineData(UiLanguage.EnUs, "en-US")]
    public void Save_PersistsCultureName(UiLanguage language, string expectedCultureName)
    {
        UiLanguageStore.Save(language);

        var path = SettingsPath(_tmp.Path);
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expectedCultureName, doc.RootElement.GetProperty("language").GetString());
    }

    [Fact]
    public void LoadOrDefault_CorruptConfig_FallsBackToDefault()
    {
        var configDir = Path.Combine(_tmp.Path, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(SettingsPath(_tmp.Path), "not valid json {");

        var expected = UiLanguageExtensions.DefaultFromCulture(null);
        Assert.Equal(expected, UiLanguageStore.LoadOrDefault());
    }

    [Theory]
    [InlineData("zh-CN", UiLanguage.ZhCn)]
    [InlineData("en-US", UiLanguage.EnUs)]
    public void LoadOrDefault_ExistingConfig_ParsesLanguage(string persistedValue, UiLanguage expected)
    {
        var configDir = Path.Combine(_tmp.Path, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(SettingsPath(_tmp.Path), $"{{\"language\":\"{persistedValue}\"}}");

        Assert.Equal(expected, UiLanguageStore.LoadOrDefault());
    }

    [Fact]
    public void Save_OverwritesPreviousValue()
    {
        UiLanguageStore.Save(UiLanguage.ZhCn);
        Assert.Equal(UiLanguage.ZhCn, UiLanguageStore.LoadOrDefault());

        UiLanguageStore.Save(UiLanguage.EnUs);
        Assert.Equal(UiLanguage.EnUs, UiLanguageStore.LoadOrDefault());
    }

    [Fact]
    public void Save_FailureDoesNotCorruptExistingValidConfig()
    {
        UiLanguageStore.Save(UiLanguage.ZhCn);
        Assert.Equal(UiLanguage.ZhCn, UiLanguageStore.LoadOrDefault());

        var path = SettingsPath(_tmp.Path);
        var originalJson = File.ReadAllText(path);

        // Make the existing config file read-only so the atomic replace fails.
        var originalAttributes = File.GetAttributes(path);
        try
        {
            File.SetAttributes(path, originalAttributes | FileAttributes.ReadOnly);

            // Save with a different language should fail silently.
            UiLanguageStore.Save(UiLanguage.EnUs);

            // The existing valid config must remain unchanged.
            Assert.True(File.Exists(path));
            Assert.Equal(originalJson.Trim(), File.ReadAllText(path).Trim());
            Assert.Equal(UiLanguage.ZhCn, UiLanguageStore.LoadOrDefault());
        }
        finally
        {
            File.SetAttributes(path, originalAttributes);
        }
    }

    [Fact]
    public void ConcurrentLoadAndSave_DoesNotProduceCorruptJson()
    {
        UiLanguageStore.Save(UiLanguage.EnUs);

        var actions = new System.Collections.Generic.List<System.Action>();
        for (int i = 0; i < 20; i++)
        {
            var language = i % 2 == 0 ? UiLanguage.ZhCn : UiLanguage.EnUs;
            actions.Add(() => UiLanguageStore.Save(language));
            actions.Add(() => UiLanguageStore.LoadOrDefault());
        }

        System.Threading.Tasks.Parallel.Invoke(actions.ToArray());

        var final = UiLanguageStore.LoadOrDefault();
        Assert.True(final == UiLanguage.ZhCn || final == UiLanguage.EnUs);

        var path = SettingsPath(_tmp.Path);
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var value = doc.RootElement.GetProperty("language").GetString();
            Assert.True(value == "zh-CN" || value == "en-US");
        }
    }
}
