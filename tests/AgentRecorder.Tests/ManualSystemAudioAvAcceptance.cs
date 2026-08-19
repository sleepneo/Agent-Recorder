using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Internal engineering acceptance entry for the system-audio media pipeline.
/// This is NOT a product-flow test: it captures the configured desktop and
/// render endpoint directly and must be explicitly enabled.
///
/// Enable explicitly with:
///   AGENT_RECORDER_RUN_REAL_SYSTEM_AUDIO_AV=true
///
/// Additional environment variables:
///   AGENT_RECORDER_REAL_RENDER_ENDPOINT_ID - exact CoreAudio render endpoint id
///   AGENT_RECORDER_REAL_SOURCE_KIND        - region or display; default region
///   AGENT_RECORDER_REAL_BOUNDS             - x,y,w,h; default 0,0,1920,1080
///   AGENT_RECORDER_REAL_DISPLAY_BOUNDS     - legacy alias for REAL_BOUNDS
///   AGENT_RECORDER_REAL_OUTPUT_DIR         - output directory
///   AGENT_RECORDER_REAL_DURATION_SECONDS  - capture duration; default 10
///
/// CaptureEnded is only evidence that screen capture stopped. The terminal
/// result for this entry is the OutputMeta delivered by OnNaturalExit after
/// audio stop, mux, probe and publish complete. This entry has no product UI;
/// it must not be used to accept save-path confirmation, countdown, REC border,
/// floating stop-button behavior, or selected-region content semantics.
/// </summary>
public sealed class ManualSystemAudioAvAcceptance
{
    private const string EnvVarEnable = "AGENT_RECORDER_RUN_REAL_SYSTEM_AUDIO_AV";
    private const string EnvVarSourceKind = "AGENT_RECORDER_REAL_SOURCE_KIND";
    private const string EnvVarBounds = "AGENT_RECORDER_REAL_BOUNDS";
    private const string LegacyEnvVarBounds = "AGENT_RECORDER_REAL_DISPLAY_BOUNDS";
    private const string DefaultBounds = "0,0,1920,1080";
    private static readonly TimeSpan AudioReadyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FallbackStopTimeout = TimeSpan.FromSeconds(20);

    private static bool IsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(EnvVarEnable)?.Trim().ToLowerInvariant();
        return value == "true" || value == "1";
    }

    [Fact]
    public async Task Real_SystemAudioAv_VerticalSlice()
    {
        if (!IsEnabled())
            return;

        var sourceKind = ParseSourceKind();
        var bounds = ParseBounds();
        ValidateRequestedBounds(sourceKind, bounds);

        var renderEndpointId = Environment.GetEnvironmentVariable("AGENT_RECORDER_REAL_RENDER_ENDPOINT_ID");
        if (string.IsNullOrWhiteSpace(renderEndpointId))
        {
            throw new InvalidOperationException(
                "AGENT_RECORDER_REAL_RENDER_ENDPOINT_ID must be set to the exact CoreAudio render endpoint id.");
        }

        var outputDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_REAL_OUTPUT_DIR") ??
            Path.Combine(Directory.GetCurrentDirectory(), ".local-data", "acceptance");
        Directory.CreateDirectory(outputDir);

        var durationSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("AGENT_RECORDER_REAL_DURATION_SECONDS"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var duration) ? Math.Max(1, duration) : 10;

        var outputPath = Path.Combine(outputDir, $"system_audio_av_{DateTime.UtcNow:yyyyMMdd_HHmmss}.mp4");
        var cfg = new CaptureConfig
        {
            SourceKind = sourceKind,
            Bounds = bounds,
            Fps = 30,
            Quality = "medium",
            OutputPath = outputPath,
            DurationSeconds = durationSeconds,
            AudioSourceKind = AudioCaptureSourceKind.SystemLoopback,
            SystemLoopbackEndpoint = renderEndpointId
        };

        var normalizationError = cfg.ValidateAudioSource();
        if (normalizationError != null)
            throw new InvalidOperationException($"Invalid audio configuration: {normalizationError}");

        var effectiveSourceKind = cfg.SourceKind;
        var effectiveBounds = cfg.Bounds;
        Assert.Equal(sourceKind, effectiveSourceKind);
        Assert.Equal(bounds, effectiveBounds);
        var effectiveScale = sourceKind == "display"
            ? DisplayScaleGeometry.GetOutputSize(bounds.w, bounds.h)
            : null;

        Console.WriteLine($"[ACCEPTANCE] Requested source/bounds: {sourceKind} {FormatBounds(bounds)}");
        Console.WriteLine($"[ACCEPTANCE] Effective capture source/bounds: {effectiveSourceKind} {FormatBounds(effectiveBounds)}");
        Console.WriteLine($"[ACCEPTANCE] Effective encoded dimensions: {FormatOptionalSize(effectiveScale) ?? FormatBoundsSize(bounds)}");

        using var backend = new AvSplitCaptureBackend();
        var audioReadySource = NewAsyncTcs<bool>();
        var captureEndedSource = NewAsyncTcs<CaptureEndedObservation>();
        var finalizationSource = NewAsyncTcs<OutputMeta>();

        backend.AudioReady += () =>
        {
            Console.WriteLine("[ACCEPTANCE] Audio ready — starting video capture.");
            audioReadySource.TrySetResult(true);
        };

        backend.CaptureEnded += observation =>
        {
            // This callback is intentionally observation-only. It must not
            // call Stop(), because finalization is not complete at this point.
            Console.WriteLine(
                $"[ACCEPTANCE] CaptureEnded observation: exitCode={observation.ExitCode}, reason={observation.Reason}");
            captureEndedSource.TrySetResult(observation);
        };

        backend.OnNaturalExit((exitCode, meta) =>
        {
            Console.WriteLine($"[ACCEPTANCE] Finalization terminal: exitCode={exitCode}");
            finalizationSource.TrySetResult(meta);
        });

        backend.Start(cfg);
        await audioReadySource.Task.WaitAsync(AudioReadyTimeout);
        backend.StartVideo();

        Console.WriteLine($"[ACCEPTANCE] Capturing media pipeline for ~{durationSeconds}s...");

        OutputMeta meta;
        bool naturalFinalization = false;
        var finalizationTimeout = TimeSpan.FromSeconds(Math.Max(45, durationSeconds + 30));
        try
        {
            meta = await finalizationSource.Task.WaitAsync(finalizationTimeout);
            naturalFinalization = true;
        }
        catch (TimeoutException)
        {
            Console.WriteLine(
                $"[ACCEPTANCE] Finalization did not complete within {finalizationTimeout.TotalSeconds:F0}s; entering bounded fallback Stop().");

            // Recheck the finalization TCS before invoking Stop. If natural
            // finalization won the race, Stop must not be called after it.
            if (finalizationSource.Task.IsCompletedSuccessfully)
            {
                meta = await finalizationSource.Task;
                naturalFinalization = true;
            }
            else
            {
                var fallbackTask = Task.Run(() => backend.Stop());
                try
                {
                    meta = await fallbackTask.WaitAsync(FallbackStopTimeout);
                }
                catch (TimeoutException)
                {
                    Console.WriteLine(
                        $"[ACCEPTANCE] Fallback Stop() exceeded {FallbackStopTimeout.TotalSeconds:F0}s; reporting available diagnostics.");
                    meta = backend.LastMeta ?? BuildFallbackFailureMeta(
                        backend,
                        outputPath,
                        $"fallback_stop_timeout after {finalizationTimeout.TotalSeconds:F0}s");
                }
            }
        }

        var captureEndedObserved = captureEndedSource.Task.IsCompletedSuccessfully;
        PrintMetaDiagnostics(backend, meta, outputPath, sourceKind, bounds, naturalFinalization, captureEndedObserved);

        var success = IsMediaPipelineSuccess(meta, outputPath, durationSeconds);
        Console.WriteLine(
            $"[ACCEPTANCE] media_pipeline_acceptance={(success ? "PASSED" : "FAILED")} " +
            "(internal media pipeline only; product_flow not evaluated)");

        Assert.True(success,
            "media_pipeline_acceptance failed; see StderrLog, Warnings and artifact paths above.");
    }

    private static string ParseSourceKind()
    {
        var value = Environment.GetEnvironmentVariable(EnvVarSourceKind)?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value))
            return "region";

        if (value is "region" or "display")
            return value;

        throw new InvalidOperationException(
            $"{EnvVarSourceKind} must be exactly 'region' or 'display'; got '{value}'.");
    }

    private static (int x, int y, int w, int h) ParseBounds()
    {
        var value = Environment.GetEnvironmentVariable(EnvVarBounds);
        if (string.IsNullOrWhiteSpace(value))
        {
            value = Environment.GetEnvironmentVariable(LegacyEnvVarBounds);
            if (!string.IsNullOrWhiteSpace(value))
            {
                Console.WriteLine(
                    $"[ACCEPTANCE] {LegacyEnvVarBounds} is deprecated; use {EnvVarBounds}.");
            }
        }

        value ??= DefaultBounds;
        var parts = value.Split(',');
        if (parts.Length != 4 ||
            !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ||
            !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ||
            !int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ||
            !int.TryParse(parts[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
        {
            throw new InvalidOperationException(
                $"{EnvVarBounds} must be in 'x,y,w,h' format (legacy alias: {LegacyEnvVarBounds}).");
        }

        return (x, y, w, h);
    }

    private static void ValidateRequestedBounds(string sourceKind, (int x, int y, int w, int h) bounds)
    {
        var cfg = new CaptureConfig { SourceKind = sourceKind, Bounds = bounds };
        DisplayScaleGeometry.ThrowIfInvalidCaptureBounds(cfg);

        // Region/window capture is physical-bounds capture. It cannot silently
        // drop a row/column to satisfy x264; the caller must provide even bounds.
        if (sourceKind == "region" && ((bounds.w & 1) != 0 || (bounds.h & 1) != 0))
        {
            throw new InvalidOperationException(
                $"region bounds must be even and are used verbatim; got {FormatBounds(bounds)}.");
        }
    }

    private static bool IsMediaPipelineSuccess(OutputMeta meta, string outputPath, int requestedDuration)
    {
        if (!meta.OutputFileExists || !string.Equals(meta.OutputPath, outputPath, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!File.Exists(outputPath) || meta.SizeBytes != new FileInfo(outputPath).Length)
            return false;
        if (meta.DurationSeconds <= 0 || meta.DurationSeconds < requestedDuration * 0.5)
            return false;
        if (meta.Width <= 0 || meta.Height <= 0 || (meta.Width & 1) != 0 || (meta.Height & 1) != 0)
            return false;
        if (!meta.HasAudioStream || !string.Equals(meta.AudioCodec, "aac", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(meta.AudioStatus, "system_loopback_recorded", StringComparison.OrdinalIgnoreCase))
            return false;

        var video = meta.ProbeStreams.FirstOrDefault(s => s.CodecType == "video");
        var audio = meta.ProbeStreams.FirstOrDefault(s => s.CodecType == "audio");
        return video != null && audio != null &&
               video.Index == 0 && audio.Index == 1 &&
               string.Equals(video.CodecName, "h264", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(audio.CodecName, "aac", StringComparison.OrdinalIgnoreCase) &&
               video.StartTimeSeconds.HasValue && audio.StartTimeSeconds.HasValue &&
               video.DurationSeconds > 0 && audio.DurationSeconds > 0;
    }

    private static void PrintMetaDiagnostics(
        AvSplitCaptureBackend backend,
        OutputMeta meta,
        string requestedOutputPath,
        string requestedSource,
        (int x, int y, int w, int h) requestedBounds,
        bool naturalFinalization,
        bool captureEndedObserved)
    {
        Console.WriteLine($"[ACCEPTANCE] Requested source/bounds: {requestedSource} {FormatBounds(requestedBounds)}");
        Console.WriteLine($"[ACCEPTANCE] Finalization terminal: {(naturalFinalization ? "OnNaturalExit" : "bounded fallback")}");
        Console.WriteLine($"[ACCEPTANCE] CaptureEnded observed: {captureEndedObserved} (not finalization terminal)");
        Console.WriteLine($"[ACCEPTANCE] Output: {meta.OutputPath ?? "<none>"}");
        Console.WriteLine($"[ACCEPTANCE] Requested output path: {requestedOutputPath}");
        Console.WriteLine($"[ACCEPTANCE] OutputFileExists: {meta.OutputFileExists}");
        Console.WriteLine($"[ACCEPTANCE] SizeBytes: {meta.SizeBytes}");
        Console.WriteLine($"[ACCEPTANCE] Duration: {meta.DurationSeconds:F3}s");
        Console.WriteLine($"[ACCEPTANCE] Video: {meta.Width}x{meta.Height}, {meta.Fps}fps, codec={meta.Codec}");
        Console.WriteLine($"[ACCEPTANCE] Audio status: {meta.AudioStatus}");
        Console.WriteLine($"[ACCEPTANCE] Audio continuity: {meta.AudioContinuityStatus}");
        Console.WriteLine($"[ACCEPTANCE] Has audio stream: {meta.HasAudioStream}");
        Console.WriteLine($"[ACCEPTANCE] Audio codec: {meta.AudioCodec}");
        Console.WriteLine($"[ACCEPTANCE] Audio capture backend: {meta.AudioCaptureBackend}");
        Console.WriteLine($"[ACCEPTANCE] Audio estimated gap: {meta.AudioEstimatedGapMs}ms");
        Console.WriteLine($"[ACCEPTANCE] Audio max gap: {meta.AudioMaxEstimatedGapMs}ms");
        Console.WriteLine($"[ACCEPTANCE] Helper source kind: {meta.AudioSourceKind}");
        Console.WriteLine($"[ACCEPTANCE] Helper anchor status: {meta.AudioAnchorStatus}");
        Console.WriteLine($"[ACCEPTANCE] Helper sample rate/channels/bits: {meta.AudioSampleRate}/{meta.AudioChannels}/{meta.AudioBitsPerSample}");
        Console.WriteLine($"[ACCEPTANCE] Helper capture method/protocol/error: {meta.AudioCaptureMethod}/{meta.AudioHelperProtocol}/{meta.AudioHelperErrorCode}");
        Console.WriteLine($"[ACCEPTANCE] StderrLog: {meta.StderrLog ?? "<empty>"}");
        Console.WriteLine($"[ACCEPTANCE] Warnings: {FormatWarnings(meta.Warnings)}");
        Console.WriteLine($"[ACCEPTANCE] Temp video path: {backend.TempVideoPath ?? "<unknown>"}");
        Console.WriteLine($"[ACCEPTANCE] Temp audio path: {backend.TempAudioPath ?? "<unknown>"}");
        Console.WriteLine($"[ACCEPTANCE] Failed artifacts directory: {backend.FailedArtifactsDirectory ?? "<unknown>"}");
        Console.WriteLine($"[ACCEPTANCE] Live worker stderr fallback: {backend.GetStderrLog()}");
    }

    private static OutputMeta BuildFallbackFailureMeta(AvSplitCaptureBackend backend, string outputPath, string reason)
    {
        var stderr = backend.GetStderrLog();
        return new OutputMeta
        {
            OutputPath = outputPath,
            OutputFileExists = false,
            StderrLog = string.IsNullOrEmpty(stderr) ? reason : stderr + Environment.NewLine + reason,
            Warnings = new[] { reason }
        };
    }

    private static string FormatWarnings(string[] warnings)
        => warnings.Length == 0 ? "<none>" : string.Join(" | ", warnings);

    private static string FormatBounds((int x, int y, int w, int h) bounds)
        => $"x={bounds.x},y={bounds.y},w={bounds.w},h={bounds.h}";

    private static string FormatBoundsSize((int x, int y, int w, int h) bounds)
        => $"{bounds.w}x{bounds.h}";

    private static string? FormatOptionalSize((int Width, int Height)? size)
        => size.HasValue ? $"{size.Value.Width}x{size.Value.Height}" : null;

    private static TaskCompletionSource<T> NewAsyncTcs<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
