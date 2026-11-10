using System.Diagnostics;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Api;

internal sealed class ManagedVideoVolumeCapacity
{
    internal const string Scope = "managed_videos_volume";
    private static readonly Admission HostAdmission = new();
    private readonly ProcessStorageCapacityProvider _provider;
    private readonly Admission _admission;
    private readonly string _videosRoot;
    private bool _stopped;

    internal ManagedVideoVolumeCapacity(string dataDirectory, ProcessStorageCapacityProvider? provider = null,
        Admission? admission = null)
    {
        _videosRoot = Path.GetFullPath(Path.Combine(dataDirectory, "Videos"));
        _provider = provider ?? new ProcessStorageCapacityProvider();
        _admission = admission ?? HostAdmission;
    }

    internal sealed record Sample(long AvailableBytes, DateTimeOffset SampledAtUtc);

    internal string? ShutdownBusyReason
    {
        get
        {
            lock (_admission.Gate)
            {
                var active = _admission.Active;
                if (active is null) return null;
                return active.Retired.Task.IsCompleted ? "storage_capacity_probe_retirement_unknown" :
                    "storage_capacity_probe_pending";
            }
        }
    }

    internal async Task<Sample> ReadAsync(CancellationToken cancellation)
    {
        Flight flight;
        lock (_admission.Gate)
        {
            if (cancellation.IsCancellationRequested) throw Unavailable("cancelled");
            if (_stopped) throw Unavailable("not_ready");
            if (_admission.Active is { } active)
                throw Unavailable(active.Retired.Task.IsCompleted ? "probe_retirement_unknown" : "probe_busy");
            flight = new Flight(this);
            _admission.Active = flight;
            // Process.Start and pipe setup can block before the provider first awaits.
            _ = Task.Run(() => ProbeAsync(flight));
        }

        try
        {
            var remaining = WindowStorageSafety.DefaultQueryTimeout - Stopwatch.GetElapsedTime(flight.StartedAt);
            var sample = await flight.Result.Task.WaitAsync(
                remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(flight.StartedAt) >= WindowStorageSafety.DefaultQueryTimeout)
                throw new TimeoutException();
            return sample ?? throw Unavailable("sample_unavailable");
        }
        catch (TimeoutException)
        {
            flight.RequestStop();
            throw Unavailable("timeout");
        }
        catch (OperationCanceledException)
        {
            flight.RequestStop();
            throw Unavailable("cancelled");
        }
    }

    private async Task ProbeAsync(Flight flight)
    {
        Task<IReadOnlyList<StorageVolumeSample>>? query = null;
        Sample? sample = null;
        var retired = false;
        try
        {
            if (flight.Token.IsCancellationRequested ||
                Stopwatch.GetElapsedTime(flight.StartedAt) >= WindowStorageSafety.DefaultQueryTimeout)
            {
                retired = true; // This worker has made no provider call.
                return;
            }
            query = _provider.QuerySingleVolumeAsync(_videosRoot, flight.Token);
            var samples = await query.ConfigureAwait(false);
            retired = true; // A successful provider task returns after exact cleanup.
            if (!flight.Token.IsCancellationRequested && samples.Count == 1 &&
                samples[0] is { AvailableBytes: >= 0 } observation &&
                string.Equals(observation.Path, _videosRoot, StringComparison.OrdinalIgnoreCase) &&
                IsVolumeGuid(observation.VolumeIdentity))
                sample = new Sample(observation.AvailableBytes, DateTimeOffset.UtcNow);
        }
        catch (Exception error)
        {
            retired = _provider.HasCleanRetirementEvidence(error, query, allowConfirmedNoStart: true);
        }
        finally
        {
            retired &= await flight.RetireCancellationAsync().ConfigureAwait(false);
            lock (_admission.Gate)
            {
                if (retired) _admission.Active = null;
                flight.Retired.TrySetResult(retired);
                flight.Result.TrySetResult(retired && !_stopped ? sample : null);
            }
        }
    }

    private static bool IsVolumeGuid(string? identity)
    {
        const string prefix = @"\\?\Volume{";
        return identity is not null && identity.Length == prefix.Length + 36 + 2 &&
            identity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            identity.EndsWith(@"}\", StringComparison.Ordinal) &&
            Guid.TryParseExact(identity.AsSpan(prefix.Length, 36), "D", out _);
    }

    internal void Stop()
    {
        Flight? owned;
        lock (_admission.Gate)
        {
            _stopped = true;
            owned = _admission.Active?.Owner == this ? _admission.Active : null;
        }
        owned?.RequestStop();
    }

    internal Task<bool> WaitForIdleAsync()
    {
        lock (_admission.Gate) return _admission.Active?.Retired.Task ?? Task.FromResult(true);
    }

    internal static ApiException Unavailable(string reason) =>
        new(503, "RETENTION_STORAGE_UNAVAILABLE", "Managed video volume capacity is unavailable.",
            new { reason_code = reason });

    internal sealed class Admission
    {
        internal readonly object Gate = new();
        internal Flight? Active;
    }

    internal sealed class Flight(ManagedVideoVolumeCapacity owner)
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly object _stopGate = new();
        private Task? _stopRequest;
        private bool _closing;
        internal readonly ManagedVideoVolumeCapacity Owner = owner;
        internal readonly long StartedAt = Stopwatch.GetTimestamp();
        internal readonly TaskCompletionSource<bool> Retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<Sample?> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token => _stop.Token;

        internal void RequestStop()
        {
            lock (_stopGate)
            {
                if (!_closing) _stopRequest ??= _stop.CancelAsync();
            }
        }

        internal async Task<bool> RetireCancellationAsync()
        {
            Task? request;
            lock (_stopGate)
            {
                _closing = true;
                request = _stopRequest;
            }
            try
            {
                if (request is not null) await request.ConfigureAwait(false);
                return true;
            }
            catch { return false; }
            finally { _stop.Dispose(); }
        }
    }
}
