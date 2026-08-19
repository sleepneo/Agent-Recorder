namespace AgentRecorder.Capture;

/// <summary>
/// Default production implementation of <see cref="IAvWorkerFactory"/>.
/// The AGENT_RECORDER_AUDIO_BACKEND environment variable is a microphone-only
/// preference: "wasapi-helper" (default) or "dshow". Unknown values fail
/// closed for microphone workers; system loopback always uses WASAPI and never
/// reads this preference.
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
        return CreateAudioWorker(AudioCaptureSourceKind.Microphone);
    }

    public IAudioCaptureWorker CreateAudioWorker(AudioCaptureSourceKind sourceKind)
    {
        if (sourceKind == AudioCaptureSourceKind.SystemLoopback)
        {
            // System loopback is always WASAPI. The microphone backend
            // preference, including dshow, must not change this selection.
            return new WasapiAudioCaptureWorker();
        }

        if (sourceKind != AudioCaptureSourceKind.Microphone)
            throw new InvalidOperationException($"Audio worker source '{sourceKind}' is not supported.");

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
