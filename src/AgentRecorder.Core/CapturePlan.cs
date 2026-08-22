using System;
using AgentRecorder.Capture;
using AgentRecorder.Windows;

namespace AgentRecorder.Core;

/// <summary>
/// Privacy-safe capture bounds stored in an immutable capture plan.
/// Coordinates are in the existing virtual-screen coordinate space.
/// </summary>
public sealed record CapturePlanBounds(int X, int Y, int Width, int Height);

/// <summary>
/// Immutable, non-capturing decision made before local confirmation.
/// Backend construction is deliberately separate from this model.
/// </summary>
public sealed class CapturePlan
{
    public CapturePlan(
        string requestedBackend,
        string plannedBackend,
        CaptureBackendSelectionEvidence evidence,
        string captureSemantics,
        string sourceKind,
        string? targetIdentity,
        nint windowHandle,
        CapturePlanBounds? bounds,
        string? targetDisplayIdentity = null,
        CapturePlanBounds? displayBounds = null,
        string? targetDisplayId = null,
        DisplayIdentityResolutionStatus targetDisplayIdentityStatus = DisplayIdentityResolutionStatus.Unresolved,
        AudioCaptureSourceKind audioSourceKind = AudioCaptureSourceKind.None,
        string? audioEndpointId = null,
        string? audioEndpointName = null,
        bool? audioEndpointIsDefault = null,
        string? previewSemantics = null,
        string coordinateSpace = "virtual_screen")
    {
        RequestedBackend = string.IsNullOrWhiteSpace(requestedBackend)
            ? "default"
            : requestedBackend;
        PlannedBackend = string.IsNullOrWhiteSpace(plannedBackend)
            ? throw new ArgumentException("Planned backend is required.", nameof(plannedBackend))
            : plannedBackend;
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        CaptureSemantics = string.IsNullOrWhiteSpace(captureSemantics)
            ? throw new ArgumentException("Capture semantics are required.", nameof(captureSemantics))
            : captureSemantics;
        PreviewSemantics = string.IsNullOrWhiteSpace(previewSemantics)
            ? CaptureSemantics
            : previewSemantics;
        CoordinateSpace = string.IsNullOrWhiteSpace(coordinateSpace)
            ? throw new ArgumentException("Coordinate space is required.", nameof(coordinateSpace))
            : coordinateSpace;
        SourceKind = string.IsNullOrWhiteSpace(sourceKind)
            ? throw new ArgumentException("Source kind is required.", nameof(sourceKind))
            : sourceKind;
        TargetIdentity = string.IsNullOrWhiteSpace(targetIdentity) ? null : targetIdentity;
        WindowHandle = windowHandle;
        Bounds = bounds;
        TargetDisplayIdentity = string.IsNullOrWhiteSpace(targetDisplayIdentity) ? null : targetDisplayIdentity;
        DisplayBounds = displayBounds;
        TargetDisplayId = string.IsNullOrWhiteSpace(targetDisplayId) ? null : targetDisplayId;
        TargetDisplayIdentityStatus = TargetDisplayIdentity == null
            ? DisplayIdentityResolutionStatus.Unresolved
            : targetDisplayIdentityStatus == DisplayIdentityResolutionStatus.Unresolved
                ? DisplayIdentityResolutionStatus.Resolved
                : targetDisplayIdentityStatus;
        FallbackOccurred = evidence.Fallback;
        AudioSourceKind = audioSourceKind;
        AudioEndpointId = string.IsNullOrWhiteSpace(audioEndpointId) ? null : audioEndpointId;
        AudioEndpointName = string.IsNullOrWhiteSpace(audioEndpointName) ? null : audioEndpointName;
        AudioEndpointIsDefault = audioEndpointIsDefault;
    }

    public string RequestedBackend { get; }
    public string PlannedBackend { get; }
    public CaptureBackendSelectionEvidence Evidence { get; }
    public string CaptureSemantics { get; }
    public string PreviewSemantics { get; }
    public string CoordinateSpace { get; }
    public string SourceKind { get; }
    public string? TargetIdentity { get; }
    public nint WindowHandle { get; }
    public CapturePlanBounds? Bounds { get; }
    /// <summary>
    /// Internal stable display fingerprint used only for region approval
    /// binding and revalidation. Never use this as the public display ID.
    /// </summary>
    public string? TargetDisplayIdentity { get; }
    public DisplayIdentityResolutionStatus TargetDisplayIdentityStatus { get; }
    /// <summary>Public ordinal displayed in confirmation/API summaries.</summary>
    public string? TargetDisplayId { get; }
    public CapturePlanBounds? DisplayBounds { get; }
    public bool FallbackOccurred { get; }
    public AudioCaptureSourceKind AudioSourceKind { get; }
    public string? AudioEndpointId { get; }
    public string? AudioEndpointName { get; }
    public bool? AudioEndpointIsDefault { get; }

    public bool IsWindowSurface => string.Equals(CaptureSemantics, "window_surface", StringComparison.Ordinal);
}
