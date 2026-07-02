using System.Text.Json;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// No-GUI boundary test for the WGC continuous recording helper IPC v2 pipeline:
/// 1. FakeProcess emits a valid Helper IPC v2 event stream (no real wgc-native-helper.exe)
/// 2. Parser parses and validates the stream into a WgcContinuousSessionSummary
/// 3. EvidenceV2Builder maps the summary + metadata to a schema v2.0 evidence JSON
/// 4. Evidence JSON is written to a temp directory mock artifact
/// 5. The JSON is validated against the schema rules by checking required fields
/// This test does NOT start a real process, does NOT capture real WGC output,
/// and does NOT write to formal matrix directories.
/// </summary>
public class WgcContinuousFakeProcessTests : IDisposable
{
    private readonly string _tempDir;

    public WgcContinuousFakeProcessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AgentRecorderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    // -----------------------------------------------------------------
    // FakeProcess: emits valid Helper IPC v2 event stream
    // -----------------------------------------------------------------

    private class FakeProcess
    {
        private readonly string _recordingId;
        private readonly string _outputPath;
        private readonly string _stopSignalPath;
        private readonly bool _stopSignalExists;

        public FakeProcess(string recordingId, string outputPath, string stopSignalPath, bool stopSignalExists)
        {
            _recordingId = recordingId;
            _outputPath = outputPath;
            _stopSignalPath = stopSignalPath;
            _stopSignalExists = stopSignalExists;
        }

        public string EmitEventStream()
        {
            bool wasStopped = _stopSignalExists && File.Exists(_stopSignalPath);

            string result =
                "RESULT: STARTED\n" +
                $"RecordingId: {_recordingId}\n" +
                $"Output: {_outputPath}\n" +
                "Container: mp4\n" +
                "Codec: h264\n" +
                "Fps: 30\n" +
                "Width: 1920\n" +
                "Height: 1080\n" +
                "CaptureMethod: WGC_D3D11_FRAME_STREAM\n" +
                "\n" +
                "RESULT: PROGRESS\n" +
                "FramesCaptured: 150\n" +
                "ElapsedMs: 5000\n" +
                "BytesWritten: 7500000\n" +
                "\n";

            if (wasStopped)
            {
                result +=
                    "RESULT: STOPPED\n" +
                    "StopReason: user_requested\n" +
                    "FramesCaptured: 150\n" +
                    "DurationMs: 5000\n" +
                    "FileSize: 7500000 bytes\n" +
                    "Width: 1920\n" +
                    "Height: 1080\n";
            }
            else
            {
                result +=
                    "RESULT: OK\n" +
                    "FramesCaptured: 300\n" +
                    "FramesDropped: 0\n" +
                    "DurationMs: 10000\n" +
                    "FileSize: 15000000 bytes\n" +
                    "Width: 1920\n" +
                    "Height: 1080\n";
            }

            return result;
        }
    }

    // -----------------------------------------------------------------
    // Mock media probe that returns configurable results
    // -----------------------------------------------------------------

    private class MockMediaProbe : AgentRecorder.Capture.IMediaFileProbe
    {
        private readonly bool _fileExists;
        private readonly long _fileSizeBytes;
        private readonly bool _isPlayable;

        public MockMediaProbe(bool fileExists, long fileSizeBytes, bool isPlayable)
        {
            _fileExists = fileExists;
            _fileSizeBytes = fileSizeBytes;
            _isPlayable = isPlayable;
        }

        public AgentRecorder.Capture.MediaProbeResult? Probe(string path)
        {
            return new AgentRecorder.Capture.MediaProbeResult
            {
                FileExists = _fileExists,
                FileSizeBytes = _fileSizeBytes,
                IsPlayable = _isPlayable
            };
        }
    }

    // -----------------------------------------------------------------
    // Test: FakeProcess -> OK (no stop signal) -> evidence completed
    // -----------------------------------------------------------------

    [Fact]
    public void FakeProcess_NoStopSignal_EvidenceCompleted()
    {
        // Arrange: FakeProcess with no stop signal produces OK event stream
        string recordingId = $"rec_r58_{Guid.NewGuid():N}";
        string outputPath = Path.Combine(_tempDir, $"{recordingId}.mp4");
        string stopSignalPath = Path.Combine(_tempDir, "stop.signal");

        // Create a mock MP4 file
        byte[] mp4Header = new byte[12];
        mp4Header[0] = 0x00; mp4Header[1] = 0x00; mp4Header[2] = 0x00; mp4Header[3] = 0x18;
        mp4Header[4] = 0x66; mp4Header[5] = 0x74; mp4Header[6] = 0x79; mp4Header[7] = 0x70; // "ftyp"
        File.WriteAllBytes(outputPath, mp4Header);
        long fileSize = new FileInfo(outputPath).Length;

        var fake = new FakeProcess(recordingId, outputPath, stopSignalPath, stopSignalExists: false);
        string stdout = fake.EmitEventStream();

        // Act: Parse the event stream
        var events = AgentRecorder.Capture.WgcContinuousEventStreamParser.ParseEvents(stdout);
        var summary = AgentRecorder.Capture.WgcContinuousEventStreamParser.ValidateAndSummarize(events);

        // Act: Build evidence JSON
        var metadata = new AgentRecorder.Capture.ContinuousSessionMetadata
        {
            WindowHwnd = "1234567",
            WindowId = "window_1234567",
            WindowTitle = "Fake Window",
            RecordingId = recordingId,
            ConfirmationId = $"conf_{recordingId}",
            RequestedDurationMs = 10000
        };
        var probe = new MockMediaProbe(fileExists: true, fileSizeBytes: fileSize, isPlayable: true);
        var builder = new AgentRecorder.Capture.EvidenceV2Builder(metadata, probe);
        string? evidenceJson = builder.Build(summary);

        // Assert: Evidence JSON is not null and is valid
        Assert.NotNull(evidenceJson);
        var evidence = JsonSerializer.Deserialize<JsonElement>(evidenceJson);

        Assert.Equal("2.0", evidence.GetProperty("schema_version").GetString());
        Assert.Equal("continuous", evidence.GetProperty("capture_kind").GetString());
        Assert.Equal("completed", evidence.GetProperty("final_status").GetString());
        Assert.Equal(recordingId, evidence.GetProperty("recording_id").GetString());
        Assert.Equal("duration_reached", evidence.GetProperty("stop_reason").GetString());
        Assert.False(evidence.GetProperty("partial_output_exists").GetBoolean());
        Assert.True(evidence.GetProperty("actual_file_exists").GetBoolean());
        Assert.Equal(fileSize, evidence.GetProperty("actual_file_size").GetInt64());
        Assert.True(evidence.GetProperty("is_playable_media").GetBoolean());

        var output = evidence.GetProperty("output");
        Assert.Equal("mp4", output.GetProperty("container").GetString());
        Assert.Equal("h264", output.GetProperty("codec").GetString());
        Assert.Equal(1920, output.GetProperty("width").GetInt32());
        Assert.Equal(1080, output.GetProperty("height").GetInt32());
        Assert.Equal(300, output.GetProperty("frames_captured").GetInt32());
        Assert.Equal(0, output.GetProperty("frames_dropped").GetInt32());
        Assert.Equal(10000, output.GetProperty("duration_ms").GetInt32());
        Assert.Equal(30, output.GetProperty("fps").GetInt32());

        var auditEvents = evidence.GetProperty("audit_events");
        Assert.Equal(5, auditEvents.GetArrayLength());
    }

    // -----------------------------------------------------------------
    // Test: FakeProcess -> STOPPED (stop signal exists) -> evidence stopped
    // -----------------------------------------------------------------

    [Fact]
    public void FakeProcess_StopSignalExists_EvidenceStoppedUserRequested()
    {
        string recordingId = $"rec_r58_{Guid.NewGuid():N}";
        string outputPath = Path.Combine(_tempDir, $"{recordingId}.mp4");
        string stopSignalPath = Path.Combine(_tempDir, "stop.signal");

        // Create stop signal file and mock MP4
        File.WriteAllText(stopSignalPath, "stop");
        byte[] mp4Header = new byte[12];
        mp4Header[0] = 0x00; mp4Header[1] = 0x00; mp4Header[2] = 0x00; mp4Header[3] = 0x18;
        mp4Header[4] = 0x66; mp4Header[5] = 0x74; mp4Header[6] = 0x79; mp4Header[7] = 0x70;
        File.WriteAllBytes(outputPath, mp4Header);
        long fileSize = new FileInfo(outputPath).Length;

        var fake = new FakeProcess(recordingId, outputPath, stopSignalPath, stopSignalExists: true);
        string stdout = fake.EmitEventStream();

        var events = AgentRecorder.Capture.WgcContinuousEventStreamParser.ParseEvents(stdout);
        var summary = AgentRecorder.Capture.WgcContinuousEventStreamParser.ValidateAndSummarize(events);

        Assert.Equal(AgentRecorder.Capture.ContinuousSessionState.Stopped, summary.State);
        Assert.Equal("user_requested", summary.GetStopReasonForEvidence());

        var metadata = new AgentRecorder.Capture.ContinuousSessionMetadata
        {
            WindowHwnd = "1234568",
            WindowId = "window_1234568",
            RecordingId = recordingId,
            ConfirmationId = $"conf_{recordingId}",
            RequestedDurationMs = 10000
        };
        var probe = new MockMediaProbe(fileExists: true, fileSizeBytes: fileSize, isPlayable: true);
        var builder = new AgentRecorder.Capture.EvidenceV2Builder(metadata, probe);
        string? evidenceJson = builder.Build(summary);

        Assert.NotNull(evidenceJson);
        var evidence = JsonSerializer.Deserialize<JsonElement>(evidenceJson);
        Assert.Equal("completed", evidence.GetProperty("final_status").GetString());
        Assert.Equal("user_requested", evidence.GetProperty("stop_reason").GetString());
        Assert.False(evidence.GetProperty("partial_output_exists").GetBoolean());
        Assert.True(evidence.GetProperty("actual_file_exists").GetBoolean());

        var auditEvents = evidence.GetProperty("audit_events");
        var eventNames = auditEvents.EnumerateArray().Select(e => e.GetProperty("event").GetString()).ToList();
        Assert.Contains("recording.stopped", eventNames);
        Assert.Contains("recording.completed", eventNames); // Task 59: Stopped must have both
    }

    // -----------------------------------------------------------------
    // Test: FakeProcess -> FAIL (encoding_error) -> evidence failed
    // -----------------------------------------------------------------

    [Fact]
    public void FakeProcess_EncodingError_EvidenceFailed()
    {
        string recordingId = $"rec_r58_{Guid.NewGuid():N}";
        string partialPath = Path.Combine(_tempDir, $"{recordingId}.mp4.partial");
        string stdout =
            "RESULT: STARTED\n" +
            $"RecordingId: {recordingId}\n" +
            $"Output: {partialPath}\n" +
            "Container: mp4\n" +
            "Codec: h264\n" +
            "Fps: 30\n" +
            "Width: 1920\n" +
            "Height: 1080\n" +
            "CaptureMethod: WGC_D3D11_FRAME_STREAM\n" +
            "\n" +
            "RESULT: FAIL\n" +
            "StopReason: encoding_error\n" +
            "ErrorCode: encoding_error\n" +
            "Reason: Encoder failed to produce valid video stream\n" +
            "PartialOutputPath: " + partialPath + "\n" +
            "FramesCaptured: 50\n" +
            "FileSize: 1048576 bytes\n" +
            "Width: 1920\n" +
            "Height: 1080\n";

        var events = AgentRecorder.Capture.WgcContinuousEventStreamParser.ParseEvents(stdout);
        var summary = AgentRecorder.Capture.WgcContinuousEventStreamParser.ValidateAndSummarize(events);

        Assert.Equal(AgentRecorder.Capture.ContinuousSessionState.Failed, summary.State);
        Assert.Equal("encoding_error", summary.GetStopReasonForEvidence());
        Assert.True(summary.PartialOutputExists);

        var metadata = new AgentRecorder.Capture.ContinuousSessionMetadata
        {
            WindowHwnd = "1234569",
            WindowId = "window_1234569",
            RecordingId = recordingId,
            ConfirmationId = $"conf_{recordingId}",
            RequestedDurationMs = 10000
        };
        // No media probe - file does not exist
        var builder = new AgentRecorder.Capture.EvidenceV2Builder(metadata);
        string? evidenceJson = builder.Build(summary);

        Assert.NotNull(evidenceJson);
        var evidence = JsonSerializer.Deserialize<JsonElement>(evidenceJson);
        Assert.Equal("failed", evidence.GetProperty("final_status").GetString());
        Assert.Equal("encoding_error", evidence.GetProperty("stop_reason").GetString());
        Assert.True(evidence.GetProperty("partial_output_exists").GetBoolean());
        Assert.True(evidence.GetProperty("error").GetString()!.Length > 0);

        var auditEvents = evidence.GetProperty("audit_events");
        var eventNames = auditEvents.EnumerateArray().Select(e => e.GetProperty("event").GetString()).ToList();
        Assert.Contains("recording.failed", eventNames);
        Assert.DoesNotContain("recording.completed", eventNames); // FAIL should NOT have recording.completed
    }

    // -----------------------------------------------------------------
    // Test: EvidenceV2Builder returns null for Unknown state
    // -----------------------------------------------------------------

    [Fact]
    public void EvidenceV2Builder_UnknownState_ReturnsNull()
    {
        var summary = new AgentRecorder.Capture.WgcContinuousSessionSummary
        {
            State = AgentRecorder.Capture.ContinuousSessionState.Unknown,
            RecordingId = "rec_unknown"
        };

        var metadata = new AgentRecorder.Capture.ContinuousSessionMetadata
        {
            WindowHwnd = "1234560",
            WindowId = "window_1234560",
            RecordingId = "rec_unknown",
            ConfirmationId = "conf_unknown",
            RequestedDurationMs = 5000
        };

        var builder = new AgentRecorder.Capture.EvidenceV2Builder(metadata);
        string? result = builder.Build(summary);

        Assert.Null(result);
    }

    // -----------------------------------------------------------------
    // Test: EvidenceV2Builder returns null for MalformedSequence without output
    // -----------------------------------------------------------------

    [Fact]
    public void EvidenceV2Builder_MalformedSequenceWithoutOutput_ReturnsNull()
    {
        var summary = new AgentRecorder.Capture.WgcContinuousSessionSummary
        {
            State = AgentRecorder.Capture.ContinuousSessionState.MalformedSequence,
            RecordingId = "rec_malformed"
        };
        summary.ValidationErrors.Add("OK event without prior STARTED");

        var metadata = new AgentRecorder.Capture.ContinuousSessionMetadata
        {
            WindowHwnd = "1234561",
            WindowId = "window_1234561",
            RecordingId = "rec_malformed",
            ConfirmationId = "conf_malformed",
            RequestedDurationMs = 5000
        };

        var builder = new AgentRecorder.Capture.EvidenceV2Builder(metadata);
        string? result = builder.Build(summary);

        Assert.NotNull(result); // MalformedSequence still produces evidence with error
        var evidence = JsonSerializer.Deserialize<JsonElement>(result);
        Assert.Equal("failed", evidence.GetProperty("final_status").GetString());
        Assert.Contains("malformed_sequence", evidence.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------
    // Test: evidence JSON passes schema structural validation
    // -----------------------------------------------------------------

    [Fact]
    public void EvidenceJson_StructuralValidation_RequiredFieldsPresent()
    {
        string recordingId = $"rec_r58_{Guid.NewGuid():N}";
        string outputPath = Path.Combine(_tempDir, $"{recordingId}.mp4");
        byte[] mp4Header = new byte[12];
        mp4Header[0] = 0x00; mp4Header[1] = 0x00; mp4Header[2] = 0x00; mp4Header[3] = 0x18;
        mp4Header[4] = 0x66; mp4Header[5] = 0x74; mp4Header[6] = 0x79; mp4Header[7] = 0x70;
        File.WriteAllBytes(outputPath, mp4Header);
        long fileSize = new FileInfo(outputPath).Length;

        string stdout =
            "RESULT: STARTED\n" +
            $"RecordingId: {recordingId}\n" +
            $"Output: {outputPath}\n" +
            "Container: mp4\n" +
            "Codec: h264\n" +
            "Fps: 30\n" +
            "Width: 1920\n" +
            "Height: 1080\n" +
            "CaptureMethod: WGC_D3D11_FRAME_STREAM\n" +
            "\n" +
            "RESULT: OK\n" +
            "FramesCaptured: 300\n" +
            "DurationMs: 10000\n" +
            "FileSize: " + fileSize + " bytes\n" +
            "Width: 1920\n" +
            "Height: 1080\n";

        var events = AgentRecorder.Capture.WgcContinuousEventStreamParser.ParseEvents(stdout);
        var summary = AgentRecorder.Capture.WgcContinuousEventStreamParser.ValidateAndSummarize(events);

        var metadata = new AgentRecorder.Capture.ContinuousSessionMetadata
        {
            WindowHwnd = "1234570",
            WindowId = "window_1234570",
            WindowTitle = "Test Window",
            RecordingId = recordingId,
            ConfirmationId = $"conf_{recordingId}",
            RequestedDurationMs = 10000
        };
        var probe = new MockMediaProbe(fileExists: true, fileSizeBytes: fileSize, isPlayable: true);
        var builder = new AgentRecorder.Capture.EvidenceV2Builder(metadata, probe);
        string? evidenceJson = builder.Build(summary);

        Assert.NotNull(evidenceJson);
        var evidence = JsonSerializer.Deserialize<JsonElement>(evidenceJson);

        // Verify all required top-level fields are present
        Assert.True(evidence.TryGetProperty("schema_version", out _));
        Assert.True(evidence.TryGetProperty("timestamp", out _));
        Assert.True(evidence.TryGetProperty("mode", out _));
        Assert.True(evidence.TryGetProperty("capture_kind", out _));
        Assert.True(evidence.TryGetProperty("window_hwnd", out _));
        Assert.True(evidence.TryGetProperty("window_id", out _));
        Assert.True(evidence.TryGetProperty("recording_id", out _));
        Assert.True(evidence.TryGetProperty("confirmation_id", out _));
        Assert.True(evidence.TryGetProperty("final_status", out _));
        Assert.True(evidence.TryGetProperty("output", out _));
        Assert.True(evidence.TryGetProperty("actual_file_exists", out _));
        Assert.True(evidence.TryGetProperty("actual_file_size", out _));
        Assert.True(evidence.TryGetProperty("is_playable_media", out _));
        Assert.True(evidence.TryGetProperty("partial_output_exists", out _));
        Assert.True(evidence.TryGetProperty("duration_ms", out _));
        Assert.True(evidence.TryGetProperty("requested_duration_ms", out _));
        Assert.True(evidence.TryGetProperty("fps", out _));
        Assert.True(evidence.TryGetProperty("frames_captured", out _));
        Assert.True(evidence.TryGetProperty("frames_dropped", out _));
        Assert.True(evidence.TryGetProperty("stop_reason", out _));
        Assert.True(evidence.TryGetProperty("warnings", out _));
        Assert.True(evidence.TryGetProperty("error", out _));
        Assert.True(evidence.TryGetProperty("audit_events", out _));

        // Verify output object required fields
        var output = evidence.GetProperty("output");
        Assert.True(output.TryGetProperty("path", out _));
        Assert.True(output.TryGetProperty("container", out _));
        Assert.True(output.TryGetProperty("codec", out _));
        Assert.True(output.TryGetProperty("bytes_written", out _));
        Assert.True(output.TryGetProperty("width", out _));
        Assert.True(output.TryGetProperty("height", out _));
        Assert.True(output.TryGetProperty("duration_ms", out _));
        Assert.True(output.TryGetProperty("fps", out _));
        Assert.True(output.TryGetProperty("frames_captured", out _));
        Assert.True(output.TryGetProperty("frames_dropped", out _));
        Assert.True(output.TryGetProperty("capture_method", out _));

        // Verify audit events are present and not empty
        var auditEvents = evidence.GetProperty("audit_events");
        Assert.True(auditEvents.GetArrayLength() > 0);
    }
}
