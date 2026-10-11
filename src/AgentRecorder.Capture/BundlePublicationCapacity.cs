namespace AgentRecorder.Capture;

// Bundle generation occurs after media's guard commits/disposes. It has no admission
// media reservation to reuse. Hosted copies use the existing finite capacity child
// and low-water contract, with only the actual unwritten attachment bytes remaining.
internal static class BundlePublicationCapacity
{
    internal static Task<IPublicationCopyReservation?> BeginHostedCopyAsync(long bytes, CancellationToken token)
    {
        var entry = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        if (entry is not ("AgentRecorder.App" or "AgentRecorder.Headless"))
            return Task.FromResult<IPublicationCopyReservation?>(null); // Library callers have no configured host probe.
        return Task.FromResult<IPublicationCopyReservation?>(new Reservation(bytes, new ProcessStorageCapacityProvider()));
    }

    internal sealed class Reservation(long bytes, IStorageCapacityProvider provider) : IPublicationCopyReservation
    {
        private long _remaining = bytes;
        private string? _directory;
        private bool _disposed;
        internal long Remaining => _remaining;
        internal void BindDirectory(string directory) => _directory = directory;
        public async Task WriteAsync(FileStream destination, ReadOnlyMemory<byte> data, CancellationToken token)
        {
            if (_disposed || data.Length > _remaining) throw new IOException("bundle_copy_capacity_binding_invalid");
            _directory ??= Path.GetDirectoryName(destination.Name);
            // Native CreateNew FileStream handles have no pathname; publisher binds the directory.
            await EnsureCapacityAsync(token).ConfigureAwait(false);
            await destination.WriteAsync(data, token).ConfigureAwait(false);
            await destination.FlushAsync(token).ConfigureAwait(false);
            _remaining -= data.Length;
        }
        public async Task EnsureCapacityAsync(CancellationToken token)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Reservation));
            token.ThrowIfCancellationRequested();
            if (_directory is null) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            IReadOnlyList<StorageVolumeSample> samples;
            try { samples = await provider.QueryAsync(new[] { _directory }, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new StorageSafetyException(WindowStorageSafety.UnavailableCode); }
            if (samples.Count != 1 || samples[0].Path != _directory || string.IsNullOrWhiteSpace(samples[0].VolumeIdentity) || samples[0].AvailableBytes < 0)
                throw new StorageSafetyException(WindowStorageSafety.UnavailableCode);
            if (samples[0].AvailableBytes < checked(WindowStorageSafety.LowWaterBytes + _remaining))
                throw new StorageSafetyException(WindowStorageSafety.LowSpaceCode);
        }
        public void Dispose() { _disposed = true; _remaining = 0; }
    }
}
