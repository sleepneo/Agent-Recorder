using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Api;

public sealed partial class ApiServer
{
    private const string RetentionStagingEndpoint = Prefix + "/retention/staging";
    private AvStagingInventory? _retentionStaging;

    internal void ConfigureRetentionStaging(string trustedDataDirectory) =>
        SetRetentionStaging(new AvStagingInventory(trustedDataDirectory));

    internal void SetRetentionStaging(AvStagingInventory inventory)
    {
        lock (_lifecycleGate)
        {
            if (_started != 0 || _acceptingStopped != 0 || _retentionStaging is not null)
                throw new InvalidOperationException("Configure A/V staging inventory before starting the API.");
            _retentionStaging = inventory;
        }
    }

    private static bool IsRetentionStagingPath(string path) => path == RetentionStagingEndpoint ||
        path.StartsWith(RetentionStagingEndpoint + "?", StringComparison.Ordinal);

    private bool RetentionStagingSupported => _retentionStaging is not null && RetentionReadHostReady;

    private sealed class RetentionStagingRequestException() : ApiException(400, "INVALID_ARGUMENT",
        "A/V staging inventory accepts no query parameters or request body.");

    private async Task<string> ReadRetentionStagingAsync(HttpRequest request, string requestId, CancellationToken cancellation)
    {
        if (request.Method != "GET") throw new ApiException(405, "METHOD_NOT_ALLOWED", "Use GET.");
        if (request.Query.Count != 0 || request.Body.Length != 0) throw new RetentionStagingRequestException();
        if (!RetentionStagingSupported) throw AvStagingInventory.Unavailable("not_ready");
        var sample = await _retentionStaging!.ReadAsync(cancellation).ConfigureAwait(false);
        return ApiResponse.Ok(new
        {
            scope = AvStagingInventory.Scope,
            observed_directories = sample.ObservedDirectories,
            verified_files = sample.VerifiedFiles,
            logical_bytes = sample.LogicalBytes,
            skipped_entries = sample.SkippedEntries,
            visited_entries = sample.VisitedEntries,
            sampled_at_utc = sample.SampledAtUtc,
            complete = sample.ReasonCodes.Length == 0,
            reason_codes = sample.ReasonCodes
        }, requestId);
    }

    private object RetentionStagingCapability() => new
    {
        supported = RetentionStagingSupported,
        endpoint = RetentionStagingEndpoint,
        scope = AvStagingInventory.Scope,
        read_only = true,
        entry_limit = WindowsAvStagingInventory.EntryLimit,
        sample_timeout_ms = (int)AvStagingInventory.Timeout.TotalMilliseconds,
        actual_workers_max = 1
    };
}
