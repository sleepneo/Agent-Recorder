using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public class AudioHelperEventStreamParserTests
{
    [Fact]
    public void ParseEvents_EmptyInput_ReturnsEmptyList()
    {
        var events = AudioHelperEventStreamParser.ParseEvents(null);
        Assert.Empty(events);

        events = AudioHelperEventStreamParser.ParseEvents("");
        Assert.Empty(events);

        events = AudioHelperEventStreamParser.ParseEvents("   \n\n  ");
        Assert.Empty(events);
    }

    [Fact]
    public void ParseEvents_SingleStartedEvent_ParsesAllFields()
    {
        var stdout = string.Join("\n", new[]
        {
            "RESULT: STARTED",
            "Stage: AudioCapturing",
            "RecordingId: rec_1",
            "SampleRate: 16000",
            "Channels: 1",
            "BitsPerSample: 16",
            "FirstSampleAnchorTicks: 123456789",
            "TimestampFrequency: 10000000",
            "BytesWritten: 320",
            "CaptureMethod: WASAPI_SHARED_CAPTURE",
            "CaptureEngine: wasapi-direct",
            ""
        });

        var events = AudioHelperEventStreamParser.ParseEvents(stdout);

        Assert.Single(events);
        var evt = events[0];
        Assert.Equal(AudioHelperEventResult.Started, evt.Result);
        Assert.Equal("AudioCapturing", evt.Stage);
        Assert.Equal("rec_1", evt.RecordingId);
        Assert.Equal(16000, evt.SampleRate);
        Assert.Equal(1, evt.Channels);
        Assert.Equal(16, evt.BitsPerSample);
        Assert.Equal(123456789L, evt.FirstSampleAnchorTicks);
        Assert.Equal(10000000L, evt.TimestampFrequency);
        Assert.Equal(320L, evt.BytesWritten);
        Assert.Equal("WASAPI_SHARED_CAPTURE", evt.CaptureMethod);
        Assert.Equal("wasapi-direct", evt.CaptureEngine);
    }

    [Fact]
    public void ParseEvents_CrlfLineEndings_AreAccepted()
    {
        var stdout = "RESULT: STARTED\r\nRecordingId: rec_1\r\n\r\nRESULT: OK\r\nDurationMs: 1000\r\n\r\n";
        var events = AudioHelperEventStreamParser.ParseEvents(stdout);

        Assert.Equal(2, events.Count);
        Assert.Equal(AudioHelperEventResult.Started, events[0].Result);
        Assert.Equal(AudioHelperEventResult.Ok, events[1].Result);
    }

    [Fact]
    public void ParseEvents_MixedBlankLinesAndWhitespace_AreIgnored()
    {
        var stdout = "\n\nRESULT: STARTED\nRecordingId: rec_1\n   \n\nRESULT: PROGRESS\nElapsedMs: 100\n\n";
        var events = AudioHelperEventStreamParser.ParseEvents(stdout);

        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void ParseEvents_MalformedLines_AreIgnored()
    {
        var stdout = string.Join("\n", new[]
        {
            "RESULT: STARTED",
            "RecordingId: rec_1",
            "no-colon-line",
            ": no-key",
            "",
            "RESULT: OK",
            "DurationMs: 1000",
            ""
        });

        var events = AudioHelperEventStreamParser.ParseEvents(stdout);

        Assert.Equal(2, events.Count);
        Assert.Equal("rec_1", events[0].RecordingId);
        Assert.Equal(1000L, events[1].DurationMs);
    }

    [Fact]
    public void ParseEvents_UnknownResult_MapsToUnknown()
    {
        var stdout = "RESULT: BOGUS\nRecordingId: rec_1\n\n";
        var events = AudioHelperEventStreamParser.ParseEvents(stdout);

        Assert.Single(events);
        Assert.Equal(AudioHelperEventResult.Unknown, events[0].Result);
    }

    [Fact]
    public void ParseEvents_InvalidNumeric_MarksParseFailed()
    {
        var stdout = string.Join("\n", new[]
        {
            "RESULT: STARTED",
            "SampleRate: not-a-number",
            "Channels: 1",
            "BitsPerSample: 16",
            "FirstSampleAnchorTicks: 123",
            "TimestampFrequency: 10000000",
            ""
        });

        var events = AudioHelperEventStreamParser.ParseEvents(stdout);

        Assert.Single(events);
        Assert.Null(events[0].SampleRate);
        Assert.True(events[0].SampleRateParseFailed);
    }

    [Fact]
    public void ValidateAndSummarize_CompleteSequence_ReturnsSuccess()
    {
        var events = new List<AudioHelperEvent>
        {
            new()
            {
                Result = AudioHelperEventResult.Started,
                RecordingId = "rec_1",
                SampleRate = 16000,
                Channels = 1,
                BitsPerSample = 16,
                FirstSampleAnchorTicks = 100,
                TimestampFrequency = Stopwatch.Frequency,
                BytesWritten = 0
            },
            new()
            {
                Result = AudioHelperEventResult.Ok,
                DurationMs = 1000,
                BytesWritten = 32000,
                EstimatedGapMs = 0
            }
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(events);

        Assert.Equal(AudioHelperSessionState.Success, summary.State);
        Assert.Equal("rec_1", summary.RecordingId);
        Assert.Equal(16000, summary.SampleRate);
        Assert.Equal(1, summary.Channels);
        Assert.Equal(16, summary.BitsPerSample);
        Assert.Equal(100L, summary.FirstSampleAnchorTicks);
        Assert.Equal(1000L, summary.DurationMs);
        Assert.Equal(32000L, summary.BytesWritten);
        Assert.Empty(summary.ValidationErrors);
    }

    [Fact]
    public void ValidateAndSummarize_NativeMediaCaptureStopped_PreservesCaptureEngine()
    {
        var stdout = string.Join("\n", new[]
        {
            "RESULT: STARTED",
            "RecordingId: rec_native",
            "SampleRate: 16000",
            "Channels: 1",
            "BitsPerSample: 16",
            "FirstSampleAnchorTicks: 123456789",
            "TimestampFrequency: " + Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture),
            "BytesWritten: 0",
            "CaptureMethod: WINDOWS_MEDIACAPTURE",
            "CaptureEngine: windows-mediacapture",
            "",
            "RESULT: STOPPED",
            "StopReason: user_requested",
            "DurationMs: 100",
            "BytesWritten: 3244",
            "EstimatedGapMs: 0",
            "CaptureMethod: WINDOWS_MEDIACAPTURE",
            "CaptureEngine: windows-mediacapture",
            ""
        });

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(AudioHelperEventStreamParser.ParseEvents(stdout));

        Assert.Equal(AudioHelperSessionState.Stopped, summary.State);
        Assert.Equal("WINDOWS_MEDIACAPTURE", summary.CaptureMethod);
        Assert.Equal("windows-mediacapture", summary.CaptureEngine);
        Assert.Empty(summary.ValidationErrors);
    }

    [Fact]
    public void ValidateAndSummarize_NativeMediaCaptureFail_PreservesStructuredDiagnostics()
    {
        var stdout = string.Join("\n", new[]
        {
            "RESULT: STARTED",
            "RecordingId: rec_native",
            "SampleRate: 16000",
            "Channels: 1",
            "BitsPerSample: 16",
            "FirstSampleAnchorTicks: 123456789",
            "TimestampFrequency: " + Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture),
            "BytesWritten: 0",
            "CaptureMethod: WINDOWS_MEDIACAPTURE",
            "CaptureEngine: windows-mediacapture",
            "",
            "RESULT: FAIL",
            "ErrorCode: audio_native_recording_failed",
            "Reason: stage=recording; endpoint={0.0.1.00000000}.{endpoint}; sourceEvent=MediaCapture.Failed",
            "HRESULT: 0x88990001",
            "FailureStage: recording",
            "EndpointId: {0.0.1.00000000}.{endpoint}",
            "PartialOutputPath: C:\\root\\rec.partial.wav",
            "SecondaryFailure: stop:NativeAudioRecorderException:0x80004005:Injected",
            "BytesWritten: 44",
            "CaptureMethod: WINDOWS_MEDIACAPTURE",
            "CaptureEngine: windows-mediacapture",
            ""
        });

        var events = AudioHelperEventStreamParser.ParseEvents(stdout);
        var fail = events[1];
        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(events);

        Assert.Equal("recording", fail.FailureStage);
        Assert.Equal("{0.0.1.00000000}.{endpoint}", fail.EndpointId);
        Assert.Equal("C:\\root\\rec.partial.wav", fail.PartialOutputPath);
        Assert.Equal("stop:NativeAudioRecorderException:0x80004005:Injected", fail.SecondaryFailure);
        Assert.Equal(AudioHelperSessionState.Failed, summary.State);
        Assert.Equal("recording", summary.FailureStage);
        Assert.Equal("{0.0.1.00000000}.{endpoint}", summary.EndpointId);
        Assert.Equal("C:\\root\\rec.partial.wav", summary.PartialOutputPath);
        Assert.Equal("stop:NativeAudioRecorderException:0x80004005:Injected", summary.SecondaryFailure);
        Assert.Equal("windows-mediacapture", summary.CaptureEngine);
    }

    [Fact]
    public void ValidateAndSummarize_StoppedUserRequested_ReturnsStopped()
    {
        var events = new List<AudioHelperEvent>
        {
            MakeStartedEvent("rec_1"),
            new()
            {
                Result = AudioHelperEventResult.Stopped,
                StopReason = "user_requested",
                DurationMs = 500,
                BytesWritten = 16000,
                EstimatedGapMs = 0
            }
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(events);

        Assert.Equal(AudioHelperSessionState.Stopped, summary.State);
        Assert.Equal("user_requested", summary.StopReason);
    }

    [Fact]
    public void ValidateAndSummarize_FailWithoutStarted_PreservesDeclarativeFailure()
    {
        var events = new List<AudioHelperEvent>
        {
            new()
            {
                Result = AudioHelperEventResult.Fail,
                ErrorCode = "audio_endpoint_not_found",
                Reason = "Endpoint gone"
            }
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(events);

        // A FAIL event that carries a stable error code is still reported as Failed
        // so the operator sees the helper's declared failure category, while the
        // illegal sequence is recorded as a validation error.
        Assert.Equal(AudioHelperSessionState.Failed, summary.State);
        Assert.Equal("audio_endpoint_not_found", summary.ErrorCode);
        Assert.Contains("without prior STARTED", summary.ValidationErrors[0]);
    }

    [Fact]
    public void ValidateAndSummarize_FailWithoutStartedAndNoErrorCode_ReturnsMalformedSequence()
    {
        var events = new List<AudioHelperEvent>
        {
            new()
            {
                Result = AudioHelperEventResult.Fail,
                Reason = "Endpoint gone"
            }
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(events);

        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, e => e.Contains("missing required field: ErrorCode"));
    }

    [Fact]
    public void ValidateAndSummarize_ProgressBeforeStarted_ReturnsMalformedSequence()
    {
        var events = new List<AudioHelperEvent>
        {
            MakeProgressEvent(recordingId: null, elapsedMs: 100, wallElapsedMs: 100, bytesWritten: 100, estimatedGapMs: 0),
            MakeStartedEvent("rec_1"),
            new() { Result = AudioHelperEventResult.Ok, DurationMs = 1000, BytesWritten = 100 }
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(events);

        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, e => e.Contains("before STARTED"));
    }

    [Fact]
    public void ValidateAndSummarize_DoubleTerminal_ReturnsMalformedSequence()
    {
        var events = new List<AudioHelperEvent>
        {
            MakeStartedEvent("rec_1"),
            new() { Result = AudioHelperEventResult.Ok, DurationMs = 1000, BytesWritten = 100 },
            new() { Result = AudioHelperEventResult.Ok, DurationMs = 1000, BytesWritten = 100 }
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(events);

        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains("Duplicate terminal", summary.ValidationErrors[0]);
    }

    [Fact]
    public void ValidateAndSummarize_NoTerminal_ReturnsMalformedSequence()
    {
        var events = new List<AudioHelperEvent>
        {
            MakeStartedEvent("rec_1"),
            new() { Result = AudioHelperEventResult.Progress, ElapsedMs = 100 }
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(events);

        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, e => e.Contains("No terminal event"));
    }

    [Fact]
    public void ValidateAndSummarize_RecordingIdMismatch_ReturnsMalformedSequence()
    {
        var events = new List<AudioHelperEvent>
        {
            MakeStartedEvent("rec_1"),
            MakeProgressEvent(recordingId: "rec_2", elapsedMs: 100, wallElapsedMs: 100, bytesWritten: 100, estimatedGapMs: 0),
            new() { Result = AudioHelperEventResult.Ok, DurationMs = 1000, BytesWritten = 100 }
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(events);

        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, e => e.Contains("RecordingId mismatch"));
    }

    [Fact]
    public void ValidateAndSummarize_TimestampFrequencyMismatch_ReturnsMalformedSequence()
    {
        var events = new List<AudioHelperEvent>
        {
            new()
            {
                Result = AudioHelperEventResult.Started,
                RecordingId = "rec_1",
                SampleRate = 16000,
                Channels = 1,
                BitsPerSample = 16,
                FirstSampleAnchorTicks = 100,
                TimestampFrequency = Stopwatch.Frequency + 1,
                BytesWritten = 0
            },
            new() { Result = AudioHelperEventResult.Ok, DurationMs = 1000, BytesWritten = 100 }
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(events);

        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains("TimestampFrequency mismatch", summary.ValidationErrors[0]);
    }

    [Fact]
    public void ValidateAndSummarize_MissingRequiredStartedFields_ReturnsValidationErrors()
    {
        var events = new List<AudioHelperEvent>
        {
            new()
            {
                Result = AudioHelperEventResult.Started,
                RecordingId = "rec_1",
                SampleRate = 0,
                Channels = 1,
                BitsPerSample = 16,
                FirstSampleAnchorTicks = 100,
                TimestampFrequency = Stopwatch.Frequency,
                BytesWritten = 0
            },
            new() { Result = AudioHelperEventResult.Ok, DurationMs = 1000, BytesWritten = 100 }
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(events);

        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, e => e.Contains("SampleRate"));
    }

    [Fact]
    public void ParseAndValidate_Integration_ReturnsSummary()
    {
        var stdout = string.Join("\n", new[]
        {
            "RESULT: STARTED",
            "RecordingId: rec_1",
            "SampleRate: 16000",
            "Channels: 1",
            "BitsPerSample: 16",
            $"FirstSampleAnchorTicks: {Stopwatch.GetTimestamp()}",
            $"TimestampFrequency: {Stopwatch.Frequency}",
            "BytesWritten: 0",
            "",
            "RESULT: OK",
            "DurationMs: 1000",
            "BytesWritten: 32000",
            "EstimatedGapMs: 0",
            ""
        });

        var summary = AudioHelperEventStreamParser.ParseAndValidate(stdout);

        Assert.Equal(AudioHelperSessionState.Success, summary.State);
        Assert.Equal("rec_1", summary.RecordingId);
    }

    private static AudioHelperEvent MakeStartedEvent(string recordingId)
    {
        return new AudioHelperEvent
        {
            Result = AudioHelperEventResult.Started,
            RecordingId = recordingId,
            SampleRate = 16000,
            Channels = 1,
            BitsPerSample = 16,
            FirstSampleAnchorTicks = 100,
            TimestampFrequency = Stopwatch.Frequency,
            BytesWritten = 0
        };
    }

    private static AudioHelperEvent MakeProgressEvent(string? recordingId, long elapsedMs, long wallElapsedMs, long bytesWritten, long estimatedGapMs)
    {
        return new AudioHelperEvent
        {
            Result = AudioHelperEventResult.Progress,
            RecordingId = recordingId,
            ElapsedMs = elapsedMs,
            WallElapsedMs = wallElapsedMs,
            BytesWritten = bytesWritten,
            EstimatedGapMs = estimatedGapMs
        };
    }

    [Fact]
    public void ParseEventBlock_MissingResult_ReturnsNull()
    {
        var block = new List<string> { "RecordingId: rec_1", "SampleRate: 16000" };
        var evt = AudioHelperEventStreamParser.ParseEventBlock(block);
        Assert.Null(evt);
    }

    [Fact]
    public void ValidateAndSummarize_NoEvents_ReturnsMalformedSequence()
    {
        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(new List<AudioHelperEvent>());
        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, e => e.Contains("No events"));
    }

    [Fact]
    public void ValidateAndSummarize_MissingStartedRecordingId_ReturnsMalformedSequence()
    {
        var events = new List<AudioHelperEvent>
        {
            new()
            {
                Result = AudioHelperEventResult.Started,
                SampleRate = 16000,
                Channels = 1,
                BitsPerSample = 16,
                FirstSampleAnchorTicks = 100,
                TimestampFrequency = Stopwatch.Frequency,
                BytesWritten = 0
            },
            new() { Result = AudioHelperEventResult.Ok, DurationMs = 1000, BytesWritten = 100 }
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(events);
        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, e => e.Contains("RecordingId"));
    }

    [Fact]
    public void ValidateAndSummarize_NonPositiveAnchorAndBytes_ReturnsMalformedSequence()
    {
        var events = new List<AudioHelperEvent>
        {
            new()
            {
                Result = AudioHelperEventResult.Started,
                RecordingId = "rec_1",
                SampleRate = 16000,
                Channels = 1,
                BitsPerSample = 16,
                FirstSampleAnchorTicks = 0,
                TimestampFrequency = Stopwatch.Frequency,
                BytesWritten = -1
            },
            new() { Result = AudioHelperEventResult.Ok, DurationMs = 1000, BytesWritten = 100 }
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(events);
        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, e => e.Contains("FirstSampleAnchorTicks"));
        Assert.Contains(summary.ValidationErrors, e => e.Contains("BytesWritten"));
    }

    [Fact]
    public void ValidateAndSummarize_DuplicateStarted_ReturnsMalformedSequence()
    {
        var events = new List<AudioHelperEvent>
        {
            MakeStartedEvent("rec_1"),
            MakeStartedEvent("rec_1"),
            new() { Result = AudioHelperEventResult.Ok, DurationMs = 1000, BytesWritten = 100 }
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(events);
        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, e => e.Contains("Duplicate"));
    }

    [Fact]
    public void ValidateAndSummarize_MalformedProgressNumeric_FailsClosed()
    {
        var events = new List<AudioHelperEvent>
        {
            MakeStartedEvent("rec_1"),
            new()
            {
                Result = AudioHelperEventResult.Progress,
                ElapsedMsParseFailed = true,
                WallElapsedMs = 100,
                BytesWritten = 100,
                EstimatedGapMs = 0
            },
            new() { Result = AudioHelperEventResult.Ok, DurationMs = 1000, BytesWritten = 100 }
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(events);
        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.True(summary.HasNumericParseError);
    }

    [Fact]
    public void ValidateAndSummarize_ProgressValuesRegress_ReturnsMalformedSequence()
    {
        var events = new List<AudioHelperEvent>
        {
            MakeStartedEvent("rec_1"),
            MakeProgressEvent(null, 100, 100, 100, 0),
            MakeProgressEvent(null, 50, 100, 100, 0),
            new() { Result = AudioHelperEventResult.Ok, DurationMs = 1000, BytesWritten = 100 }
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(events);
        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, e => e.Contains("regressed"));
    }

    [Fact]
    public void ValidateAndSummarize_UnknownResult_ReturnsMalformedSequence()
    {
        var events = new List<AudioHelperEvent>
        {
            MakeStartedEvent("rec_1"),
            new() { Result = AudioHelperEventResult.Unknown }
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(events);
        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, e => e.Contains("Unknown RESULT"));
    }

    [Fact]
    public void ValidateAndSummarize_EventAfterTerminal_ReturnsMalformedSequence()
    {
        var events = new List<AudioHelperEvent>
        {
            MakeStartedEvent("rec_1"),
            new() { Result = AudioHelperEventResult.Ok, DurationMs = 1000, BytesWritten = 100 },
            MakeProgressEvent(null, 200, 200, 200, 0)
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(events);
        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, e => e.Contains("after terminal"));
    }

    [Fact]
    public void ValidateAndSummarize_DifferentTerminalTypes_ReturnsMalformedSequence()
    {
        var events = new List<AudioHelperEvent>
        {
            MakeStartedEvent("rec_1"),
            new() { Result = AudioHelperEventResult.Ok, DurationMs = 1000, BytesWritten = 100 },
            new() { Result = AudioHelperEventResult.Stopped, DurationMs = 1000, BytesWritten = 100 }
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(events);
        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, e => e.Contains("Duplicate terminal"));
    }
}
