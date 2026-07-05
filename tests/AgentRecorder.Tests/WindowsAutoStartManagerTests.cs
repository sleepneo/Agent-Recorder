using System;
using System.Collections.Generic;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public class FakeRegistryRunKey : IRegistryRunKey
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string? GetValue(string valueName)
    {
        _values.TryGetValue(valueName, out var v);
        return v;
    }

    public void SetValue(string valueName, string command)
    {
        _values[valueName] = command;
    }

    public void DeleteValue(string valueName)
    {
        _values.Remove(valueName);
    }

    public bool ValueExists(string valueName)
    {
        return _values.ContainsKey(valueName);
    }
}

public class WindowsAutoStartManagerTests
{
    private const string TestValueName = "AgentRecorder_Test_Task114";
    private const string TestAppPath = @"C:\Test\AgentRecorder.App.exe";

    private FakeRegistryRunKey CreateFake() => new();

    [Fact]
    public void GetStatus_Disabled_ReturnsDisabled()
    {
        var registry = CreateFake();
        var manager = new WindowsAutoStartManager(registry, TestAppPath, TestValueName);

        var status = manager.GetStatus();

        Assert.False(status.Enabled);
        Assert.False(status.MatchesCurrentApp);
        Assert.Equal("disabled", status.Code);
        Assert.Equal(TestValueName, status.ValueName);
        Assert.Equal(TestAppPath, status.AppPath);
    }

    [Fact]
    public void Enable_WritesCorrectCommand()
    {
        var registry = CreateFake();
        // Use an actual existing file in the test output directory
        var existingExe = Path.Combine(AppContext.BaseDirectory, "AgentRecorder.Tests.dll");
        Assert.True(File.Exists(existingExe), $"Test setup: expected file at {existingExe}");
        var manager = new WindowsAutoStartManager(registry, existingExe, TestValueName);

        var result = manager.Enable();

        Assert.True(result.Ok);
        Assert.True(result.Enabled);
        Assert.True(result.MatchesCurrentApp);
        Assert.Equal("enabled", result.Code);
        Assert.NotNull(result.ConfiguredCommand);
        Assert.Contains(existingExe, result.ConfiguredCommand);

        // Verify registry value
        Assert.True(registry.ValueExists(TestValueName));
        var stored = registry.GetValue(TestValueName);
        Assert.Equal(result.ConfiguredCommand, stored);
    }

    [Fact]
    public void Enable_AppNotFound_ReturnsError()
    {
        var registry = CreateFake();
        var manager = new WindowsAutoStartManager(registry, @"C:\Does\Not\Exist.exe", TestValueName);

        var result = manager.Enable();

        Assert.False(result.Ok);
        Assert.False(result.Enabled);
        Assert.Equal("app_not_found", result.Code);
        Assert.NotNull(result.SuggestedAction);
    }

    [Fact]
    public void Disable_RemovesOnlyOurValue()
    {
        var registry = CreateFake();
        registry.SetValue(TestValueName, "\"some path\"");
        registry.SetValue("OtherApp", "other.exe");
        var manager = new WindowsAutoStartManager(registry, TestAppPath, TestValueName);

        var result = manager.Disable();

        Assert.True(result.Ok);
        Assert.False(result.Enabled);
        Assert.Equal("disabled", result.Code);

        // Verify our value is gone but other values remain
        Assert.False(registry.ValueExists(TestValueName));
        Assert.True(registry.ValueExists("OtherApp"));
    }

    [Fact]
    public void GetStatus_EnabledButMismatch_ReturnsMismatch()
    {
        var registry = CreateFake();
        registry.SetValue(TestValueName, @"C:\Old\Path\AgentRecorder.App.exe");
        var manager = new WindowsAutoStartManager(registry, TestAppPath, TestValueName);

        var status = manager.GetStatus();

        Assert.True(status.Enabled);
        Assert.False(status.MatchesCurrentApp);
        Assert.Equal("enabled_mismatch", status.Code);
    }

    [Fact]
    public void GetStatus_EnabledAndMatch_ReturnsMatch()
    {
        var registry = CreateFake();
        var existingExe = Path.Combine(AppContext.BaseDirectory, "AgentRecorder.Tests.dll");
        registry.SetValue(TestValueName, $"\"{existingExe}\"");
        var manager = new WindowsAutoStartManager(registry, existingExe, TestValueName);

        var status = manager.GetStatus();

        Assert.True(status.Enabled);
        Assert.True(status.MatchesCurrentApp);
        Assert.Equal("enabled", status.Code);
    }

    [Theory]
    [InlineData(@"C:\App\AgentRecorder.App.exe", @"C:\App\AgentRecorder.App.exe", true)]
    [InlineData(@"""C:\App\AgentRecorder.App.exe""", @"C:\App\AgentRecorder.App.exe", true)]
    [InlineData(@"""C:\App\AgentRecorder.App.exe"" --tray", @"C:\App\AgentRecorder.App.exe", true)]
    [InlineData(@"C:\App\AgentRecorder.App.exe --tray", @"C:\App\AgentRecorder.App.exe", true)]
    [InlineData(@"C:\Other\App.exe", @"C:\App\AgentRecorder.App.exe", false)]
    [InlineData(@"", @"C:\App\AgentRecorder.App.exe", false)]
    public void CommandMatchesApp_VariousFormats(string command, string appPath, bool expected)
    {
        var result = WindowsAutoStartManager.CommandMatchesApp(command, appPath);
        Assert.Equal(expected, result);
    }
}
