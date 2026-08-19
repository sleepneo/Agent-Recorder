using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using ApiException = AgentRecorder.Infrastructure.ApiException;
namespace AgentRecorder.Capture;

public sealed class FfmpegCaptureBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend, IMicrophoneStatusConsumer
{
    private Process? _proc;
    private string _output = "";
    private CaptureConfig? _cfg;
    private readonly StringBuilder _stderrLog = new();
    private readonly object _lock = new();
    private Task? _watcher;
    private Task? _stdoutReader;
    private OutputMeta? _completionMeta;
    private bool _hasExited = false;
    private bool _manualStopped = false;
    private int _firstFrameObserved;
    private long _runtimeAudioLostAtMs;
    private IMicrophoneStatusProvider? _microphoneStatusProvider;
    private CancellationTokenSource? _microphoneMonitorCts;
    private Task? _microphoneMonitorTask;

    private static readonly TimeSpan StdoutDrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MicrophoneMonitorInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MicrophoneMonitorShutdownTimeout = TimeSpan.FromSeconds(3);

    public event Action<FirstFrameObservation>? FirstFrameObserved;

    /// <summary>
    /// Injected microphone status provider used to supervise the capture
    /// endpoint while a recording is active. May be null.
    /// </summary>
    public IMicrophoneStatusProvider MicrophoneStatusProvider
    {
        set => _microphoneStatusProvider = value;
    }

    public void Start(CaptureConfig cfg)
    {
        DisplayScaleGeometry.ThrowIfInvalidCaptureBounds(cfg);
        _cfg = cfg;
        _output = cfg.OutputPath;

        var dir = Path.GetDirectoryName(_output);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var args = BuildArgs(cfg);
        // Render a safe, single-space-separated diagnostic string. This must
        // never be used as the actual command source; ArgumentList is.
        cfg.CommandArgs = RenderCommandArgs(args);

        var parser = new FFmpegProgressParser();
        parser.GroupCompleted += g =>
        {
            if (!g.HasFirstFrameEvidence)
                return;

            // Ensure exactly-once notification even if multiple progress groups qualify.
            if (Interlocked.Exchange(ref _firstFrameObserved, 1) != 0)
                return;

            try
            {
                FirstFrameObserved?.Invoke(new FirstFrameObservation
                {
                    FrameNumber = g.Frame,
                    TotalSizeBytes = g.TotalSize,
                    OutTimeUs = g.OutTimeUs
                });
            }
            catch
            {
                // Observers must not affect the recording flow.
            }
        };

        _proc = new Process
        {
            StartInfo = new ProcessStartInfo
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
            },
            EnableRaisingEvents = true
        };

        foreach (var a in args)
            _proc.StartInfo.ArgumentList.Add(a);

        _proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                lock (_lock) _stderrLog.AppendLine(e.Data);
        };

        try { _proc.Start(); }
        catch (Exception ex)
        {
            throw new ApiException(500, "ENCODER_ERROR", "Failed to launch ffmpeg: " + ex.Message);
        }
        _proc.BeginErrorReadLine();

        // Continuously consume stdout so the -progress pipe does not block FFmpeg.
        _stdoutReader = RunStdoutReader(_proc.StandardOutput, parser);

        int timeoutMs;
        if (cfg.DurationSeconds.HasValue && cfg.DurationSeconds > 0)
            timeoutMs = (cfg.DurationSeconds.Value + 15) * 1000;
        else
            timeoutMs = 4 * 3600 * 1000;

        _watcher = Task.Run(() =>
        {
            bool exited = _proc.WaitForExit(timeoutMs);
            lock (_lock) _hasExited = true;
            int exitCode = -1;
            try { if (exited) exitCode = _proc.ExitCode; } catch { }

            if (!exited)
            {
                try { _proc.Kill(true); } catch { }
            }

            RunNaturalExitLifecycle(_stdoutReader!, exitCode, _output, _stderrLog, StdoutDrainTimeout,
                (code, meta) => _onNaturalExit?.Invoke(code, meta));
        });

        StartMicrophoneMonitor(cfg);
    }

    private Action<int, OutputMeta>? _onNaturalExit;

    public void OnNaturalExit(Action<int, OutputMeta> cb) => _onNaturalExit = cb;

    public string GetStderrLog()
    {
        lock (_lock) return _stderrLog.ToString();
    }

    /// <summary>
    /// Reads FFmpeg -progress output from <paramref name="reader"/> until EOF,
    /// feeding each line to <paramref name="parser"/>. The parser is flushed
    /// exactly once by this reader, in its <c>finally</c> block, so no other
    /// thread should flush concurrently. This reader owns the single flush;
    /// it only ends when <see cref="TextReader.ReadLine"/> returns null.
    /// </summary>
    internal Task RunStdoutReader(TextReader reader, FFmpegProgressParser parser)
    {
        return Task.Run(() =>
        {
            try
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                    parser.FeedLine(line);
            }
            catch
            {
                // Stdout reader failures must not stop the recording.
            }
            finally
            {
                try { parser.Flush(); }
                catch { }
            }
        });
    }

    /// <summary>
    /// Waits for the stdout reader to reach EOF or <paramref name="timeout"/>.
    /// Does not flush the parser; the reader owns the single flush on completion.
    /// </summary>
    private void DrainStdoutReader(TimeSpan timeout)
    {
        if (_stdoutReader is null) return;
        DrainTask(_stdoutReader, timeout);
    }

    /// <summary>
    /// Test seam: wait for an arbitrary reader task with the same timeout/exception
    /// isolation policy used in production.
    /// </summary>
    internal void DrainReaderTask(Task readerTask, TimeSpan timeout)
    {
        DrainTask(readerTask, timeout);
    }

    private static void DrainTask(Task task, TimeSpan timeout)
    {
        if (task is null) return;
        try
        {
            if (task.IsCompleted) return;
            task.Wait(timeout);
        }
        catch
        {
            // Drain failures must not stop the recording or affect Stop()/watcher.
        }
    }

    /// <summary>
    /// Production natural-exit orchestration: wait for the stdout reader to drain,
    /// probe the output, attach stderr, and invoke the natural-exit callback.
    /// Exposed internally so tests can regress the real call order without
    /// launching FFmpeg.
    /// </summary>
    internal void RunNaturalExitLifecycle(Task stdoutReader, int exitCode, string output,
        StringBuilder stderrLog, TimeSpan drainTimeout, Action<int, OutputMeta> onNaturalExit)
    {
        DrainTask(stdoutReader, drainTimeout);

        var meta = Probe(output);
        string stderr;
        lock (_lock) stderr = stderrLog.ToString();
        meta.StderrLog = stderr;
        ClassifyAudioOutcome(meta, stderr, _cfg, _runtimeAudioLostAtMs);
        _completionMeta = meta;

        StopMicrophoneMonitor();

        lock (_lock)
        {
            if (_manualStopped) return;
        }
        onNaturalExit(exitCode, meta);
    }

    /// <summary>
    /// Production stop orchestration: wait for the stdout reader to drain and
    /// return the output meta. Exposed internally so tests can regress the real
    /// call order without launching FFmpeg.
    /// </summary>
    internal OutputMeta RunStopLifecycle(Task stdoutReader, string output, string stderr, TimeSpan drainTimeout)
    {
        DrainTask(stdoutReader, drainTimeout);

        var meta = Probe(output);
        meta.StderrLog = stderr;
        ClassifyAudioOutcome(meta, stderr, _cfg, _runtimeAudioLostAtMs);
        _completionMeta = meta;
        _hasExited = true;
        return meta;
    }

    public OutputMeta Stop()
    {
        string stderr;

        lock (_lock)
        {
            _manualStopped = true;
            stderr = _stderrLog.ToString();
        }

        if (_proc != null && !_proc.HasExited)
        {
            try
            {
                _proc.StandardInput.Write('q');
                _proc.StandardInput.Flush();
                if (!_proc.WaitForExit(8000))
                {
                    try { _proc.Kill(true); } catch { }
                }
            }
            catch { try { _proc?.Kill(true); } catch { } }
        }

        // Wait for any remaining -progress lines to be processed before the
        // caller finalizes the recording. This keeps first-frame observations
        // ordered before recording.terminal in the performance trace.
        var meta = RunStopLifecycle(_stdoutReader!, _output, stderr, StdoutDrainTimeout);
        StopMicrophoneMonitor();
        return meta;
    }

    public bool HasExited => _proc?.HasExited ?? _hasExited;
    public OutputMeta? LastMeta => _completionMeta;
    public int ExitCode
    {
        get
        {
            try { if (_proc != null && _proc.HasExited) return _proc.ExitCode; } catch { }
            return -1;
        }
    }

    /// <summary>
    /// Starts a bounded, cancellable monitor that watches the selected microphone
    /// endpoint while recording. The monitor only flags a loss after it has
    /// first observed an active state and then sees inactive/unavailable.
    /// Unknown states are ignored. The monitor is tied to this recording instance
    /// and does not use any global threads.
    /// </summary>
    private void StartMicrophoneMonitor(CaptureConfig cfg)
    {
        if (!cfg.Microphone || _microphoneStatusProvider == null)
            return;

        var deviceId = string.IsNullOrEmpty(cfg.MicDevice) ? "default" : cfg.MicDevice;
        var cts = new CancellationTokenSource();
        var oldCts = Interlocked.CompareExchange(ref _microphoneMonitorCts, cts, null);
        if (oldCts != null)
        {
            cts.Dispose();
            return; // already running
        }

        _microphoneMonitorTask = Task.Run(() => RunMicrophoneMonitorAsync(deviceId, cts.Token), cts.Token);
    }

    private async Task RunMicrophoneMonitorAsync(string deviceId, CancellationToken cancellationToken)
    {
        bool? wasActive = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                MicrophoneStatus status;
                try
                {
                    status = await _microphoneStatusProvider!.GetStatusAsync(deviceId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Provider failure is treated as unknown; do not flip to lost.
                    status = new MicrophoneStatus(null, null, null, null);
                }

                // "unknown" (all null) must not be interpreted as lost.
                if (string.IsNullOrEmpty(status.State))
                {
                    await Task.Delay(MicrophoneMonitorInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                bool isActive = string.Equals(status.State, "Active", StringComparison.OrdinalIgnoreCase);

                if (wasActive == true && !isActive)
                {
                    Interlocked.CompareExchange(ref _runtimeAudioLostAtMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0);
                    break; // loss detected; stop monitoring
                }

                wasActive = isActive;
                await Task.Delay(MicrophoneMonitorInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch
        {
            // Monitor failures must never affect video recording.
        }
    }

    /// <summary>
    /// Stops the microphone monitor and waits for it to complete. Safe to call
    /// more than once and from either the Stop() path or the natural-exit path.
    /// </summary>
    private void StopMicrophoneMonitor()
    {
        var cts = Interlocked.Exchange(ref _microphoneMonitorCts, null);
        if (cts == null)
            return;

        try { cts.Cancel(); } catch { }

        var task = _microphoneMonitorTask;
        if (task != null)
        {
            try { task.Wait(MicrophoneMonitorShutdownTimeout); } catch { }
        }

        try { cts.Dispose(); } catch { }
    }

    // Silence-detection constants. These are chosen to catch Bluetooth/ wireless
    // microphone dropouts and slow-start artifacts without flagging normal pauses.
    // They are public for tests and must stay in sync with the parser thresholds.
    internal const double SilenceDetectThresholdDb = -50.0;
    internal const double SilenceDetectMinDurationSeconds = 3.0;
    internal const double InternalSilenceWarningThresholdSeconds = 3.0;

    internal static List<string> BuildArgs(CaptureConfig cfg)
    {
        DisplayScaleGeometry.ThrowIfInvalidCaptureBounds(cfg);
        var args = new List<string>();

        // Global options.
        // -nostats suppresses the default periodic stderr stats so we only
        // receive structured progress groups on stdout.
        // -progress pipe:1 writes key=value progress groups to stdout, which
        // the stdout reader parses to detect the first encoded/muxed frame.
        // Note: -stats_period is intentionally omitted because the bundled
        // FFmpeg (git-2019-10-22) does not recognize this option.
        args.Add("-y");
        args.Add("-nostats");
        args.Add("-progress");
        args.Add("pipe:1");

        // When a microphone is enabled, open the (potentially slow-starting)
        // dshow audio input FIRST so that FFmpeg blocks on audio readiness
        // before gdigrab begins capturing screen frames. This prevents the
        // confirmation window from being recorded while the microphone initializes.
        if (cfg.Microphone)
        {
            AppendAudioInputArgs(args, cfg);
        }

        AppendVideoInputArgs(args, cfg);

        AppendOutputArgs(args, cfg);

        return args;
    }

    private static void AppendVideoInputArgs(List<string> args, CaptureConfig cfg)
    {
        args.Add("-f");
        args.Add("gdigrab");
        args.Add("-framerate");
        args.Add(cfg.Fps.ToString(CultureInfo.InvariantCulture));
        args.Add("-thread_queue_size");
        args.Add("512");

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
        else
        {
            if (w > 0 && h > 0)
            {
                args.Add("-offset_x");
                args.Add(x.ToString(CultureInfo.InvariantCulture));
                args.Add("-offset_y");
                args.Add(y.ToString(CultureInfo.InvariantCulture));
                args.Add("-video_size");
                args.Add($"{w}x{h}");
            }
        }

        // Input-level duration for gdigrab so the video input also respects
        // the planned length. Output-level -t is added as well to guarantee
        // the final output (and any audio input) does not outrun the limit.
        if (cfg.DurationSeconds.HasValue && cfg.DurationSeconds > 0)
        {
            args.Add("-t");
            args.Add(cfg.DurationSeconds.Value.ToString(CultureInfo.InvariantCulture));
        }

        args.Add("-i");
        args.Add("desktop");
    }

    private static void AppendAudioInputArgs(List<string> args, CaptureConfig cfg)
    {
        var device = string.IsNullOrEmpty(cfg.MicDevice) ? "default" : cfg.MicDevice;

        args.Add("-f");
        args.Add("dshow");
        args.Add("-thread_queue_size");
        args.Add("512");
        args.Add("-i");
        args.Add($"audio={device}");
    }

    private static void AppendOutputArgs(List<string> args, CaptureConfig cfg)
    {
        var (_, _, capW, capH) = cfg.Bounds;
        if (string.Equals(cfg.SourceKind, "display", StringComparison.OrdinalIgnoreCase))
        {
            var scaleFilter = DisplayScaleGeometry.BuildFilter(capW, capH);
            if (scaleFilter != null)
            {
                args.Add("-vf");
                args.Add(scaleFilter);
            }
        }

        // With microphone enabled the dshow audio input is index 0 and the
        // gdigrab video input is index 1. Explicit -map makes the final
        // stream selection independent of input order and guarantees a single
        // video stream plus the selected audio stream.
        if (cfg.Microphone)
        {
            args.Add("-map");
            args.Add("0:a:0");
            args.Add("-map");
            args.Add("1:v:0");
        }

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

        if (cfg.Microphone)
        {
            args.Add("-c:a");
            args.Add("aac");
            args.Add("-b:a");
            args.Add("128k");
            args.Add("-af");
            args.Add(BuildAudioFilter(cfg));
        }

        // Output-level duration guarantees the whole muxed output stops at the
        // requested length even if the audio input would otherwise continue.
        if (cfg.DurationSeconds.HasValue && cfg.DurationSeconds > 0)
        {
            args.Add("-t");
            args.Add(cfg.DurationSeconds.Value.ToString(CultureInfo.InvariantCulture));
        }

        args.Add("-movflags");
        args.Add("+faststart");
        args.Add(cfg.OutputPath);
    }

    /// <summary>
    /// Builds the audio filter chain for microphone captures. aresample keeps
    /// A/V sync robust; silencedetect runs after resampling and emits stderr
    /// markers for later continuity diagnosis without changing the audio stream.
    /// </summary>
    private static string BuildAudioFilter(CaptureConfig cfg)
    {
        var thresholdDb = SilenceDetectThresholdDb.ToString(CultureInfo.InvariantCulture);
        var minDuration = SilenceDetectMinDurationSeconds.ToString(CultureInfo.InvariantCulture);
        return $"aresample=async=1:first_pts=0,silencedetect=noise={thresholdDb}dB:d={minDuration}";
    }

    /// <summary>
    /// Renders an argument list as a single-space diagnostic string. Each
    /// argument is quoted only when it contains spaces or quotes, so the
    /// displayed value is readable but is not the actual command source.
    /// </summary>
    internal static string RenderCommandArgs(List<string> args)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < args.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(RenderArg(args[i]));
        }
        return sb.ToString();
    }

    private static string RenderArg(string arg)
    {
        if (arg.Contains(' ') || arg.Contains('"'))
        {
            // Escape backslashes and double quotes for display only.
            return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
        return arg;
    }

    /// <summary>
    /// Examines stderr for stable, non-localized signs of microphone failure,
    /// runtime CoreAudio device loss, and internal long silences. Records the
    /// result in <paramref name="meta"/>. Both <c>recorded</c> and <c>lost</c>
    /// require the final output to contain a compliant AAC audio stream, as
    /// reported by ffprobe. Stderr only distinguishes between an explicit open
    /// failure, a runtime loss with media evidence, and a clean capture. This
    /// prevents falsely claiming success when FFmpeg exits cleanly but produces
    /// a silent video, or claiming <c>lost</c> when there was never an audio
    /// track to lose.
    /// </summary>
    private static void ClassifyAudioOutcome(OutputMeta meta, string stderr, CaptureConfig? cfg, long runtimeAudioLostAtMs)
    {
        if (cfg == null || !cfg.Microphone)
        {
            meta.AudioStatus = "not_requested";
            meta.AudioContinuityStatus = "not_checked";
            return;
        }

        var lower = (stderr ?? "").ToLowerInvariant();

        // Pre-launch or early open failures. These patterns appear when dshow
        // cannot open the selected device or the device disappears before start.
        bool openFailed = lower.Contains("could not open audio device") ||
                          lower.Contains("audio device not found") ||
                          lower.Contains("no such audio device") ||
                          lower.Contains("cannot open audio device") ||
                          (lower.Contains("i/o error") && lower.Contains("audio="));

        if (openFailed)
        {
            meta.AudioStatus = "start_failed";
            meta.AudioContinuityStatus = "not_checked";
            meta.Warnings = (meta.Warnings ?? Array.Empty<string>())
                .Append("microphone_start_failed: ffmpeg could not open the selected audio device")
                .ToArray();
            return;
        }

        // The invariant: recorded and lost both require an AAC audio track in
        // the final file. Without that evidence the outcome is missing_audio_track,
        // regardless of what stderr suggests about runtime behaviour.
        bool hasAacTrack = meta.HasAudioStream &&
                           string.Equals(meta.AudioCodec, "aac", StringComparison.OrdinalIgnoreCase);

        if (!hasAacTrack)
        {
            meta.AudioStatus = "missing_audio_track";
            meta.AudioContinuityStatus = "not_checked";
            meta.Warnings = (meta.Warnings ?? Array.Empty<string>())
                .Append("microphone_missing_audio_track: the output does not contain an AAC audio stream")
                .ToArray();
            return;
        }

        // Runtime device loss / disconnection. CoreAudio evidence (when available)
        // takes precedence over stderr heuristics because it is tied to the
        // specific endpoint. Stderr patterns require an actual AAC track as
        // evidence that audio was present before being lost.
        bool runtimeLost = runtimeAudioLostAtMs > 0;
        bool lostInCapture = runtimeLost ||
                             lower.Contains("error reading input") ||
                             (lower.Contains("i/o error") && lower.Contains("dshow"));

        bool bufferUnderrun = lower.Contains("buffer underrun");

        if (lostInCapture)
        {
            meta.AudioStatus = "lost";
            meta.AudioContinuityStatus = "not_checked";
            meta.AudioLostAtMs = runtimeLost ? runtimeAudioLostAtMs : null;
            meta.Warnings = (meta.Warnings ?? Array.Empty<string>())
                .Append("microphone_lost: audio input was lost during recording")
                .ToArray();
            return;
        }

        if (bufferUnderrun)
        {
            meta.Warnings = (meta.Warnings ?? Array.Empty<string>())
                .Append("microphone_buffer_underrun: transient audio queue pressure detected")
                .ToArray();
        }

        // Non-destructive continuity check: internal long silences produce a
        // warning but do not flip the status away from recorded.
        var silence = SilenceIntervalParser.ParseAndClassify(
            stderr ?? "",
            meta.DurationSeconds,
            InternalSilenceWarningThresholdSeconds);

        meta.AudioContinuityStatus = silence.HasInternalSilence ? "degraded" : "continuous";
        if (silence.HasInternalSilence)
        {
            var longest = silence.LongestInternalSeconds;
            meta.Warnings = (meta.Warnings ?? Array.Empty<string>())
                .Append($"microphone_signal_interruption_suspected: internal silence {longest:F1}s >= {InternalSilenceWarningThresholdSeconds:F1}s")
                .ToArray();
        }

        meta.AudioStatus = "recorded";
    }

    public static OutputMeta Probe(string path)
    {
        var m = new OutputMeta();
        m.OutputPath = string.IsNullOrWhiteSpace(path) ? null : path;
        bool fileExists = false;
        try
        {
            var fi = new FileInfo(path);
            fileExists = fi.Exists;
            m.SizeBytes = fileExists ? fi.Length : 0;
        }
        catch { }

        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = FfmpegLocator.FfprobePath,
                Arguments = $"-v quiet -print_format json -show_format -show_streams \"{path}\"",
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = false
            });
            if (p != null)
            {
                var json = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(3000))
                {
                    try { p.Kill(true); } catch { }
                    return m;
                }

                if (p.ExitCode != 0)
                    return m;

                var root = JsonNode.Parse(json);
                if (root == null)
                    return m;

                m.DurationSeconds = double.TryParse(
                    root?["format"]?["duration"]?.GetValue<string>(),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;

                // Container format: normalize "mov,mp4,m4a,..." to "mp4".
                m.Container = NormalizeContainer(root?["format"]?["format_name"]?.GetValue<string>());

                var streams = root?["streams"]?.AsArray();
                if (streams != null)
                {
                    var infos = new List<ProbeStreamInfo>();
                    JsonNode? videoNode = null;
                    JsonNode? audioNode = null;
                    foreach (var s in streams)
                    {
                        if (s == null) continue;
                        var info = new ProbeStreamInfo
                        {
                            Index = s["index"]?.GetValue<int>() ?? -1,
                            CodecType = s["codec_type"]?.GetValue<string>(),
                            CodecName = s["codec_name"]?.GetValue<string>(),
                            StartTimeSeconds = TryParseProbeDouble(s["start_time"]?.GetValue<string>()),
                            DurationSeconds = TryParseProbeDouble(s["duration"]?.GetValue<string>())
                        };
                        infos.Add(info);

                        if (info.CodecType == "video" && videoNode == null)
                            videoNode = s;
                        else if (info.CodecType == "audio" && audioNode == null)
                            audioNode = s;
                    }
                    m.ProbeStreams = infos.ToArray();

                    if (videoNode != null)
                    {
                        m.Width = videoNode["width"]?.GetValue<int>() ?? 0;
                        m.Height = videoNode["height"]?.GetValue<int>() ?? 0;
                        m.Codec = NormalizeCodec(videoNode["codec_name"]?.GetValue<string>());
                        var fr = videoNode["r_frame_rate"]?.GetValue<string>() ?? "30/1";
                        var parts = fr.Split('/');
                        if (parts.Length == 2 && int.TryParse(parts[1], out var den) && den != 0)
                            m.Fps = (int)Math.Round(double.Parse(parts[0]) / den);
                    }

                    // Detect whether an audio stream was actually produced.
                    if (audioNode != null)
                    {
                        m.HasAudioStream = true;
                        m.AudioCodec = audioNode["codec_name"]?.GetValue<string>();
                    }
                }

                // OutputFileExists is a probe result, not merely a directory
                // existence check: a file that cannot be parsed by ffprobe is
                // not a valid successful output.
                m.OutputFileExists = fileExists;
            }
        }
        catch
        {
            m.OutputFileExists = false;
        }
        return m;
    }

    private static string? NormalizeContainer(string? formatName)
    {
        if (string.IsNullOrWhiteSpace(formatName))
            return null;

        var parts = formatName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (string.Equals(part, "mp4", StringComparison.OrdinalIgnoreCase))
                return "mp4";
        }
        return parts.FirstOrDefault()?.ToLowerInvariant();
    }

    private static string? NormalizeCodec(string? codecName)
    {
        if (string.IsNullOrWhiteSpace(codecName))
            return null;
        return codecName.ToLowerInvariant();
    }

    private static double? TryParseProbeDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return d;
        return null;
    }

    public void Dispose() { try { _proc?.Dispose(); } catch { } }
}
