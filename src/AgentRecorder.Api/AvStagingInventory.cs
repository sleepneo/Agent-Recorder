using System.Diagnostics;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Api;

internal sealed class AvStagingInventory
{
    internal const string Scope = "av_split_staging";
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private static readonly Admission HostAdmission = new();
    private readonly string _root;
    private readonly ProcessStorageCapacityProvider _provider;
    private readonly Admission _admission;
    private bool _stopped;

    internal AvStagingInventory(string trustedDataDirectory, ProcessStorageCapacityProvider? provider = null,
        Admission? admission = null)
    {
        _root = Path.GetFullPath(Path.Combine(trustedDataDirectory, "temp"));
        _provider = provider ?? new ProcessStorageCapacityProvider();
        _admission = admission ?? HostAdmission;
    }

    internal async Task<AvStagingInventorySample> ReadAsync(CancellationToken cancellation)
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
            // Launch can block before its first await. The request only waits for a bounded result.
            _ = Task.Run(() => ScanAsync(flight));
        }

        AvStagingInventoryObservation result;
        try
        {
            var remaining = Timeout - Stopwatch.GetElapsedTime(flight.Started);
            result = await flight.Result.Task.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancellation)
                .ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(flight.Started) >= Timeout) throw new TimeoutException();
        }
        catch (TimeoutException) { flight.RequestStop(); throw Unavailable("timeout"); }
        catch (OperationCanceledException) { flight.RequestStop(); throw Unavailable("cancelled"); }
        return result.Sample ?? throw Unavailable(result.FailureReason ?? "probe_failed");
    }

    private async Task ScanAsync(Flight flight)
    {
        Task<AvStagingInventoryObservation>? query = null;
        AvStagingInventoryObservation? result = null;
        bool retired = false;
        try
        {
            if (flight.Token.IsCancellationRequested || Stopwatch.GetElapsedTime(flight.Started) >= Timeout)
            {
                retired = true;
                return;
            }
            query = _provider.QueryAvStagingAsync(_root, flight.Token);
            result = await query.ConfigureAwait(false);
            retired = true; // The provider returns only after the child and its pipes retire.
        }
        catch (Exception error) { retired = _provider.HasCleanRetirementEvidence(error, query, allowConfirmedNoStart: true); }
        finally
        {
            retired &= await flight.RetireCancellationAsync().ConfigureAwait(false);
            lock (_admission.Gate)
            {
                if (retired) _admission.Active = null;
                flight.Retired.TrySetResult(retired);
                flight.Result.TrySetResult(retired && !_stopped && result is not null ? result :
                    new(null, !retired ? "probe_retirement_unknown" : _stopped ? "not_ready" : "probe_failed"));
            }
        }
    }

    internal string? ShutdownBusyReason
    {
        get
        {
            lock (_admission.Gate)
                return _admission.Active is not { } active ? null : active.Retired.Task.IsCompleted ?
                    "staging_inventory_retirement_unknown" : "staging_inventory_pending";
        }
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

    internal static ApiException Unavailable(string reason) => new(503, "RETENTION_STAGING_UNAVAILABLE",
        "A/V staging inventory is unavailable.", new { reason_code = reason });

    internal sealed class Admission
    {
        internal readonly object Gate = new();
        internal Flight? Active;
    }

    internal sealed class Flight(AvStagingInventory owner)
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly object _stopGate = new();
        private Task? _stopRequest;
        private bool _closing;
        internal readonly AvStagingInventory Owner = owner;
        internal readonly long Started = Stopwatch.GetTimestamp();
        internal readonly TaskCompletionSource<bool> Retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<AvStagingInventoryObservation> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token => _stop.Token;

        internal void RequestStop()
        {
            lock (_stopGate)
                if (!_closing) _stopRequest ??= _stop.CancelAsync();
        }

        internal async Task<bool> RetireCancellationAsync()
        {
            Task? request;
            lock (_stopGate) { _closing = true; request = _stopRequest; }
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
