using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;

namespace AgentRecorder.Api;

public sealed partial class ApiServer
{
    private const string RetentionReconciliationEndpoint = Prefix + "/retention/reconciliation";
    private ManagedVideoReconciliation? _retentionReconciliation;

    internal void ConfigureRetentionReconciliation(SqliteOperationalStore store, string trustedDataDirectory) =>
        SetRetentionReconciliation(new ManagedVideoReconciliation(store, trustedDataDirectory));

    internal void SetRetentionReconciliation(ManagedVideoReconciliation reconciliation)
    {
        lock (_lifecycleGate)
        {
            if (_started != 0 || _acceptingStopped != 0 || _retentionReconciliation is not null)
                throw new InvalidOperationException("Configure managed video reconciliation before starting the API.");
            _retentionReconciliation = reconciliation;
        }
    }

    private static bool IsRetentionReconciliationPath(string path) => path == RetentionReconciliationEndpoint ||
        path.StartsWith(RetentionReconciliationEndpoint + "?", StringComparison.Ordinal);

    private bool RetentionReconciliationSupported => _retentionReconciliation is not null && RetentionReadHostReady;

    private sealed class RetentionReconciliationRequestException() : ApiException(400, "INVALID_ARGUMENT",
        "Managed video reconciliation accepts no query parameters or request body.");

    private static ApiException RetentionReconciliationUnavailable(string reason) =>
        new(503, "RETENTION_RECONCILIATION_UNAVAILABLE", "Managed video reconciliation is unavailable.",
            new { reason_code = reason });

    private async Task<string> ReadRetentionReconciliationAsync(HttpRequest request, string requestId, CancellationToken cancellation)
    {
        if (request.Method != "GET") throw new ApiException(405, "METHOD_NOT_ALLOWED", "Use GET.");
        if (request.Query.Count != 0 || request.Body.Length != 0) throw new RetentionReconciliationRequestException();
        if (!RetentionReconciliationSupported) throw RetentionReconciliationUnavailable("not_ready");
        var sample = await _retentionReconciliation!.ReadAsync(cancellation).ConfigureAwait(false);
        if (sample.UnavailableReason is { } reason) throw RetentionReconciliationUnavailable(reason);

        return ApiResponse.Ok(new
        {
            scope = ManagedVideoUsage.Scope,
            root_state = sample.RootState,
            matched_files = sample.MatchedFiles,
            files_without_matching_registration = sample.FilesWithoutMatchingRegistration,
            missing_registrations = sample.MissingRegistrations,
            uncertain_candidates = sample.UncertainCandidates,
            sampled_at_utc = sample.SampledAtUtc,
            complete = sample.ReasonCodes.Length == 0,
            reason_codes = sample.ReasonCodes
        }, requestId);
    }

    private object RetentionReconciliationCapability() => new
    {
        supported = RetentionReconciliationSupported,
        endpoint = RetentionReconciliationEndpoint,
        scope = ManagedVideoUsage.Scope,
        read_only = true,
        history_limit = ManagedVideoUsage.HistoryLimit,
        entries_per_pass_limit = WindowsManagedVideoInventory.EntryLimit,
        ancestor_limit = WindowsManagedVideoInventory.AncestorLimit,
        database_deadline_ms = ManagedVideoReconciliation.DatabaseMilliseconds,
        scan_deadline_ms = WindowsManagedVideoInventory.ScanMilliseconds,
        deadline_ms = (int)ManagedVideoReconciliation.Timeout.TotalMilliseconds,
        actual_workers_max = 1
    };
}
