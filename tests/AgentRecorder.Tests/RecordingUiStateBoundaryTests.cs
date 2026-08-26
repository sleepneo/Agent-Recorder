using System;
using System.IO;
using System.Linq;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class RecordingUiStateBoundaryTests
{
    [Fact]
    public void InfrastructureDtoSource_HasNoCoreAppUiOrJsonBoundary()
    {
        var path = Path.Combine(
            TestHelper.ProjectRoot,
            "src",
            "AgentRecorder.Infrastructure",
            "RecordingUiPresentation.cs");
        var source = File.ReadAllText(path);

        Assert.DoesNotContain("AgentRecorder.Core", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentRecorder.App", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Windows.Forms", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Windows", source, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonNode", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Rectangle", source, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic", source, StringComparison.Ordinal);
        Assert.DoesNotContain("object Recording", source, StringComparison.Ordinal);

        var references = typeof(RecordingUiPresentation).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name != null)
            .ToArray();
        Assert.DoesNotContain("AgentRecorder.Core", references);
        Assert.DoesNotContain("AgentRecorder.App", references);
    }

    [Fact]
    public void TrayBoundarySource_HasNoWeakObjectRecordingSignatures()
    {
        var path = Path.Combine(
            TestHelper.ProjectRoot,
            "src",
            "AgentRecorder.Infrastructure",
            "ITrayContext.cs");
        var source = File.ReadAllText(path);

        Assert.DoesNotContain("SetRecording(object", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetPreparing(object", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetCountdown(object", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetSeriesProgress(object", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetFinalizing(object", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetStopping(object", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetIdle(object", source, StringComparison.Ordinal);

        var methods = typeof(ITrayContext).GetMethods();
        foreach (var name in new[]
                 {
                     "SetRecording", "SetPreparing", "SetCountdown", "SetSeriesProgress",
                     "SetFinalizing", "SetStopping", "SetIdle"
                 })
        {
            var method = Assert.Single(methods, candidate => candidate.Name == name);
            Assert.Single(method.GetParameters(), parameter =>
                parameter.ParameterType == typeof(RecordingUiPresentation));
        }
    }

    [Fact]
    public void CoreSnapshot_IsolatedFromLaterRecordingConfigAndSeriesMutation()
    {
        var started = new DateTime(2026, 8, 24, 9, 10, 11, DateTimeKind.Utc);
        var recording = new Recording
        {
            SourceType = "region",
            StartedAtUtc = started,
            DurationSeconds = 42,
            NestedRole = "inner",
            ParentRecordingId = "outer-a",
            NestedSessionId = "session-a",
            Config = new CaptureConfig
            {
                SourceKind = "region",
                Mode = ScreenshotSeriesConfig.ModeName,
                Bounds = (10, 20, 300, 200),
                ScreenshotSeries = new ScreenshotSeriesConfig
                {
                    IntervalMs = 1000,
                    MaxCount = 3,
                    PlannedFrameCount = 3
                }
            },
            ScreenshotSeries = new ScreenshotSeriesRuntime { PlannedFrameCount = 3 }
        };

        var nextDue = started.AddSeconds(2);
        var snapshot = RecordingEngine.CreateRecordingUiPresentationForTests(
            recording,
            RecordingUiState.Countdown,
            countdownRemainingSeconds: 2,
            seriesCapturedFrameCount: 1,
            seriesPlannedFrameCount: 3,
            seriesNextCaptureDueAtUtc: nextDue);

        recording.SourceType = "window";
        recording.StartedAtUtc = started.AddHours(1);
        recording.DurationSeconds = 99;
        recording.NestedRole = "outer";
        recording.ParentRecordingId = "changed";
        recording.NestedSessionId = "changed-session";
        recording.Config.Bounds = (900, 901, 12, 13);
        recording.ScreenshotSeries = new ScreenshotSeriesRuntime { PlannedFrameCount = 99 };

        Assert.Equal("region", snapshot.SourceType);
        Assert.Equal(new RecordingUiBounds(10, 20, 300, 200), snapshot.CaptureBounds);
        Assert.Equal(started, snapshot.StartedAtUtc);
        Assert.Equal(42, snapshot.DurationSeconds);
        Assert.True(snapshot.IsScreenshotSeries);
        Assert.Equal(1, snapshot.SeriesCapturedFrameCount);
        Assert.Equal(3, snapshot.SeriesPlannedFrameCount);
        Assert.Equal(nextDue, snapshot.SeriesNextCaptureDueAtUtc);
        Assert.Equal(2, snapshot.CountdownRemainingSeconds);
        Assert.Equal("inner", snapshot.NestedRole);
        Assert.Equal("outer-a", snapshot.ParentRecordingId);
        Assert.Equal("session-a", snapshot.NestedSessionId);
    }

    [Fact]
    public void CoreSnapshot_UsesExplicitTypedPhaseForAllTrayNotifications()
    {
        var recording = new Recording
        {
            SourceType = "display",
            Config = new CaptureConfig { Bounds = (1, 2, 3, 4) }
        };

        foreach (var state in new[]
                 {
                     RecordingUiState.PendingConfirmation,
                     RecordingUiState.Preparing,
                     RecordingUiState.Countdown,
                     RecordingUiState.Recording,
                     RecordingUiState.Stopping,
                     RecordingUiState.Finalizing,
                     RecordingUiState.Idle
                 })
        {
            var snapshot = RecordingEngine.CreateRecordingUiPresentationForTests(recording, state);
            Assert.Equal(state, snapshot.State);
            Assert.Equal(recording.Id, snapshot.RecordingId);
            Assert.Equal(new RecordingUiBounds(1, 2, 3, 4), snapshot.CaptureBounds);
        }
    }
}
