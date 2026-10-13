using AgentRecorder.Infrastructure;
using System.Security.Cryptography;

namespace AgentRecorder.Capture;

// Only this nested formal publisher can construct a receipt. No signing/rebinding seam.
internal sealed class BundlePublicationReceipt
{
    internal BundleIdentityEvidence Evidence { get; }
    internal AttachmentHashes ContentHashes { get; }
    private BundlePublicationReceipt(BundleIdentityEvidence evidence, IReadOnlyList<string> values)
    { Evidence = evidence; ContentHashes = AttachmentHashes.FromStored(evidence, values); }
    public override string ToString() => "BundlePublicationReceipt(private)";

    // Issuance stays inside Publisher; reconstruction requires the fully validated durable identity set.
    internal sealed class AttachmentHashes : IEquatable<AttachmentHashes>
    {
        internal const string Algorithm = "sha256";
        internal const string Basis = "bundle_publication";
        internal BundleIdentityEvidence Identity { get; }
        internal IReadOnlyList<string> Values { get; }
        private AttachmentHashes(BundleIdentityEvidence identity, IEnumerable<string> values)
        {
            var copy = values.Take(6).ToArray();
            if (copy.Length != 5 || copy.Any(v => !BundleMediaHashEvidence.ValidValue(v)))
                throw new InvalidDataException("Invalid attachment digest set.");
            Identity = identity; Values = Array.AsReadOnly(copy);
        }
        internal static AttachmentHashes FromStored(BundleIdentityEvidence identity, IEnumerable<string> values) => new(identity, values);
        public bool Equals(AttachmentHashes? other) => other is not null && Identity.Equals(other.Identity) && Values.SequenceEqual(other.Values);
        public override bool Equals(object? other) => other is AttachmentHashes hashes && Equals(hashes);
        public override int GetHashCode() { var hash = new HashCode(); hash.Add(Identity); foreach (var value in Values) hash.Add(value); return hash.ToHashCode(); }
        public override string ToString() => "AttachmentHashes(private)";
    }

    internal sealed class Publisher
    {
        internal IWindowsFileMetadataApi IdentityApi { get; init; } = WindowsFileMetadataApi.Instance;
        internal IPublicationFileOperations Files { get; init; } = BundlePublicationFileOperations.Instance;
        internal Func<long, CancellationToken, Task<IPublicationCopyReservation?>>? BeginCopy { get; init; }
        internal Action<string, FileStream?>? StageForTests { get; init; }
        internal long CopiedBytes { get; private set; }
        internal PublicationCleanupOutcome Cleanup { get; private set; }
        internal string? PublishingDirectoryForTests { get; private set; }

        internal async Task<RecordingBundleGenerationResult> PublishAsync(RecordingBundleRequest request, string staging,
            string finalPath, RecordingBundleContentsEvidence contents, CancellationToken token)
        {
            var streams = new List<FileStream>(5);
            var observations = new List<PublicationHandleObservation?>(5);
            var hashes = new List<string>(5);
            var originalPaths = new List<string>(5);
            var operation = request.PublicationOperationId; // Freeze before any producer await.
            var originalHolds = new List<BundleOriginalAccess>(5);
            BundleDirectoryTransaction? publication = null;
            OwnedBundleDirectory? directory = null;
            var gate = new object(); var cancelled = false;
            using var registration = token.Register(() => { lock (gate) cancelled = true; });
            try
            {
                token.ThrowIfCancellationRequested(); StageForTests?.Invoke("prepare", null);
                directory = OwnedBundleDirectory.Create(finalPath + ".publish-" + Guid.NewGuid().ToString("N"));
                PublishingDirectoryForTests = directory.Path;
                using var reservation = BeginCopy is null ? null : await BeginCopy(contents.Contents.Sum(i => i.SizeBytes), token).ConfigureAwait(false);
                if (reservation is BundlePublicationCapacity.Reservation physical) physical.BindDirectory(Path.GetDirectoryName(finalPath)!);
                foreach (var item in contents.Contents)
                {
                    token.ThrowIfCancellationRequested();
                    if (reservation is not null) await reservation.EnsureCapacityAsync(token).ConfigureAwait(false);
                    await using var source = new FileStream(Path.Combine(staging, item.Name), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    if (source.Length != item.SizeBytes) throw new IOException("bundle_source_length_changed");
                    // Original copies stay outside the transaction-owned final namespace.
                    var path = finalPath + ".object-" + Guid.NewGuid().ToString("N") + "-" + item.Name;
                    var output = Files.CreateNew(path); streams.Add(output);
                    originalPaths.Add(path);
                    StageForTests?.Invoke("create", output);
                    var buffer = new byte[81920]; long copied = 0; int read;
                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                    {
                        if (checked(copied + read) > item.SizeBytes) throw new IOException("bundle_source_length_changed");
                        if (reservation is null) await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        else await reservation.WriteAsync(output, buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        digest.AppendData(buffer, 0, read); // Only bytes whose write has completed.
                        copied += read; CopiedBytes += read; StageForTests?.Invoke("copy", output);
                    }
                    if (copied != item.SizeBytes || source.Length != copied) throw new IOException("bundle_source_length_changed");
                    StageForTests?.Invoke("flush", output); token.ThrowIfCancellationRequested(); output.Flush(true);
                    // Structural checks are mandatory; optional physical evidence has a separate API seam.
                    RequireOriginal(output.SafeFileHandle, path, item.SizeBytes);
                    observations.Add(PublicationHandleObservation.Read(IdentityApi, output.SafeFileHandle, path));
                    hashes.Add(Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant());
                }
                if (reservation is not null) await reservation.EnsureCapacityAsync(token).ConfigureAwait(false);
                StageForTests?.Invoke("before_commit", null);
                var finalPaths = contents.Contents.Select(i => Path.Combine(finalPath, i.Name)).ToArray();
                for (var i = 0; i < 5; i++)
                {
                    RequireOriginal(streams[i].SafeFileHandle, originalPaths[i], contents.Contents[i].SizeBytes);
                    originalHolds.Add(BundleOriginalAccess.Hold(streams[i].SafeFileHandle));
                }
                // Every original remains held continuously; only data-write handles retire.
                foreach (var stream in streams) stream.Dispose();
                StageForTests?.Invoke("objects_held", null);
                lock (gate)
                {
                    if (cancelled || token.IsCancellationRequested) throw new OperationCanceledException(token);
                    publication = new BundleDirectoryTransaction(finalPath);
                    for (var i = 0; i < 5; i++)
                    {
                        RequireOriginal(originalHolds[i].Handle, originalPaths[i], contents.Contents[i].SizeBytes);
                        publication.Add(originalPaths[i], finalPaths[i], originalHolds[i].Handle, contents.Contents[i].SizeBytes);
                    }
                    publication.ValidateAll(finalPaths, contents);
                    StageForTests?.Invoke("namespace_prepared", null);
                    if (cancelled || token.IsCancellationRequested) throw new OperationCanceledException(token);
                    publication.Commit(); // Whole-directory no-replace linearization point.
                }
                // Cancellation after commit cannot turn a complete publication into a partial result.
                StageForTests?.Invoke("after_commit", null);
                var anchors = new List<PublicationIdentityAnchor>(5);
                for (var i = 0; i < 5; i++)
                {
                    var path = Path.Combine(finalPath, contents.Contents[i].Name);
                    RequireOriginal(originalHolds[i].Handle, originalPaths[i], contents.Contents[i].SizeBytes, 2);
                    if (observations[i] is { } before && before.Snapshot.Identity == ReadOptionalIdentity(originalHolds[i].Handle))
                        anchors.Add(new(path, before.Snapshot.Length, before.Snapshot.Identity!));
                }
                publication.ValidateAll(finalPaths, contents);
                BundlePublicationReceipt? receipt = anchors.Count == 5 && operation is not null
                    ? new(new(request.RecordingId, operation, request.MediaPath, finalPath, contents, anchors), hashes) : null;
                return RecordingBundleGenerationResult.Published(finalPath, contents, receipt);
            }
            finally
            {
                // Rollback errors are explicit, but all handles close even on that failure.
                try { publication?.Dispose(); }
                finally
                {
                    for (var i = 0; i < streams.Count; i++)
                    {
                        try
                        {
                            var outcome = i < originalHolds.Count ? originalHolds[i].DeleteOriginal() : Files.DeleteOriginal(streams[i].SafeFileHandle);
                            if (outcome is PublicationCleanupOutcome.RetainedDeleteFailed or PublicationCleanupOutcome.RetainedHandleUnavailable || Cleanup == PublicationCleanupOutcome.NotNeeded) Cleanup = outcome;
                        }
                        catch { Cleanup = PublicationCleanupOutcome.RetainedDeleteFailed; }
                    }
                    foreach (var stream in streams) stream.Dispose();
                    foreach (var hold in originalHolds) hold.Dispose();
                    if (directory is not null)
                    {
                        try
                        {
                            var outcome = directory.DeleteEmptyOriginal();
                            if (outcome != PublicationCleanupOutcome.MarkedForDeletion) Cleanup = outcome;
                        }
                        finally { directory.Dispose(); }
                    }
                }
            }
        }
        private ObservedFileIdentity? ReadOptionalIdentity(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
        {
            try { return IdentityApi.Identity(handle, out var id, out _) ? new(id.VolumeSerialNumber, id.IdLow, id.IdHigh) : null; }
            catch { return null; }
        }
        private static void RequireOriginal(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path, long length, uint links = 1)
        {
            // Basic/path/length/link validity is required even when optional FILE_ID_INFO fails.
            var api = WindowsFileMetadataApi.Instance;
            var buffer = new System.Text.StringBuilder(32768);
            if (handle.IsClosed || !api.Basic(handle, out var basic, out _) || !api.Standard(handle, out var standard, out _) ||
                !WindowsReadOnlyFileMetadataProbe.ValidAttributes(basic.Attributes) ||
                (basic.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || standard.EndOfFile != length ||
                standard.NumberOfLinks != links || standard.DeletePending != 0 || standard.Directory != 0 ||
                api.FinalPath(handle, buffer, buffer.Capacity, out _) == 0 ||
                !string.Equals(buffer.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("bundle_original_binding_changed");
        }
    }
}
