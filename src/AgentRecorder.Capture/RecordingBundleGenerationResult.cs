using System;

namespace AgentRecorder.Capture;

/// <summary>
/// Result of a bundle generation attempt. On success, <see cref="BundlePath"/>
/// points to the published bundle directory. On failure, <see cref="ErrorCode"/>
/// contains a stable code and <see cref="BundlePath"/> is null.
/// </summary>
public sealed class RecordingBundleGenerationResult
{
    public bool Success { get; }
    public string? BundlePath { get; }
    public string? ErrorCode { get; }
    public string? ErrorDetail { get; }

    private RecordingBundleGenerationResult(bool success, string? bundlePath, string? errorCode, string? errorDetail)
    {
        Success = success;
        BundlePath = bundlePath;
        ErrorCode = errorCode;
        ErrorDetail = errorDetail;
    }

    public static RecordingBundleGenerationResult Ready(string bundlePath)
    {
        if (string.IsNullOrWhiteSpace(bundlePath))
            throw new ArgumentException("Bundle path cannot be empty.", nameof(bundlePath));
        return new RecordingBundleGenerationResult(true, bundlePath, null, null);
    }

    public static RecordingBundleGenerationResult Failed(string errorCode, string? errorDetail = null)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
            throw new ArgumentException("Error code cannot be empty.", nameof(errorCode));
        return new RecordingBundleGenerationResult(false, null, errorCode, errorDetail);
    }
}
