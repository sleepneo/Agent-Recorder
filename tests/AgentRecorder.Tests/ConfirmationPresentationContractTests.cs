using System;
using System.Linq;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class ConfirmationPresentationContractTests
{
    [Fact]
    public void ApiProjection_NoAudioNoSeries_MatchesTask213BeforeReference()
    {
        var presentation = CreatePresentation(new RecordingRequestSummary
        {
            Mode = "video",
            Source = "display: Display 1",
            Audio = "No audio",
            AudioSourceKind = "none",
            AudioSystemEnabled = false,
            AudioSystemDefaultOutput = null,
            AudioSystemOutputName = null,
            AudioSystemOutputIsDefault = null,
            AudioSystemOutputSelection = "selected",
            Duration = "30s",
            CountdownSeconds = 0,
            Output = @"C:\recordings\capture.mp4",
            NestedRole = "none"
        }, selectionFallback: false,
            targetDisplayBounds: new ConfirmationCaptureBounds(0, 0, 1920, 1080),
            captureBounds: new ConfirmationCaptureBounds(0, 0, 1920, 1080));

        var actual = AssertMatchesTask213BeforeReference(presentation);

        Assert.Equal(JsonValueKind.String, actual.GetProperty("audio_system_enabled").ValueKind);
        Assert.Equal("False", actual.GetProperty("audio_system_enabled").GetString());
        Assert.Equal(JsonValueKind.String, actual.GetProperty("audio_system_default_output").ValueKind);
        Assert.Equal("", actual.GetProperty("audio_system_default_output").GetString());
        Assert.Equal(JsonValueKind.String, actual.GetProperty("audio_system_output_name").ValueKind);
        Assert.Equal("", actual.GetProperty("audio_system_output_name").GetString());
        Assert.Equal(JsonValueKind.String, actual.GetProperty("audio_system_output_is_default").ValueKind);
        Assert.Equal("", actual.GetProperty("audio_system_output_is_default").GetString());
        Assert.Equal(JsonValueKind.String, actual.GetProperty("series").ValueKind);
        Assert.Equal("", actual.GetProperty("series").GetString());
        Assert.Equal(JsonValueKind.Null, actual.GetProperty("series_interval_ms").ValueKind);
        Assert.Equal(JsonValueKind.Object, actual.GetProperty("target_display_bounds").ValueKind);
        Assert.Equal(JsonValueKind.Object, actual.GetProperty("capture_bounds").ValueKind);
        Assert.Equal(JsonValueKind.False, actual.GetProperty("selection_fallback").ValueKind);
    }

    [Fact]
    public void ApiProjection_MicrophoneSummary_MatchesTask213BeforeReference()
    {
        var presentation = CreatePresentation(new RecordingRequestSummary
        {
            Mode = "video",
            Source = "window: Editor",
            Audio = "Microphone: USB Mic",
            AudioSourceKind = "microphone",
            AudioSystemEnabled = false,
            AudioSystemOutputSelection = "selected",
            AudioDevice = "mic_usb",
            AudioVolumePercent = 42,
            Duration = "60s",
            CountdownSeconds = 3,
            Output = @"C:\recordings\mic.mp4",
            NestedRole = "none"
        }, selectionFallback: true, targetDisplayBounds: null,
            captureBounds: new ConfirmationCaptureBounds(20, 30, 800, 600));

        var actual = AssertMatchesTask213BeforeReference(presentation);

        Assert.Equal(JsonValueKind.String, actual.GetProperty("audio_system_enabled").ValueKind);
        Assert.Equal("False", actual.GetProperty("audio_system_enabled").GetString());
        Assert.Equal(JsonValueKind.Null, actual.GetProperty("target_display_bounds").ValueKind);
        Assert.Equal(JsonValueKind.Object, actual.GetProperty("capture_bounds").ValueKind);
        Assert.Equal(JsonValueKind.True, actual.GetProperty("selection_fallback").ValueKind);
    }

    [Fact]
    public void ApiProjection_SystemAudioDefaultOutput_MatchesTask213BeforeReference()
    {
        var presentation = CreatePresentation(new RecordingRequestSummary
        {
            Mode = "video",
            Source = "display: Display 1",
            Audio = "System audio: On (Default output: Speakers)",
            AudioSourceKind = "system-loopback",
            AudioSystemEnabled = true,
            AudioSystemDefaultOutput = "Speakers",
            AudioSystemOutputName = "Speakers",
            AudioSystemOutputIsDefault = true,
            AudioSystemOutputSelection = "default",
            Duration = "Manual stop",
            CountdownSeconds = 5,
            Output = @"C:\recordings\system.mp4",
            NestedRole = "none"
        }, selectionFallback: false, targetDisplayBounds: null,
            captureBounds: new ConfirmationCaptureBounds(0, 0, 1920, 1080));

        var actual = AssertMatchesTask213BeforeReference(presentation);

        Assert.Equal(JsonValueKind.String, actual.GetProperty("audio_system_enabled").ValueKind);
        Assert.Equal("True", actual.GetProperty("audio_system_enabled").GetString());
        Assert.Equal(JsonValueKind.String, actual.GetProperty("audio_system_default_output").ValueKind);
        Assert.Equal("Speakers", actual.GetProperty("audio_system_default_output").GetString());
        Assert.Equal(JsonValueKind.String, actual.GetProperty("audio_system_output_is_default").ValueKind);
        Assert.Equal("True", actual.GetProperty("audio_system_output_is_default").GetString());
    }

    [Fact]
    public void ApiProjection_SystemAudioSelectedOutput_MatchesTask213BeforeReference()
    {
        var presentation = CreatePresentation(new RecordingRequestSummary
        {
            Mode = "video",
            Source = "display: Display 1",
            Audio = "System audio: On (Selected output: Headphones)",
            AudioSourceKind = "system-loopback",
            AudioSystemEnabled = true,
            AudioSystemDefaultOutput = null,
            AudioSystemOutputName = "Headphones",
            AudioSystemOutputIsDefault = false,
            AudioSystemOutputSelection = "selected",
            Duration = "30s",
            CountdownSeconds = 0,
            Output = @"C:\recordings\selected-system.mp4",
            NestedRole = "none"
        }, selectionFallback: false, targetDisplayBounds: null, captureBounds: null);

        var actual = AssertMatchesTask213BeforeReference(presentation);

        Assert.Equal(JsonValueKind.String, actual.GetProperty("audio_system_default_output").ValueKind);
        Assert.Equal("", actual.GetProperty("audio_system_default_output").GetString());
        Assert.Equal(JsonValueKind.String, actual.GetProperty("audio_system_output_name").ValueKind);
        Assert.Equal("Headphones", actual.GetProperty("audio_system_output_name").GetString());
        Assert.Equal(JsonValueKind.String, actual.GetProperty("audio_system_output_is_default").ValueKind);
        Assert.Equal("False", actual.GetProperty("audio_system_output_is_default").GetString());
        Assert.Equal(JsonValueKind.Null, actual.GetProperty("target_display_bounds").ValueKind);
        Assert.Equal(JsonValueKind.Null, actual.GetProperty("capture_bounds").ValueKind);
    }

    [Fact]
    public void ApiProjection_ScreenshotSeries_MatchesTask213BeforeReferenceAndKeepsNumericTopLevelFields()
    {
        var presentation = CreatePresentation(new RecordingRequestSummary
        {
            Mode = "screenshot_series",
            Source = "region: test",
            Audio = "No audio",
            AudioSourceKind = "none",
            AudioSystemEnabled = false,
            AudioSystemOutputSelection = "selected",
            Duration = "Manual stop",
            CountdownSeconds = 5,
            Output = @"C:\recordings\series",
            Series = new RecordingSeriesPresentation
            {
                IntervalMs = 1000,
                MaxCount = 3,
                MaxDurationSeconds = null,
                PlannedFrameCount = 3,
                OutputKind = "png_sequence_directory"
            },
            NestedRole = "inner"
        }, selectionFallback: true,
            targetDisplayBounds: new ConfirmationCaptureBounds(-1920, 0, 1920, 1080),
            captureBounds: new ConfirmationCaptureBounds(10, 20, 640, 480),
            outputKind: "png_sequence_directory");

        var actual = AssertMatchesTask213BeforeReference(presentation);
        var series = actual.GetProperty("series");

        Assert.Equal(JsonValueKind.String, series.ValueKind);
        Assert.Equal(
            "{ interval_ms = 1000, max_count = 3, max_duration_seconds = , planned_frame_count = 3, output_kind = png_sequence_directory }",
            series.GetString());
        Assert.Equal(JsonValueKind.Number, actual.GetProperty("series_interval_ms").ValueKind);
        Assert.Equal(1000, actual.GetProperty("series_interval_ms").GetInt32());
        Assert.Equal(JsonValueKind.Number, actual.GetProperty("series_max_count").ValueKind);
        Assert.Equal(3, actual.GetProperty("series_max_count").GetInt32());
        Assert.Equal(JsonValueKind.Null, actual.GetProperty("series_max_duration_seconds").ValueKind);
        Assert.Equal(JsonValueKind.Number, actual.GetProperty("series_planned_frame_count").ValueKind);
        Assert.Equal(3, actual.GetProperty("series_planned_frame_count").GetInt32());
        Assert.Equal(JsonValueKind.True, actual.GetProperty("selection_fallback").ValueKind);
    }

    [Fact]
    public void PendingItem_UsesPresentationExpiryWithoutRecomputingIt()
    {
        var created = new DateTime(2026, 8, 23, 11, 59, 0, DateTimeKind.Utc);
        var expires = new DateTime(2026, 8, 23, 12, 1, 0, DateTimeKind.Utc);
        var presentation = CreatePresentation(new RecordingRequestSummary(),
            createdAtUtc: created, expiresAtUtc: expires);

        var item = new PendingConfirmationItem(presentation, _ => { });

        Assert.Equal(created, item.CreatedAtUtc);
        Assert.Equal(expires, item.ExpiresAtUtc);
        Assert.True(item.IsExpiredLocal);
    }

    private static RecordingConfirmationPresentation CreatePresentation(
        RecordingRequestSummary summary,
        bool selectionFallback = false,
        ConfirmationCaptureBounds? targetDisplayBounds = null,
        ConfirmationCaptureBounds? captureBounds = null,
        string outputKind = "mp4_file",
        DateTime? createdAtUtc = null,
        DateTime? expiresAtUtc = null)
    {
        var created = createdAtUtc ?? new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
        return new RecordingConfirmationPresentation
        {
            Summary = summary,
            RecordingId = "rec_contract",
            ConfirmationId = "confirm_contract",
            TimeoutSeconds = 60,
            CreatedAtUtc = created,
            ExpiresAtUtc = expiresAtUtc ?? created.AddMinutes(1),
            SourceType = "display",
            SourceTitle = "Display 1",
            SourceApplication = null,
            WindowId = "display_1",
            TraceId = "trace_contract",
            CoordinateSpace = "virtual_screen",
            CaptureSemantics = "display_surface",
            PlannedBackend = "ffmpeg",
            PreviewSemantics = "display_surface",
            SelectionReasonCode = "default",
            SelectionAvailabilitySource = "test",
            SelectionFallback = selectionFallback,
            TargetDisplayId = "display_1",
            TargetDisplayBounds = targetDisplayBounds,
            CaptureBounds = captureBounds,
            OutputKind = outputKind
        };
    }

    private static JsonElement AssertMatchesTask213BeforeReference(
        RecordingConfirmationPresentation presentation)
    {
        var actual = JsonSerializer.SerializeToElement(
            RecordingConfirmationApiProjection.ToObject(presentation), ApiResponse.Json);
        var expected = JsonSerializer.SerializeToElement(
            LegacySummary(presentation), ApiResponse.Json);

        var expectedNames = expected.EnumerateObject().Select(property => property.Name).ToArray();
        var actualNames = actual.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(expectedNames, actualNames);

        foreach (var expectedProperty in expected.EnumerateObject())
        {
            var actualProperty = actual.GetProperty(expectedProperty.Name);
            Assert.Equal(expectedProperty.Value.ValueKind, actualProperty.ValueKind);
            Assert.Equal(expectedProperty.Value.GetRawText(), actualProperty.GetRawText());
        }

        return actual;
    }

    /// <summary>
    /// Independent characterization constructor for the legacy
    /// RecordingEngine summaryWithMeta contract. It preserves the old
    /// GetSummaryField(value).ToString() ?? "" behavior and deliberately does
    /// not call the production projection to build expected values.
    /// </summary>
    private static object LegacySummary(RecordingConfirmationPresentation presentation)
    {
        var summary = presentation.Summary;
        var series = summary.Series == null
            ? ""
            : new
            {
                interval_ms = summary.Series.IntervalMs,
                max_count = summary.Series.MaxCount,
                max_duration_seconds = summary.Series.MaxDurationSeconds,
                planned_frame_count = summary.Series.PlannedFrameCount,
                output_kind = summary.Series.OutputKind
            }.ToString() ?? "";

        return new
        {
            mode = summary.Mode,
            source = summary.Source,
            audio = summary.Audio,
            audio_source_kind = summary.AudioSourceKind,
            audio_system_enabled = summary.AudioSystemEnabled.ToString(),
            audio_system_default_output = summary.AudioSystemDefaultOutput ?? "",
            audio_system_output_name = summary.AudioSystemOutputName ?? "",
            audio_system_output_is_default = summary.AudioSystemOutputIsDefault?.ToString() ?? "",
            audio_system_output_selection = summary.AudioSystemOutputSelection,
            duration = summary.Duration,
            countdown_seconds = summary.CountdownSeconds,
            output = summary.Output,
            series,
            series_interval_ms = summary.Series?.IntervalMs,
            series_max_count = summary.Series?.MaxCount,
            series_max_duration_seconds = summary.Series?.MaxDurationSeconds,
            series_planned_frame_count = summary.Series?.PlannedFrameCount,
            output_kind = presentation.OutputKind,
            nested_role = summary.NestedRole,
            recording_id = presentation.RecordingId,
            confirmation_id = presentation.ConfirmationId,
            timeout_seconds = presentation.TimeoutSeconds,
            expires_at = presentation.ExpiresAtUtc.ToUniversalTime()
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            source_type = presentation.SourceType,
            source_title = presentation.SourceTitle,
            source_application = presentation.SourceApplication,
            window_id = presentation.WindowId,
            trace_id = presentation.TraceId,
            coordinate_space = presentation.CoordinateSpace,
            capture_semantics = presentation.CaptureSemantics,
            planned_backend = presentation.PlannedBackend,
            preview_semantics = presentation.PreviewSemantics,
            selection_reason_code = presentation.SelectionReasonCode,
            selection_availability_source = presentation.SelectionAvailabilitySource,
            selection_fallback = presentation.SelectionFallback,
            target_display_id = presentation.TargetDisplayId ?? "",
            target_display_bounds = LegacyBounds(presentation.TargetDisplayBounds),
            capture_bounds = LegacyBounds(presentation.CaptureBounds)
        };
    }

    private static object? LegacyBounds(ConfirmationCaptureBounds? bounds) => bounds == null
        ? null
        : new
        {
            x = bounds.X,
            y = bounds.Y,
            width = bounds.Width,
            height = bounds.Height
        };
}
