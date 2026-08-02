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
    /// Monotonic timestamp (Stopwatch.GetTimestamp ticks) recorded immediately
    /// after the FFmpeg process reports a successful start. This is the video
    /// A/V alignment anchor and remains zero when process start fails.
    /// </summary>
    long LaunchAnchorTicks { get; }

    /// <summary>
    /// Diagnostic monotonic timestamp estimated from the first credible
    /// positive out_time_us progress group. This must not be used as the A/V
    /// alignment anchor because it includes unmeasured stdout delivery delay.
    /// </summary>
    long FirstFrameAnchorTicks { get; }

    /// <summary>First credible progress frame number, or null when absent.</summary>
    long? FirstProgressFrame { get; }

    /// <summary>First credible progress out_time_us, or null when absent.</summary>
    long? FirstProgressOutTimeUs { get; }

    /// <summary>
    /// Progress-derived anchor minus the launch anchor, in milliseconds. Null
    /// until both anchors exist; bounded to a finite diagnostic range.
    /// </summary>
    double? ProgressAnchorDeltaMs { get; }

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
