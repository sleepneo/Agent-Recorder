using System;

namespace AgentRecorder.Capture;

/// <summary>
/// Immutable input describing a completed recording for which a bundle should
/// be generated next to the main media file.
/// </summary>
public sealed class RecordingBundleRequest
{
    public string RecordingId { get; }
    public string? ConfirmationId { get; }
    public string SourceType { get; }
    public string SourceTitle { get; }
    public (int x, int y, int w, int h) SourceBounds { get; }
    public string CoordinateSpace { get; }

    public DateTime StartedAtUtc { get; }
    public DateTime CompletedAtUtc { get; }
    public int? RequestedDurationSeconds { get; }
    public double ActualDurationSeconds { get; }
    public int Fps { get; }
    public string Backend { get; }
    public string StopReason { get; }
    public bool AudioMicrophone { get; }
    public string AudioStatus { get; }
    public string? AudioDeviceId { get; }
    public long? AudioLostAtMs { get; }

    public string? NestedRole { get; }
    public string? NestedSessionId { get; }
    public string? ParentRecordingId { get; }

    public string MediaPath { get; }
    public string Container { get; }
    public string Codec { get; }
    public int Width { get; }
    public int Height { get; }

    public RecordingBundleRequest(
        string recordingId,
        string? confirmationId,
        string sourceType,
        string sourceTitle,
        (int x, int y, int w, int h) sourceBounds,
        string coordinateSpace,
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        int? requestedDurationSeconds,
        double actualDurationSeconds,
        int fps,
        string backend,
        string stopReason,
        bool audioMicrophone,
        string audioStatus,
        string? audioDeviceId,
        long? audioLostAtMs,
        string? nestedRole,
        string? nestedSessionId,
        string? parentRecordingId,
        string mediaPath,
        string container,
        string codec,
        int width,
        int height)
    {
        RecordingId = recordingId ?? throw new ArgumentNullException(nameof(recordingId));
        ConfirmationId = confirmationId;
        SourceType = sourceType ?? "";
        SourceTitle = sourceTitle ?? "";
        SourceBounds = sourceBounds;
        CoordinateSpace = coordinateSpace ?? "virtual_screen";
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        RequestedDurationSeconds = requestedDurationSeconds;
        ActualDurationSeconds = actualDurationSeconds;
        Fps = fps;
        Backend = backend ?? "";
        StopReason = stopReason ?? "";
        AudioMicrophone = audioMicrophone;
        AudioStatus = audioStatus ?? "not_requested";
        AudioDeviceId = audioDeviceId;
        AudioLostAtMs = audioLostAtMs;
        NestedRole = nestedRole;
        NestedSessionId = nestedSessionId;
        ParentRecordingId = parentRecordingId;
        MediaPath = mediaPath ?? throw new ArgumentNullException(nameof(mediaPath));
        Container = container ?? "mp4";
        Codec = codec ?? "h264";
        Width = width;
        Height = height;
    }
}
