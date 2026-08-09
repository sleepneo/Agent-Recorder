using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;
using ApiException = AgentRecorder.Infrastructure.ApiException;

namespace AgentRecorder.Tests;

[Collection("NonParallel-WindowBackend")]
public sealed class CaptureBackendSelectorTests
{
    [Fact]
    public void DisplaySource_DefaultsToFfmpeg()
    {
        using var env = WindowBackendEnvironment.Unset();
        var result = CaptureBackendSelector.Select(new CaptureConfig { SourceKind = "display" });

        Assert.Equal("ffmpeg", result.BackendType);
        Assert.IsType<FfmpegCaptureBackend>(result.Backend);
    }

    [Fact]
    public void WindowSource_DefaultsToFfmpegWindowRegion()
    {
        using var env = WindowBackendEnvironment.Unset();
        var result = CaptureBackendSelector.Select(new CaptureConfig { SourceKind = "window" });

        Assert.Equal("ffmpeg-window-region", result.BackendType);
        Assert.IsType<FfmpegCaptureBackend>(result.Backend);
    }

    [Fact]
    public void RegionSource_AlwaysUsesFfmpegRegion()
    {
        using var env = WindowBackendEnvironment.Set("wgc-continuous");
        var result = CaptureBackendSelector.Select(new CaptureConfig { SourceKind = "region" });

        Assert.Equal("ffmpeg-region", result.BackendType);
        Assert.IsType<FfmpegCaptureBackend>(result.Backend);
    }

    [Fact]
    public void CreateBackend_LegacyAliasFailsFastWithoutOldBackend()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CaptureBackendSelector.CreateBackend("wgc"));
        Assert.Contains("Unknown capture backend decision", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyAliasHasNoIndependentWindowSemantics()
    {
        var ex = Assert.Throws<ApiException>(() =>
            CaptureBackendSelector.DetermineSemanticsForTests("window", "wgc"));

        Assert.Equal("CAPTURE_SEMANTICS_UNKNOWN", ex.Code);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData("wgc-continuous", "wgc-continuous")]
    [InlineData(" WGC-CONTINUOUS ", "wgc-continuous")]
    [InlineData("wgc", "wgc-continuous")]
    [InlineData(" WGC ", "wgc-continuous")]
    public void StartupBackendArgument_NormalizesOnlySupportedValues(string? value, string expected)
    {
        Assert.Equal(expected, CaptureBackendSelector.NormalizeWindowBackendArgument(value));
    }

    [Fact]
    public void StartupBackendArgument_RejectsUnknownValue()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            CaptureBackendSelector.NormalizeWindowBackendArgument("future-backend"));

        Assert.Contains("wgc-continuous", ex.Message, StringComparison.Ordinal);
        Assert.Contains("wgc", ex.Message, StringComparison.Ordinal);
    }

    private sealed class WindowBackendEnvironment : IDisposable
    {
        private readonly string? _previous;

        private WindowBackendEnvironment(string? value)
        {
            _previous = Environment.GetEnvironmentVariable(CaptureBackendSelector.WgcEnvVar);
            Environment.SetEnvironmentVariable(CaptureBackendSelector.WgcEnvVar, value);
        }

        public static WindowBackendEnvironment Set(string value) => new(value);
        public static WindowBackendEnvironment Unset() => new(null);

        public void Dispose() =>
            Environment.SetEnvironmentVariable(CaptureBackendSelector.WgcEnvVar, _previous);
    }
}

public sealed class WindowIdParserTests
{
    [Fact]
    public void TryParse_ValidWindowId_ReturnsHwnd()
    {
        Assert.True(WindowIdParser.TryParse("window_123456", out var hwnd));
        Assert.Equal(123456, hwnd.ToInt64());
    }

    [Fact]
    public void TryParse_InvalidInputsReturnFalse()
    {
        foreach (var value in new[] { null, "", "   ", "123456", "display_123456", "window_abc" })
            Assert.False(WindowIdParser.TryParse(value, out _));
    }

    [Fact]
    public void Parse_RejectsZeroAndMalformedIds()
    {
        var zero = Assert.Throws<ApiException>(() => WindowIdParser.Parse("window_0"));
        var malformed = Assert.Throws<ApiException>(() => WindowIdParser.Parse("window_abc"));

        Assert.Equal("INVALID_ARGUMENT", zero.Code);
        Assert.Equal("INVALID_ARGUMENT", malformed.Code);
    }

    [Fact]
    public void RejectMinimized_ThrowsSourceUnavailable()
    {
        var ex = Assert.Throws<ApiException>(() =>
            WindowIdParser.RejectMinimized(isMinimized: true, "Notepad"));

        Assert.Equal(403, ex.Status);
        Assert.Equal("SOURCE_UNAVAILABLE", ex.Code);
    }
}
