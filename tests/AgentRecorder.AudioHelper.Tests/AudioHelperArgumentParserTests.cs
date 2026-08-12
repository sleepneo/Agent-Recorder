using Xunit;

namespace AgentRecorder.AudioHelper.Tests;

public class AudioHelperArgumentParserTests
{
    [Fact]
    public void Parse_CaptureModeWithAllArgs_ReturnsOk()
    {
        var args = new[]
        {
            "--endpoint-id", "{0.0.1.00000000}.{guid}",
            "--output", "C:\\temp\\rec.wav",
            "--allowed-root", "C:\\temp",
            "--stop-signal", "C:\\temp\\stop.signal",
            "--recording-id", "rec_abc123"
        };

        var result = AudioHelperArgumentParser.Parse(args);

        Assert.True(result.Ok);
        Assert.Equal(AudioHelperMode.Capture, result.Options.Mode);
        Assert.Equal("{0.0.1.00000000}.{guid}", result.Options.EndpointId);
        Assert.Equal("C:\\temp\\rec.wav", result.Options.OutputPath);
        Assert.Equal("C:\\temp", result.Options.AllowedRoot);
        Assert.Equal("C:\\temp\\stop.signal", result.Options.StopSignalPath);
        Assert.Equal("rec_abc123", result.Options.RecordingId);
        Assert.Equal(AudioCaptureEngine.WasapiDirect, result.Options.CaptureEngine);
        Assert.Equal(AudioSourceKind.Microphone, result.Options.SourceKind);
    }

    [Theory]
    [InlineData("microphone", "Microphone")]
    [InlineData("MICROPHONE", "Microphone")]
    [InlineData("system-loopback", "SystemLoopback")]
    [InlineData("SYSTEM-LOOPBACK", "SystemLoopback")]
    public void Parse_SourceKind_NormalizesKnownValues(string value, string expectedName)
    {
        var result = AudioHelperArgumentParser.Parse(new[]
        {
            "--endpoint-id", "endpoint",
            "--output", "C:\\temp\\rec.wav",
            "--allowed-root", "C:\\temp",
            "--stop-signal", "C:\\temp\\stop.signal",
            "--recording-id", "rec_1",
            "--source-kind", value
        });

        Assert.True(result.Ok);
        Assert.Equal(expectedName, result.Options.SourceKind.ToString());
    }

    [Theory]
    [InlineData("screen")]
    [InlineData("")]
    public void Parse_SourceKind_UnknownOrEmptyReturnsError(string value)
    {
        var result = AudioHelperArgumentParser.Parse(new[] { "--source-kind", value });

        Assert.False(result.Ok);
        Assert.Contains("source", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_SourceKindMissingValueReturnsError()
    {
        var result = AudioHelperArgumentParser.Parse(new[] { "--source-kind" });

        Assert.False(result.Ok);
        Assert.Contains("Missing value", result.Error);
    }

    [Fact]
    public void Parse_LoopbackRejectsWindowsMediaCaptureAndHfpArguments()
    {
        var common = new[]
        {
            "--endpoint-id", "render",
            "--output", "C:\\temp\\rec.wav",
            "--allowed-root", "C:\\temp",
            "--stop-signal", "C:\\temp\\stop.signal",
            "--recording-id", "rec_1",
            "--source-kind", "system-loopback"
        };

        var mediaCapture = AudioHelperArgumentParser.Parse(common.Concat(new[]
        {
            "--capture-engine", "windows-mediacapture"
        }).ToArray());
        var explicitHfp = AudioHelperArgumentParser.Parse(common.Concat(new[]
        {
            "--hfp-render-endpoint-id", "render-hfp"
        }).ToArray());
        var autoHfp = AudioHelperArgumentParser.Parse(common.Concat(new[] { "--auto-hfp-pair" }).ToArray());

        Assert.False(mediaCapture.Ok);
        Assert.False(explicitHfp.Ok);
        Assert.False(autoHfp.Ok);
        Assert.Contains("system-loopback", mediaCapture.Error);
        Assert.Contains("system-loopback", explicitHfp.Error);
        Assert.Contains("system-loopback", autoHfp.Error);
    }

    [Fact]
    public void Parse_ProbeAndVersionRejectSourceKind()
    {
        var probe = AudioHelperArgumentParser.Parse(new[] { "--probe", "--source-kind", "system-loopback" });
        var version = AudioHelperArgumentParser.Parse(new[] { "--version", "--source-kind", "microphone" });

        Assert.False(probe.Ok);
        Assert.False(version.Ok);
        Assert.Contains("cannot be mixed", probe.Error);
        Assert.Contains("cannot be mixed", version.Error);
    }

    [Theory]
    [InlineData("wasapi-direct", false)]
    [InlineData("windows-mediacapture", true)]
    [InlineData("WINDOWS-MEDIACAPTURE", true)]
    public void Parse_CaptureEngine_ReturnsSelectedEngine(string value, bool expectedNative)
    {
        var result = AudioHelperArgumentParser.Parse(new[]
        {
            "--endpoint-id", "{0.0.1.00000000}.{guid}",
            "--output", "C:\\temp\\rec.wav",
            "--allowed-root", "C:\\temp",
            "--stop-signal", "C:\\temp\\stop.signal",
            "--recording-id", "rec_abc123",
            "--capture-engine", value
        });

        Assert.True(result.Ok);
        Assert.Equal(expectedNative, result.Options.CaptureEngine == AudioCaptureEngine.WindowsMediaCapture);
    }

    [Fact]
    public void Parse_UnknownCaptureEngine_ReturnsError()
    {
        var result = AudioHelperArgumentParser.Parse(new[] { "--capture-engine", "media-foundation" });

        Assert.False(result.Ok);
        Assert.Contains("Unknown capture engine", result.Error);
    }

    [Fact]
    public void Parse_VersionMode_DoesNotRequireOutput()
    {
        var result = AudioHelperArgumentParser.Parse(new[] { "--version" });

        Assert.True(result.Ok);
        Assert.Equal(AudioHelperMode.Version, result.Options.Mode);
    }

    [Fact]
    public void Parse_ProbeMode_DoesNotRequireOutput()
    {
        var result = AudioHelperArgumentParser.Parse(new[] { "--probe" });

        Assert.True(result.Ok);
        Assert.Equal(AudioHelperMode.Probe, result.Options.Mode);
    }

    [Theory]
    [InlineData("--endpoint-id")]
    [InlineData("--output")]
    [InlineData("--allowed-root")]
    [InlineData("--stop-signal")]
    [InlineData("--recording-id")]
    public void Parse_MissingValue_ReturnsError(string arg)
    {
        var result = AudioHelperArgumentParser.Parse(new[] { arg });

        Assert.False(result.Ok);
        Assert.Contains("Missing value", result.Error);
    }

    [Fact]
    public void Parse_UnknownArgument_ReturnsError()
    {
        var result = AudioHelperArgumentParser.Parse(new[] { "--unknown", "value" });

        Assert.False(result.Ok);
        Assert.Contains("Unknown argument", result.Error);
    }

    [Fact]
    public void Parse_PositionalArgument_ReturnsError()
    {
        var result = AudioHelperArgumentParser.Parse(new[] { "positional" });

        Assert.False(result.Ok);
        Assert.Contains("positional", result.Error);
    }

    [Fact]
    public void Parse_DuplicateArgument_ReturnsError()
    {
        var result = AudioHelperArgumentParser.Parse(new[] { "--endpoint-id", "a", "--endpoint-id", "b" });

        Assert.False(result.Ok);
        Assert.Contains("Duplicate", result.Error);
    }

    [Fact]
    public void Parse_ProbeAndVersion_ReturnsError()
    {
        var result = AudioHelperArgumentParser.Parse(new[] { "--probe", "--version" });

        Assert.False(result.Ok);
        Assert.Contains("cannot be used together", result.Error);
    }

    [Fact]
    public void Parse_VersionWithCaptureArgs_ReturnsError()
    {
        var result = AudioHelperArgumentParser.Parse(new[] { "--version", "--endpoint-id", "x" });

        Assert.False(result.Ok);
        Assert.Contains("cannot be mixed", result.Error);
    }

    [Fact]
    public void Parse_ProbeWithCaptureArgs_ReturnsError()
    {
        var result = AudioHelperArgumentParser.Parse(new[] { "--probe", "--output", "C:\\temp\\rec.wav" });

        Assert.False(result.Ok);
        Assert.Contains("cannot be mixed", result.Error);
    }

    [Fact]
    public void Parse_ProbeWithCaptureEngine_ReturnsError()
    {
        var result = AudioHelperArgumentParser.Parse(new[] { "--probe", "--capture-engine", "windows-mediacapture" });

        Assert.False(result.Ok);
        Assert.Contains("cannot be mixed", result.Error);
    }

    [Fact]
    public void Parse_EndpointIdWithControlChar_ReturnsError()
    {
        var result = AudioHelperArgumentParser.Parse(new[] { "--endpoint-id", "a\nb" });

        Assert.False(result.Ok);
        Assert.Contains("control characters", result.Error);
    }

    [Fact]
    public void Parse_EmptyValue_ReturnsError()
    {
        var result = AudioHelperArgumentParser.Parse(new[] { "--endpoint-id", "" });

        Assert.False(result.Ok);
        Assert.Contains("Empty value", result.Error);
    }

    [Theory]
    [InlineData("rec_valid-123.abc")]
    [InlineData("a")]
    [InlineData("123")]
    public void ValidateRecordingId_Valid_ReturnsNull(string id)
    {
        Assert.Null(AudioHelperArgumentParser.ValidateRecordingId(id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a b")]
    [InlineData("a:b")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 65 chars
    public void ValidateRecordingId_Invalid_ReturnsError(string id)
    {
        Assert.NotNull(AudioHelperArgumentParser.ValidateRecordingId(id));
    }
}
