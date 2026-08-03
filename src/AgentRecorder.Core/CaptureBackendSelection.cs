using AgentRecorder.Capture;

namespace AgentRecorder.Core;

public sealed class CaptureBackendSelectionEvidence
{
    public CaptureBackendSelectionEvidence(
        string requestedBackend,
        string selectedBackend,
        string selectionReasonCode,
        string availabilitySource,
        int? availabilityElapsedMs,
        bool fallback)
    {
        RequestedBackend = requestedBackend;
        SelectedBackend = selectedBackend;
        SelectionReasonCode = selectionReasonCode;
        AvailabilitySource = availabilitySource;
        AvailabilityElapsedMs = availabilityElapsedMs.HasValue
            ? Math.Max(0, availabilityElapsedMs.Value)
            : null;
        Fallback = fallback;
    }

    public string RequestedBackend { get; }
    public string SelectedBackend { get; }
    public string SelectionReasonCode { get; }
    public string AvailabilitySource { get; }
    public int? AvailabilityElapsedMs { get; }
    public bool Fallback { get; }
}

public sealed class CaptureBackendSelection
{
    public CaptureBackendSelection(
        ICaptureBackend backend,
        string backendType,
        CaptureBackendSelectionEvidence evidence)
    {
        Backend = backend ?? throw new ArgumentNullException(nameof(backend));
        BackendType = backendType;
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
    }

    public ICaptureBackend Backend { get; }
    public string BackendType { get; }
    public CaptureBackendSelectionEvidence Evidence { get; }

    public (ICaptureBackend Backend, string BackendType) AsTuple() =>
        (Backend, BackendType);
}
