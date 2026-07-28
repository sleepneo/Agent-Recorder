namespace AgentRecorder.Capture;

/// <summary>
/// Default production implementation of <see cref="IAvWorkerFactory"/>.
/// Selects the audio backend using the AGENT_RECORDER_AUDIO_BACKEND environment
/// variable: "wasapi-helper" (default) or "dshow". Unknown values fail closed.
/// </summary>
public sealed class AvWorkerFactory : IAvWorkerFactory
{
    public const string BackendEnvVarName = "AGENT_RECORDER_AUDIO_BACKEND";
    public const string WasapiBackend = "wasapi-helper";
    public const string DshowBackend = "dshow";

    private readonly IExternalProcessRunner? _runner;

    public AvWorkerFactory() : this(null) { }

    public AvWorkerFactory(IExternalProcessRunner? runner)
    {
        _runner = runner;
    }

    public IAudioCaptureWorker CreateAudioWorker()
    {
        var backend = GetBackend();
        return backend switch
        {
            DshowBackend => new AudioCaptureWorker(_runner),
            _ => new WasapiAudioCaptureWorker()
        };
    }

    public IVideoCaptureWorker CreateVideoWorker() => new VideoCaptureWorker(_runner);

    public static string GetBackend()
    {
        var value = Environment.GetEnvironmentVariable(BackendEnvVarName)?.Trim();
        if (string.IsNullOrEmpty(value))
            return WasapiBackend;

        if (value == WasapiBackend || value == DshowBackend)
            return value;

        throw new InvalidOperationException(
            $"Invalid {BackendEnvVarName} value '{value}'. " +
            $"Supported values are '{WasapiBackend}' (default) or '{DshowBackend}'.");
    }
}
