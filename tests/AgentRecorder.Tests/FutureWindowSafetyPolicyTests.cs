using AgentRecorder.Capture;
using AgentRecorder.Core;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class FutureWindowSafetyPolicyTests
{
    [Fact]
    public void AudioEndpointLossOrIdentityChangeFailsClosed()
    {
        var valid = FutureWindowAudioEndpointPolicy.Validate("endpoint-1", "Speakers",
            new SystemAudioEndpointInfo("endpoint-1", "Speakers", "render", "active", true), out var validFailure);
        Assert.NotNull(valid);
        Assert.Null(validFailure);

        Assert.Null(FutureWindowAudioEndpointPolicy.Validate("endpoint-1", "Speakers", null, out var missingFailure));
        Assert.Equal("future_window_audio_endpoint_unavailable", missingFailure);
        Assert.Null(FutureWindowAudioEndpointPolicy.Validate("endpoint-1", "Speakers",
            new SystemAudioEndpointInfo("endpoint-2", "Speakers", "render", "active", true), out var changedFailure));
        Assert.Equal("future_window_audio_endpoint_changed", changedFailure);
        Assert.Null(FutureWindowAudioEndpointPolicy.Validate("endpoint-1", "Speakers",
            new SystemAudioEndpointInfo("endpoint-1", "Speakers", "capture", "active", true), out changedFailure));
        Assert.Equal("future_window_audio_endpoint_changed", changedFailure);
        Assert.Null(FutureWindowAudioEndpointPolicy.Validate("endpoint-1", "Speakers",
            new SystemAudioEndpointInfo("endpoint-1", "Speakers", "render", "unplugged", true), out changedFailure));
        Assert.Equal("future_window_audio_endpoint_changed", changedFailure);
    }

    [Fact]
    public void FrozenOutputMustRemainAvailableAndUnoccupied()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentRecorderTests", "future-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var output = Path.Combine(directory, "approved.mp4");
            Assert.Null(FutureWindowOutputPolicy.ValidateAndProbe(directory, output));
            File.WriteAllBytes(output, new byte[] { 0x41 });
            Assert.Equal("future_window_output_environment_changed",
                FutureWindowOutputPolicy.ValidateAndProbe(directory, output));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
