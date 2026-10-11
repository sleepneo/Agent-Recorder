using AgentRecorder.Core;

namespace AgentRecorder.Api;

/// <summary>
/// Shared final host sequence for ordinary recording history: stop admission,
/// settle Engine winners and in-flight durable registrations, then close the
/// API clients and history writer. A timeout deliberately leaves the writer
/// open so restart recovery can identify unfinished rows.
/// </summary>
public static class HostRecordingShutdown
{
    private static readonly TimeSpan ApiClientDrainTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PostClientRegistrationDrainTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HistoryWriterDrainTimeout = TimeSpan.FromSeconds(2);

    public static bool Stop(ApiServer server, RecordingEngine engine, IDisposable history, string reason,
        Action<string, object>? audit = null) =>
        Stop(server, engine, history, reason, ApiClientDrainTimeout, PostClientRegistrationDrainTimeout,
            HistoryWriterDrainTimeout, audit);

    internal static bool Stop(ApiServer server, RecordingEngine engine, IDisposable history, string reason,
        TimeSpan apiClientDrainTimeout, TimeSpan postClientRegistrationDrainTimeout, TimeSpan historyWriterDrainTimeout,
        Action<string, object>? audit = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(history);

        server.StopAccepting();
        engine.BeginHostShutdown(reason);
        var engineDrained = engine.ShutdownForHost(reason);
        var clientsDrained = server.Stop(apiClientDrainTimeout);
        var registrationsDrained = engineDrained ||
            engine.WaitForHostHistoryRegistrations(postClientRegistrationDrainTimeout);
        if (!clientsDrained || !registrationsDrained)
        {
            try
            {
                audit?.Invoke("service.recording_history_shutdown_incomplete", new
                {
                    reason,
                    api_clients_drained = clientsDrained,
                    history_registrations_drained = registrationsDrained,
                    writer_closed = false,
                    restart_recovery_required = true
                });
            }
            catch { }
            return false;
        }

        var historyWriterDrained = history is IOrdinaryRecordingHistoryDrainable drainable
            ? drainable.DrainAndDispose(historyWriterDrainTimeout)
            : DisposeAndSucceed(history);
        if (!historyWriterDrained)
        {
            try
            {
                audit?.Invoke("service.recording_history_shutdown_incomplete", new
                {
                    reason,
                    api_clients_drained = clientsDrained,
                    history_registrations_drained = registrationsDrained,
                    history_writer_drained = false,
                    writer_accepting_updates = false,
                    writer_worker_may_still_be_running = true,
                    restart_recovery_required = true
                });
            }
            catch { }
            return false;
        }
        return true;
    }

    private static bool DisposeAndSucceed(IDisposable value)
    {
        value.Dispose();
        return true;
    }
}
