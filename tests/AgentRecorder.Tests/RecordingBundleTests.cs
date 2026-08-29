using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderEnvVar")]
public class RecordingBundleTests : IDisposable
{
    private readonly string _tmpDir;

    public RecordingBundleTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"bundle-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tmpDir))
                Directory.Delete(_tmpDir, recursive: true);
        }
        catch { }
    }

    private string MediaPath(string stem) => Path.Combine(_tmpDir, stem + ".mp4");

    private static JsonNode ConfigWithTempOutput(string tempDir, string? filename = null)
    {
        var output = new JsonObject { ["directory"] = tempDir };
        if (!string.IsNullOrEmpty(filename))
            output["filename"] = filename;
        return new JsonObject
        {
            ["source"] = new JsonObject { ["type"] = "display", ["display_id"] = "0" },
            ["video"] = new JsonObject { ["fps"] = 30 },
            ["output"] = output
        };
    }

    private static string DefaultBackendFor(string sourceType) => sourceType switch
    {
        "display" => "ffmpeg",
        "window" => "ffmpeg-window-region",
        _ => "ffmpeg-region"
    };

    private static RecordingBundleRequest BuildRequest(string mediaPath,
        string? confirmationId = null,
        string sourceType = "region",
        string sourceTitle = "region:Display 1",
        (int x, int y, int w, int h)? bounds = null,
        DateTime? startedAt = null,
        DateTime? completedAt = null,
        int? requestedDuration = 30,
        double actualDuration = 30.0,
        int fps = 30,
        int width = 1280,
        int height = 720,
        string? backend = null,
        bool audioMicrophone = false,
        string audioStatus = "not_requested",
        string? nestedRole = null,
        string? nestedSessionId = null,
        string? parentRecordingId = null,
        IEnumerable<RecordingMark>? marks = null)
    {
        return new RecordingBundleRequest(
            recordingId: "rec_test",
            confirmationId: confirmationId,
            sourceType: sourceType,
            sourceTitle: sourceTitle,
            sourceBounds: bounds ?? (100, 200, width, height),
            coordinateSpace: "virtual_screen",
            startedAtUtc: startedAt ?? DateTime.UtcNow.AddSeconds(-actualDuration),
            completedAtUtc: completedAt ?? DateTime.UtcNow,
            requestedDurationSeconds: requestedDuration,
            actualDurationSeconds: actualDuration,
            fps: fps,
            backend: backend ?? DefaultBackendFor(sourceType),
            stopReason: "duration_reached",
            audioMicrophone: audioMicrophone,
            audioStatus: audioStatus,
            audioContinuityStatus: audioMicrophone ? "continuous" : "not_checked",
            audioDeviceId: audioMicrophone ? "mic_test" : null,
            audioLostAtMs: null,
            nestedRole: nestedRole,
            nestedSessionId: nestedSessionId,
            parentRecordingId: parentRecordingId,
            mediaPath: mediaPath,
            container: "mp4",
            codec: "h264",
            width: width,
            height: height,
            marks: marks);
    }

    private static byte[] PngHeader() => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static byte[] JpegHeader() => new byte[] { 0xFF, 0xD8 };
    private Func<string> FakeFfmpegPathProvider => () => Path.Combine(_tmpDir, "fake-ffmpeg.exe");

    [Fact]
    public void DeriveBundlePath_FromMediaPath_UsesSameDirectoryAndStem()
    {
        string mediaPath = MediaPath("demo");
        // Use reflection to exercise the private helper; equivalent logic is used in production.
        var generator = new FfmpegRecordingBundleGenerator(new FakeExternalProcessRunner(), ffmpegPathProvider: FakeFfmpegPathProvider);
        var method = typeof(FfmpegRecordingBundleGenerator).GetMethod("DeriveBundlePath",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        string bundlePath = (string)method!.Invoke(null, new object[] { mediaPath })!;

        Assert.Equal(Path.Combine(_tmpDir, "demo.bundle"), bundlePath);
    }

    [Fact]
    public async Task GenerateAsync_MissingMedia_ReturnsFrameOutputInvalid()
    {
        var runner = new FakeExternalProcessRunner();
        var generator = new FfmpegRecordingBundleGenerator(runner, ffmpegPathProvider: FakeFfmpegPathProvider);
        var request = BuildRequest(MediaPath("missing"));

        var result = await generator.GenerateAsync(request);

        Assert.False(result.Success);
        Assert.Equal(RecordingBundleErrorCodes.FrameOutputInvalid, result.ErrorCode);
        Assert.False(runner.WasCalled);
    }

    [Fact]
    public async Task GenerateAsync_AlreadyExists_ReturnsAlreadyExistsWithoutTouchingMedia()
    {
        var mediaPath = MediaPath("existing");
        File.WriteAllText(mediaPath, "fake video");
        Directory.CreateDirectory(Path.Combine(_tmpDir, "existing.bundle"));

        var runner = new FakeExternalProcessRunner();
        var generator = new FfmpegRecordingBundleGenerator(runner, ffmpegPathProvider: FakeFfmpegPathProvider);
        var request = BuildRequest(mediaPath);

        var result = await generator.GenerateAsync(request);

        Assert.False(result.Success);
        Assert.Equal(RecordingBundleErrorCodes.AlreadyExists, result.ErrorCode);
        Assert.False(runner.WasCalled);
    }

    [Fact]
    public async Task GenerateAsync_SuccessProducesFiveFiles_AndCleansTempDir()
    {
        var mediaPath = MediaPath("success");
        File.WriteAllText(mediaPath, "fake video");

        var runner = new FakeExternalProcessRunner()
            .WithSuccess();
        var generator = new FfmpegRecordingBundleGenerator(runner, ffmpegPathProvider: FakeFfmpegPathProvider);
        var request = BuildRequest(mediaPath);

        var result = await generator.GenerateAsync(request);

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(_tmpDir, "success.bundle"), result.BundlePath);
        Assert.True(Directory.Exists(result.BundlePath));
        Assert.True(File.Exists(Path.Combine(result.BundlePath, "metadata.json")));
        Assert.True(File.Exists(Path.Combine(result.BundlePath, "marks.json")));
        Assert.True(File.Exists(Path.Combine(result.BundlePath, "first_frame.png")));
        Assert.True(File.Exists(Path.Combine(result.BundlePath, "last_frame.png")));
        Assert.True(File.Exists(Path.Combine(result.BundlePath, "thumbnail.jpg")));

        // No temp dir leftovers.
        var leftoverTempDirs = Directory.GetDirectories(_tmpDir, ".*.bundle.tmp-*");
        Assert.Empty(leftoverTempDirs);
    }

    [Fact]
    public async Task GenerateAsync_Hash_MatchesSha256OfFile()
    {
        var mediaPath = MediaPath("hash");
        var bytes = Encoding.UTF8.GetBytes("not really a video");
        File.WriteAllBytes(mediaPath, bytes);
        string expectedHash;
        using (var sha = SHA256.Create())
            expectedHash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();

        var runner = new FakeExternalProcessRunner().WithSuccess();
        var generator = new FfmpegRecordingBundleGenerator(runner, ffmpegPathProvider: FakeFfmpegPathProvider);
        var request = BuildRequest(mediaPath);

        await generator.GenerateAsync(request);

        var metadataPath = Path.Combine(_tmpDir, "hash.bundle", "metadata.json");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath));
        var media = doc.RootElement.GetProperty("media");
        Assert.Equal(expectedHash, media.GetProperty("sha256").GetString());
        Assert.Equal(bytes.Length, media.GetProperty("size_bytes").GetInt64());
    }

    [Fact]
    public async Task GenerateAsync_MetadataSchema_HasExpectedFieldsAndNulls()
    {
        var mediaPath = MediaPath("metadata");
        File.WriteAllBytes(mediaPath, new byte[] { 0, 1, 2, 3 });

        var runner = new FakeExternalProcessRunner().WithSuccess();
        var generator = new FfmpegRecordingBundleGenerator(runner, ffmpegPathProvider: FakeFfmpegPathProvider);
        var started = new DateTime(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc);
        var completed = new DateTime(2026, 7, 18, 12, 0, 30, 100, DateTimeKind.Utc);
        var request = BuildRequest(mediaPath,
            confirmationId: "conf_abc",
            sourceType: "window",
            sourceTitle: "My Window",
            bounds: (10, 20, 640, 480),
            startedAt: started,
            completedAt: completed,
            requestedDuration: 30,
            actualDuration: 30.1,
            fps: 24,
            width: 640,
            height: 480,
            nestedRole: "outer",
            nestedSessionId: "session_xyz");

        var result = await generator.GenerateAsync(request);
        Assert.True(result.Success, $"Bundle generation failed: {result.ErrorCode} {result.ErrorDetail}");

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_tmpDir, "metadata.bundle", "metadata.json")));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("bundle_version").GetInt32());
        Assert.Equal("rec_test", root.GetProperty("recording_id").GetString());
        Assert.Equal("conf_abc", root.GetProperty("confirmation_id").GetString());

        var source = root.GetProperty("source");
        Assert.Equal("window", source.GetProperty("type").GetString());
        Assert.Equal("My Window", source.GetProperty("title").GetString());
        Assert.Equal("virtual_screen", source.GetProperty("coordinate_space").GetString());
        var bounds = source.GetProperty("bounds");
        Assert.Equal(10, bounds.GetProperty("x").GetInt32());
        Assert.Equal(20, bounds.GetProperty("y").GetInt32());
        Assert.Equal(640, bounds.GetProperty("width").GetInt32());
        Assert.Equal(480, bounds.GetProperty("height").GetInt32());

        var recording = root.GetProperty("recording");
        Assert.Equal(started.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), recording.GetProperty("started_at").GetString());
        Assert.Equal(completed.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), recording.GetProperty("completed_at").GetString());
        Assert.Equal(30, recording.GetProperty("requested_duration_seconds").GetInt32());
        Assert.Equal(30.1, recording.GetProperty("actual_duration_seconds").GetDouble());
        Assert.Equal(24, recording.GetProperty("fps").GetInt32());
        Assert.Equal("ffmpeg-window-region", recording.GetProperty("backend").GetString());
        Assert.Equal("duration_reached", recording.GetProperty("stop_reason").GetString());
        Assert.False(recording.GetProperty("audio_microphone").GetBoolean());
        Assert.Equal("outer", recording.GetProperty("nested_role").GetString());
        Assert.Equal("session_xyz", recording.GetProperty("nested_session_id").GetString());
        Assert.True(recording.GetProperty("parent_recording_id").ValueKind == JsonValueKind.Null);

        var media = root.GetProperty("media");
        Assert.Equal(mediaPath, media.GetProperty("path").GetString());
        Assert.Equal("metadata.mp4", media.GetProperty("file_name").GetString());
        Assert.Equal("mp4", media.GetProperty("container").GetString());
        Assert.Equal("h264", media.GetProperty("codec").GetString());
        Assert.Equal(640, media.GetProperty("width").GetInt32());
        Assert.Equal(480, media.GetProperty("height").GetInt32());
        Assert.True(media.GetProperty("size_bytes").GetInt64() > 0);
        Assert.Matches("^[a-f0-9]{64}$", media.GetProperty("sha256").GetString()!);

        Assert.Equal("rec_test", root.GetProperty("audit_correlation").GetProperty("recording_id").GetString());
        Assert.Equal("conf_abc", root.GetProperty("audit_correlation").GetProperty("confirmation_id").GetString());
    }

    [Fact]
    public async Task GenerateAsync_MicrophoneEnabled_MetadataRecordsAudioStatus()
    {
        var mediaPath = MediaPath("mic-enabled");
        File.WriteAllBytes(mediaPath, new byte[] { 0, 1, 2, 3 });

        var generator = new FfmpegRecordingBundleGenerator(new FakeExternalProcessRunner().WithSuccess(), ffmpegPathProvider: FakeFfmpegPathProvider);
        var request = BuildRequest(mediaPath, audioMicrophone: true, audioStatus: "recorded");

        var result = await generator.GenerateAsync(request);
        Assert.True(result.Success, $"Bundle generation failed: {result.ErrorCode} {result.ErrorDetail}");

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_tmpDir, "mic-enabled.bundle", "metadata.json")));
        var recording = doc.RootElement.GetProperty("recording");
        Assert.True(recording.GetProperty("audio_microphone").GetBoolean());
        Assert.Equal("recorded", recording.GetProperty("audio_status").GetString());
    }

    [Fact]
    public async Task GenerateAsync_MarksSchema_IsVersionedEmptyArray()
    {
        var mediaPath = MediaPath("marks");
        File.WriteAllBytes(mediaPath, new byte[] { 0 });

        var generator = new FfmpegRecordingBundleGenerator(new FakeExternalProcessRunner().WithSuccess(), ffmpegPathProvider: FakeFfmpegPathProvider);
        await generator.GenerateAsync(BuildRequest(mediaPath));

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_tmpDir, "marks.bundle", "marks.json")));
        Assert.Equal(1, doc.RootElement.GetProperty("bundle_version").GetInt32());
        Assert.Equal("rec_test", doc.RootElement.GetProperty("recording_id").GetString());
        Assert.Empty(doc.RootElement.GetProperty("marks").EnumerateArray());
    }

    [Fact]
    public async Task GenerateAsync_MarksSchema_WritesOrderedUnicodeMarks()
    {
        var mediaPath = MediaPath("marks-unicode");
        File.WriteAllBytes(mediaPath, new byte[] { 0 });

        var marks = new[]
        {
            new RecordingMark(1234, "重要决定 😀", "agent"),
            new RecordingMark(5678, "第二章", "hotkey")
        };
        var generator = new FfmpegRecordingBundleGenerator(new FakeExternalProcessRunner().WithSuccess(), ffmpegPathProvider: FakeFfmpegPathProvider);
        await generator.GenerateAsync(BuildRequest(mediaPath, marks: marks));

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(_tmpDir, "marks-unicode.bundle", "marks.json")));
        var serialized = doc.RootElement.GetProperty("marks").EnumerateArray().ToArray();
        Assert.Equal(2, serialized.Length);
        Assert.Equal(1234, serialized[0].GetProperty("t_ms").GetInt64());
        Assert.Equal("重要决定 😀", serialized[0].GetProperty("label").GetString());
        Assert.Equal("agent", serialized[0].GetProperty("source").GetString());
        Assert.Equal(5678, serialized[1].GetProperty("t_ms").GetInt64());
        Assert.Equal("第二章", serialized[1].GetProperty("label").GetString());
        Assert.Equal("hotkey", serialized[1].GetProperty("source").GetString());
    }

    [Fact]
    public void RecordingBundleRequest_CopiesMarksAndDoesNotExposeMutableList()
    {
        var source = new List<RecordingMark>
        {
            new(10, "before", "agent")
        };
        var request = BuildRequest(MediaPath("snapshot"), marks: source);
        source.Add(new RecordingMark(20, "after", "agent"));

        Assert.Single(request.Marks);
        Assert.Equal("before", request.Marks[0].Label);
        var mutableView = Assert.IsAssignableFrom<IList<RecordingMark>>(request.Marks);
        Assert.Throws<NotSupportedException>(() => mutableView.Add(new RecordingMark(30, "blocked", "agent")));
    }

    [Fact]
    public async Task GenerateAsync_JsonFiles_AreUtf8WithoutBom()
    {
        var mediaPath = MediaPath("encoding");
        File.WriteAllBytes(mediaPath, new byte[] { 0 });

        var generator = new FfmpegRecordingBundleGenerator(new FakeExternalProcessRunner().WithSuccess(), ffmpegPathProvider: FakeFfmpegPathProvider);
        await generator.GenerateAsync(BuildRequest(mediaPath));

        foreach (var name in new[] { "metadata.json", "marks.json" })
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(_tmpDir, "encoding.bundle", name));
            Assert.NotEmpty(bytes);
            Assert.NotEqual(0xEF, bytes[0]);
            Assert.NotEqual(0xBB, bytes[1]);
            Assert.NotEqual(0xBF, bytes[2]);
        }
    }

    [Fact]
    public async Task GenerateAsync_FrameExtractTimeout_MapsToFrameExtractFailed()
    {
        var mediaPath = MediaPath("timeout");
        File.WriteAllBytes(mediaPath, new byte[] { 0 });

        var runner = new FakeExternalProcessRunner().WithTimeout();
        var generator = new FfmpegRecordingBundleGenerator(runner, TimeSpan.FromMilliseconds(1), ffmpegPathProvider: FakeFfmpegPathProvider);
        var result = await generator.GenerateAsync(BuildRequest(mediaPath));

        Assert.False(result.Success);
        Assert.Equal(RecordingBundleErrorCodes.FrameExtractFailed, result.ErrorCode);
        Assert.False(Directory.Exists(Path.Combine(_tmpDir, "timeout.bundle")));
        Assert.Empty(Directory.GetDirectories(_tmpDir, ".timeout.bundle.tmp-*"));
    }

    [Fact]
    public async Task GenerateAsync_NonZeroExit_MapsToFrameExtractFailed()
    {
        var mediaPath = MediaPath("nonzero");
        File.WriteAllBytes(mediaPath, new byte[] { 0 });

        var runner = new FakeExternalProcessRunner().WithExitCode(1);
        var generator = new FfmpegRecordingBundleGenerator(runner, ffmpegPathProvider: FakeFfmpegPathProvider);
        var result = await generator.GenerateAsync(BuildRequest(mediaPath));

        Assert.False(result.Success);
        Assert.Equal(RecordingBundleErrorCodes.FrameExtractFailed, result.ErrorCode);
    }

    [Fact]
    public async Task GenerateAsync_InvalidImageFile_MapsToFrameOutputInvalid()
    {
        var mediaPath = MediaPath("badimage");
        File.WriteAllBytes(mediaPath, new byte[] { 0 });

        // Runner succeeds but writes no image bytes.
        var runner = new FakeExternalProcessRunner(runAfterSuccess: (args, path) => { });
        var generator = new FfmpegRecordingBundleGenerator(runner, ffmpegPathProvider: FakeFfmpegPathProvider);
        var result = await generator.GenerateAsync(BuildRequest(mediaPath));

        Assert.False(result.Success);
        Assert.Equal(RecordingBundleErrorCodes.FrameOutputInvalid, result.ErrorCode);
    }

    [Fact]
    public async Task GenerateAsync_Failure_DoesNotDeleteMainMedia()
    {
        var mediaPath = MediaPath("keepmedia");
        File.WriteAllBytes(mediaPath, new byte[] { 0 });

        var runner = new FakeExternalProcessRunner().WithTimeout();
        var generator = new FfmpegRecordingBundleGenerator(runner, TimeSpan.FromMilliseconds(1), ffmpegPathProvider: FakeFfmpegPathProvider);
        await generator.GenerateAsync(BuildRequest(mediaPath));

        Assert.True(File.Exists(mediaPath));
    }

    [Theory]
    [InlineData(1920, 1080, "'min(640,iw)':-1")]   // landscape
    [InlineData(1080, 1920, "-1:'min(640,ih)'")]   // portrait
    [InlineData(320, 240, "'min(640,iw)':-1")]     // small landscape - no upscale
    public async Task ThumbnailScaleFilter_RespectsOrientationAndNeverUpscales(int w, int h, string expected)
    {
        var runner = new FakeExternalProcessRunner();
        var generator = new FfmpegRecordingBundleGenerator(runner, ffmpegPathProvider: FakeFfmpegPathProvider);
        var mediaPath = MediaPath("scale");
        File.WriteAllBytes(mediaPath, new byte[] { 0 });
        var request = BuildRequest(mediaPath, width: w, height: h, actualDuration: 2.0);

        await generator.GenerateAsync(request);

        var call = runner.Calls.First(c => c.Args.Contains("-vf"));
        var argsList = call.Args.ToList();
        var vfIndex = argsList.IndexOf("-vf");
        Assert.Equal($"scale={expected}", argsList[vfIndex + 1]);
    }

    [Fact]
    public async Task ArgumentList_WithSpecialCharacters_DoesNotBreakShellBoundaries()
    {
        var runner = new FakeExternalProcessRunner();
        var generator = new FfmpegRecordingBundleGenerator(runner, ffmpegPathProvider: FakeFfmpegPathProvider);
        var specialDir = Path.Combine(_tmpDir, "path with spaces 中文 ' quote");
        Directory.CreateDirectory(specialDir);
        var mediaPath = Path.Combine(specialDir, "media.mp4");
        File.WriteAllBytes(mediaPath, new byte[] { 0 });
        var request = BuildRequest(mediaPath);

        await generator.GenerateAsync(request);

        foreach (var call in runner.Calls)
        {
            // ArgumentList must preserve the path as a single argument; no shell splitting.
            var argsList = call.Args.ToList();
            var inputIndex = argsList.IndexOf("-i");
            Assert.Equal(mediaPath, argsList[inputIndex + 1]);
        }
    }

    [Fact]
    public void BundleSnapshot_Ready_ContentsFixedOrder()
    {
        var bundlePath = Path.Combine(_tmpDir, "demo.bundle");
        var snapshot = RecordingBundleSnapshot.Ready(bundlePath, new List<RecordingBundleContentItem>
        {
            new("metadata.json", "application/json", 1),
            new("thumbnail.jpg", "image/jpeg", 2),
            new("first_frame.png", "image/png", 3),
            new("last_frame.png", "image/png", 4),
            new("marks.json", "application/json", 5)
        });

        Assert.Equal("ready", snapshot.Status);
        Assert.Null(snapshot.ErrorCode);
        Assert.Equal(5, snapshot.Contents.Count);
        Assert.Equal(new[] { "metadata.json", "thumbnail.jpg", "first_frame.png", "last_frame.png", "marks.json" },
            snapshot.Contents.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void BundleSnapshot_Pending_HasNullPathAndEmptyContents()
    {
        var snapshot = RecordingBundleSnapshot.Pending();
        Assert.Equal("pending", snapshot.Status);
        Assert.Null(snapshot.Path);
        Assert.Empty(snapshot.Contents);
        Assert.Null(snapshot.ErrorCode);
    }

    [Fact]
    public void BundleSnapshot_Generating_UsesExpectedPathAndEmptyContents()
    {
        var bundlePath = Path.Combine(_tmpDir, "demo.bundle");
        var snapshot = RecordingBundleSnapshot.Generating(bundlePath);
        Assert.Equal("generating", snapshot.Status);
        Assert.Equal(bundlePath, snapshot.Path);
        Assert.Empty(snapshot.Contents);
        Assert.Null(snapshot.ErrorCode);
    }

    [Fact]
    public void BundleSnapshot_Failed_HasErrorCode()
    {
        var bundlePath = Path.Combine(_tmpDir, "demo.bundle");
        var snapshot = RecordingBundleSnapshot.Failed(bundlePath, "bundle_hash_failed");
        Assert.Equal("failed", snapshot.Status);
        Assert.Equal("bundle_hash_failed", snapshot.ErrorCode);
        Assert.Equal(bundlePath, snapshot.Path);
        Assert.Empty(snapshot.Contents);
    }

    [Fact]
    public void BundleSnapshot_NotApplicable_HasNoPath()
    {
        var snapshot = RecordingBundleSnapshot.NotApplicable();
        Assert.Equal("not_applicable", snapshot.Status);
        Assert.Null(snapshot.Path);
        Assert.Empty(snapshot.Contents);
        Assert.Null(snapshot.ErrorCode);
    }

    [Fact]
    public void CreateRecording_ResponseIncludesBundlePending()
    {
        var audit = new CaptureAuditLogger();
        var engine = new RecordingEngine(audit);
        engine.SetTray(new NoOpTray());
        // Force no-confirmation path for direct creation.
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1");
        try
        {
            var cfg = ConfigWithTempOutput(_tmpDir);
            var resp = engine.CreateRecording(cfg, "test", new NoOpTray());
            var json = JsonSerializer.Serialize(resp);
            using var doc = JsonDocument.Parse(json);
            var bundle = doc.RootElement.GetProperty("bundle");
            Assert.Equal("pending", bundle.GetProperty("status").GetString());
            Assert.True(bundle.GetProperty("path").ValueKind == JsonValueKind.Null);
            Assert.Empty(bundle.GetProperty("contents").EnumerateArray());
            Assert.True(bundle.GetProperty("error_code").ValueKind == JsonValueKind.Null);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "0");
        }
    }

    [Fact]
    public void GetStatus_Output_List_StatusWait_IncludeBundle()
    {
        var audit = new CaptureAuditLogger();
        var engine = new RecordingEngine(audit);
        engine.SetTray(new NoOpTray());
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1");
        try
        {
            var cfg = ConfigWithTempOutput(_tmpDir);
            var resp = engine.CreateRecording(cfg, "test", new NoOpTray());
            var recId = JsonDocument.Parse(JsonSerializer.Serialize(resp)).RootElement.GetProperty("recording_id").GetString()!;

            Assert.True(JsonDocument.Parse(JsonSerializer.Serialize(engine.GetStatus(recId))).RootElement.TryGetProperty("bundle", out _));
            Assert.True(JsonDocument.Parse(JsonSerializer.Serialize(engine.GetOutput(recId))).RootElement.TryGetProperty("bundle", out _));
            Assert.True(JsonDocument.Parse(JsonSerializer.Serialize(engine.GetStatusWait(recId, "recording", 100))).RootElement.TryGetProperty("Bundle", out _));
            Assert.True(JsonDocument.Parse(JsonSerializer.Serialize(engine.List().First())).RootElement.TryGetProperty("bundle", out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "0");
        }
    }

    [Fact]
    public void RecordingEngine_WithoutBundleGenerator_MarksNotApplicable()
    {
        var audit = new CaptureAuditLogger();
        var engine = new RecordingEngine(audit); // no generator
        engine.SetTray(new NoOpTray());

        var backend = new FakeCaptureBackend();
        engine.BackendFactory = _ => (backend, "ffmpeg");

        var rec = new Recording
        {
            SourceType = "region",
            BackendType = "ffmpeg",
            OutputPath = MediaPath("no-gen"),
            Config = new CaptureConfig { SourceKind = "region", Bounds = (0, 0, 100, 100), Fps = 30, OutputPath = MediaPath("no-gen") }
        };
        rec.LastMeta = new OutputMeta { Container = "mp4", Codec = "h264", DurationSeconds = 1.0, SizeBytes = 1024, Width = 100, Height = 100 };
        engine.StartCaptureForTests(rec, new NoOpTray());

        backend.FireNaturalExit(0, rec.LastMeta);

        Assert.Equal(RecState.completed, rec.State);
        Assert.Equal("not_applicable", rec.BundleSnapshot.Status);
    }

    [Fact]
    public void RecordingEngine_FfmpegMp4Success_TriggersBundleGenerationOnce()
    {
        var audit = new CaptureAuditLogger();
        var generator = new CountingBundleGenerator();
        var engine = new RecordingEngine(audit, bundleGenerator: generator);
        engine.SetTray(new NoOpTray());

        var backend = new FakeCaptureBackend();
        engine.BackendFactory = _ => (backend, "ffmpeg");

        var rec = new Recording
        {
            SourceType = "region",
            BackendType = "ffmpeg",
            OutputPath = MediaPath("mp4-once"),
            Config = new CaptureConfig { SourceKind = "region", Bounds = (0, 0, 100, 100), Fps = 30, OutputPath = MediaPath("mp4-once") }
        };
        rec.LastMeta = new OutputMeta { Container = "mp4", Codec = "h264", DurationSeconds = 1.0, SizeBytes = 1024, Width = 100, Height = 100 };
        engine.StartCaptureForTests(rec, new NoOpTray());

        backend.FireNaturalExit(0, rec.LastMeta);

        Assert.Equal(RecState.completed, rec.State);
        // The fake generator returns synchronously, so the snapshot may already be
        // "ready" by the time we observe it. The important invariant is that
        // generation was triggered exactly once.
        SpinWait.SpinUntil(() => generator.CallCount > 0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, generator.CallCount);
        Assert.True(rec.BundleSnapshot.Status is "generating" or "ready");
    }

    [Fact]
    public void RecordingEngine_FailedRecording_DoesNotTriggerBundleGeneration()
    {
        var audit = new CaptureAuditLogger();
        var generator = new CountingBundleGenerator();
        var engine = new RecordingEngine(audit, bundleGenerator: generator);
        engine.SetTray(new NoOpTray());

        var backend = new FakeCaptureBackend();
        engine.BackendFactory = _ => (backend, "ffmpeg");

        var rec = new Recording
        {
            SourceType = "region",
            BackendType = "ffmpeg",
            OutputPath = MediaPath("failed-no-bundle"),
            Config = new CaptureConfig { SourceKind = "region", Bounds = (0, 0, 100, 100), Fps = 30, OutputPath = MediaPath("failed-no-bundle") }
        };
        rec.LastMeta = new OutputMeta { Container = "mp4", Codec = "h264", DurationSeconds = 0, SizeBytes = 0, Width = 0, Height = 0 };
        engine.StartCaptureForTests(rec, new NoOpTray());

        backend.FireNaturalExit(1, rec.LastMeta);

        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal("not_applicable", rec.BundleSnapshot.Status);
        Assert.Equal(0, generator.CallCount);
    }

    [Theory]
    [InlineData("ffmpeg", true)]
    [InlineData("ffmpeg-region", true)]
    [InlineData("ffmpeg-window-region", true)]
    [InlineData("wgc-continuous", false)]
    [InlineData("some-unknown-backend", false)]
    public void RecordingEngine_BackendEligibility_OnlyFfmpegMp4BackendsGenerateBundle(string backendType, bool shouldGenerate)
    {
        var audit = new CaptureAuditLogger();
        var generator = new CapturingBundleGenerator();
        var engine = new RecordingEngine(audit, bundleGenerator: generator);
        engine.SetTray(new NoOpTray());

        var backend = new FakeCaptureBackend();
        engine.BackendFactory = _ => (backend, backendType);

        var rec = new Recording
        {
            SourceType = backendType == "ffmpeg" ? "display" : "region",
            BackendType = backendType,
            OutputPath = MediaPath("eligible-" + backendType),
            Config = new CaptureConfig { SourceKind = "region", Bounds = (0, 0, 100, 100), Fps = 30, OutputPath = MediaPath("eligible-" + backendType) }
        };
        rec.LastMeta = new OutputMeta { Container = "mp4", Codec = "h264", DurationSeconds = 1.0, SizeBytes = 1024, Width = 100, Height = 100 };
        engine.StartCaptureForTests(rec, new NoOpTray());

        backend.FireNaturalExit(0, rec.LastMeta);

        if (shouldGenerate)
        {
            SpinWait.SpinUntil(() => generator.CallCount > 0, TimeSpan.FromSeconds(2));
            Assert.Equal(1, generator.CallCount);
            Assert.Equal(backendType, generator.LastRequest?.Backend);
            Assert.True(rec.BundleSnapshot.Status is "generating" or "ready");
        }
        else
        {
            Assert.Equal(0, generator.CallCount);
            Assert.Equal("not_applicable", rec.BundleSnapshot.Status);
        }
    }

    [Theory]
    [InlineData("display", "ffmpeg")]
    [InlineData("region", "ffmpeg-region")]
    [InlineData("window", "ffmpeg-window-region")]
    public void RecordingEngine_MetadataBackend_WritesActualBackendIdentifier(string sourceType, string backendType)
    {
        var audit = new CaptureAuditLogger();
        var generator = new CapturingBundleGenerator();
        var engine = new RecordingEngine(audit, bundleGenerator: generator);
        engine.SetTray(new NoOpTray());

        var backend = new FakeCaptureBackend();
        engine.BackendFactory = _ => (backend, backendType);

        var rec = new Recording
        {
            SourceType = sourceType,
            BackendType = backendType,
            OutputPath = MediaPath("backend-" + sourceType),
            Config = new CaptureConfig { SourceKind = sourceType, Bounds = (0, 0, 100, 100), Fps = 30, OutputPath = MediaPath("backend-" + sourceType) }
        };
        engine.StartCaptureForTests(rec, new NoOpTray());

        backend.FireNaturalExit(0, new OutputMeta { Container = "mp4", Codec = "h264", DurationSeconds = 1.0, SizeBytes = 1024, Width = 100, Height = 100 });

        SpinWait.SpinUntil(() => generator.CallCount > 0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, generator.CallCount);
        Assert.Equal(backendType, generator.LastRequest?.Backend);
    }

    [Fact]
    public void RecordingEngine_UserRejectedConfirmation_BundleIsNotApplicable()
    {
        var audit = new CaptureAuditLogger();
        var generator = new CountingBundleGenerator();
        var engine = new RecordingEngine(audit, bundleGenerator: generator);
        engine.SetTray(new NoOpTray());
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1");
        try
        {
            var tray = new CallbackTray((_, cb) => { cb(new ConfirmationDecision(false)); return true; });
            var cfg = ConfigWithTempOutput(_tmpDir);
            var resp = engine.CreateRecording(cfg, "test", tray);
            var recId = JsonDocument.Parse(JsonSerializer.Serialize(resp)).RootElement.GetProperty("recording_id").GetString()!;
            var rec = engine._recs[recId];
            Assert.Equal(RecState.rejected, rec.State);
            Assert.Equal("not_applicable", rec.BundleSnapshot.Status);
            Assert.Equal(0, generator.CallCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "0");
        }
    }

    [Fact]
    public void RecordingEngine_OutputDirectoryOverrideFailed_BundleIsNotApplicable()
    {
        var audit = new CaptureAuditLogger();
        var generator = new CountingBundleGenerator();
        var engine = new RecordingEngine(audit, bundleGenerator: generator);
        engine.SetTray(new NoOpTray());
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1");
        try
        {
            var tray = new CallbackTray((_, cb) => { cb(new ConfirmationDecision(true, "\\\\invalid\\path")); return true; });
            var cfg = ConfigWithTempOutput(_tmpDir);
            var resp = engine.CreateRecording(cfg, "test", tray);
            var recId = JsonDocument.Parse(JsonSerializer.Serialize(resp)).RootElement.GetProperty("recording_id").GetString()!;
            var rec = engine._recs[recId];
            Assert.Equal(RecState.rejected, rec.State);
            Assert.Equal("not_applicable", rec.BundleSnapshot.Status);
            Assert.Equal(0, generator.CallCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "0");
        }
    }

    [Fact]
    public void RecordingEngine_ConfirmationExpired_BundleIsNotApplicable()
    {
        var audit = new CaptureAuditLogger();
        var generator = new CountingBundleGenerator();
        var engine = new RecordingEngine(audit, bundleGenerator: generator);
        engine.SetTray(new NoOpTray());
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1");
        try
        {
            var tray = new NoOpTray();
            var cfg = ConfigWithTempOutput(_tmpDir);
            var resp = engine.CreateRecording(cfg, "test", tray);
            var doc = JsonDocument.Parse(JsonSerializer.Serialize(resp));
            var recId = doc.RootElement.GetProperty("recording_id").GetString()!;
            var confId = doc.RootElement.GetProperty("confirmation_id").GetString()!;
            engine.TriggerConfirmationExpiryForTests(confId);
            var rec = engine._recs[recId];
            Assert.Equal(RecState.expired, rec.State);
            Assert.Equal("not_applicable", rec.BundleSnapshot.Status);
            Assert.Equal(0, generator.CallCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "0");
        }
    }

    [Fact]
    public void RecordingEngine_PreflightBeforeStartFailed_BundleIsNotApplicable()
    {
        var audit = new CaptureAuditLogger();
        var generator = new CountingBundleGenerator();
        var engine = new RecordingEngine(audit, bundleGenerator: generator);
        engine.SetTray(new NoOpTray());
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1");
        try
        {
            var tray = new CallbackTray((_, cb) =>
            {
                RecordingPreflightChecker.EncoderProvider = (out string? ffmpegPath, out string? ffprobePath) =>
                {
                    ffmpegPath = null;
                    ffprobePath = null;
                    return false;
                };
                cb(new ConfirmationDecision(true));
                return true;
            });
            var cfg = ConfigWithTempOutput(_tmpDir);
            var resp = engine.CreateRecording(cfg, "test", tray);
            var recId = JsonDocument.Parse(JsonSerializer.Serialize(resp)).RootElement.GetProperty("recording_id").GetString()!;
            var rec = engine._recs[recId];
            Assert.Equal(RecState.failed, rec.State);
            Assert.Equal("not_applicable", rec.BundleSnapshot.Status);
            Assert.Equal(0, generator.CallCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "0");
            RecordingPreflightChecker.EncoderProvider = RecordingPreflightChecker.DefaultEncoderProvider;
        }
    }

    [Fact]
    public void RecordingEngine_BackendStartException_BundleIsNotApplicable()
    {
        var audit = new CaptureAuditLogger();
        var generator = new CountingBundleGenerator();
        var engine = new RecordingEngine(audit, bundleGenerator: generator);
        engine.SetTray(new NoOpTray());

        var rec = new Recording
        {
            SourceType = "region",
            BackendType = "ffmpeg",
            CountdownSeconds = 0,
            OutputPath = MediaPath("start-exc"),
            Config = new CaptureConfig { SourceKind = "region", CountdownSeconds = 0, Bounds = (0, 0, 100, 100), Fps = 30, OutputPath = MediaPath("start-exc") }
        };
        engine.BackendFactory = _ => (new ThrowingCaptureBackend(), "ffmpeg");
        engine.StartCaptureForTests(rec, new NoOpTray());

        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal("not_applicable", rec.BundleSnapshot.Status);
        Assert.Equal(0, generator.CallCount);
    }

    [Fact]
    public async Task RecordingEngine_CompletedStateNeverPublishedWithPendingBundle()
    {
        var audit = new CaptureAuditLogger();
        var generator = new BlockingBundleGenerator();
        var engine = new RecordingEngine(audit, bundleGenerator: generator);
        engine.SetTray(new NoOpTray());

        var backend = new FakeCaptureBackend();
        engine.BackendFactory = _ => (backend, "ffmpeg");

        var rec = new Recording
        {
            SourceType = "region",
            BackendType = "ffmpeg",
            OutputPath = MediaPath("race-pending"),
            Config = new CaptureConfig { SourceKind = "region", Bounds = (0, 0, 100, 100), Fps = 30, OutputPath = MediaPath("race-pending") }
        };
        engine.StartCaptureForTests(rec, new NoOpTray());

        generator.Release = new TaskCompletionSource();
        backend.FireNaturalExit(0, new OutputMeta { Container = "mp4", Codec = "h264", DurationSeconds = 1.0, SizeBytes = 1024, Width = 100, Height = 100 });

        await generator.Entered;
        Assert.Equal(RecState.completed, rec.State);
        Assert.Equal("generating", rec.BundleSnapshot.Status);

        generator.Release.SetResult();
    }

    [Fact]
    public void RecordingEngine_MicrophoneMissingAudioTrack_MergesWarningIntoRecording()
    {
        var audit = new CaptureAuditLogger();
        var engine = new RecordingEngine(audit, bundleGenerator: new CountingBundleGenerator());
        engine.SetTray(new NoOpTray());

        var backend = new FakeCaptureBackend();
        engine.BackendFactory = _ => (backend, "ffmpeg");

        var rec = new Recording
        {
            SourceType = "region",
            BackendType = "ffmpeg",
            Microphone = true,
            OutputPath = MediaPath("mic-warning"),
            Config = new CaptureConfig { SourceKind = "region", Bounds = (0, 0, 100, 100), Fps = 30, OutputPath = MediaPath("mic-warning"), Microphone = true }
        };
        engine.StartCaptureForTests(rec, new NoOpTray());

        backend.FireNaturalExit(0, new OutputMeta
        {
            Container = "mp4",
            Codec = "h264",
            DurationSeconds = 1.0,
            SizeBytes = 1024,
            Width = 100,
            Height = 100,
            AudioStatus = "missing_audio_track",
            Warnings = new[] { "microphone_missing_audio_track: the output does not contain an AAC audio stream" }
        });

        Assert.Equal(RecState.failed, rec.State);
        Assert.Contains(rec.Warnings, w => w.Contains("microphone_missing_audio_track"));
    }

    [Fact]
    public void RecordingEngine_MicrophoneLostStderrButNoAudioTrack_FailsWithMissingAudioTrack()
    {
        var audit = new CaptureAuditLogger();
        var generator = new CapturingBundleGenerator();
        var engine = new RecordingEngine(audit, bundleGenerator: generator);
        engine.SetTray(new NoOpTray());

        var backend = new FakeCaptureBackend();
        engine.BackendFactory = _ => (backend, "ffmpeg");

        var rec = new Recording
        {
            SourceType = "region",
            BackendType = "ffmpeg",
            Microphone = true,
            OutputPath = MediaPath("mic-lost-no-track"),
            Config = new CaptureConfig { SourceKind = "region", Bounds = (0, 0, 100, 100), Fps = 30, OutputPath = MediaPath("mic-lost-no-track"), Microphone = true }
        };
        engine.StartCaptureForTests(rec, new NoOpTray());

        // Simulate runtime-loss stderr but no actual audio stream evidence.
        backend.FireNaturalExit(0, new OutputMeta
        {
            Container = "mp4",
            Codec = "h264",
            DurationSeconds = 1.0,
            SizeBytes = 1024,
            Width = 100,
            Height = 100,
            AudioStatus = "missing_audio_track",
            Warnings = new[] { "microphone_missing_audio_track: the output does not contain an AAC audio stream" }
        });

        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal("not_applicable", rec.BundleSnapshot.Status);
        Assert.Equal(0, generator.CallCount);
        Assert.Contains(rec.Warnings, w => w.Contains("microphone_missing_audio_track"));
    }

    [Fact]
    public void RecordingEngine_MicrophoneAacTrackWithLostStderr_CompletesWithBundleAudioStatusLost()
    {
        var audit = new CaptureAuditLogger();
        var generator = new CapturingBundleGenerator();
        var engine = new RecordingEngine(audit, bundleGenerator: generator);
        engine.SetTray(new NoOpTray());

        var backend = new FakeCaptureBackend();
        engine.BackendFactory = _ => (backend, "ffmpeg");

        var rec = new Recording
        {
            SourceType = "region",
            BackendType = "ffmpeg",
            Microphone = true,
            MicrophoneDeviceId = "mic_1",
            OutputPath = MediaPath("mic-lost-with-track"),
            Config = new CaptureConfig { SourceKind = "region", Bounds = (0, 0, 100, 100), Fps = 30, OutputPath = MediaPath("mic-lost-with-track"), Microphone = true }
        };
        engine.StartCaptureForTests(rec, new NoOpTray());

        backend.FireNaturalExit(0, new OutputMeta
        {
            Container = "mp4",
            Codec = "h264",
            DurationSeconds = 1.0,
            SizeBytes = 1024,
            Width = 100,
            Height = 100,
            HasAudioStream = true,
            AudioCodec = "aac",
            AudioStatus = "lost",
            Warnings = new[] { "microphone_lost: audio input was lost during recording" }
        });

        Assert.Equal(RecState.completed, rec.State);
        SpinWait.SpinUntil(() => generator.CallCount > 0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, generator.CallCount);
        Assert.NotNull(generator.LastRequest);
        Assert.True(rec.BundleSnapshot.Status is "generating" or "ready");
        Assert.True(generator.LastRequest!.AudioMicrophone);
        Assert.Equal("lost", generator.LastRequest.AudioStatus);
        Assert.Equal("mic_1", generator.LastRequest.AudioDeviceId);
        Assert.Contains(rec.Warnings, w => w.Contains("microphone_lost"));
    }

    [Fact]
    public async Task RecordingEngine_StopResponse_StoppingState_IncludesBundleSchema()
    {
        var audit = new CaptureAuditLogger();
        var engine = new RecordingEngine(audit, bundleGenerator: new CountingBundleGenerator());
        engine.SetTray(new NoOpTray());

        var backend = new SlowFakeCaptureBackend();
        engine.BackendFactory = _ => (backend, "ffmpeg");

        var rec = new Recording
        {
            SourceType = "region",
            BackendType = "ffmpeg",
            CountdownSeconds = 0,
            OutputPath = MediaPath("stopping"),
            Config = new CaptureConfig { SourceKind = "region", CountdownSeconds = 0, Bounds = (0, 0, 100, 100), Fps = 30, OutputPath = MediaPath("stopping") }
        };
        engine.StartCaptureForTests(rec, new NoOpTray());

        backend.StopResult = new OutputMeta { Container = "mp4", Codec = "h264", DurationSeconds = 1.0, SizeBytes = 1024, Width = 100, Height = 100 };

        var stop1Task = Task.Run(() => engine.Stop(rec.Id, "user_requested"));
        try
        {
            await backend.EnteredStop.WaitAsync(TimeSpan.FromSeconds(5));

            // Concurrent second stop observes the stopping state and must still include bundle schema.
            var resp2 = engine.Stop(rec.Id, "user_requested");
            var json2 = JsonSerializer.Serialize(resp2);
            using var doc2 = JsonDocument.Parse(json2);
            Assert.Equal("stopping", doc2.RootElement.GetProperty("status").GetString());
            Assert.True(doc2.RootElement.TryGetProperty("bundle", out var bundle));
            Assert.Equal("pending", bundle.GetProperty("status").GetString());
        }
        finally
        {
            backend.Release();
            await stop1Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task RecordingEngine_ConcurrentSecondStop_ResponseIncludesBundleSchema()
    {
        var audit = new CaptureAuditLogger();
        var engine = new RecordingEngine(audit, bundleGenerator: new CountingBundleGenerator());
        engine.SetTray(new NoOpTray());

        var backend = new SlowFakeCaptureBackend();
        engine.BackendFactory = _ => (backend, "ffmpeg");

        var rec = new Recording
        {
            SourceType = "region",
            BackendType = "ffmpeg",
            OutputPath = MediaPath("stopping-concurrent"),
            Config = new CaptureConfig { SourceKind = "region", Bounds = (0, 0, 100, 100), Fps = 30, OutputPath = MediaPath("stopping-concurrent") }
        };
        engine.StartCaptureForTests(rec, new NoOpTray());

        backend.StopResult = new OutputMeta { Container = "mp4", Codec = "h264", DurationSeconds = 1.0, SizeBytes = 1024, Width = 100, Height = 100 };

        var stop1Task = Task.Run(() => engine.Stop(rec.Id, "user_requested"));
        object resp1;
        object resp2;
        try
        {
            await backend.EnteredStop.WaitAsync(TimeSpan.FromSeconds(5));
            resp2 = engine.Stop(rec.Id, "user_requested");
        }
        finally
        {
            backend.Release();
            resp1 = await stop1Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        foreach (var resp in new[] { resp1, resp2 })
        {
            var json = JsonSerializer.Serialize(resp);
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.TryGetProperty("bundle", out var bundle), "stop response missing bundle");
            Assert.True(bundle.TryGetProperty("status", out _));
            Assert.True(bundle.TryGetProperty("path", out _));
            Assert.True(bundle.TryGetProperty("contents", out _));
            Assert.True(bundle.TryGetProperty("error_code", out _));
        }
    }

    [Theory]
    [InlineData("ffmpeg", true)]
    [InlineData("ffmpeg-region", true)]
    [InlineData("ffmpeg-window-region", true)]
    [InlineData("wgc-continuous", false)]
    [InlineData("ffmpeg-window", false)]
    [InlineData("", false)]
    public void CaptureBackendSelector_IsFfmpegMp4Backend_RecognizesProductionBackends(string backendType, bool expected)
    {
        Assert.Equal(expected, CaptureBackendSelector.IsFfmpegMp4Backend(backendType));
    }

    [Fact]
    public async Task RecordingEngine_BundleFailure_KeepsRecordingCompleted_AndLogsSingleBundleFailed()
    {
        var audit = new CaptureAuditLogger();
        var generator = new FailingBundleGenerator("bundle_frame_extract_failed");
        var engine = new RecordingEngine(audit, bundleGenerator: generator);
        engine.SetTray(new NoOpTray());

        var backend = new FakeCaptureBackend();
        engine.BackendFactory = _ => (backend, "ffmpeg");

        var rec = new Recording
        {
            SourceType = "region",
            BackendType = "ffmpeg",
            OutputPath = MediaPath("bundle-fail"),
            Config = new CaptureConfig { SourceKind = "region", Bounds = (0, 0, 100, 100), Fps = 30, OutputPath = MediaPath("bundle-fail") }
        };
        rec.LastMeta = new OutputMeta { Container = "mp4", Codec = "h264", DurationSeconds = 1.0, SizeBytes = 1024, Width = 100, Height = 100 };
        engine.StartCaptureForTests(rec, new NoOpTray());

        backend.FireNaturalExit(0, rec.LastMeta);

        await Task.Delay(100);
        Assert.Equal(RecState.completed, rec.State);
        Assert.Equal("failed", rec.BundleSnapshot.Status);
        Assert.Equal("bundle_frame_extract_failed", rec.BundleSnapshot.ErrorCode);
        Assert.Single(audit.Events, e => e.evt == "recording.bundle_failed");
    }

    [Fact]
    public async Task RecordingEngine_StopAndNaturalExitRace_OnlyGeneratesBundleOnce()
    {
        var audit = new CaptureAuditLogger();
        var generator = new CountingBundleGenerator();
        var engine = new RecordingEngine(audit, bundleGenerator: generator);
        var tray = new NoOpTray();
        engine.SetTray(tray);

        var backend = new FakeCaptureBackend();
        engine.BackendFactory = _ => (backend, "ffmpeg");

        var rec = new Recording
        {
            SourceType = "region",
            BackendType = "ffmpeg",
            CountdownSeconds = 0,
            OutputPath = MediaPath("race"),
            Config = new CaptureConfig { SourceKind = "region", CountdownSeconds = 0, Bounds = (0, 0, 100, 100), Fps = 30, OutputPath = MediaPath("race") }
        };
        var meta = new OutputMeta { Container = "mp4", Codec = "h264", DurationSeconds = 1.0, SizeBytes = 1024, Width = 100, Height = 100 };
        rec.LastMeta = meta;
        engine.StartCaptureForTests(rec, tray);

        backend.StopResult = meta;

        // Fire natural exit and explicit stop concurrently many times.
        var tasks = new List<Task>();
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(Task.Run(() => backend.FireNaturalExit(0, meta)));
            tasks.Add(Task.Run(() =>
            {
                try { engine.Stop(rec.Id, "user_requested"); } catch { }
            }));
        }
        await Task.WhenAll(tasks);

        SpinWait.SpinUntil(() => generator.CallCount >= 1, TimeSpan.FromSeconds(2));
        Assert.Equal(1, generator.CallCount);
    }

    [Fact]
    public async Task RecordingEngine_ConfirmationOutputDirectoryOverride_BundleUsesNewPath()
    {
        var audit = new CaptureAuditLogger();
        var generator = new CapturingBundleGenerator();
        var engine = new RecordingEngine(audit, bundleGenerator: generator);
        var tray = new NoOpTray();
        engine.SetTray(tray);

        var backend = new FakeCaptureBackend();
        engine.BackendFactory = _ => (backend, "ffmpeg");

        var originalDir = _tmpDir;
        var newDir = Path.Combine(_tmpDir, "overridden");
        Directory.CreateDirectory(newDir);

        var rec = new Recording
        {
            SourceType = "region",
            BackendType = "ffmpeg",
            OutputPath = MediaPath("orig"),
            Config = new CaptureConfig { SourceKind = "region", Bounds = (0, 0, 100, 100), Fps = 30, OutputPath = MediaPath("orig") }
        };
        engine.StartCaptureForTests(rec, tray);

        // Simulate user choosing a different output directory after confirmation.
        var newPath = Path.Combine(newDir, "moved.mp4");
        File.WriteAllText(newPath, "video");
        rec.OutputPath = newPath;
        rec.Config.OutputPath = newPath;

        backend.FireNaturalExit(0, new OutputMeta
        {
            Container = "mp4",
            Codec = "h264",
            DurationSeconds = 1.0,
            SizeBytes = 1024,
            Width = 100,
            Height = 100
        });

        await Task.Delay(100);
        Assert.Equal(newPath, generator.LastRequest?.MediaPath);
        Assert.EndsWith("moved.bundle", rec.BundleSnapshot.Path!);
    }

    [Fact]
    public async Task RecordingEngine_MetaOutputPath_DiffersFromRecordingOutputPath_GeneratingUsesApprovedFinalPath()
    {
        var audit = new CaptureAuditLogger();
        var generator = new CapturingBundleGenerator();
        var engine = new RecordingEngine(audit, bundleGenerator: generator);
        engine.SetTray(new NoOpTray());

        var backend = new FakeCaptureBackend();
        engine.BackendFactory = _ => (backend, "ffmpeg");

        var recOutputPath = MediaPath("recording-output");
        var metaOutputPath = MediaPath("actual-media");
        File.WriteAllText(recOutputPath, "approved final video bytes");
        File.WriteAllText(metaOutputPath, "actual video bytes");

        var rec = new Recording
        {
            SourceType = "region",
            BackendType = "ffmpeg",
            OutputPath = recOutputPath,
            Config = new CaptureConfig { SourceKind = "region", Bounds = (0, 0, 100, 100), Fps = 30, OutputPath = recOutputPath }
        };
        engine.StartCaptureForTests(rec, new NoOpTray());

        backend.FireNaturalExit(0, new OutputMeta
        {
            Container = "mp4",
            Codec = "h264",
            DurationSeconds = 1.0,
            SizeBytes = 1024,
            Width = 100,
            Height = 100,
            OutputPath = metaOutputPath
        });

        await Task.Delay(100);
        Assert.Equal(recOutputPath, generator.LastRequest?.MediaPath);
        Assert.EndsWith("recording-output.bundle", rec.BundleSnapshot.Path!);
    }

    [Fact]
    public async Task RecordingEngine_BundleReady_UpdatesSnapshotContents()
    {
        var audit = new CaptureAuditLogger();
        var bundleDir = Path.Combine(_tmpDir, "ready-bundle");
        Directory.CreateDirectory(bundleDir);
        foreach (var name in new[] { "metadata.json", "thumbnail.jpg", "first_frame.png", "last_frame.png", "marks.json" })
            File.WriteAllText(Path.Combine(bundleDir, name), name);

        var generator = new FixedBundleGenerator(bundleDir);
        var engine = new RecordingEngine(audit, bundleGenerator: generator);
        engine.SetTray(new NoOpTray());

        var backend = new FakeCaptureBackend();
        engine.BackendFactory = _ => (backend, "ffmpeg");

        var rec = new Recording
        {
            SourceType = "region",
            BackendType = "ffmpeg",
            OutputPath = MediaPath("ready"),
            Config = new CaptureConfig { SourceKind = "region", Bounds = (0, 0, 100, 100), Fps = 30, OutputPath = MediaPath("ready") }
        };
        engine.StartCaptureForTests(rec, new NoOpTray());

        backend.FireNaturalExit(0, new OutputMeta { Container = "mp4", Codec = "h264", DurationSeconds = 1.0, SizeBytes = 1024, Width = 100, Height = 100 });

        await Task.Delay(100);
        Assert.Equal("ready", rec.BundleSnapshot.Status);
        Assert.Equal(5, rec.BundleSnapshot.Contents.Count);
        Assert.All(rec.BundleSnapshot.Contents, c => Assert.True(c.SizeBytes > 0));
    }

    [Fact]
    public async Task BundledFfmpeg_GeneratesValidBundle()
    {
        var ffmpegPath = Path.GetFullPath(Path.Combine(TestHelper.FfmpegBinDir, "ffmpeg.exe"));
        Assert.True(File.Exists(ffmpegPath), $"Bundled FFmpeg not found at {ffmpegPath}");

        var mediaPath = Path.Combine(_tmpDir, "bundle-integration.mp4");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-nostats");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("testsrc=duration=1.5:size=320x240:rate=30");
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-preset");
        psi.ArgumentList.Add("ultrafast");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add("1.5");
        psi.ArgumentList.Add(mediaPath);

        using var proc = System.Diagnostics.Process.Start(psi);
        Assert.NotNull(proc);

        string? stderr = null;
        bool exited;
        const int timeoutMs = 30000;
        try
        {
            // Drain stderr in the background so a hung FFmpeg cannot block us here forever.
            var stderrTask = proc.StandardError.ReadToEndAsync();

            exited = proc.WaitForExit(timeoutMs);
            if (!exited)
            {
                try { proc.Kill(true); } catch { }
                try { proc.WaitForExit(5000); } catch { }
            }

            // Even after the process exits, the stderr stream read must be bounded.
            stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(exited, "Fixture FFmpeg process did not exit within timeout");
            Assert.Equal(0, proc.ExitCode);
        }
        finally
        {
            // Guarantee the fixture process is gone and its tree is waited on.
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(true);
                    proc.WaitForExit(5000);
                }
            }
            catch { }
        }

        Assert.True(File.Exists(mediaPath));
        Assert.True(new FileInfo(mediaPath).Length > 0);

        var generator = new FfmpegRecordingBundleGenerator(
            frameExtractTimeout: TimeSpan.FromSeconds(60),
            ffmpegPathProvider: () => ffmpegPath);
        var request = BuildRequest(mediaPath, sourceType: "region", actualDuration: 1.5, width: 320, height: 240, fps: 30);

        var result = await generator.GenerateAsync(request);

        Assert.True(result.Success, $"Bundle generation failed: {result.ErrorCode} {result.ErrorDetail}");
        var bundlePath = result.BundlePath!;
        Assert.True(Directory.Exists(bundlePath));
        Assert.Empty(Directory.GetDirectories(_tmpDir, ".bundle-integration.bundle.tmp-*"));

        var files = Directory.GetFiles(bundlePath).Select(f => Path.GetFileName(f)!).Where(f => f != null).OrderBy(f => f).ToArray();
        Assert.Equal(new[] { "first_frame.png", "last_frame.png", "marks.json", "metadata.json", "thumbnail.jpg" }, files);

        foreach (var f in files)
            Assert.True(new FileInfo(Path.Combine(bundlePath, f)).Length > 0, $"{f} is empty");

        var metadataText = await File.ReadAllTextAsync(Path.Combine(bundlePath, "metadata.json"));
        using var doc = JsonDocument.Parse(metadataText);
        Assert.Equal(1, doc.RootElement.GetProperty("bundle_version").GetInt32());

        var actualHash = await Sha256Async(mediaPath);
        Assert.Equal(actualHash, doc.RootElement.GetProperty("media").GetProperty("sha256").GetString());

        Assert.True(IsPng(Path.Combine(bundlePath, "first_frame.png")));
        Assert.True(IsPng(Path.Combine(bundlePath, "last_frame.png")));
        Assert.True(IsJpeg(Path.Combine(bundlePath, "thumbnail.jpg")));

        // first/last should be original dimensions
        var (fw, fh) = ReadPngDimensions(Path.Combine(bundlePath, "first_frame.png"));
        Assert.Equal(320, fw);
        Assert.Equal(240, fh);

        var (lw, lh) = ReadPngDimensions(Path.Combine(bundlePath, "last_frame.png"));
        Assert.Equal(320, lw);
        Assert.Equal(240, lh);

        // thumbnail must not be larger than the original 320x240 (no upscale)
        var (tw, th) = ReadJpegDimensions(Path.Combine(bundlePath, "thumbnail.jpg"));
        Assert.True(tw <= 320 && th <= 240, $"thumbnail upscaled to {tw}x{th}");
        // aspect ratio roughly preserved: 4/3 = 1.333
        var ratio = tw / (double)th;
        Assert.InRange(ratio, 1.2, 1.5);

        using var marksDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(bundlePath, "marks.json")));
        Assert.Empty(marksDoc.RootElement.GetProperty("marks").EnumerateArray());
    }

    private static async Task<string> Sha256Async(string path)
    {
        using var sha = SHA256.Create();
        await using var stream = File.OpenRead(path);
        var hash = await sha.ComputeHashAsync(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private static bool IsPng(string path)
    {
        var header = PngHeader();
        using var fs = File.OpenRead(path);
        var buf = new byte[header.Length];
        fs.ReadExactly(buf, 0, header.Length);
        return buf.SequenceEqual(header);
    }

    private static bool IsJpeg(string path)
    {
        using var fs = File.OpenRead(path);
        var buf = new byte[2];
        fs.ReadExactly(buf, 0, 2);
        return buf[0] == 0xFF && buf[1] == 0xD8;
    }

    private static (int width, int height) ReadPngDimensions(string path)
    {
        using var fs = File.OpenRead(path);
        var buf = new byte[24];
        fs.ReadExactly(buf, 0, 24);
        int width = (buf[16] << 24) | (buf[17] << 16) | (buf[18] << 8) | buf[19];
        int height = (buf[20] << 24) | (buf[21] << 16) | (buf[22] << 8) | buf[23];
        return (width, height);
    }

    private static (int width, int height) ReadJpegDimensions(string path)
    {
        using var fs = File.OpenRead(path);
        // Minimal SOF0/SOF2 parser.
        var buf = new byte[2];
        fs.ReadExactly(buf, 0, 2); // SOI
        while (fs.Position < fs.Length)
        {
            fs.ReadExactly(buf, 0, 2);
            if (buf[0] != 0xFF) continue;
            byte marker = buf[1];
            if (marker == 0xD9) break; // EOI
            if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7)) continue;
            var lenBuf = new byte[2];
            fs.ReadExactly(lenBuf, 0, 2);
            int len = (lenBuf[0] << 8) | lenBuf[1];
            if (marker == 0xC0 || marker == 0xC2)
            {
                var sof = new byte[7];
                fs.ReadExactly(sof, 0, 7);
                int h = (sof[1] << 8) | sof[2];
                int w = (sof[3] << 8) | sof[4];
                return (w, h);
            }
            if (len > 2)
                fs.Position += len - 2;
        }
        return (0, 0);
    }

    [Fact]
    public async Task GenerateAsync_LastFrameExit0NoFile_FallbackSucceeds()
    {
        var mediaPath = MediaPath("fallback-success");
        File.WriteAllText(mediaPath, "fake video");

        var runner = new FallbackSimulatingRunner(failCountBeforeSuccess: 2);
        var generator = new FfmpegRecordingBundleGenerator(runner, ffmpegPathProvider: FakeFfmpegPathProvider);
        var request = BuildRequest(mediaPath, actualDuration: 30.0);

        var result = await generator.GenerateAsync(request);

        Assert.True(result.Success);
        Assert.True(Directory.Exists(result.BundlePath));
        Assert.True(File.Exists(Path.Combine(result.BundlePath, "last_frame.png")));
        Assert.True(runner.LastFrameCalls >= 2);
    }

    [Fact]
    public async Task GenerateAsync_LastFrameAllFallbacksFail_ReturnsFrameOutputInvalidAndCleansTemp()
    {
        var mediaPath = MediaPath("fallback-fail");
        File.WriteAllText(mediaPath, "fake video");

        var runner = new FallbackSimulatingRunner(failCountBeforeSuccess: int.MaxValue);
        var generator = new FfmpegRecordingBundleGenerator(runner, ffmpegPathProvider: FakeFfmpegPathProvider);
        var request = BuildRequest(mediaPath, actualDuration: 30.0);

        var result = await generator.GenerateAsync(request);

        Assert.False(result.Success);
        Assert.Equal(RecordingBundleErrorCodes.FrameOutputInvalid, result.ErrorCode);
        Assert.Contains("last_frame_fallback_exhausted", result.ErrorDetail);

        // Temp directory must be cleaned up.
        var leftoverTempDirs = Directory.GetDirectories(_tmpDir, ".*.bundle.tmp-*");
        Assert.Empty(leftoverTempDirs);
    }

    // Helpers

    private sealed class NoOpTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(RecordingUiPresentation rec) { }
        public void SetIdle(RecordingUiPresentation rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class FakeCaptureBackend : ICaptureBackend
    {
        public OutputMeta StopResult { get; set; } = new();
        public int ExitCodeValue { get; set; }
        private Action<int, OutputMeta>? _onNaturalExit;

        public void Start(CaptureConfig cfg) => cfg.CommandArgs = "fake args";
        public OutputMeta Stop() => StopResult;
        public void OnNaturalExit(Action<int, OutputMeta> callback) => _onNaturalExit = callback;
        public int ExitCode => ExitCodeValue;
        public void Dispose() { }
        public void FireNaturalExit(int exitCode, OutputMeta meta) => _onNaturalExit?.Invoke(exitCode, meta);
    }

    private sealed class FakeExternalProcessRunner : IExternalProcessRunner
    {
        private readonly Action<IReadOnlyList<string>, string>? _afterSuccess;
        private ExternalProcessResult _result = new(0, false, "");

        public FakeExternalProcessRunner(Action<IReadOnlyList<string>, string>? runAfterSuccess = null)
        {
            _afterSuccess = runAfterSuccess;
        }

        public List<(IReadOnlyList<string> Args, string OutputPath)> Calls { get; } = new();
        public bool WasCalled => Calls.Count > 0;

        public FakeExternalProcessRunner WithSuccess()
        {
            _result = new ExternalProcessResult(0, false, "");
            return this;
        }

        public FakeExternalProcessRunner WithTimeout()
        {
            _result = new ExternalProcessResult(-1, true, "");
            return this;
        }

        public FakeExternalProcessRunner WithExitCode(int exitCode)
        {
            _result = new ExternalProcessResult(exitCode, false, "");
            return this;
        }

        public async Task<ExternalProcessResult> RunAsync(string fileName, IReadOnlyList<string> argumentList, TimeSpan timeout, bool captureStderr = true, Encoding? stderrEncoding = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((argumentList, argumentList.Last()));
            await Task.Yield();

            if (_result.ExitCode == 0 && !_result.TimedOut && _afterSuccess != null)
            {
                _afterSuccess(argumentList, argumentList.Last());
            }
            else if (_result.ExitCode == 0 && !_result.TimedOut)
            {
                // Write a minimal valid file based on extension so validation passes.
                var outputPath = argumentList.Last();
                var ext = Path.GetExtension(outputPath).ToLowerInvariant();
                if (ext == ".png")
                    File.WriteAllBytes(outputPath, PngHeader().Concat(new byte[100]).ToArray());
                else if (ext == ".jpg")
                    File.WriteAllBytes(outputPath, JpegHeader().Concat(new byte[100]).ToArray());
            }

            return _result;
        }
    }

    /// <summary>
    /// Simulates FFmpeg frame extraction: first N calls succeed with exit 0 but
    /// do not create a valid output file, then writes a valid PNG on subsequent
    /// last-frame calls. Used to exercise the -sseof fallback path.
    /// </summary>
    private sealed class FallbackSimulatingRunner : IExternalProcessRunner
    {
        private readonly int _failCountBeforeSuccess;
        private int _lastFrameCalls;
        private int _successfulLastFrameCalls;

        public FallbackSimulatingRunner(int failCountBeforeSuccess)
        {
            _failCountBeforeSuccess = failCountBeforeSuccess;
        }

        public int LastFrameCalls => _lastFrameCalls;

        public Task<ExternalProcessResult> RunAsync(string fileName, IReadOnlyList<string> argumentList, TimeSpan timeout, bool captureStderr = true, Encoding? stderrEncoding = null, CancellationToken cancellationToken = default)
        {
            var outputPath = argumentList.Last();
            var ext = Path.GetExtension(outputPath).ToLowerInvariant();
            bool isLastFrame = argumentList.Contains("-sseof");

            if (isLastFrame)
            {
                _lastFrameCalls++;
                if (_successfulLastFrameCalls < _failCountBeforeSuccess)
                {
                    _successfulLastFrameCalls++;
                    // Exit 0 but do not create the file, mimicking the real bug.
                    try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                    return Task.FromResult(new ExternalProcessResult(0, false, ""));
                }

                WriteValidFile(outputPath, ext);
                return Task.FromResult(new ExternalProcessResult(0, false, ""));
            }

            WriteValidFile(outputPath, ext);
            return Task.FromResult(new ExternalProcessResult(0, false, ""));
        }

        private static void WriteValidFile(string outputPath, string ext)
        {
            if (ext == ".png")
                File.WriteAllBytes(outputPath, PngHeader().Concat(new byte[100]).ToArray());
            else if (ext == ".jpg")
                File.WriteAllBytes(outputPath, JpegHeader().Concat(new byte[100]).ToArray());
        }
    }

    private sealed class CountingBundleGenerator : IRecordingBundleGenerator
    {
        private int _count;
        public int CallCount => _count;

        public Task<RecordingBundleGenerationResult> GenerateAsync(RecordingBundleRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            var dir = Path.GetDirectoryName(request.MediaPath) ?? "";
            var stem = Path.GetFileNameWithoutExtension(request.MediaPath);
            return Task.FromResult(RecordingBundleGenerationResult.Ready(Path.Combine(dir, stem + ".bundle")));
        }
    }

    private sealed class FailingBundleGenerator : IRecordingBundleGenerator
    {
        private readonly string _errorCode;
        public FailingBundleGenerator(string errorCode) => _errorCode = errorCode;

        public Task<RecordingBundleGenerationResult> GenerateAsync(RecordingBundleRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(RecordingBundleGenerationResult.Failed(_errorCode));
    }

    private sealed class FixedBundleGenerator : IRecordingBundleGenerator
    {
        private readonly string _bundlePath;
        public FixedBundleGenerator(string bundlePath) => _bundlePath = bundlePath;

        public Task<RecordingBundleGenerationResult> GenerateAsync(RecordingBundleRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(RecordingBundleGenerationResult.Ready(_bundlePath));
    }

    private sealed class CapturingBundleGenerator : IRecordingBundleGenerator
    {
        private int _count;
        public int CallCount => _count;
        public RecordingBundleRequest? LastRequest { get; private set; }

        public Task<RecordingBundleGenerationResult> GenerateAsync(RecordingBundleRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            LastRequest = request;
            var dir = Path.GetDirectoryName(request.MediaPath) ?? "";
            var stem = Path.GetFileNameWithoutExtension(request.MediaPath);
            return Task.FromResult(RecordingBundleGenerationResult.Ready(Path.Combine(dir, stem + ".bundle")));
        }
    }

    private sealed class BlockingBundleGenerator : IRecordingBundleGenerator
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task;
        public TaskCompletionSource? Release { get; set; }

        public Task<RecordingBundleGenerationResult> GenerateAsync(RecordingBundleRequest request, CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            var dir = Path.GetDirectoryName(request.MediaPath) ?? "";
            var stem = Path.GetFileNameWithoutExtension(request.MediaPath);
            var result = RecordingBundleGenerationResult.Ready(Path.Combine(dir, stem + ".bundle"));
            if (Release == null)
                return Task.FromResult(result);
            return Release.Task.ContinueWith(_ => result, cancellationToken);
        }
    }

    private sealed class CallbackTray : ITrayContext
    {
        private readonly Func<RecordingConfirmationPresentation, Action<ConfirmationDecision>, bool> _onConfirm;
        public CallbackTray(Func<RecordingConfirmationPresentation, Action<ConfirmationDecision>, bool> onConfirm) => _onConfirm = onConfirm;
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback)
        {
            if (!_onConfirm(presentation, callback))
                callback(new ConfirmationDecision(false));
        }
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(RecordingUiPresentation rec) { }
        public void SetIdle(RecordingUiPresentation rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class ThrowingCaptureBackend : ICaptureBackend
    {
        public int ExitCodeValue { get; set; }
        public void Start(CaptureConfig cfg) => throw new InvalidOperationException("backend start failed");
        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public int ExitCode => ExitCodeValue;
        public void Dispose() { }
    }

    private sealed class SlowFakeCaptureBackend : ICaptureBackend
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _enteredStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public OutputMeta StopResult { get; set; } = new();
        public Task EnteredStop => _enteredStop.Task;
        public void Release() => _release.TrySetResult();
        public void Start(CaptureConfig cfg) => cfg.CommandArgs = "fake args";
        public OutputMeta Stop() { _enteredStop.TrySetResult(); _release.Task.Wait(); return StopResult; }
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public int ExitCode => 0;
        public void Dispose() { }
    }
}
