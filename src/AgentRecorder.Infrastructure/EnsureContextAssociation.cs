namespace AgentRecorder.Infrastructure;

/// <summary>
/// Privacy-safe ensure-running association carried into a performance trace.
/// Contains no raw context ID, file path, or header text.
/// </summary>
public sealed class EnsureContextAssociation
{
    public string? StartupKind { get; init; }
    public long? EnsureElapsedMs { get; init; }
    public long? ServiceStartupElapsedMs { get; init; }
    public EnsureContextStatus Status { get; init; }

    public static EnsureContextAssociation FromResult(EnsureContextResult result) => new()
    {
        StartupKind = result.StartupKind,
        EnsureElapsedMs = result.EnsureElapsedMs,
        ServiceStartupElapsedMs = result.ServiceStartupElapsedMs,
        Status = result.Status
    };
}
