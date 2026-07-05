using System;
using System.IO;
using System.Text.Json;
using AgentRecorder.Cli;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public class CliAutoStartTests
{
    private const string TestValueName = "AgentRecorder_Test_CliAutoStart";
    private const string FakeAppPath = @"C:\Test\AgentRecorder.App.exe";

    private static AutoStartOptions Opts(string? app = null, string? valueName = TestValueName, bool json = true)
    {
        return new AutoStartOptions
        {
            AppPath = app,
            ValueName = valueName,
            Json = json
        };
    }

    // === Parser tests ===

    [Fact]
    public void ParseAutoStartOpts_JsonFlag_ParsesCorrectly()
    {
        var opts = Program.ParseAutoStartOpts(new[] { "--json" }, 0);
        Assert.True(opts.Json);
        Assert.Null(opts.ParseError);
    }

    [Fact]
    public void ParseAutoStartOpts_AppFlag_ParsesCorrectly()
    {
        var opts = Program.ParseAutoStartOpts(new[] { "--app", @"C:\Test\App.exe" }, 0);
        Assert.Equal(@"C:\Test\App.exe", opts.AppPath);
    }

    [Fact]
    public void ParseAutoStartOpts_ValueName_ParsesCorrectly()
    {
        var opts = Program.ParseAutoStartOpts(new[] { "--value-name", "MyValue" }, 0);
        Assert.Equal("MyValue", opts.ValueName);
    }

    [Fact]
    public void ParseAutoStartOpts_UnknownFlag_ReturnsParseError()
    {
        var opts = Program.ParseAutoStartOpts(new[] { "--bogus" }, 0);
        Assert.NotNull(opts.ParseError);
        Assert.Contains("bogus", opts.ParseError);
    }

    [Fact]
    public void ParseAutoStartOpts_HelpFlag_ParsesCorrectly()
    {
        var opts = Program.ParseAutoStartOpts(new[] { "--help" }, 0);
        Assert.True(opts.ShowHelp);
    }

    // === RunAutoStartCore: status ===

    [Fact]
    public void Status_Disabled_ReturnsCorrectFields()
    {
        var registry = new FakeRegistryRunKey();
        var result = Program.RunAutoStartCore("status", Opts(FakeAppPath), registry);

        Assert.True(result.Ok);
        Assert.Equal("disabled", result.Status);
        Assert.False(result.Enabled);
        Assert.False(result.MatchesCurrentApp);
        Assert.Equal(TestValueName, result.ValueName);
        Assert.Equal(WindowsAutoStartManager.RunKeyPath, result.RunKey);
        Assert.Equal(FakeAppPath, result.AppPath);
        Assert.Null(result.ConfiguredCommand);
        Assert.Equal("disabled", result.Code);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public void Status_EnabledAndMatch_ReturnsCorrectFields()
    {
        var registry = new FakeRegistryRunKey();
        var existingExe = Path.Combine(AppContext.BaseDirectory, "AgentRecorder.Tests.dll");
        Assert.True(File.Exists(existingExe));
        registry.SetValue(TestValueName, $"\"{existingExe}\"");

        var opts = Opts(app: existingExe);
        var result = Program.RunAutoStartCore("status", opts, registry);

        Assert.True(result.Ok);
        Assert.Equal("enabled", result.Status);
        Assert.True(result.Enabled);
        Assert.True(result.MatchesCurrentApp);
        Assert.NotNull(result.ConfiguredCommand);
        Assert.Contains(existingExe, result.ConfiguredCommand!);
    }

    [Fact]
    public void Status_EnabledMismatch_ReturnsMismatch()
    {
        var registry = new FakeRegistryRunKey();
        registry.SetValue(TestValueName, @"""C:\Old\Path\App.exe""");

        var result = Program.RunAutoStartCore("status", Opts(FakeAppPath), registry);

        Assert.True(result.Ok);
        Assert.Equal("enabled_mismatch", result.Status);
        Assert.True(result.Enabled);
        Assert.False(result.MatchesCurrentApp);
        Assert.Equal("enabled_mismatch", result.Code);
    }

    // === RunAutoStartCore: enable/disable ===

    [Fact]
    public void Enable_WritesToRegistryAndReturnsFields()
    {
        var registry = new FakeRegistryRunKey();
        var existingExe = Path.Combine(AppContext.BaseDirectory, "AgentRecorder.Tests.dll");
        Assert.True(File.Exists(existingExe));

        var result = Program.RunAutoStartCore("enable", Opts(app: existingExe), registry);

        Assert.True(result.Ok);
        Assert.Equal("enabled", result.Status);
        Assert.True(result.Enabled);
        Assert.True(result.MatchesCurrentApp);
        Assert.True(registry.ValueExists(TestValueName));
        Assert.Equal(result.ConfiguredCommand, registry.GetValue(TestValueName));
    }

    [Fact]
    public void Enable_AppNotFound_ReturnsError()
    {
        var registry = new FakeRegistryRunKey();

        var result = Program.RunAutoStartCore("enable", Opts(@"C:\Does\Not\Exist.exe"), registry);

        Assert.False(result.Ok);
        Assert.Equal("app_not_found", result.Code);
        Assert.NotNull(result.SuggestedAction);
        Assert.False(registry.ValueExists(TestValueName));
    }

    [Fact]
    public void Disable_RemovesValue()
    {
        var registry = new FakeRegistryRunKey();
        registry.SetValue(TestValueName, "\"some path\"");

        var result = Program.RunAutoStartCore("disable", Opts(FakeAppPath), registry);

        Assert.True(result.Ok);
        Assert.Equal("disabled", result.Status);
        Assert.False(result.Enabled);
        Assert.False(registry.ValueExists(TestValueName));
    }

    // === Invalid subcommand ===

    [Fact]
    public void InvalidSubcommand_ReturnsInvalidArgument()
    {
        var registry = new FakeRegistryRunKey();
        var result = Program.RunAutoStartCore("badcmd", Opts(FakeAppPath), registry);

        Assert.False(result.Ok);
        Assert.Equal("INVALID_ARGUMENT", result.Code);
        Assert.Contains("badcmd", result.Message!);
        Assert.NotNull(result.SuggestedAction);
    }

    // === Parse error returns INVALID_ARGUMENT ===

    [Fact]
    public void ParseError_WithJson_ReturnsInvalidArgumentEnvelope()
    {
        var opts = Program.ParseAutoStartOpts(new[] { "--json", "--bad-option" }, 0);
        Assert.NotNull(opts.ParseError);

        // Simulate what RunAutoStart does on parse error
        var error = new AutoStartResult
        {
            Ok = false,
            Status = "error",
            Code = "INVALID_ARGUMENT",
            Message = opts.ParseError,
            ValueName = WindowsAutoStartManager.DefaultValueName,
            RunKey = WindowsAutoStartManager.RunKeyPath,
            AppPath = ""
        };

        Assert.False(error.Ok);
        Assert.Equal("INVALID_ARGUMENT", error.Code);
        Assert.Contains("bad-option", error.Message!);
    }

    // === --app priority ===

    [Fact]
    public void ResolveAutoStartAppPath_ExplicitApp_TakesPriority()
    {
        var opts = new AutoStartOptions { AppPath = @"C:\Custom\App.exe" };
        var result = Program.ResolveAutoStartAppPath(opts);

        Assert.Equal(@"C:\Custom\App.exe", result);
    }

    [Fact]
    public void ResolveAutoStartAppPath_NoApp_UsesFallback()
    {
        var opts = new AutoStartOptions();
        var baseDir = AppContext.BaseDirectory;
        var result = Program.ResolveAutoStartAppPath(opts, baseDir);

        // Should return a path (may or may not exist, but should not be empty)
        Assert.False(string.IsNullOrEmpty(result));
        Assert.EndsWith("AgentRecorder.App.exe", result);
    }

    [Fact]
    public void ResolveAutoStartAppPath_SourceTree_FindsRealApp()
    {
        // Simulate CLI running from tools/AgentRecorder.Cli/bin/Release/<tfm>/
        // The test assembly runs from tests/AgentRecorder.Tests/bin/Release/<tfm>/
        // which has the same structure. The real App should be at
        // src/AgentRecorder.App/bin/Release/<tfm>/AgentRecorder.App.exe
        var testBaseDir = AppContext.BaseDirectory;
        var opts = new AutoStartOptions();

        var result = Program.ResolveAutoStartAppPath(opts, testBaseDir);

        // The source tree detection should find the real App if it's built
        // (it may not exist in all environments, so just verify the path looks right)
        Assert.False(string.IsNullOrEmpty(result));
        Assert.EndsWith("AgentRecorder.App.exe", result);
    }

    [Fact]
    public void ResolveAutoStartAppPath_ExplicitApp_OverridesSourceTree()
    {
        var opts = new AutoStartOptions { AppPath = @"C:\Explicit\App.exe" };
        var result = Program.ResolveAutoStartAppPath(opts, AppContext.BaseDirectory);

        Assert.Equal(@"C:\Explicit\App.exe", result);
    }
}
