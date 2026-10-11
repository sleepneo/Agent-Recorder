using AgentRecorder.Infrastructure;

namespace AgentRecorder.Capture;

// Private value used by the trusted writer and durable reader, never an API input.
internal sealed class BundleIdentityEvidence : IEquatable<BundleIdentityEvidence>
{
    internal const int Version = 1;
    internal const string Source = "bundle_copy_handle/v1";
    internal string RecordingId { get; }
    internal string OperationId { get; }
    internal string MediaPath { get; }
    internal string BundlePath { get; }
    internal IReadOnlyList<PublicationIdentityAnchor> Anchors { get; }
    internal BundleIdentityEvidence(string recordingId, string operationId, string mediaPath, string bundlePath,
        RecordingBundleContentsEvidence contents, IEnumerable<PublicationIdentityAnchor> anchors)
    {
        var copy = anchors.Take(6).ToArray();
        if (string.IsNullOrWhiteSpace(recordingId) || operationId.Length != 32 ||
            operationId.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')) ||
            !WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(mediaPath) ||
            bundlePath != Path.Combine(Path.GetDirectoryName(mediaPath)!, Path.GetFileNameWithoutExtension(mediaPath) + ".bundle") ||
            copy.Length != 5 || copy.Where((a, i) => a is null || !a.Valid ||
                a.Path != Path.Combine(bundlePath, contents.Contents[i].Name) || a.Length != contents.Contents[i].SizeBytes).Any())
            throw new InvalidDataException("Invalid Bundle identity binding.");
        RecordingId = recordingId; OperationId = operationId; MediaPath = mediaPath; BundlePath = bundlePath;
        Anchors = Array.AsReadOnly(copy);
    }
    public bool Equals(BundleIdentityEvidence? other) => other is not null && RecordingId == other.RecordingId &&
        OperationId == other.OperationId && MediaPath == other.MediaPath && BundlePath == other.BundlePath && Anchors.SequenceEqual(other.Anchors);
    public override bool Equals(object? other) => other is BundleIdentityEvidence evidence && Equals(evidence);
    public override int GetHashCode() { var hash = new HashCode(); hash.Add(RecordingId); hash.Add(OperationId); hash.Add(MediaPath); hash.Add(BundlePath); foreach (var a in Anchors) hash.Add(a); return hash.ToHashCode(); }
    public override string ToString() => "BundleIdentityEvidence(private)";
}
