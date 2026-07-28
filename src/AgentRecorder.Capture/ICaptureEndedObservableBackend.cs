using System;

namespace AgentRecorder.Capture;

/// <summary>
/// Optional capability for backends that can distinguish "screen capture has ended"
/// from "the final output file is ready". Used to drive the finalizing lifecycle
/// state so the UI stops showing REC before muxing/probing/bundle generation.
/// </summary>
public interface ICaptureEndedObservableBackend
{
    /// <summary>
    /// Raised once when the actual screen capture (e.g. the gdigrab video worker)
    /// has stopped. Raised BEFORE finalization/muxing completes.
    /// </summary>
    event Action<CaptureEndedObservation>? CaptureEnded;
}

/// <summary>
/// Best-effort observation emitted when screen capture ends.
/// </summary>
public sealed class CaptureEndedObservation
{
    /// <summary>
    /// Wall-clock timestamp when the video worker process exited or was stopped.
    /// </summary>
    public DateTime EndedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Exit code of the video worker, when available.
    /// </summary>
    public int ExitCode { get; init; }

    /// <summary>
    /// Optional classification: "natural" (duration reached) or "manual" (Stop called).
    /// </summary>
    public string Reason { get; init; } = "";
}
