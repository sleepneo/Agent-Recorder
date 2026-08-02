using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Logging;
using ApiException = AgentRecorder.Infrastructure.ApiException;

namespace AgentRecorder.Capture;

/// <summary>
/// Captures a single gdigrab video stream to a temporary MP4 file (no audio).
/// Exposes a first-frame signal so callers can transition to the recording state.
/// </summary>
public sealed class VideoCaptureWorker : IVideoCaptureWorker
{
    private IVideoCaptureProcess? _proc;
    private readonly StringBuilder _stderrLog = new();
    private readonly object _lock = new();
    private readonly object _anchorLock = new();
    private Task? _stdoutReader;
    private Task? _watcher;
    private TaskCompletionSource<bool>? _exitTcs;
    private int _firstFrameObserved;
    private int _firstProgressEvidenceObserved;
    private long _firstPositiveMediaAnchorObserved;
    private long _launchAnchorTicks;
    private long _firstFrameAnchorTicks;
    private long _firstProgressFrame = -1;
    private long _firstProgressOutTimeUs = -1;
    private double? _progressAnchorDeltaMs;
    private int _lifecycleGeneration;
    private bool _hasExited;
    private bool _manualStopped;
    private ManualResetEventSlim? _stderrClosed;
    private readonly IExternalProcessRunner? _runner;

    private static readonly TimeSpan StdoutDrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StderrDrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan KillDrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Test seam: when set, Start uses these arguments instead of the real
    /// gdigrab arguments. Allows unit tests to exercise lifecycle with a
    /// short-lived, non-recording FFmpeg invocation such as "-version".
    /// </summary>
    internal List<string>? TestArgumentsOverride { get; set; }

    /// <summary>Test seam for process start and lifecycle behavior.</summary>
    internal Func<ProcessStartInfo, IVideoCaptureProcess>? TestProcessFactory { get; set; }

    /// <summary>Test seam for monotonic timestamps used by anchor diagnostics.</summary>
    internal Func<long> TimestampProvider { get; set; } = Stopwatch.GetTimestamp;

    public event Action<FirstFrameObservation>? FirstFrameObserved;
    public event Action<int, string>? NaturalExit;

    public VideoCaptureWorker(IExternalProcessRunner? runner = null)
    {
        _runner = runner;
    }

    public string? OutputPath { get; private set; }
    public int ExitCode { get; private set; } = -1;
    public long LaunchAnchorTicks => Interlocked.Read(ref _launchAnchorTicks);
    public long FirstFrameAnchorTicks => Interlocked.Read(ref _firstFrameAnchorTicks);
    public long? FirstProgressFrame
    {
        get
        {
            var value = Interlocked.Read(ref _firstProgressFrame);
            return value >= 0 ? value : null;
        }
    }

    public long? FirstProgressOutTimeUs
    {
        get
        {
            var value = Interlocked.Read(ref _firstProgressOutTimeUs);
            return value >= 0 ? value : null;
        }
    }

    public double? ProgressAnchorDeltaMs
    {
        get { lock (_anchorLock) return _progressAnchorDeltaMs; }
    }

    public bool HasExited
    {
        get
        {
            lock (_lock) return _hasExited;
        }
    }

    public void Start(CaptureConfig cfg, string outputPath)
    {
        ResetForStart();
        OutputPath = outputPath;
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var args = TestArgumentsOverride ?? BuildArgs(cfg, outputPath);
        var parser = new FFmpegProgressParser();
        parser.GroupCompleted += HandleProgressGroup;

        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegLocator.FfmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };

        var proc = (TestProcessFactory ?? (info => new SystemVideoCaptureProcess(info)))(startInfo);
        _proc = proc;
        var lifecycleGeneration = Volatile.Read(ref _lifecycleGeneration);
        foreach (var a in args)
            proc.StartInfo.ArgumentList.Add(a);

        var stderrClosed = new ManualResetEventSlim();
        _stderrClosed = stderrClosed;
        var exitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _exitTcs = exitTcs;
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                stderrClosed.Set();
                return;
            }
            if (!string.IsNullOrWhiteSpace(e.Data))
                lock (_lock) _stderrLog.AppendLine(e.Data);
        };

        try
        {
            if (!proc.Start())
                throw new InvalidOperationException("The video process reported an unsuccessful start.");

            // This is deliberately taken after the successful Start() boundary,
            // before any progress output can be consumed by the worker.
            Interlocked.Exchange(ref _launchAnchorTicks, TimestampProvider());
        }
        catch (Exception ex)
        {
            stderrClosed.Set();
            exitTcs.TrySetResult(true);
            throw new ApiException(500, "ENCODER_ERROR", "Failed to launch video worker: " + ex.Message);
        }
        proc.BeginErrorReadLine();
        var stdoutReader = RunStdoutReader(proc.StandardOutput, parser);
        _stdoutReader = stdoutReader;

        int timeoutMs = (cfg.DurationSeconds.HasValue && cfg.DurationSeconds > 0)
            ? (cfg.DurationSeconds.Value + 30) * 1000
            : 4 * 3600 * 1000;

        _watcher = Task.Run(() =>
        {
            bool exited = proc.WaitForExit(timeoutMs);
            int exitCode = -1;
            try { if (exited) exitCode = proc.ExitCode; } catch { }
            if (!exited)
            {
                try { proc.Kill(true); } catch { }
                // Give the killed process tree a bounded window to actually exit
                // before publishing completion, so HasExited/ExitCode reflect the
                // real termination.
                try { exited = proc.WaitForExit(KillDrainTimeout); } catch { }
                try { if (exited) exitCode = proc.ExitCode; } catch { }
            }

            DrainTask(stdoutReader, StdoutDrainTimeout);
            if (!proc.ErrorStreamClosed)
                WaitStderrClosed(stderrClosed, StderrDrainTimeout);
            if (lifecycleGeneration != Volatile.Read(ref _lifecycleGeneration))
                return;
            string stderr;
            lock (_lock)
            {
                _hasExited = true;
                ExitCode = exitCode;
                stderr = _stderrLog.ToString();
            }
            exitTcs.TrySetResult(true);
            if (!_manualStopped)
            {
                try { NaturalExit?.Invoke(exitCode, stderr); }
                catch { }
            }
        });
    }

    public OutputMeta Stop()
    {
        lock (_lock) _manualStopped = true;
        var proc = _proc;
        if (proc != null && !proc.HasExited)
        {
            try
            {
                proc.StandardInput.Write('q');
                proc.StandardInput.Flush();
            }
            catch { }

            if (!WaitForExit(StopTimeout))
            {
                try { proc.Kill(true); } catch { }
                WaitForExit(KillDrainTimeout);
            }
        }
        else
        {
            // The process may already be gone from the OS, but we still need
            // to wait for the watcher to publish ExitCode/HasExited/drain.
            WaitForExit(KillDrainTimeout);
        }

        var meta = Probe(OutputPath ?? "");
        string stderr;
        lock (_lock) stderr = _stderrLog.ToString();
        meta.StderrLog = stderr;
        ApplyAnchorDiagnostics(meta);
        return meta;
    }

    public bool WaitForExit(TimeSpan timeout)
    {
        var tcs = _exitTcs;
        if (tcs == null) return true;
        try { return tcs.Task.Wait(timeout); }
        catch { return false; }
    }

    public string GetStderrLog()
    {
        lock (_lock) return _stderrLog.ToString();
    }

    private static List<string> BuildArgs(CaptureConfig cfg, string outputPath)
    {
        var args = new List<string>
        {
            "-y",
            "-nostats",
            "-progress", "pipe:1",
            "-f", "gdigrab",
            "-framerate", cfg.Fps.ToString(CultureInfo.InvariantCulture),
            "-thread_queue_size", "512"
        };

        var (x, y, w, h) = cfg.Bounds;
        if (cfg.SourceKind == "window" || cfg.SourceKind == "region")
        {
            args.Add("-offset_x");
            args.Add(x.ToString(CultureInfo.InvariantCulture));
            args.Add("-offset_y");
            args.Add(y.ToString(CultureInfo.InvariantCulture));
            args.Add("-video_size");
            args.Add($"{w}x{h}");
        }
        else if (w > 0 && h > 0)
        {
            args.Add("-offset_x");
            args.Add(x.ToString(CultureInfo.InvariantCulture));
            args.Add("-offset_y");
            args.Add(y.ToString(CultureInfo.InvariantCulture));
            args.Add("-video_size");
            args.Add($"{w}x{h}");
        }

        if (cfg.DurationSeconds.HasValue && cfg.DurationSeconds > 0)
        {
            args.Add("-t");
            args.Add(cfg.DurationSeconds.Value.ToString(CultureInfo.InvariantCulture));
        }

        args.Add("-i");
        args.Add("desktop");

        if (cfg.SourceKind == "display" && (w > 1920 || h > 1080))
        {
            args.Add("-vf");
            args.Add("scale=1920:1080:force_original_aspect_ratio=decrease");
        }

        args.Add("-an"); // no audio
        args.Add("-c:v");
        args.Add("libx264");
        args.Add("-preset");
        args.Add("veryfast");

        var crf = cfg.Quality switch { "high" => 18, "low" => 28, _ => 23 };
        args.Add("-crf");
        args.Add(crf.ToString(CultureInfo.InvariantCulture));

        args.Add("-pix_fmt");
        args.Add("yuv420p");
        args.Add("-threads");
        args.Add("4");
        args.Add("-movflags");
        args.Add("+faststart");
        args.Add(outputPath);

        return args;
    }

    internal void HandleProgressGroup(FFmpegProgressGroup g)
    {
        if (!g.HasFirstFrameEvidence) return;

        if (Interlocked.CompareExchange(ref _firstProgressEvidenceObserved, 1, 0) == 0)
        {
            Interlocked.Exchange(ref _firstProgressFrame, g.Frame);
            Interlocked.Exchange(ref _firstProgressOutTimeUs, g.OutTimeUs ?? -1);
        }

        var observedTicks = TimestampProvider();
        if (MediaAnchorHelper.TryEstimateMediaStartAnchor(observedTicks, g.OutTimeUs, out var anchorTicks) &&
            Interlocked.CompareExchange(ref _firstPositiveMediaAnchorObserved, 1, 0) == 0)
        {
            Interlocked.Exchange(ref _firstFrameAnchorTicks, anchorTicks);
            UpdateProgressAnchorDelta(anchorTicks);
        }

        if (Interlocked.Exchange(ref _firstFrameObserved, 1) == 0)
        {
            try
            {
                FirstFrameObserved?.Invoke(new FirstFrameObservation
                {
                    FrameNumber = g.Frame,
                    TotalSizeBytes = g.TotalSize,
                    OutTimeUs = g.OutTimeUs,
                    EvidenceKind = "video_progress"
                });
            }
            catch { }
        }
    }

    private void ResetForStart()
    {
        IVideoCaptureProcess? previousProcess;
        ManualResetEventSlim? previousStderrClosed;
        lock (_lock)
        {
            previousProcess = _proc;
            previousStderrClosed = _stderrClosed;
            _proc = null;
            _stderrClosed = null;
            _stdoutReader = null;
            _watcher = null;
            _exitTcs = null;
            _stderrLog.Clear();
            _hasExited = false;
            _manualStopped = false;
            ExitCode = -1;
        }

        try { previousProcess?.Dispose(); } catch { }
        try { previousStderrClosed?.Dispose(); } catch { }

        Interlocked.Increment(ref _lifecycleGeneration);
        Interlocked.Exchange(ref _firstFrameObserved, 0);
        Interlocked.Exchange(ref _firstProgressEvidenceObserved, 0);
        Interlocked.Exchange(ref _firstPositiveMediaAnchorObserved, 0);
        Interlocked.Exchange(ref _launchAnchorTicks, 0);
        Interlocked.Exchange(ref _firstFrameAnchorTicks, 0);
        Interlocked.Exchange(ref _firstProgressFrame, -1);
        Interlocked.Exchange(ref _firstProgressOutTimeUs, -1);
        lock (_anchorLock) _progressAnchorDeltaMs = null;
    }

    private void UpdateProgressAnchorDelta(long progressAnchorTicks)
    {
        var launchAnchorTicks = LaunchAnchorTicks;
        if (launchAnchorTicks <= 0 || progressAnchorTicks <= 0)
            return;

        var deltaMs = ((double)progressAnchorTicks - launchAnchorTicks) * 1000.0 / Stopwatch.Frequency;
        if (double.IsNaN(deltaMs) || double.IsInfinity(deltaMs))
            return;

        // Keep diagnostics bounded even if a synthetic clock or a corrupt
        // progress value produces an implausibly large signed delta.
        const double maxDeltaMs = 24 * 60 * 60 * 1000;
        lock (_anchorLock) _progressAnchorDeltaMs = Math.Clamp(deltaMs, -maxDeltaMs, maxDeltaMs);
    }

    private void ApplyAnchorDiagnostics(OutputMeta meta)
    {
        meta.VideoAnchorStatus = LaunchAnchorTicks > 0 ? "available" : "missing";
        meta.VideoLaunchAnchorTicks = LaunchAnchorTicks > 0 ? LaunchAnchorTicks : null;
        meta.VideoProgressAnchorTicks = FirstFrameAnchorTicks > 0 ? FirstFrameAnchorTicks : null;
        meta.VideoProgressAnchorDeltaMs = ProgressAnchorDeltaMs;
        meta.VideoFirstProgressFrame = FirstProgressFrame;
        meta.VideoFirstProgressOutTimeUs = FirstProgressOutTimeUs;
    }

    private static Task RunStdoutReader(TextReader reader, FFmpegProgressParser parser)
    {
        return Task.Run(() =>
        {
            try
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                    parser.FeedLine(line);
            }
            catch { }
            finally
            {
                try { parser.Flush(); }
                catch { }
            }
        });
    }

    private static void DrainTask(Task? task, TimeSpan timeout)
    {
        if (task is null) return;
        try
        {
            if (task.IsCompleted) return;
            task.Wait(timeout);
        }
        catch { }
    }

    private static void WaitStderrClosed(ManualResetEventSlim stderrClosed, TimeSpan timeout)
    {
        try
        {
            stderrClosed.Wait(timeout);
        }
        catch { }
    }

    private static OutputMeta Probe(string path)
    {
        var m = FfmpegCaptureBackend.Probe(path);
        m.Container ??= "mp4";
        m.Codec ??= "h264";
        return m;
    }

    public void Dispose()
    {
        try { _proc?.Dispose(); } catch { }
        try { _stderrClosed?.Dispose(); } catch { }
    }
}
