using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;

namespace AgentRecorder.Api;

public sealed partial class ApiServer
{
    private const string RetentionUsageEndpoint = Prefix + "/retention/usage";
    private ManagedVideoUsage? _retentionUsage;

    internal void ConfigureRetentionUsage(SqliteOperationalStore store, string trustedDataDirectory) =>
        SetRetentionUsage(new ManagedVideoUsage(store, trustedDataDirectory));

    internal void SetRetentionUsage(ManagedVideoUsage usage)
    {
        lock (_lifecycleGate)
        {
            if (_started != 0 || _acceptingStopped != 0 || _retentionUsage is not null)
                throw new InvalidOperationException("Configure managed video usage before starting the API.");
            _retentionUsage = usage;
        }
    }

    private static bool IsRetentionUsagePath(string path) => path == RetentionUsageEndpoint ||
        path.StartsWith(RetentionUsageEndpoint + "?", StringComparison.Ordinal);

    private bool RetentionUsageSupported => _retentionUsage is not null && RetentionReadHostReady;

    private sealed class RetentionUsageRequestException() : ApiException(400, "INVALID_ARGUMENT",
        "Managed video usage accepts no query parameters or request body.");

    private static ApiException RetentionUsageUnavailable(string reason) =>
        new(503, "RETENTION_USAGE_UNAVAILABLE", "Managed video usage is unavailable.", new { reason_code = reason });

    private async Task<string> ReadRetentionUsageAsync(HttpRequest request, string requestId, CancellationToken cancellation)
    {
        if (request.Method != "GET") throw new ApiException(405, "METHOD_NOT_ALLOWED", "Use GET.");
        if (request.Query.Count != 0 || request.Body.Length != 0) throw new RetentionUsageRequestException();
        if (!RetentionUsageSupported) throw RetentionUsageUnavailable("not_ready");
        var sample = await _retentionUsage!.ReadAsync(cancellation).ConfigureAwait(false);
        if (sample.UnavailableReason is { } reason) throw RetentionUsageUnavailable(reason);

        return ApiResponse.Ok(new
        {
            scope = ManagedVideoUsage.Scope,
            recorded_logical_bytes = sample.RecordedLogicalBytes,
            scoped_recordings = sample.ScopedRecordings,
            included_recordings = sample.IncludedRecordings,
            included_objects = sample.IncludedObjects,
            unknown_recordings = sample.UnknownRecordings,
            excluded_settled_recordings = sample.ExcludedSettledRecordings,
            excluded_settled_dispositions = sample.ExcludedSettledDispositions,
            sampled_at_utc = sample.SampledAtUtc,
            complete = sample.ReasonCodes.Length == 0,
            reason_codes = sample.ReasonCodes
        }, requestId);
    }

    private object RetentionUsageCapability() => new
    {
        supported = RetentionUsageSupported,
        endpoint = RetentionUsageEndpoint,
        scope = ManagedVideoUsage.Scope,
        read_only = true,
        history_limit = ManagedVideoUsage.HistoryLimit,
        ledger_limit = ManagedVideoUsage.LedgerLimit,
        receipt_validation_limit = ManagedVideoUsage.ReceiptValidationLimit,
        deadline_ms = (int)ManagedVideoUsage.Timeout.TotalMilliseconds,
        actual_workers_max = 1
    };
}
