namespace AgentRecorder.Capture;

/// <summary>
/// Describes one file inside a published bundle directory.
/// </summary>
public sealed class RecordingBundleContentItem
{
    public string Name { get; }
    public string MediaType { get; }
    public long SizeBytes { get; }

    public RecordingBundleContentItem(string name, string mediaType, long sizeBytes)
    {
        Name = name ?? throw new System.ArgumentNullException(nameof(name));
        MediaType = mediaType ?? "";
        SizeBytes = sizeBytes;
    }
}
