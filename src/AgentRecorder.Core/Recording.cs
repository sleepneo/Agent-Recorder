using System;
using System.Collections.Generic;
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
    public bool Microphone { get; set; }
    public string OutputPath { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int? DurationSeconds { get; set; }
    public ICaptureBackend? Backend { get; set; }
    public string? Error { get; set; }
    public CaptureConfig Config { get; set; } = new();
    public OutputMeta? LastMeta;
    public List<string> Warnings { get; } = new();
    public string? StderrExcerpt;
    public int ExitCode = -1;
    public string BackendType { get; set; } = "ffmpeg";

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
}
