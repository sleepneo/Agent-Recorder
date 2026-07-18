using System;
using System.Collections.Generic;

namespace AgentRecorder.Capture;

/// <summary>
/// Fixed-status snapshot of a recording's bundle, exposed by the API.
/// </summary>
public sealed class RecordingBundleSnapshot
{
    public const int BundleVersion = 1;

    public string Status { get; }
    public string? Path { get; }
    public IReadOnlyList<RecordingBundleContentItem> Contents { get; }
    public string? ErrorCode { get; }

    private RecordingBundleSnapshot(string status, string? path, IReadOnlyList<RecordingBundleContentItem> contents, string? errorCode)
    {
        Status = status ?? throw new ArgumentNullException(nameof(status));
        Path = path;
        Contents = contents ?? Array.Empty<RecordingBundleContentItem>();
        ErrorCode = errorCode;
    }

    public static RecordingBundleSnapshot Pending()
        => new("pending", null, Array.Empty<RecordingBundleContentItem>(), null);

    public static RecordingBundleSnapshot Generating(string bundlePath)
        => new("generating", bundlePath, Array.Empty<RecordingBundleContentItem>(), null);

    public static RecordingBundleSnapshot Ready(string bundlePath, IReadOnlyList<RecordingBundleContentItem> contents)
        => new("ready", bundlePath, contents, null);

    public static RecordingBundleSnapshot Failed(string bundlePath, string errorCode)
        => new("failed", bundlePath, Array.Empty<RecordingBundleContentItem>(), errorCode);

    public static RecordingBundleSnapshot NotApplicable()
        => new("not_applicable", null, Array.Empty<RecordingBundleContentItem>(), null);
}
