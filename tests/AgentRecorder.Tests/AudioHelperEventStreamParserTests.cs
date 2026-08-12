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

    private static AudioHelperEvent MakeProgressEvent(
        string? recordingId,
        long elapsedMs,
        long wallElapsedMs,
        long bytesWritten,
        long estimatedGapMs,
        long? maxEstimatedGapMs = null)
    {
        return new AudioHelperEvent
        {
            Result = AudioHelperEventResult.Progress,
            RecordingId = recordingId,
            ElapsedMs = elapsedMs,
            WallElapsedMs = wallElapsedMs,
            BytesWritten = bytesWritten,
            EstimatedGapMs = estimatedGapMs,
            MaxEstimatedGapMs = maxEstimatedGapMs ?? estimatedGapMs
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
        Assert.Contains(summary.ValidationErrors, e => e.Contains("ElapsedMs regressed"));
    }

    [Fact]
    public void ValidateAndSummarize_CurrentEstimatedGapMayDecreaseWhileHistoricalMaxRemains()
    {
        var events = new List<AudioHelperEvent>
        {
            MakeStartedEvent("rec_gap"),
            MakeProgressEvent(null, 100, 140, 100, 40, 40),
            MakeProgressEvent(null, 200, 240, 200, 12, 40),
            new()
            {
                Result = AudioHelperEventResult.Stopped,
                DurationMs = 200,
                BytesWritten = 200,
                EstimatedGapMs = 12,
                MaxEstimatedGapMs = 40
            }
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(events);

        Assert.Equal(AudioHelperSessionState.Stopped, summary.State);
        Assert.Empty(summary.ValidationErrors);
        Assert.Equal(12, summary.EstimatedGapMs);
        Assert.Equal(40, summary.MaxEstimatedGapMs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1L)]
    public void ValidateAndSummarize_MissingOrNegativeCurrentEstimatedGap_FailsClosed(long? currentGap)
    {
        var progress = MakeProgressEvent(null, 100, 100, 100, 0, 0);
        progress.EstimatedGapMs = currentGap;
        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(new List<AudioHelperEvent>
        {
            MakeStartedEvent("rec_gap_current"),
            progress,
            new() { Result = AudioHelperEventResult.Ok, DurationMs = 100, BytesWritten = 100, EstimatedGapMs = 0, MaxEstimatedGapMs = 0 }
        });

        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, e => e.Contains("EstimatedGapMs") &&
            (currentGap is null ? e.Contains("missing") : e.Contains("negative")));
    }

    [Fact]
    public void ValidateAndSummarize_MissingMaxEstimatedGap_FailsClosed()
    {
        var progress = MakeProgressEvent(null, 100, 100, 100, 0, 0);
        progress.MaxEstimatedGapMs = null;
        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(new List<AudioHelperEvent>
        {
            MakeStartedEvent("rec_gap_max_missing"),
            progress,
            new() { Result = AudioHelperEventResult.Ok, DurationMs = 100, BytesWritten = 100, EstimatedGapMs = 0, MaxEstimatedGapMs = 0 }
        });

        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, e => e.Contains("missing required field: MaxEstimatedGapMs"));
    }

    [Fact]
    public void ValidateAndSummarize_NegativeMaxEstimatedGap_FailsClosed()
    {
        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(new List<AudioHelperEvent>
        {
            MakeStartedEvent("rec_gap_max_negative"),
            MakeProgressEvent(null, 100, 100, 100, 0, -1),
            new() { Result = AudioHelperEventResult.Ok, DurationMs = 100, BytesWritten = 100, EstimatedGapMs = 0, MaxEstimatedGapMs = 0 }
        });

        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, e => e.Contains("negative MaxEstimatedGapMs"));
    }

    [Fact]
    public void ValidateAndSummarize_MaxEstimatedGapRegression_FailsClosed()
    {
        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(new List<AudioHelperEvent>
        {
            MakeStartedEvent("rec_gap_max_regress"),
            MakeProgressEvent(null, 100, 100, 100, 50, 50),
            MakeProgressEvent(null, 200, 200, 200, 10, 40),
            new() { Result = AudioHelperEventResult.Ok, DurationMs = 200, BytesWritten = 200, EstimatedGapMs = 10, MaxEstimatedGapMs = 50 }
        });

        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, e => e.Contains("MaxEstimatedGapMs regressed"));
    }

    [Fact]
    public void ValidateAndSummarize_MaxEstimatedGapBelowCurrent_FailsClosed()
    {
        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(new List<AudioHelperEvent>
        {
            MakeStartedEvent("rec_gap_max_below_current"),
            MakeProgressEvent(null, 100, 100, 100, 50, 40),
            new() { Result = AudioHelperEventResult.Ok, DurationMs = 100, BytesWritten = 100, EstimatedGapMs = 50, MaxEstimatedGapMs = 50 }
        });

        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, e => e.Contains("MaxEstimatedGapMs below EstimatedGapMs"));
    }

    [Fact]
    public void ValidateAndSummarize_TerminalMaxBelowProgressHistoricalMax_FailsClosed()
    {
        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(new List<AudioHelperEvent>
        {
            MakeStartedEvent("rec_gap_terminal_max"),
            MakeProgressEvent(null, 100, 140, 100, 40, 40),
            new() { Result = AudioHelperEventResult.Stopped, DurationMs = 100, BytesWritten = 100, EstimatedGapMs = 5, MaxEstimatedGapMs = 5 }
        });

        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, e => e.Contains("Terminal MaxEstimatedGapMs below last PROGRESS historical max"));
        Assert.Equal(5, summary.EstimatedGapMs);
        Assert.Equal(40, summary.MaxEstimatedGapMs);
    }

    [Theory]
    [InlineData("elapsed")]
    [InlineData("wall")]
    [InlineData("bytes")]
    public void ValidateAndSummarize_TrueMonotonicProgressRegression_FailsClosed(string field)
    {
        var first = MakeProgressEvent(null, 100, 100, 100, 0, 0);
        var second = MakeProgressEvent(null, 200, 200, 200, 0, 0);
        switch (field)
        {
            case "elapsed": second.ElapsedMs = 50; break;
            case "wall": second.WallElapsedMs = 50; break;
            case "bytes": second.BytesWritten = 50; break;
        }

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(new List<AudioHelperEvent>
        {
            MakeStartedEvent("rec_true_regression"),
            first,
            second,
            new() { Result = AudioHelperEventResult.Ok, DurationMs = 200, BytesWritten = 200, EstimatedGapMs = 0, MaxEstimatedGapMs = 0 }
        });

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

    [Fact]
    public void ParseAndValidate_SystemLoopbackSource_PropagatesAllowlistedValue()
    {
        var stdout = string.Join("\n", new[]
        {
            "RESULT: STARTED",
            "RecordingId: rec_loopback",
            "AudioSourceKind: system-loopback",
            "SampleRate: 48000",
            "Channels: 2",
            "BitsPerSample: 32",
            "FirstSampleAnchorTicks: 100",
            "TimestampFrequency: 10000000",
            "BytesWritten: 0",
            "CaptureMethod: WASAPI_SHARED_LOOPBACK",
            "CaptureEngine: wasapi-direct",
            "",
            "RESULT: STOPPED",
            "AudioSourceKind: system-loopback",
            "DurationMs: 1000",
            "BytesWritten: 192000",
            "EstimatedGapMs: 0",
            "CaptureMethod: WASAPI_SHARED_LOOPBACK",
            "CaptureEngine: wasapi-direct",
            "MaxEstimatedGapMs: 0",
            ""
        });

        var summary = AudioHelperEventStreamParser.ParseAndValidate(stdout);

        Assert.Equal(AudioHelperSessionState.Stopped, summary.State);
        Assert.Equal("system-loopback", summary.AudioSourceKind);
        Assert.Equal("WASAPI_SHARED_LOOPBACK", summary.CaptureMethod);
    }

    [Theory]
    [InlineData("screen")]
    [InlineData("SYSTEM-LOOPBACK")]
    public void ParseAndValidate_UnknownOrNonCanonicalSource_IsMalformed(string source)
    {
        var stdout = string.Join("\n", new[]
        {
            "RESULT: STARTED",
            "RecordingId: rec_source",
            $"AudioSourceKind: {source}",
            "SampleRate: 48000",
            "Channels: 2",
            "BitsPerSample: 32",
            "FirstSampleAnchorTicks: 100",
            "TimestampFrequency: 10000000",
            "BytesWritten: 0",
            "CaptureMethod: WASAPI_SHARED_LOOPBACK",
            "CaptureEngine: wasapi-direct",
            "",
            "RESULT: FAIL",
            "ErrorCode: audio_capture_error",
            $"AudioSourceKind: {source}",
            ""
        });

        var summary = AudioHelperEventStreamParser.ParseAndValidate(stdout);

        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, error => error.Contains("AudioSourceKind"));
    }

    [Fact]
    public void ValidateAndSummarize_SourceConflictBetweenStartedAndTerminal_IsMalformed()
    {
        var started = MakeStartedEvent("rec_source_conflict");
        started.AudioSourceKind = "microphone";
        var stopped = new AudioHelperEvent
        {
            Result = AudioHelperEventResult.Stopped,
            AudioSourceKind = "system-loopback",
            DurationMs = 100,
            BytesWritten = 100,
            EstimatedGapMs = 0,
            MaxEstimatedGapMs = 0
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(new List<AudioHelperEvent> { started, stopped });

        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, error => error.Contains("AudioSourceKind mismatch"));
    }

    [Fact]
    public void ValidateAndSummarize_OldFixtureWithoutSourceKind_RemainsCompatible()
    {
        var started = MakeStartedEvent("rec_old_fixture");
        var stopped = new AudioHelperEvent
        {
            Result = AudioHelperEventResult.Stopped,
            DurationMs = 100,
            BytesWritten = 100,
            EstimatedGapMs = 0,
            MaxEstimatedGapMs = 0
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(new List<AudioHelperEvent> { started, stopped });

        Assert.Equal(AudioHelperSessionState.Stopped, summary.State);
        Assert.Null(summary.AudioSourceKind);
    }

    [Fact]
    public void ValidateAndSummarize_LoopbackHfpMetadataOrMethodConflict_IsMalformed()
    {
        var started = MakeStartedEvent("rec_loopback_hfp");
        started.AudioSourceKind = "system-loopback";
        started.CaptureMethod = "WASAPI_SHARED_LOOPBACK";
        started.PairEvidence = "hfp-must-not-appear";
        var stopped = new AudioHelperEvent
        {
            Result = AudioHelperEventResult.Stopped,
            AudioSourceKind = "system-loopback",
            CaptureMethod = "WASAPI_SHARED_CAPTURE",
            DurationMs = 100,
            BytesWritten = 100,
            EstimatedGapMs = 0,
            MaxEstimatedGapMs = 0
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(new List<AudioHelperEvent> { started, stopped });

        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, error => error.Contains("HFP metadata"));
        Assert.Contains(summary.ValidationErrors, error => error.Contains("CaptureMethod"));
    }

    [Fact]
    public void ParseAndValidate_DuplicateAudioSourceKindInDeclarativeFail_IsMalformed()
    {
        var stdout = string.Join("\n", new[]
        {
            "RESULT: STARTED",
            "RecordingId: rec_duplicate_source",
            "AudioSourceKind: system-loopback",
            "SampleRate: 48000",
            "Channels: 2",
            "BitsPerSample: 32",
            "FirstSampleAnchorTicks: 100",
            "TimestampFrequency: 10000000",
            "BytesWritten: 0",
            "CaptureMethod: WASAPI_SHARED_LOOPBACK",
            "CaptureEngine: wasapi-direct",
            "",
            "RESULT: FAIL",
            "ErrorCode: audio_capture_error",
            "AudioSourceKind: system-loopback",
            "AudioSourceKind: microphone",
            ""
        });

        var summary = AudioHelperEventStreamParser.ParseAndValidate(stdout);

        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, error => error.Contains("Duplicate AudioSourceKind"));
    }

    [Fact]
    public void ValidateAndSummarize_SourceAwareStreamMissingTerminalSource_IsMalformed()
    {
        var started = MakeStartedEvent("rec_half_new");
        started.AudioSourceKind = "system-loopback";
        started.CaptureMethod = "WASAPI_SHARED_LOOPBACK";
        started.CaptureEngine = "wasapi-direct";
        var stopped = new AudioHelperEvent
        {
            Result = AudioHelperEventResult.Stopped,
            DurationMs = 100,
            BytesWritten = 100,
            EstimatedGapMs = 0,
            MaxEstimatedGapMs = 0
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(new List<AudioHelperEvent> { started, stopped });

        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, error => error.Contains("missing AudioSourceKind"));
    }

    [Fact]
    public void ValidateAndSummarize_LegacyStreamIntroducingSource_IsMalformed()
    {
        var started = MakeStartedEvent("rec_half_old");
        var stopped = new AudioHelperEvent
        {
            Result = AudioHelperEventResult.Stopped,
            AudioSourceKind = "microphone",
            DurationMs = 100,
            BytesWritten = 100,
            EstimatedGapMs = 0,
            MaxEstimatedGapMs = 0
        };

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(new List<AudioHelperEvent> { started, stopped });

        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Contains(summary.ValidationErrors, error => error.Contains("introduced AudioSourceKind"));
    }
}
