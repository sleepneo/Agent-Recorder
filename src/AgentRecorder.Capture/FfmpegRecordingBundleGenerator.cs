using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgentRecorder.Capture;

/// <summary>
/// Production bundle generator that uses the bundled FFmpeg to extract frames,
/// compute a SHA-256 hash, and atomically publish a five-file bundle next to
/// the main MP4.
/// </summary>
public sealed class FfmpegRecordingBundleGenerator : IRecordingBundleGenerator
{
    private readonly IExternalProcessRunner _runner;
    private readonly TimeSpan _frameExtractTimeout;
    private readonly Func<string> _ffmpegPathProvider;

    public FfmpegRecordingBundleGenerator(
        IExternalProcessRunner? runner = null,
        TimeSpan? frameExtractTimeout = null,
        Func<string>? ffmpegPathProvider = null)
    {
        _runner = runner ?? new ExternalProcessRunner();
        _frameExtractTimeout = frameExtractTimeout ?? TimeSpan.FromSeconds(30);
        _ffmpegPathProvider = ffmpegPathProvider ?? (() => FfmpegLocator.FfmpegPath);
    }

    public async Task<RecordingBundleGenerationResult> GenerateAsync(
        RecordingBundleRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        string mediaPath = request.MediaPath;
        if (!File.Exists(mediaPath))
            return RecordingBundleGenerationResult.Failed(RecordingBundleErrorCodes.FrameOutputInvalid, "media_missing");

        string bundlePath = DeriveBundlePath(mediaPath);
        string tempDir = DeriveTempDir(mediaPath);

        try
        {
            if (Directory.Exists(bundlePath))
                return RecordingBundleGenerationResult.Failed(RecordingBundleErrorCodes.AlreadyExists);

            Directory.CreateDirectory(tempDir);

            string metadataPath = Path.Combine(tempDir, "metadata.json");
            string marksPath = Path.Combine(tempDir, "marks.json");
            string firstFramePath = Path.Combine(tempDir, "first_frame.png");
            string lastFramePath = Path.Combine(tempDir, "last_frame.png");
            string thumbnailPath = Path.Combine(tempDir, "thumbnail.jpg");

            // 1. Hash the main video (streamed, bounded memory).
            string sha256;
            try { sha256 = ComputeSha256(mediaPath); }
            catch (Exception ex) { return FailedAndCleanup(tempDir, RecordingBundleErrorCodes.HashFailed, ex.Message); }

            long mediaSize;
            try { mediaSize = new FileInfo(mediaPath).Length; }
            catch (Exception ex) { return FailedAndCleanup(tempDir, RecordingBundleErrorCodes.HashFailed, ex.Message); }

            // 2. Extract representative frames.
            var firstResult = await ExtractFirstFrameAsync(mediaPath, firstFramePath, cancellationToken);
            if (!firstResult.Success)
                return FailedAndCleanup(tempDir, firstResult.ErrorCode!, firstResult.ErrorDetail);

            var lastResult = await ExtractLastFrameAsync(mediaPath, request.ActualDurationSeconds, lastFramePath, cancellationToken);
            if (!lastResult.Success)
                return FailedAndCleanup(tempDir, lastResult.ErrorCode!, lastResult.ErrorDetail);

            var thumbResult = await ExtractThumbnailAsync(mediaPath, request.ActualDurationSeconds, request.Width, request.Height, thumbnailPath, cancellationToken);
            if (!thumbResult.Success)
                return FailedAndCleanup(tempDir, thumbResult.ErrorCode!, thumbResult.ErrorDetail);

            // 3. Validate frame files.
            if (!ValidateImageFile(firstFramePath, "png"))
                return FailedAndCleanup(tempDir, RecordingBundleErrorCodes.FrameOutputInvalid, "first_frame");
            if (!ValidateImageFile(lastFramePath, "png"))
                return FailedAndCleanup(tempDir, RecordingBundleErrorCodes.FrameOutputInvalid, "last_frame");
            if (!ValidateImageFile(thumbnailPath, "jpeg"))
                return FailedAndCleanup(tempDir, RecordingBundleErrorCodes.FrameOutputInvalid, "thumbnail");

            // 4. Write metadata and marks.
            var metadata = BuildMetadata(request, mediaPath, mediaSize, sha256);
            try { await WriteJsonUtf8Async(metadataPath, metadata, cancellationToken); }
            catch (Exception ex) { return FailedAndCleanup(tempDir, RecordingBundleErrorCodes.MetadataWriteFailed, ex.Message); }

            var marks = BuildMarks(request);
            try { await WriteJsonUtf8Async(marksPath, marks, cancellationToken); }
            catch (Exception ex) { return FailedAndCleanup(tempDir, RecordingBundleErrorCodes.MarksWriteFailed, ex.Message); }

            // 5. Atomic publish.
            try
            {
                // Guard against a race where bundle appeared while we were generating.
                if (Directory.Exists(bundlePath))
                {
                    Directory.Delete(tempDir, recursive: true);
                    return RecordingBundleGenerationResult.Failed(RecordingBundleErrorCodes.AlreadyExists);
                }
                Directory.Move(tempDir, bundlePath);
            }
            catch (Exception ex)
            {
                return FailedAndCleanup(tempDir, RecordingBundleErrorCodes.PublishFailed, ex.Message);
            }

            return RecordingBundleGenerationResult.Ready(bundlePath);
        }
        catch (OperationCanceledException)
        {
            SafeDelete(tempDir);
            throw;
        }
        catch (Exception ex)
        {
            return FailedAndCleanup(tempDir, RecordingBundleErrorCodes.GenerationFailed, ex.Message);
        }
    }

    private static string DeriveBundlePath(string mediaPath)
    {
        string dir = Path.GetDirectoryName(mediaPath) ?? "";
        string stem = Path.GetFileNameWithoutExtension(mediaPath);
        return Path.Combine(dir, stem + ".bundle");
    }

    private static string DeriveTempDir(string mediaPath)
    {
        string dir = Path.GetDirectoryName(mediaPath) ?? "";
        string stem = Path.GetFileNameWithoutExtension(mediaPath);
        return Path.Combine(dir, $".{stem}.bundle.tmp-{Guid.NewGuid():N}");
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        var hash = sha.ComputeHash(stream);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private async Task<StepResult> ExtractFirstFrameAsync(string mediaPath, string outputPath, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-y", "-nostats",
            "-ss", "00:00:00.000",
            "-i", mediaPath,
            "-frames:v", "1",
            "-update", "1",
            outputPath
        };
        return await RunFfmpegFrameExtractAsync(args, ct);
    }

    private async Task<StepResult> ExtractLastFrameAsync(string mediaPath, double durationSeconds, string outputPath, CancellationToken ct)
    {
        // Seek near the end, but stay within the file. For very short clips,
        // fall back to the midpoint to avoid decoding issues.
        double seekFromEnd = Math.Min(0.5, Math.Max(0.05, durationSeconds * 0.1));
        string seekArg = durationSeconds > seekFromEnd
            ? $"-{seekFromEnd:F3}"
            : $"-{Math.Max(0.0, durationSeconds - 0.05):F3}";

        var args = new List<string>
        {
            "-y", "-nostats",
            "-sseof", seekArg,
            "-i", mediaPath,
            "-frames:v", "1",
            "-update", "1",
            outputPath
        };
        return await RunFfmpegFrameExtractAsync(args, ct);
    }

    private async Task<StepResult> ExtractThumbnailAsync(string mediaPath, double durationSeconds, int width, int height, string outputPath, CancellationToken ct)
    {
        double midpoint = Math.Max(0.0, durationSeconds / 2.0);

        // Maintain aspect ratio, longest side <= 640, never upscale.
        string scaleFilter = width > height
            ? "'min(640,iw)':-1"
            : "-1:'min(640,ih)'";

        var args = new List<string>
        {
            "-y", "-nostats",
            "-ss", midpoint.ToString("F3", CultureInfo.InvariantCulture),
            "-i", mediaPath,
            "-frames:v", "1",
            "-vf", $"scale={scaleFilter}",
            "-q:v", "5",
            "-update", "1",
            outputPath
        };
        return await RunFfmpegFrameExtractAsync(args, ct);
    }

    private async Task<StepResult> RunFfmpegFrameExtractAsync(List<string> args, CancellationToken ct)
    {
        try
        {
            var ffmpegPath = _ffmpegPathProvider();
            var result = await _runner.RunAsync(ffmpegPath, args, _frameExtractTimeout, captureStderr: true, ct);
            if (result.TimedOut)
                return StepResult.Fail(RecordingBundleErrorCodes.FrameExtractFailed, "timeout");
            if (result.ExitCode != 0)
                return StepResult.Fail(RecordingBundleErrorCodes.FrameExtractFailed, $"exit_code={result.ExitCode}");
            return StepResult.Ok();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StepResult.Fail(RecordingBundleErrorCodes.FrameExtractFailed, ex.Message);
        }
    }

    private static bool ValidateImageFile(string path, string expectedKind)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length == 0) return false;

            byte[] header = new byte[8];
            using var fs = File.OpenRead(path);
            int read = fs.Read(header, 0, header.Length);
            if (read < 8) return false;

            if (expectedKind == "png")
            {
                // PNG signature: 89 50 4E 47 0D 0A 1A 0A
                return header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
                    && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A;
            }
            if (expectedKind == "jpeg")
            {
                // JPEG SOI: FF D8
                return header[0] == 0xFF && header[1] == 0xD8;
            }
            return false;
        }
        catch { return false; }
    }

    private static object BuildMetadata(RecordingBundleRequest r, string mediaPath, long sizeBytes, string sha256)
    {
        return new
        {
            bundle_version = RecordingBundleSnapshot.BundleVersion,
            recording_id = r.RecordingId,
            confirmation_id = (object?)r.ConfirmationId ?? null,
            generated_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            source = new
            {
                type = r.SourceType,
                title = r.SourceTitle,
                coordinate_space = r.CoordinateSpace,
                bounds = new
                {
                    x = r.SourceBounds.x,
                    y = r.SourceBounds.y,
                    width = r.SourceBounds.w,
                    height = r.SourceBounds.h
                }
            },
            recording = new
            {
                started_at = r.StartedAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                completed_at = r.CompletedAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                requested_duration_seconds = (object?)(r.RequestedDurationSeconds.HasValue ? r.RequestedDurationSeconds.Value : null),
                actual_duration_seconds = r.ActualDurationSeconds,
                fps = r.Fps,
                backend = r.Backend,
                stop_reason = r.StopReason,
                audio_microphone = r.AudioMicrophone,
                nested_role = (object?)(r.NestedRole ?? "none"),
                nested_session_id = (object?)r.NestedSessionId ?? null,
                parent_recording_id = (object?)r.ParentRecordingId ?? null
            },
            media = new
            {
                path = mediaPath,
                file_name = Path.GetFileName(mediaPath),
                container = r.Container,
                codec = r.Codec,
                width = r.Width,
                height = r.Height,
                size_bytes = sizeBytes,
                sha256
            },
            audit_correlation = new
            {
                recording_id = r.RecordingId,
                confirmation_id = (object?)r.ConfirmationId ?? null
            }
        };
    }

    private static object BuildMarks(RecordingBundleRequest r)
    {
        return new
        {
            bundle_version = RecordingBundleSnapshot.BundleVersion,
            recording_id = r.RecordingId,
            marks = Array.Empty<object>()
        };
    }

    private static async Task WriteJsonUtf8Async(string path, object value, CancellationToken ct)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, options);
        await File.WriteAllBytesAsync(path, bytes, ct);
    }

    private static RecordingBundleGenerationResult FailedAndCleanup(string tempDir, string errorCode, string? detail)
    {
        SafeDelete(tempDir);
        return RecordingBundleGenerationResult.Failed(errorCode, detail);
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { }
    }

    private sealed class StepResult
    {
        public bool Success { get; }
        public string? ErrorCode { get; }
        public string? ErrorDetail { get; }

        private StepResult(bool success, string? errorCode, string? errorDetail)
        {
            Success = success;
            ErrorCode = errorCode;
            ErrorDetail = errorDetail;
        }

        public static StepResult Ok() => new(true, null, null);
        public static StepResult Fail(string errorCode, string? detail = null) => new(false, errorCode, detail);
    }
}
