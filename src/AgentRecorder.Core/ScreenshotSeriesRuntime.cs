using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;

namespace AgentRecorder.Core;

public sealed class ScreenshotSeriesFrame
{
    public int Index { get; init; }
    public string FileName { get; init; } = "";
    public long ScheduledOffsetMs { get; init; }
    public long CapturedOffsetMs { get; init; }
    public long LatenessMs { get; init; }
    public long CaptureDurationMs { get; init; }
    public DateTime CaptureStartedAtUtc { get; init; }
    public DateTime CompletedAtUtc { get; init; }
    public DateTime CapturedAtUtc { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = "";
}

public sealed class ScreenshotSeriesRuntime
{
    public int IntervalMs { get; init; }
    public int? MaxCount { get; init; }
    public int? MaxDurationSeconds { get; init; }
    public int PlannedFrameCount { get; init; }
    public string OutputDirectory { get; set; } = "";
    public string? StagingDirectory { get; set; }
    public string? FinalDirectory { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? NextCaptureDueAtUtc { get; set; }
    public string Status { get; set; } = "pending";
    public string? ErrorCode { get; set; }
    public string? StopReason { get; set; }
    public long AnchorTicks { get; set; }
    public List<ScreenshotSeriesFrame> Frames { get; } = new();
}

internal static class ScreenshotSeriesArtifacts
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static string CreateStagingDirectory(string recordingId)
    {
        var root = Path.Combine(Paths.DataDir, "temp", "screenshot-series");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"{recordingId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static bool TryValidatePng(string path, out int width, out int height, out long size)
    {
        width = 0;
        height = 0;
        size = 0;
        try
        {
            var bytes = File.ReadAllBytes(path);
            size = bytes.LongLength;
            if (bytes.Length < PngSignature.Length + 12 ||
                !bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
                return false;

            using var compressed = new MemoryStream();
            int offset = PngSignature.Length;
            bool sawHeader = false;
            bool sawData = false;
            bool sawEnd = false;
            int bitDepth = 0;
            int colorType = 0;
            while (offset < bytes.Length)
            {
                if (bytes.Length - offset < 12)
                    return false;

                uint chunkLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
                if (chunkLength > int.MaxValue || chunkLength > bytes.Length - offset - 12)
                    return false;

                int typeOffset = offset + 4;
                int dataOffset = offset + 8;
                int nextOffset = checked(dataOffset + (int)chunkLength + 4);
                var type = bytes.AsSpan(typeOffset, 4);
                var data = bytes.AsSpan(dataOffset, (int)chunkLength);
                uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(nextOffset - 4, 4));
                uint actualCrc = Crc32(bytes.AsSpan(typeOffset, 4 + (int)chunkLength));
                if (expectedCrc != actualCrc)
                    return false;

                if (type.SequenceEqual("IHDR"u8))
                {
                    if (sawHeader || chunkLength != 13)
                        return false;
                    uint widthValue = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
                    uint heightValue = BinaryPrimitives.ReadUInt32BigEndian(data[4..8]);
                    if (widthValue == 0 || heightValue == 0 || widthValue > int.MaxValue || heightValue > int.MaxValue)
                        return false;
                    width = (int)widthValue;
                    height = (int)heightValue;
                    bitDepth = data[8];
                    colorType = data[9];
                    if (data[10] != 0 || data[11] != 0 || data[12] > 1)
                        return false;
                    if (!IsSupportedPngFormat(bitDepth, colorType))
                        return false;
                    sawHeader = true;
                }
                else if (type.SequenceEqual("IDAT"u8))
                {
                    if (!sawHeader || sawEnd || chunkLength == 0)
                        return false;
                    compressed.Write(data);
                    sawData = true;
                }
                else if (type.SequenceEqual("IEND"u8))
                {
                    if (!sawHeader || !sawData || sawEnd || chunkLength != 0)
                        return false;
                    sawEnd = true;
                }

                offset = nextOffset;
                if (sawEnd)
                    break;
            }

            if (!sawHeader || !sawData || !sawEnd || offset != bytes.Length)
                return false;

            compressed.Position = 0;
            using var decoded = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true))
                zlib.CopyTo(decoded);

            int channels = colorType switch
            {
                0 => 1,
                2 => 3,
                3 => 1,
                4 => 2,
                6 => 4,
                _ => 0
            };
            long rowBytesLong = ((long)width * channels * bitDepth + 7) / 8;
            long decodedLengthLong = checked((rowBytesLong + 1) * height);
            if (channels == 0 || rowBytesLong <= 0 || rowBytesLong > int.MaxValue ||
                decodedLengthLong != decoded.Length)
                return false;

            var scanlines = decoded.ToArray();
            int rowBytes = (int)rowBytesLong;
            int filterBytesPerPixel = Math.Max(1, (channels * bitDepth + 7) / 8);
            var previous = new byte[rowBytes];
            var current = new byte[rowBytes];
            int scanOffset = 0;
            for (int row = 0; row < height; row++)
            {
                byte filter = scanlines[scanOffset++];
                if (filter > 4)
                    return false;
                for (int i = 0; i < rowBytes; i++)
                {
                    byte raw = scanlines[scanOffset++];
                    byte left = i >= filterBytesPerPixel ? current[i - filterBytesPerPixel] : (byte)0;
                    byte up = previous[i];
                    byte upperLeft = i >= filterBytesPerPixel ? previous[i - filterBytesPerPixel] : (byte)0;
                    current[i] = filter switch
                    {
                        0 => raw,
                        1 => (byte)(raw + left),
                        2 => (byte)(raw + up),
                        3 => (byte)(raw + ((left + up) / 2)),
                        4 => (byte)(raw + Paeth(left, up, upperLeft)),
                        _ => raw
                    };
                }
                (previous, current) = (current, previous);
            }

            return scanOffset == scanlines.Length;
        }
        catch
        {
            return false;
        }
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string WriteManifest(
        Recording rec,
        ScreenshotSeriesRuntime series,
        string status,
        string? errorCode,
        string? stopReason,
        IReadOnlyList<ScreenshotSeriesFrame>? frameSnapshot = null)
    {
        var plan = rec.ApprovedCapturePlan
            ?? throw new InvalidOperationException("Screenshot-series capture plan is missing.");
        var frames = frameSnapshot ?? series.Frames;
        var manifestPath = Path.Combine(series.StagingDirectory!, "series.json");
        var payload = new
        {
            schema_version = 1,
            recording_id = rec.Id,
            confirmation_id = rec.ConfirmationId,
            mode = ScreenshotSeriesConfig.ModeName,
            backend = plan.PlannedBackend,
            status,
            source = new
            {
                type = rec.SourceType,
                title = rec.SourceTitle,
                capture_semantics = plan.CaptureSemantics,
                preview_semantics = plan.PreviewSemantics,
                bounds = new { x = rec.Config.Bounds.x, y = rec.Config.Bounds.y, width = rec.Config.Bounds.w, height = rec.Config.Bounds.h },
                coordinate_space = plan.CoordinateSpace
            },
            series = new
            {
                interval_ms = series.IntervalMs,
                max_count = series.MaxCount,
                max_duration_seconds = series.MaxDurationSeconds,
                planned_frame_count = series.PlannedFrameCount,
                captured_frame_count = frames.Count
            },
            started_at = series.StartedAtUtc,
            completed_at = series.CompletedAtUtc,
            stop_reason = stopReason,
            error_code = errorCode,
            frames = frames.Select(frame => new
            {
                index = frame.Index,
                file_name = frame.FileName,
                scheduled_offset_ms = frame.ScheduledOffsetMs,
                captured_offset_ms = frame.CapturedOffsetMs,
                elapsed_ms = frame.CapturedOffsetMs,
                lateness_ms = frame.LatenessMs,
                capture_duration_ms = frame.CaptureDurationMs,
                capture_started_at = frame.CaptureStartedAtUtc,
                completed_at = frame.CompletedAtUtc,
                captured_at = frame.CapturedAtUtc,
                width = frame.Width,
                height = frame.Height,
                size_bytes = frame.SizeBytes,
                sha256 = frame.Sha256
            }).ToArray()
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true
        });
        // Write bytes explicitly so the manifest contract cannot inherit a BOM
        // from a platform/default text writer.
        var manifestTempPath = manifestPath + ".tmp";
        File.WriteAllBytes(manifestTempPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json));
        File.Move(manifestTempPath, manifestPath, overwrite: false);
        return manifestPath;
    }

    public static string Publish(Recording rec, ScreenshotSeriesRuntime series, string conflictPolicy)
    {
        var desired = series.OutputDirectory;
        var final = ResolveDirectoryConflict(desired, conflictPolicy);
        Directory.Move(series.StagingDirectory!, final);
        series.FinalDirectory = final;
        series.StagingDirectory = null;
        return final;
    }

    public static void DeleteStaging(ScreenshotSeriesRuntime series)
    {
        var path = series.StagingDirectory;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(Path.Combine(Paths.DataDir, "temp", "screenshot-series"));
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(full, recursive: true);
        }
        catch { }
        series.StagingDirectory = null;
    }

    private static string ResolveDirectoryConflict(string desired, string policy)
    {
        if (!Directory.Exists(desired) && !File.Exists(desired)) return desired;
        if (string.Equals(policy, "fail", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(policy, "error", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(409, "OUTPUT_PATH_INVALID", "Output directory already exists.");
        if (string.Equals(policy, "overwrite", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(403, "PERMISSION_DENIED", "Overwriting a screenshot-series directory is not permitted.");

        var parent = Path.GetDirectoryName(desired)!;
        var stem = Path.GetFileName(desired);
        for (int i = 1; ; i++)
        {
            var candidate = Path.Combine(parent, $"{stem}-{i}");
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
    }

    private static bool IsSupportedPngFormat(int bitDepth, int colorType) =>
        colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            2 => bitDepth is 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            4 or 6 => bitDepth is 8 or 16,
            _ => false
        };

    private static byte Paeth(byte left, byte up, byte upperLeft)
    {
        int p = left + up - upperLeft;
        int pa = Math.Abs(p - left);
        int pb = Math.Abs(p - up);
        int pc = Math.Abs(p - upperLeft);
        return pa <= pb && pa <= pc ? left : pb <= pc ? up : upperLeft;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xffffffffu;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
        return crc ^ 0xffffffffu;
    }
}
