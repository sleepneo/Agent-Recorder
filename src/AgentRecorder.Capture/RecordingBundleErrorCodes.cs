namespace AgentRecorder.Capture;

/// <summary>
/// Stable error codes returned by <see cref="IRecordingBundleGenerator"/>.
/// These are written to audit logs and API responses; they must never contain
/// paths, arguments, stderr, or free-text exceptions.
/// </summary>
public static class RecordingBundleErrorCodes
{
    public const string AlreadyExists = "bundle_already_exists";
    public const string HashFailed = "bundle_hash_failed";
    public const string FrameExtractFailed = "bundle_frame_extract_failed";
    public const string FrameOutputInvalid = "bundle_frame_output_invalid";
    public const string MetadataWriteFailed = "bundle_metadata_write_failed";
    public const string MarksWriteFailed = "bundle_marks_write_failed";
    public const string PublishFailed = "bundle_publish_failed";
    public const string GenerationFailed = "bundle_generation_failed";
}
