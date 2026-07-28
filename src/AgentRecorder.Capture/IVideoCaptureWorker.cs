using System;
using System.Threading.Tasks;

namespace AgentRecorder.Capture;

/// <summary>
/// Abstraction over a screen-capture process that writes a temporary video file.
/// </summary>
public interface IVideoCaptureWorker : IDisposable
{
    event Action<FirstFrameObservation>? FirstFrameObserved;
    event Action<int, string>? NaturalExit;

    string? OutputPath { get; }
    int ExitCode { get; }
    bool HasExited { get; }

    /// <summary>
    /// Monotonic timestamp (Stopwatch.GetTimestamp ticks) of the video media
    /// start estimate. This is set only from the first credible positive
    /// out_time_us progress group and remains zero while the anchor is missing.
    /// </summary>
    long FirstFrameAnchorTicks { get; }

    /// <summary>
    /// Starts capturing video.
    /// </summary>
    void Start(CaptureConfig cfg, string outputPath);

    /// <summary>
    /// Stops the worker, drains output, and returns the output metadata.
    /// </summary>
    OutputMeta Stop();

    /// <summary>
    /// Returns the captured stderr log.
    /// </summary>
    string GetStderrLog();

    /// <summary>
    /// Waits for the process to exit with the supplied timeout.
    /// Returns true if the process exited in time.
    /// </summary>
    bool WaitForExit(TimeSpan timeout);
}
