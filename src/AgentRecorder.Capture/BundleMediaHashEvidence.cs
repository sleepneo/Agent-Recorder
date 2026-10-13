using System.Security.Cryptography;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Capture;

// A generation-time content observation, never a publication or deletion authority.
internal sealed record BundleMediaHashEvidence
{
    internal const string Algorithm = "sha256", Basis = "bundle_generation";
    internal string RecordingId { get; }
    internal string OperationId { get; }
    internal string MediaPath { get; }
    internal string BundlePath { get; }
    internal string Value { get; }
    internal PublicationIdentityAnchor Anchor { get; }
    private BundleMediaHashEvidence(string recordingId, string operationId, string mediaPath, string bundlePath,
        string value, PublicationIdentityAnchor anchor)
    {
        if (string.IsNullOrWhiteSpace(recordingId) || operationId.Length != 32 ||
            operationId.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')) || !ValidValue(value) ||
            !anchor.Valid || !string.Equals(mediaPath, anchor.Path, StringComparison.OrdinalIgnoreCase) ||
            bundlePath != Path.Combine(Path.GetDirectoryName(mediaPath)!, Path.GetFileNameWithoutExtension(mediaPath) + ".bundle"))
            throw new InvalidDataException("Invalid generation hash binding.");
        RecordingId = recordingId; OperationId = operationId; MediaPath = mediaPath; BundlePath = bundlePath;
        Value = value; Anchor = anchor;
    }
    internal static bool ValidValue(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    // Durable data reconstruction does not create a PublicationReceipt. The reader
    // validates this against the existing media and Bundle facts in the same snapshot.
    internal static BundleMediaHashEvidence FromStored(string id, string operation, string media, string bundle,
        string value, PublicationIdentityAnchor anchor) => new(id, operation, media, bundle, value, anchor);
    public override string ToString() => "BundleMediaHashEvidence(private)";

    internal static async Task<(string Value, long Size, BundleMediaHashEvidence? Evidence)> ReadAsync(
        RecordingBundleRequest request, IWindowsFileMetadataApi api, CancellationToken token,
        Action<FileStream>? opened = null,
        Func<FileStream, Memory<byte>, CancellationToken, ValueTask<int>>? readChunk = null)
    {
        var binding = request.MediaHashBinding; // Immutable receipt/operation binding before any await.
        token.ThrowIfCancellationRequested();
        await using var stream = new FileStream(request.MediaPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var before = PublicationHandleObservation.Read(api, stream.SafeFileHandle, request.MediaPath);
        if (binding is not null && before is not null && !Matches(before, binding.Anchor))
            throw new IOException("media_hash_original_mismatch");
        var size = stream.Length;
        if (binding is not null && size != binding.Anchor.Length) throw new IOException("media_hash_length_changed");
        opened?.Invoke(stream);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long read = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = readChunk is null ? await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)
                : await readChunk(stream, buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) break;
            if (count < 0 || count > buffer.Length || checked(read + count) > size) throw new IOException("media_hash_length_changed");
            hash.AppendData(buffer.AsSpan(0, count)); read += count;
        }
        token.ThrowIfCancellationRequested();
        if (read != size || stream.Length != size) throw new IOException("media_hash_length_changed");
        var after = PublicationHandleObservation.Read(api, stream.SafeFileHandle, request.MediaPath);
        if (before is not null && after is not null && before != after) throw new IOException("media_hash_metadata_changed");
        var value = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        // Unsupported/unavailable native evidence keeps the legacy Bundle path
        // on this very same read handle; there is no reopen-by-name fallback.
        var evidence = binding is not null && before is not null && after is not null &&
            Matches(before, binding.Anchor) && Matches(after, binding.Anchor)
            ? new BundleMediaHashEvidence(binding.RecordingId, binding.OperationId, binding.MediaPath,
                binding.BundlePath, value, binding.Anchor) : null;
        token.ThrowIfCancellationRequested();
        return (value, size, evidence);
    }
    private static bool Matches(PublicationHandleObservation observation, PublicationIdentityAnchor anchor) =>
        observation.Snapshot.Length == anchor.Length && observation.Snapshot.Identity == anchor.Identity &&
        string.Equals(observation.Path, anchor.Path, StringComparison.OrdinalIgnoreCase);
}

internal sealed record BundleMediaHashBinding
{
    internal string RecordingId { get; }
    internal string OperationId { get; }
    internal string MediaPath { get; }
    internal string BundlePath { get; }
    internal PublicationIdentityAnchor Anchor { get; }
    private BundleMediaHashBinding(RecordingBundleRequest request, PublicationReceipt receipt)
    {
        RecordingId = request.RecordingId; OperationId = request.PublicationOperationId!;
        MediaPath = request.MediaPath;
        BundlePath = Path.Combine(Path.GetDirectoryName(MediaPath)!, Path.GetFileNameWithoutExtension(MediaPath) + ".bundle");
        // The durable media row uses the frozen operation path spelling. Windows
        // receipt/native names may differ only in case; preserve the same identity.
        Anchor = new(request.MediaPath, receipt.Anchor.Length, receipt.Anchor.Identity);
    }
    internal static BundleMediaHashBinding? Bind(RecordingBundleRequest request, PublicationReceipt receipt) =>
        request.Container == "mp4" &&
        (new[] { "ffmpeg", "ffmpeg-region", "ffmpeg-window-region", "ffmpeg-av-split", "ffmpeg-region-av-split", "ffmpeg-window-region-av-split" }.Contains(request.Backend, StringComparer.OrdinalIgnoreCase) ||
            request.Backend is "wgc-continuous" or "wgc-window-av-split") &&
        request.PublicationOperationId is { Length: 32 } operation && operation.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        receipt.Anchor.Valid && string.Equals(request.MediaPath, receipt.Anchor.Path, StringComparison.OrdinalIgnoreCase)
        ? new(request, receipt) : null;
    public override string ToString() => "BundleMediaHashBinding(private)";
}
