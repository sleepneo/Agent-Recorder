namespace AgentRecorder.Capture;
public sealed class CaptureConfig
{
    public string SourceKind = "display";
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
    public int Fps = 30;
    public string Quality = "medium";
    public string OutputPath = "";
    public int? DurationSeconds;
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
