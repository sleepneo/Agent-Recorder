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
    public RecordingBundleContentsEvidence? ContentsEvidence { get; }
    internal BundlePublicationReceipt? PublicationReceipt { get; private init; }
    internal BundleMediaHashEvidence? MediaHashEvidence { get; private init; }
    internal RecordingBundleGenerationResult WithMediaHash(BundleMediaHashEvidence? evidence)
    {
        var bundle = PublicationReceipt?.Evidence;
        var bound = Success && bundle is not null && evidence is not null &&
            evidence.RecordingId == bundle.RecordingId && evidence.OperationId == bundle.OperationId &&
            evidence.MediaPath == bundle.MediaPath && evidence.BundlePath == bundle.BundlePath;
        return new(Success, BundlePath, ErrorCode, ErrorDetail, ContentsEvidence)
            { PublicationReceipt = PublicationReceipt, MediaHashEvidence = bound ? evidence : null };
    }

    internal static RecordingBundleGenerationResult Published(string path, RecordingBundleContentsEvidence contents, BundlePublicationReceipt? receipt)
        => new(true, path, null, null, contents) { PublicationReceipt = receipt };

    private RecordingBundleGenerationResult(bool success, string? bundlePath, string? errorCode, string? errorDetail,
        RecordingBundleContentsEvidence? contentsEvidence = null)
    {
        Success = success;
        BundlePath = bundlePath;
        ErrorCode = errorCode;
        ErrorDetail = errorDetail;
        ContentsEvidence = contentsEvidence;
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

    public static RecordingBundleGenerationResult Ready(string bundlePath, IEnumerable<RecordingBundleContentItem> contents)
    {
        if (string.IsNullOrWhiteSpace(bundlePath)) throw new ArgumentException("Bundle path cannot be empty.", nameof(bundlePath));
        return new(true, bundlePath, null, null, new RecordingBundleContentsEvidence(contents));
    }
}
