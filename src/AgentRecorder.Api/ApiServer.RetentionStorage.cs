using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Api;

public sealed partial class ApiServer
{
    private const string RetentionStorageEndpoint = Prefix + "/retention/storage";
    private ManagedVideoVolumeCapacity? _retentionStorage;

    internal void ConfigureRetentionStorage(string trustedDataDirectory) =>
        SetRetentionStorage(new ManagedVideoVolumeCapacity(trustedDataDirectory));

    internal void SetRetentionStorage(ManagedVideoVolumeCapacity storage)
    {
        lock (_lifecycleGate)
        {
            if (_started != 0 || _acceptingStopped != 0 || _retentionStorage is not null)
                throw new InvalidOperationException("Configure the managed video volume before starting the API.");
            _retentionStorage = storage;
        }
    }

    private static bool IsRetentionStoragePath(string path) => path == RetentionStorageEndpoint ||
        path.StartsWith(RetentionStorageEndpoint + "?", StringComparison.Ordinal);

    private bool RetentionStorageSupported => _retentionStorage is not null && RetentionReadHostReady;

    private bool RetentionReadHostReady => OperatingSystem.IsWindows() &&
        _started != 0 && _stopped == 0 && _acceptingStopped == 0 &&
        _readiness?.ReadObservation() is { Disposed: false, PublishedReadyFile: not null } ready &&
        (ready.Mode == "headless" || ready is { Mode: "tray", StartupInteractiveDesktopReady: true });

    private sealed class RetentionStorageRequestException() : ApiException(400, "INVALID_ARGUMENT",
        "Managed video volume capacity accepts no query parameters or request body.");

    private async Task<string> ReadRetentionStorageAsync(HttpRequest request, string requestId,
        CancellationToken cancellation)
    {
        if (request.Method != "GET") throw new ApiException(405, "METHOD_NOT_ALLOWED", "Use GET.");
        if (request.Query.Count != 0 || request.Body.Length != 0) throw new RetentionStorageRequestException();
        if (!RetentionStorageSupported) throw ManagedVideoVolumeCapacity.Unavailable("not_ready");

        var sample = await _retentionStorage!.ReadAsync(cancellation).ConfigureAwait(false);
        return ApiResponse.Ok(new
        {
            scope = ManagedVideoVolumeCapacity.Scope,
            available_bytes = sample.AvailableBytes,
            sampled_at_utc = sample.SampledAtUtc
        }, requestId);
    }

    private object RetentionStorageCapability() => new
    {
        supported = RetentionStorageSupported,
        endpoint = RetentionStorageEndpoint,
        scope = ManagedVideoVolumeCapacity.Scope,
        read_only = true,
        sample_timeout_ms = (int)WindowStorageSafety.DefaultQueryTimeout.TotalMilliseconds,
        actual_workers_max = 1
    };
}
