using System;
using System.Collections.Generic;
using System.Threading;
using AgentRecorder.Capture;
using AgentRecorder.Core.Automation;
namespace AgentRecorder.Core;
public sealed class Recording
{
    public string Id { get; }

    public Recording()
        : this("rec_" + Guid.NewGuid().ToString("N")[..12])
    {
    }

    internal Recording(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Recording identity is required.", nameof(id));
        Id = id;
    }

    public RecState State { get; set; } = RecState.created;
    public string? ConfirmationId { get; set; }
    public string Agent { get; set; } = "unknown";
    public string SourceType { get; set; } = "";
    public string SourceTitle { get; set; } = "";
    public string SourceApplication { get; set; } = "";
    public bool Microphone { get; set; }
    public string? MicrophoneDeviceId { get; set; }
    public string? MicrophoneDeviceName { get; set; }
    public AudioCaptureSourceKind AudioSourceKind { get; set; } = AudioCaptureSourceKind.None;
    public string? SystemAudioEndpointId { get; set; }
    public string? SystemAudioEndpointName { get; set; }
    public bool? SystemAudioEndpointIsDefault { get; set; }
    public string OutputPath { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Internal timestamp when the backend was asked to start. This is the
    /// "wall-clock" beginning of initialization (including microphone warmup),
    /// not the user-visible recording start. <see cref="StartedAtUtc"/> is set
    /// to the first-frame / credible-recording time and is what APIs expose.
    /// </summary>
    public DateTime BackendStartAtUtc { get; set; }

    /// <summary>
    /// Set immediately before the physical backend Start call. It lets the
    /// standing lifecycle owner distinguish a durable pre-start rejection
    /// (which must not call backend Stop) from a backend that was actually
    /// asked to start and then failed.
    /// </summary>
    internal bool BackendStartAttempted { get; set; }

    /// <summary>
    /// Timestamp when the countdown phase began. Used to anchor the transition
    /// from microphone-ready to screen capture start.
    /// </summary>
    public DateTime? CountdownStartedAtUtc { get; set; }

    /// <summary>
    /// Timestamp when screen capture actually ended (video worker stopped).
    /// Used to freeze elapsed time before finalization completes.
    /// </summary>
    public DateTime? CaptureEndedAtUtc { get; set; }

    /// <summary>
    /// Path to the temporary audio file captured by the audio worker before
    /// final cropping and muxing. Written to the isolated data directory.
    /// </summary>
    public string? TempAudioPath { get; set; }

    /// <summary>
    /// Path to the temporary video file captured by the video worker before
    /// final muxing. Written to the isolated data directory.
    /// </summary>
    public string? TempVideoPath { get; set; }

    /// <summary>
    /// Wall-clock anchor recorded when screen capture begins, used to crop the
    /// corresponding audio interval during finalization.
    /// </summary>
    public DateTime? AudioAnchorUtc { get; set; }

    /// <summary>
    /// Best-effort audio continuity classification for the final media.
    /// Values: not_checked, continuous, degraded.
    /// </summary>
    public string? AudioContinuityStatus { get; set; }

    public int? DurationSeconds { get; set; }
    /// <summary>
    /// Normalized countdown requested for this recording. Kept on the
    /// recording as well as <see cref="Config"/> so status and lifecycle
    /// events cannot accidentally fall back to a process-wide default.
    /// </summary>
    public int CountdownSeconds { get; set; } = CaptureConfig.DefaultCountdownSeconds;
    public ICaptureBackend? Backend { get; set; }
    public string? Error { get; set; }
    public CaptureConfig Config { get; set; } = new();
    public OutputMeta? LastMeta;
    public List<string> Warnings { get; } = new();
    public string? StderrExcerpt;
    public int ExitCode = -1;
    public string BackendType { get; set; } = "ffmpeg";
    public string Mode => Config.IsScreenshotSeries ? ScreenshotSeriesConfig.ModeName : "video";
    public ScreenshotSeriesRuntime? ScreenshotSeries { get; internal set; }
    public bool IsScreenshotSeries => Config.IsScreenshotSeries;

    private readonly object _marksLock = new();
    private readonly List<RecordingMark> _marks = new();

    /// <summary>
    /// Stopwatch tick anchor captured with the trusted first-frame transition.
    /// It is intentionally internal: it is not public recording metadata.
    /// </summary>
    internal long? MarkTimelineAnchorTicks { get; set; }

    /// <summary>
    /// Adds one accepted mark while holding the recording-local mark lock.
    /// Callers must validate recording state and timestamp before invoking this.
    /// </summary>
    internal void AddMark(RecordingMark mark)
    {
        if (mark is null) throw new ArgumentNullException(nameof(mark));
        lock (_marksLock)
        {
            _marks.Add(mark);
        }
    }

    /// <summary>
    /// Returns a detached, read-only snapshot in insertion order. The snapshot
    /// is safe to hand to asynchronous bundle generation.
    /// </summary>
    public IReadOnlyList<RecordingMark> SnapshotMarks()
    {
        lock (_marksLock)
        {
            return Array.AsReadOnly(_marks.ToArray());
        }
    }

    /// <summary>
    /// The immutable, privacy-safe decision shown to the user before approval.
    /// It is reused after approval only after non-capturing revalidation.
    /// </summary>
    public CapturePlan? ApprovedCapturePlan { get; set; }

    /// <summary>
    /// Main-process-only capture authorization. It is deliberately internal so
    /// it cannot become an API/JSON field or be supplied by a helper.
    /// </summary>
    internal CaptureAuthorizationProof? AuthorizationProof { get; set; }

    /// <summary>
    /// Marks the direct StartCaptureForTests seam. Production API requests can
    /// never opt into this synthetic authorization path.
    /// </summary>
    internal bool SyntheticAuthorizationForTests { get; set; }

    /// <summary>
    /// Marks a plan created by the direct test seam rather than supplied by a
    /// production approval flow. Such a plan has no live topology identity and
    /// must not start the production display-loss monitor.
    /// </summary>
    internal bool SyntheticCapturePlanForTests { get; set; }

    /// <summary>
    /// Why the recording ended. Populated by explicit Stop(...) and natural exit finalize.
    /// Known values: duration_reached, user_requested, floating_button, tray_menu, global_hotkey,
    /// process_exit, application_exit, service_exit, and caller-supplied reasons.
    /// </summary>
    public string? StopReason { get; set; }

    /// <summary>
    /// Internal application-owned lifecycle abort claimed by runtime
    /// supervision. It is intentionally separate from <see cref="StopReason"/>
    /// so an abort cannot be mistaken for a user-initiated stop while the
    /// backend is being terminated.
    /// </summary>
    internal CaptureAbortReason? TrustedLifecycleAbortReason { get; set; }

    /// <summary>
    /// True only for the process-local standing execution entry. This flag is
    /// never populated from an API request or a serialized recording payload.
    /// </summary>
    internal bool IsStandingLeaseExecution { get; set; }

    internal string? StandingLeaseUseId { get; set; }

    internal AuthorizedFixedRegionScope? StandingLeaseScope { get; set; }

    internal StandingLeaseCaptureSpecification? StandingLeaseSpecification { get; set; }

    internal StandingLeaseCaptureExecutionTicket? StandingLeaseExecutionTicket { get; set; }

    /// <summary>
    /// Standing lifecycle ownership is injected by the trusted App host after
    /// the start gate has produced a claimed execution ticket.
    /// </summary>
    internal Func<ICaptureBackend, IStandingLeaseCaptureLifecycleSession?>? StandingLifecycleFactory { get; set; }

    internal IStandingLeaseCaptureLifecycleSession? StandingLifecycleSession { get; set; }

    /// <summary>
    /// True only for the process-local recurring execution entry. The ticket,
    /// proof and specification remain internal and are never serialized.
    /// </summary>
    internal bool IsRecurringLeaseExecution { get; set; }

    internal RecurringLeaseUseProof? RecurringLeaseUseProof { get; set; }

    internal RecurringOccurrenceExecutionSpecification? RecurringLeaseSpecification { get; set; }

    internal RecurringLeaseCaptureExecutionTicket? RecurringLeaseExecutionTicket { get; set; }

    /// <summary>
    /// The trusted App/Persistence host supplies the durable recurring session
    /// after the engine has selected the physical backend.
    /// </summary>
    internal Func<ICaptureBackend, IRecurringLeaseCaptureLifecycleSession?>? RecurringLifecycleFactory { get; set; }

    internal IRecurringLeaseCaptureLifecycleSession? RecurringLifecycleSession { get; set; }

    // Set only after the lifecycle owner has successfully accepted the exact
    // backend. It lets the trusted engine cleanup path distinguish a session
    // that owns the backend from one that was merely constructed before
    // attachment failed.
    internal bool RecurringLifecycleAttached { get; set; }

    internal CancellationToken RecurringExecutionCancellationToken { get; set; }

    internal bool IsUnattendedLeaseExecution =>
        IsStandingLeaseExecution || IsRecurringLeaseExecution;

    /// <summary>
    /// Published only after the complete terminal snapshot has been written.
    /// Lock-free readers use this acquire read as the publication barrier;
    /// lifecycle ownership is still established by the recording-local lock.
    /// </summary>
    private int _isFinalized;

    public bool IsFinalized => Volatile.Read(ref _isFinalized) != 0;

    /// <summary>
    /// Publishes a complete terminal snapshot. Callers must already hold
    /// <c>lock (this)</c>; this method is the final publication step and does
    /// not replace the existing lock-based exactly-once ownership.
    /// </summary>
    internal bool PublishFinalized()
    {
        if (State is not (RecState.completed or RecState.failed or RecState.cancelled or RecState.rejected or RecState.expired))
            return false;

        // Interlocked.CompareExchange is an acquire/release-equivalent
        // atomic publication and keeps duplicate publication impossible even
        // if an internal caller accidentally invokes this twice.
        var published = Interlocked.CompareExchange(ref _isFinalized, 1, 0) == 0;
        if (published)
            AuthorizationProof?.Revoke();
        return published;
    }

    public string? NestedRole { get; set; }
    public string? NestedSessionId { get; set; }
    public string? ParentRecordingId { get; set; }
    public bool IsNestedParent { get; set; }

    /// <summary>
    /// Current bundle snapshot exposed to the API. Starts as pending and is
    /// atomically replaced as the bundle moves through generating/ready/failed.
    /// </summary>
    public RecordingBundleSnapshot BundleSnapshot { get; set; } = RecordingBundleSnapshot.Pending();

    /// <summary>
    /// Ensures bundle generation is started at most once, even if
    /// Stop/natural-exit races occur.
    /// </summary>
    internal int BundleGenerationStarted;
}
