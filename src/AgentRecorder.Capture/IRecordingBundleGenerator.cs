using System.Threading;
using System.Threading.Tasks;

namespace AgentRecorder.Capture;

/// <summary>
/// Generates a structured recording-bundle next to the main media file after
/// a successful recording. Implementations must be thread-safe and must not
/// block the caller on FFmpeg or file I/O.
/// </summary>
public interface IRecordingBundleGenerator
{
    /// <summary>
    /// Generates the bundle for <paramref name="request"/>.
    /// </summary>
    /// <returns>
    /// A <see cref="RecordingBundleGenerationResult"/> describing success or
    /// a stable failure code.
    /// </returns>
    Task<RecordingBundleGenerationResult> GenerateAsync(
        RecordingBundleRequest request,
        CancellationToken cancellationToken = default);
}
