using System;
using System.Threading.Tasks;

namespace AgentRecorder.Capture;

/// <summary>
/// Abstraction over a microphone capture process that writes a temporary WAV file.
/// </summary>
public interface IAudioCaptureWorker : IDisposable
{
    event Action? AudioReady;
    event Action<int, string>? NaturalExit;

    /// <summary>
    /// Wall-clock time when the worker first reported readiness. This is the
    /// notification time, NOT the media timestamp of the first audio sample.
    /// </summary>
    DateTime? ReadyAtUtc { get; }

    /// <summary>
    /// Best-effort monotonic-clock estimate (Stopwatch.GetTimestamp ticks) of
    /// the first audio sample's media time zero. Derived from the first progress
    /// event's out_time_us and the monotonic time at which it was observed.
    /// </summary>
    long MediaStartAnchorTicks { get; }

    string? OutputPath { get; }
    int ExitCode { get; }
    bool HasExited { get; }
    bool IsAudioReady { get; }

    /// <summary>
    /// Best-effort wall-clock timestamp (UTC milliseconds since epoch) at which
    /// the microphone endpoint transitioned from active to inactive/unavailable.
    /// </summary>
    long RuntimeAudioLostAtMs { get; }

    /// <summary>
    /// Starts capturing audio for the configured microphone.
    /// </summary>
    void Start(CaptureConfig cfg, string outputPath);

    /// <summary>
    /// Stops the worker and blocks until it has exited or the timeout is reached.
    /// </summary>
    void Stop();

    /// <summary>
    /// Returns the captured stderr log.
    /// </summary>
    string GetStderrLog();

    /// <summary>
    /// Waits for the process to exit with the supplied timeout.
    /// Returns true if the process exited in time.
    /// </summary>
    bool WaitForExit(TimeSpan timeout);

    /// <summary>
    /// Sets the provider used for runtime microphone status monitoring.
    /// May be null when monitoring is unavailable.
    /// </summary>
    void SetMicrophoneStatusProvider(IMicrophoneStatusProvider? provider);
}
