using System.Globalization;
using System.Text;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.App;

internal enum ObservationTimeRange { All, Today, LastSevenDays }
internal static class ObservationFormatting
{
    internal static UnifiedHistoryFilters Filters(string source, ObservationTimeRange range, DateTimeOffset now, TimeZoneInfo zone, string? status = null)
    {
        if (range == ObservationTimeRange.All) return new(source, Status: status);
        var date = TimeZoneInfo.ConvertTime(now, zone).Date;
        return new(source, Boundary(range == ObservationTimeRange.Today ? date : date.AddDays(-6), zone), Boundary(date.AddDays(1), zone), status);
    }
    private static DateTimeOffset Boundary(DateTime date, TimeZoneInfo zone)
    {
        date = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        // Some zones skip local midnight. Use the first valid instant; an
        // ambiguous boundary uses the earliest UTC occurrence of that midnight.
        for (var i = 0; zone.IsInvalidTime(date) && i < 2880; i++) date = date.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(date) ? zone.GetAmbiguousTimeOffsets(date).Max() : zone.GetUtcOffset(date);
        return new DateTimeOffset(date, offset).ToUniversalTime();
    }
    internal static bool CanStop(RecordingUiPresentation value) => value.State is RecordingUiState.Preparing or RecordingUiState.Countdown or RecordingUiState.Recording;
    internal static string State(string? raw, IUiTextProvider text)
    {
        if (raw is null) return text.Get("Observation_Unknown");
        var key = "Observation_State_" + raw; var translated = text.Get(key);
        return translated == key ? raw : translated;
    }
    internal static string Phase(RecordingUiPresentation p, IUiTextProvider text) => p.State switch
    {
        RecordingUiState.Preparing => text.Get("Observation_State_preparing"),
        RecordingUiState.Countdown when p.CountdownRemainingSeconds is { } seconds => text.Format("Observation_Countdown", seconds),
        RecordingUiState.Countdown => text.Get("Observation_AwaitingFrame"),
        _ => State(p.State.ToString().ToLowerInvariant(), text)
    };
    internal static string Time(DateTimeOffset? value, TimeZoneInfo zone) => value is { } v
        ? TimeZoneInfo.ConvertTime(v, zone).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) : "—";
    internal static string Media(UnifiedHistoryEntry item, IUiTextProvider text) => text.Get(!item.OutputPathRecorded
        ? "Observation_NoRecordedOutput" : item.OutputFileExists == true ? "Observation_FilePresent"
        : item.OutputFileExists == false ? "Observation_FileMissing" : "Observation_FileUnknown");
    internal static string HistoryDetails(UnifiedHistoryEntry e, IUiTextProvider text, TimeZoneInfo zone)
    {
        var b = new StringBuilder();
        void Add(string key, object? value) => b.Append(text.Get("Observation_" + key)).Append(": ").AppendLine(value?.ToString() ?? "—");
        Add("EntryId", e.EntryId); Add("Source", text.Get("Observation_Source_" + e.Source)); Add("Kind", text.Get("Observation_Kind_" + e.EntryKind));
        Add("RecordingId", e.RecordingId); Add("PlanId", e.PlanId); Add("OccurrenceId", e.OccurrenceId); Add("AuthorizationId", e.AuthorizationId); Add("RunId", e.RunId);
        Add("Status", e.Status is null ? null : State(e.Status, text) + " (" + e.Status + ")"); Add("Reason", e.ReasonCode);
        Add("AuthorizationStatus", e.AuthorizationStatus); Add("OccurrenceStatus", e.OccurrenceStatus); Add("RunStatus", e.RunStatus); Add("RunReason", e.RunReasonCode); Add("ExecutionStatus", e.ExecutionStatusCode);
        Add("Created", Time(e.CreatedAtUtc, zone)); Add("Started", Time(e.StartedAtUtc, zone)); Add("Completed", Time(e.CompletedAtUtc, zone));
        Add("WindowStart", Time(e.WindowStartUtc, zone)); Add("WindowEnd", Time(e.WindowEndUtc, zone));
        Add("Media", Media(e, text)); Add("RecordedPath", e.OutputPathRecorded ? e.OutputPath : null);
        Add("Bytes", e.OutputSizeBytes); Add("Duration", e.OutputDurationSeconds?.ToString(CultureInfo.InvariantCulture));
        return b.ToString();
    }
    internal static string ActiveDetails(ObservationActiveRecording row, IUiTextProvider text, TimeZoneInfo zone)
    {
        var p = row.Presentation;
        var started = p.StartedAtUtc == default ? null : (DateTimeOffset?)new DateTimeOffset(DateTime.SpecifyKind(p.StartedAtUtc, DateTimeKind.Utc));
        return $"{text.Get("Observation_RecordingId")}: {p.RecordingId}\r\n{text.Get("Observation_Status")}: {Phase(p, text)} ({p.State})\r\n" +
            $"{text.Get("Observation_Kind")}: {text.Get(p.IsScreenshotSeries ? "Observation_ScreenshotSeries" : "Observation_Kind_ordinary_video")}\r\n" +
            $"{text.Get("Observation_Target")}: {p.SourceType} ({p.CaptureBounds.X}, {p.CaptureBounds.Y}, {p.CaptureBounds.Width} × {p.CaptureBounds.Height})\r\n" +
            $"{text.Get("Observation_Role")}: {p.NestedRole ?? "—"}\r\n{text.Get("Observation_ParentId")}: {p.ParentRecordingId ?? "—"}\r\n" +
            $"{text.Get("Observation_Started")}: {Time(started, zone)}\r\n{text.Get("Observation_RecordedPath")}: {text.Get("Observation_Unknown")}\r\n" +
            (p.IsScreenshotSeries ? $"{text.Get("Observation_Frames")}: {p.SeriesCapturedFrameCount?.ToString() ?? "—"} / {p.SeriesPlannedFrameCount?.ToString() ?? "—"}\r\n" : "");
    }
}
