namespace AgentRecorder.Capture;
public sealed class CaptureConfig
{
    public const int DefaultCountdownSeconds = 3;
    public const int MinCountdownSeconds = 0;
    public const int MaxCountdownSeconds = 10;

    public string SourceKind = "display";
    public string Mode = "video";
    public ScreenshotSeriesConfig? ScreenshotSeries;
    public bool IsScreenshotSeries => string.Equals(Mode, ScreenshotSeriesConfig.ModeName, StringComparison.Ordinal);
    public (int x, int y, int w, int h) Bounds;
    /// <summary>
    /// Public ordinal selected by the request, for example <c>display_1</c>.
    /// This is not a topology-stable identity.
    /// </summary>
    public string? DisplayId;

    /// <summary>
    /// Internal fixed-format fingerprint frozen from the same active topology
    /// snapshot as <see cref="DisplayId"/> and <see cref="DisplayBounds"/>.
    /// It is never accepted from the client or exposed in API summaries.
    /// </summary>
    public string? DisplayStableIdentity;

    /// <summary>
    /// Reliability state for <see cref="DisplayStableIdentity"/>. Production
    /// region requests require <c>Resolved</c> before confirmation.
    /// </summary>
    public AgentRecorder.Windows.DisplayIdentityResolutionStatus DisplayIdentityStatus =
        AgentRecorder.Windows.DisplayIdentityResolutionStatus.Unresolved;

    /// <summary>
    /// Complete physical bounds of the selected display. Region captures keep
    /// this separate from <see cref="Bounds"/>, which is the user-confirmed
    /// region rectangle.
    /// </summary>
    public (int x, int y, int w, int h)? DisplayBounds;
    public string? WindowTitle;
    public nint WindowHandle;
    public bool Microphone;
    public string? MicDevice;
    public string? MicDeviceName;

    /// <summary>
    /// Strong-typed internal audio source. Defaults to <see cref="AudioCaptureSourceKind.None"/>
    /// which is equivalent to no audio. When set to <see cref="AudioCaptureSourceKind.Microphone"/>,
    /// the legacy <see cref="Microphone"/> and <see cref="MicDevice"/> fields are used.
    /// When set to <see cref="AudioCaptureSourceKind.SystemLoopback"/>, the
    /// <see cref="SystemLoopbackEndpoint"/> field is used instead.
    /// </summary>
    public AudioCaptureSourceKind AudioSourceKind = AudioCaptureSourceKind.None;

    /// <summary>
    /// Exact CoreAudio render endpoint id for system loopback capture.
    /// Only used when <see cref="AudioSourceKind"/> is <see cref="AudioCaptureSourceKind.SystemLoopback"/>.
    /// Must not be empty or whitespace in that mode.
    /// </summary>
    public string? SystemLoopbackEndpoint;
    public string? SystemLoopbackEndpointName;
    /// <summary>
    /// Trusted snapshot of whether the selected render endpoint was the
    /// current eRender/eMultimedia default when the intent was resolved.
    /// </summary>
    public bool? SystemLoopbackEndpointIsDefault;

    // --- Backward-compatible normalization helpers ---

    /// <summary>
    /// Returns true when any audio source is requested (microphone or system loopback).
    /// </summary>
    public bool AudioRequested => AudioSourceKind != AudioCaptureSourceKind.None;

    /// <summary>
    /// Returns true when the effective audio source is microphone.
    /// </summary>
    public bool IsMicrophone => AudioSourceKind == AudioCaptureSourceKind.Microphone;

    /// <summary>
    /// Returns true when the effective audio source is system loopback.
    /// </summary>
    public bool IsSystemLoopback => AudioSourceKind == AudioCaptureSourceKind.SystemLoopback;

    /// <summary>
    /// Normalizes the legacy <see cref="Microphone"/> field into <see cref="AudioSourceKind"/>
    /// if <see cref="AudioSourceKind"/> is still <see cref="AudioCaptureSourceKind.None"/>.
    /// This ensures existing callers that only set <c>Microphone=true</c> continue to work
    /// without being rewritten.
    /// Call this once after all configuration has been set, before any audio worker starts.
    /// </summary>
    public void NormalizeAudioSource()
    {
        if (AudioSourceKind == AudioCaptureSourceKind.None && Microphone)
        {
            AudioSourceKind = AudioCaptureSourceKind.Microphone;
        }
    }

    /// <summary>
    /// Validates the audio source configuration. Returns null on success, or
    /// an error message describing the illegal combination. Call this before
    /// starting any audio worker.
    /// </summary>
    public string? ValidateAudioSource()
    {
        NormalizeAudioSource();

        if (AudioSourceKind == AudioCaptureSourceKind.SystemLoopback)
        {
            if (Microphone)
                return "Microphone and SystemLoopback cannot both be requested";
            if (string.IsNullOrWhiteSpace(SystemLoopbackEndpoint))
                return "SystemLoopback requires a valid SystemLoopbackEndpoint";
            if (!string.IsNullOrEmpty(MicDevice))
                return "MicDevice must not be set when using SystemLoopback";
            return null;
        }

        if (AudioSourceKind == AudioCaptureSourceKind.Microphone)
        {
            if (!string.IsNullOrEmpty(SystemLoopbackEndpoint))
                return "SystemLoopbackEndpoint must not be set when using Microphone";
            if (string.IsNullOrWhiteSpace(MicDevice))
                return "Microphone requires a valid MicDevice";
            return null;
        }

        // None: no audio validation needed
        return null;
    }

    public int Fps = 30;
    public string Quality = "medium";
    public string OutputPath = "";
    public string OutputConflictPolicy = "rename";
    public int? DurationSeconds;
    /// <summary>
    /// Normalized per-recording pre-capture countdown. The API parser owns
    /// validation; this strongly typed value is carried through backend
    /// selection and the engine state machine.
    /// </summary>
    public int CountdownSeconds = DefaultCountdownSeconds;
    public string CommandArgs = "";
    /// <summary>
    /// If non-null, indicates that bounds were normalized to even dimensions
    /// for x264/yuv420p compatibility. Value is (normalized_width, normalized_height).
    /// </summary>
    public (int w, int h)? RegionNormalizedBounds;

    /// <summary>
    /// Additive internal field (not settable from the public API): when true, a
    /// backend implementing <see cref="IDeferredCaptureStartBackend"/> prepares
    /// its capture session during Start without authorizing screen capture.
    /// RecordingEngine sets this only for the no-microphone deferred countdown
    /// path; the explicit capture start happens at countdown zero.
    /// </summary>
    public bool DeferCaptureStart;
}
