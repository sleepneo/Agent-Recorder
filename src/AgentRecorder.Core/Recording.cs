using System;
using System.Collections.Generic;
using System.Threading;
using AgentRecorder.Capture;
namespace AgentRecorder.Core;
public sealed class Recording
{
    public string Id { get; } = "rec_" + Guid.NewGuid().ToString("N")[..12];
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
    /// Why the recording ended. Populated by explicit Stop(...) and natural exit finalize.
    /// Known values: duration_reached, user_requested, floating_button, tray_menu, global_hotkey,
    /// process_exit, application_exit, service_exit, and caller-supplied reasons.
    /// </summary>
    public string? StopReason { get; set; }

    /// <summary>
    /// Guards FinalizeRecording so a recording can only be terminalized once,
    /// even if the backend's natural-exit callback races with an explicit Stop(...).
    /// </summary>
    public bool IsFinalized { get; set; }

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
