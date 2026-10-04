using AgentRecorder.Api;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class FutureWindowOneShotApiRequestParserTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(600)]
    [InlineData(601)]
    [InlineData(1800)]
    public void CreateRequiresExplicitAudioAndPreservesExactScope(int durationSeconds)
    {
        var parsed = FutureWindowOneShotApiRequestParser.ParseCreate(
            $$"""{"executable_path":"C:\\Program Files\\Player\\player.exe","audio":{"mode":"system_loopback","endpoint_id":"{render-endpoint}"},"maximum_duration_seconds":{{durationSeconds}},"validity_seconds":3600,"output_directory":"D:\\Recordings"}""",
            "future-window-idempotency-1");

        Assert.Equal("future-window-idempotency-1", parsed.IdempotencyKey);
        Assert.Equal(@"C:\Program Files\Player\player.exe", parsed.ExecutablePath);
        Assert.Equal("{render-endpoint}", parsed.SystemAudioEndpointId);
        Assert.Equal(durationSeconds, parsed.MaximumDurationSeconds);
        Assert.Equal(3600, parsed.ValiditySeconds);
        Assert.Equal(@"D:\Recordings", parsed.OutputDirectory);
    }

    [Fact]
    public void CreateAcceptsExplicitNoAudioButRejectsMissingOrUnknownAudioAndUnknownFields()
    {
        var noAudio = FutureWindowOneShotApiRequestParser.ParseCreate(
            """{"executable_path":"C:\\Player\\player.exe","audio":{"mode":"none"},"maximum_duration_seconds":1,"validity_seconds":1,"output_directory":"D:\\Recordings"}""",
            "future-window-no-audio");
        Assert.Null(noAudio.SystemAudioEndpointId);

        AssertInvalid(() => FutureWindowOneShotApiRequestParser.ParseCreate(
            """{"executable_path":"C:\\Player\\player.exe","maximum_duration_seconds":1,"validity_seconds":1,"output_directory":"D:\\Recordings"}""",
            "future-window-missing-audio"));
        AssertInvalid(() => FutureWindowOneShotApiRequestParser.ParseCreate(
            """{"executable_path":"C:\\Player\\player.exe","audio":{"mode":"default"},"maximum_duration_seconds":1,"validity_seconds":1,"output_directory":"D:\\Recordings"}""",
            "future-window-invalid-audio"));
        AssertInvalid(() => FutureWindowOneShotApiRequestParser.ParseCreate(
            """{"executable_path":"C:\\Player\\player.exe","audio":{"mode":"none"},"maximum_duration_seconds":1,"validity_seconds":1,"output_directory":"D:\\Recordings","fallback":"screen"}""",
            "future-window-unknown-field"));
        AssertInvalid(() => FutureWindowOneShotApiRequestParser.ParseCreate(
            """{"executable_path":"C:\\Player\\player.exe","audio":{"mode":"none"},"maximum_duration_seconds":2,"validity_seconds":1,"output_directory":"D:\\Recordings"}""",
            "future-window-run-longer-than-validity"));
        foreach (var duration in new[] { "0", "1801", "1.5", "2147483648", "\"1800\"" })
        {
            AssertInvalid(() => FutureWindowOneShotApiRequestParser.ParseCreate(
                $$"""{"executable_path":"C:\\Player\\player.exe","audio":{"mode":"none"},"maximum_duration_seconds":{{duration}},"validity_seconds":3600,"output_directory":"D:\\Recordings"}""",
                "future-window-invalid-duration-" + duration));
        }
        AssertInvalid(() => FutureWindowOneShotApiRequestParser.ParseCreate(
            """{"executable_path":"C:\\Player\\player.exe","audio":{"mode":"none"},"maximum_duration_seconds":1800,"validity_seconds":1799,"output_directory":"D:\\Recordings"}""",
            "future-window-validity-too-short"));
    }

    [Fact]
    public void StartRequiresOneCanonicalWindowIdentifierAndNoExtraFields()
    {
        Assert.Equal("window_12345", FutureWindowOneShotApiRequestParser.ParseStartWindowId("""{"window_id":"window_12345"}"""));
        AssertInvalid(() => FutureWindowOneShotApiRequestParser.ParseStartWindowId("""{"window_id":"window_012345"}"""));
        AssertInvalid(() => FutureWindowOneShotApiRequestParser.ParseStartWindowId("""{"window_id":"window_12345","process_id":42}"""));
    }

    private static void AssertInvalid(Action action)
    {
        var exception = Assert.Throws<ApiException>(action);
        Assert.Equal(400, exception.Status);
        Assert.Equal("INVALID_ARGUMENT", exception.Code);
    }
}
