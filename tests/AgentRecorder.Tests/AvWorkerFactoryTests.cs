using System;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderEnvVar")]
public class AvWorkerFactoryTests : IDisposable
{
    private readonly string? _originalBackend;

    public AvWorkerFactoryTests()
    {
        _originalBackend = Environment.GetEnvironmentVariable(AvWorkerFactory.BackendEnvVarName);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AvWorkerFactory.BackendEnvVarName, _originalBackend);
    }

    [Fact]
    public void CreateAudioWorker_DefaultBackend_ReturnsWasapiWorker()
    {
        Environment.SetEnvironmentVariable(AvWorkerFactory.BackendEnvVarName, null);

        var factory = new AvWorkerFactory();
        var worker = factory.CreateAudioWorker();

        Assert.IsType<WasapiAudioCaptureWorker>(worker);
    }

    [Fact]
    public void CreateAudioWorker_ExplicitWasapiBackend_ReturnsWasapiWorker()
    {
        Environment.SetEnvironmentVariable(AvWorkerFactory.BackendEnvVarName, AvWorkerFactory.WasapiBackend);

        var factory = new AvWorkerFactory();
        var worker = factory.CreateAudioWorker();

        Assert.IsType<WasapiAudioCaptureWorker>(worker);
    }

    [Fact]
    public void CreateAudioWorker_ExplicitDshowBackend_ReturnsAudioCaptureWorker()
    {
        Environment.SetEnvironmentVariable(AvWorkerFactory.BackendEnvVarName, AvWorkerFactory.DshowBackend);

        var factory = new AvWorkerFactory();
        var worker = factory.CreateAudioWorker();

        Assert.IsType<AudioCaptureWorker>(worker);
    }

    [Theory]
    [InlineData(AvWorkerFactory.DshowBackend, AudioCaptureSourceKind.SystemLoopback, typeof(WasapiAudioCaptureWorker))]
    [InlineData(AvWorkerFactory.DshowBackend, AudioCaptureSourceKind.Microphone, typeof(AudioCaptureWorker))]
    [InlineData(AvWorkerFactory.WasapiBackend, AudioCaptureSourceKind.SystemLoopback, typeof(WasapiAudioCaptureWorker))]
    [InlineData(AvWorkerFactory.WasapiBackend, AudioCaptureSourceKind.Microphone, typeof(WasapiAudioCaptureWorker))]
    [InlineData("invalid", AudioCaptureSourceKind.SystemLoopback, typeof(WasapiAudioCaptureWorker))]
    public void CreateAudioWorker_SourceAwareSelection_MatchesMicrophonePreferenceOnly(
        string backend,
        AudioCaptureSourceKind sourceKind,
        Type expectedType)
    {
        Environment.SetEnvironmentVariable(AvWorkerFactory.BackendEnvVarName, backend);

        var worker = new AvWorkerFactory().CreateAudioWorker(sourceKind);

        Assert.IsType(expectedType, worker);
    }

    [Theory]
    [InlineData("wasapi")]
    [InlineData("wasapi-helper-extra")]
    [InlineData("dsHOW")]
    [InlineData("dshowx")]
    [InlineData("invalid")]
    public void CreateAudioWorker_MicrophoneInvalidBackend_ThrowsInvalidOperationException(string value)
    {
        Environment.SetEnvironmentVariable(AvWorkerFactory.BackendEnvVarName, value);

        var factory = new AvWorkerFactory();
        Assert.Throws<InvalidOperationException>(() => factory.CreateAudioWorker());
    }

    [Fact]
    public void GetBackend_Unset_ReturnsWasapi()
    {
        Environment.SetEnvironmentVariable(AvWorkerFactory.BackendEnvVarName, null);

        var backend = AvWorkerFactory.GetBackend();

        Assert.Equal(AvWorkerFactory.WasapiBackend, backend);
    }

    [Fact]
    public void GetBackend_Whitespace_ReturnsWasapi()
    {
        Environment.SetEnvironmentVariable(AvWorkerFactory.BackendEnvVarName, "  ");

        var backend = AvWorkerFactory.GetBackend();

        Assert.Equal(AvWorkerFactory.WasapiBackend, backend);
    }

    [Fact]
    public void GetBackend_Empty_ReturnsWasapi()
    {
        Environment.SetEnvironmentVariable(AvWorkerFactory.BackendEnvVarName, "");

        var backend = AvWorkerFactory.GetBackend();

        Assert.Equal(AvWorkerFactory.WasapiBackend, backend);
    }

    [Fact]
    public void CreateVideoWorker_AlwaysReturnsVideoCaptureWorker()
    {
        var factory = new AvWorkerFactory();
        var worker = factory.CreateVideoWorker();

        Assert.IsType<VideoCaptureWorker>(worker);
    }
}
