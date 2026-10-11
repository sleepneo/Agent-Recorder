namespace AgentRecorder.Capture;

/// <summary>Immutable generation-time lengths for the five supported outputs, not physical identity.</summary>
public sealed class RecordingBundleContentsEvidence : IEquatable<RecordingBundleContentsEvidence>
{
    public const int EvidenceVersion = 1;
    private static readonly (string Name, string MediaType)[] Expected = {
        ("metadata.json", "application/json"), ("thumbnail.jpg", "image/jpeg"),
        ("first_frame.png", "image/png"), ("last_frame.png", "image/png"), ("marks.json", "application/json") };
    public IReadOnlyList<RecordingBundleContentItem> Contents { get; }

    public RecordingBundleContentsEvidence(IEnumerable<RecordingBundleContentItem> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        // Never enumerate an unbounded producer collection. Six items already violate the contract.
        var items = contents.Take(6).ToArray();
        if (items.Length != Expected.Length) throw new ArgumentException("Exactly five bundle outputs are required.", nameof(contents));
        var copy = new RecordingBundleContentItem[Expected.Length];
        for (var i = 0; i < Expected.Length; i++)
        {
            var matches = items.Where(item => item is not null && item.Name == Expected[i].Name).ToArray();
            if (matches.Length != 1 || matches[0].MediaType != Expected[i].MediaType || matches[0].SizeBytes <= 0)
                throw new ArgumentException("Bundle output evidence is invalid.", nameof(contents));
            copy[i] = new(Expected[i].Name, Expected[i].MediaType, matches[0].SizeBytes);
        }
        Contents = Array.AsReadOnly(copy);
    }

    public bool Equals(RecordingBundleContentsEvidence? other) => other is not null && Contents.Count == other.Contents.Count &&
        Contents.Zip(other.Contents).All(pair => pair.First.Name == pair.Second.Name &&
            pair.First.MediaType == pair.Second.MediaType && pair.First.SizeBytes == pair.Second.SizeBytes);
    public override bool Equals(object? other) => other is RecordingBundleContentsEvidence evidence && Equals(evidence);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in Contents) { hash.Add(item.Name, StringComparer.Ordinal); hash.Add(item.MediaType, StringComparer.Ordinal); hash.Add(item.SizeBytes); }
        return hash.ToHashCode();
    }
}
