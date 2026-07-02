namespace AgentRecorder.Capture;
public sealed class CaptureConfig
{
    public string SourceKind = "display";
    public (int x, int y, int w, int h) Bounds;
    public string? WindowTitle;
    public nint WindowHandle;
    public bool Microphone;
    public string? MicDevice;
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
}
