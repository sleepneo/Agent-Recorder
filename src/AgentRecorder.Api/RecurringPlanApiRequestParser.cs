using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AgentRecorder.Core.Automation;
using ApiException = AgentRecorder.Infrastructure.ApiException;

namespace AgentRecorder.Api;

internal static class RecurringPlanApiRequestParser
{
    private static readonly StringComparer Comparer = StringComparer.Ordinal;

    internal static string NormalizeIdempotencyKey(string? value) =>
        StandingPlanApiRequestParser.NormalizeIdempotencyKey(value);

    internal static RecurringPlanApiRequest Parse(string requestBody, string idempotencyKey)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(requestBody, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
        }
        catch (JsonException exception)
        {
            throw Invalid("Invalid JSON body.", exception.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            RequireObject(root, "request");
            RequireProperties(root, "request", "recording_spec", "schedule", "requested_authorization");

            var spec = RequireObjectProperty(root, "recording_spec");
            RequireProperties(spec, "recording_spec", "source", "audio", "duration_seconds", "countdown_seconds", "backend", "output");

            var source = RequireObjectProperty(spec, "source");
            RequireProperties(source, "recording_spec.source", "type");
            RequireString(source, "type", "fixed_region", "recording_spec.source.type");

            var audio = RequireObjectProperty(spec, "audio");
            RequireProperties(audio, "recording_spec.audio", "mode");
            RequireString(audio, "mode", "none", "recording_spec.audio.mode");

            var durationSeconds = RequireInt(spec, "duration_seconds", "recording_spec.duration_seconds");
            if (durationSeconds is < RecurringPlanSetupLimits.MinimumDurationSeconds or > RecurringPlanSetupLimits.MaximumDurationSeconds)
                throw Invalid("recording_spec.duration_seconds is outside the recurring setup limit.");
            if (RequireInt(spec, "countdown_seconds", "recording_spec.countdown_seconds") != 0)
                throw Invalid("recording_spec.countdown_seconds must be 0 for recurring setup.");
            RequireString(spec, "backend", "ffmpeg-region", "recording_spec.backend");

            var output = RequireObjectProperty(spec, "output");
            RequireProperties(output, "recording_spec.output", "directory", "filename_prefix");
            var outputDirectory = RequireNonBlankString(output, "directory", "recording_spec.output.directory");
            var filenamePrefix = RequireNonBlankString(output, "filename_prefix", "recording_spec.output.filename_prefix");
            outputDirectory = NormalizeOutputDirectory(outputDirectory);
            ValidateFilenamePrefix(filenamePrefix);

            var scheduleElement = RequireObjectProperty(root, "schedule");
            RequireProperties(scheduleElement, "schedule", "kind", "time_zone_id", "local_start_date", "local_end_date", "local_time", "weekdays", "maximum_occurrences", "latest_start_grace_seconds");
            var kindText = RequireNonBlankString(scheduleElement, "kind", "schedule.kind");
            var kind = kindText switch
            {
                "daily" => RecurringScheduleKind.Daily,
                "weekly" => RecurringScheduleKind.Weekly,
                _ => throw Invalid("schedule.kind must be 'daily' or 'weekly'."),
            };
            var timeZoneId = RequireNonBlankString(scheduleElement, "time_zone_id", "schedule.time_zone_id");
            if (timeZoneId.Length > RecurringPlanSetupLimits.MaximumTimeZoneIdLength)
                throw Invalid("schedule.time_zone_id is too long.");
            var localStartDate = RequireDate(scheduleElement, "local_start_date", "schedule.local_start_date");
            var localEndDate = RequireDate(scheduleElement, "local_end_date", "schedule.local_end_date");
            if (localEndDate.DayNumber < localStartDate.DayNumber ||
                localEndDate.DayNumber - localStartDate.DayNumber > RecurringPlanSetupLimits.MaximumDateSpanDays)
                throw Invalid("schedule local date range is outside the bounded recurring setup limit.");
            var localTime = RequireTime(scheduleElement, "local_time", "schedule.local_time");
            var maximumOccurrences = RequireInt(scheduleElement, "maximum_occurrences", "schedule.maximum_occurrences");
            if (maximumOccurrences is < 1 or > RecurringPlanSetupLimits.MaximumOccurrences)
                throw Invalid("schedule.maximum_occurrences is outside the bounded recurring setup limit.");
            var graceSeconds = RequireInt(scheduleElement, "latest_start_grace_seconds", "schedule.latest_start_grace_seconds");
            if (graceSeconds is < 0 or > RecurringPlanSetupLimits.MaximumGraceSeconds)
                throw Invalid("schedule.latest_start_grace_seconds is outside the recurring setup limit.");

            var weekdays = ParseWeekdays(scheduleElement, kind);
            RecurringPlanSchedule schedule;
            try
            {
                schedule = new RecurringPlanSchedule(
                    kind,
                    timeZoneId,
                    localStartDate,
                    localEndDate,
                    localTime,
                    maximumOccurrences,
                    TimeSpan.FromSeconds(durationSeconds),
                    TimeSpan.FromSeconds(graceSeconds),
                    weekdays);
            }
            catch (Phase3DomainException exception)
            {
                throw Invalid("The recurring schedule is invalid.", exception.ReasonCode);
            }

            var authorization = RequireObjectProperty(root, "requested_authorization");
            RequireProperties(authorization, "requested_authorization", "mode", "valid_until", "max_runs", "max_total_duration_seconds");
            RequireString(authorization, "mode", "recurring_lease", "requested_authorization.mode");
            var validUntilUtc = RequireUtc(authorization, "valid_until", "requested_authorization.valid_until");
            var maxRuns = RequireInt(authorization, "max_runs", "requested_authorization.max_runs");
            if (maxRuns != schedule.MaximumOccurrences)
                throw Invalid("requested_authorization.max_runs must cover the complete requested schedule.");
            var maxTotalDurationSeconds = RequireInt(authorization, "max_total_duration_seconds", "requested_authorization.max_total_duration_seconds");
            var requiredTotalDurationSeconds = checked((long)schedule.MaximumOccurrences * durationSeconds);
            if (maxTotalDurationSeconds < requiredTotalDurationSeconds ||
                maxTotalDurationSeconds > RecurringPlanSetupLimits.MaximumTotalDurationSeconds)
                throw Invalid("requested_authorization.max_total_duration_seconds does not cover the bounded schedule quota.");

            DateTimeOffset latestPlannedEndUtc;
            try
            {
                latestPlannedEndUtc = RecurringScheduleAuthorizationBounds.GetLatestValidPlannedEndUtc(
                    "recurring-setup-intent-boundary",
                    1,
                    schedule);
            }
            catch (Phase3DomainException exception)
            {
                throw Invalid("The recurring schedule has no valid bounded execution end.", exception.ReasonCode);
            }

            if (validUntilUtc <= latestPlannedEndUtc)
                throw Invalid("requested_authorization.valid_until must be strictly later than the latest planned end.", "recurring_setup_intent_lease_validity_boundary_invalid");

            return new RecurringPlanApiRequest(
                idempotencyKey,
                schedule,
                validUntilUtc,
                maxRuns,
                TimeSpan.FromSeconds(maxTotalDurationSeconds),
                outputDirectory,
                filenamePrefix);
        }
    }

    private static IReadOnlyList<DayOfWeek>? ParseWeekdays(JsonElement schedule, RecurringScheduleKind kind)
    {
        if (!schedule.TryGetProperty("weekdays", out var value))
            throw Invalid("schedule.weekdays is required.");

        if (kind == RecurringScheduleKind.Daily)
        {
            if (value.ValueKind != JsonValueKind.Null)
                throw Invalid("schedule.weekdays must be null for a daily schedule.");
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
            throw Invalid("schedule.weekdays must be a non-empty array for a weekly schedule.");

        var result = new List<DayOfWeek>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
                throw Invalid("schedule.weekdays entries must be lowercase weekday codes.");
            var text = element.GetString();
            if (text is null || !string.Equals(text, text.ToLowerInvariant(), StringComparison.Ordinal))
                throw Invalid("schedule.weekdays entries must be lowercase weekday codes.");
            var day = text switch
            {
                "monday" => DayOfWeek.Monday,
                "tuesday" => DayOfWeek.Tuesday,
                "wednesday" => DayOfWeek.Wednesday,
                "thursday" => DayOfWeek.Thursday,
                "friday" => DayOfWeek.Friday,
                "saturday" => DayOfWeek.Saturday,
                "sunday" => DayOfWeek.Sunday,
                _ => throw Invalid("schedule.weekdays contains an unsupported weekday code."),
            };
            if (!result.Contains(day))
                result.Add(day);
            else
                throw Invalid("schedule.weekdays must contain unique weekday codes.");
        }
        return result;
    }

    private static DateOnly RequireDate(JsonElement parent, string property, string field)
    {
        var value = RequireNonBlankString(parent, property, field);
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw Invalid($"{field} must be an ISO local date (yyyy-MM-dd).");
        return date;
    }

    private static TimeOnly RequireTime(JsonElement parent, string property, string field)
    {
        var value = RequireNonBlankString(parent, property, field);
        if (!TimeOnly.TryParseExact(value, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw Invalid($"{field} must be a whole-second local time (HH:mm:ss).");
        return time;
    }

    private static DateTimeOffset RequireUtc(JsonElement parent, string property, string field)
    {
        var value = RequireNonBlankString(parent, property, field);
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) || !HasExplicitOffset(value))
            throw Invalid($"{field} must be an ISO-8601 timestamp with an explicit UTC offset.");
        try
        {
            return parsed.ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw Invalid($"{field} is outside the supported UTC range.", exception.Message);
        }
    }

    private static bool HasExplicitOffset(string value)
    {
        var separator = value.IndexOf('T');
        if (separator < 0) separator = value.IndexOf('t');
        if (separator < 0) return false;
        var time = value[(separator + 1)..];
        return time.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || time.IndexOf('+', 1) >= 0 || time.IndexOf('-', 1) >= 0;
    }

    private static string NormalizeOutputDirectory(string value)
    {
        if (value.Any(char.IsControl) || !Path.IsPathFullyQualified(value))
            throw Invalid("recording_spec.output.directory must be an absolute path without control characters.");
        if (RecurringPlanSetupLimits.ContainsTraversalPathSegment(value))
            throw Invalid("recording_spec.output.directory must not contain traversal path segments.", "recurring_setup_intent_output_directory_traversal");
        try
        {
            var normalized = AuthorizedFixedRegionScope.NormalizeOutputDirectoryForAuthorization(value);
            if (normalized.Length > RecurringPlanSetupLimits.MaximumOutputDirectoryLength)
                throw Invalid("recording_spec.output.directory is too long.");
            return normalized;
        }
        catch (Phase3DomainException exception)
        {
            throw Invalid("recording_spec.output.directory is invalid.", exception.ReasonCode);
        }
    }

    private static void ValidateFilenamePrefix(string value)
    {
        if (value.Length is < 1 or > RecurringPlanSetupLimits.MaximumFilenamePrefixLength ||
            value is "." or ".." || value.EndsWith('.') || value.Contains("..", StringComparison.Ordinal) ||
            value.Any(char.IsControl) || value.Contains('/') || value.Contains('\\') ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsReservedDeviceName(value))
            throw Invalid("recording_spec.output.filename_prefix is not a safe filename prefix.");
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune) || rune.Value is '-' or '_' or ' ')
                continue;
            throw Invalid("recording_spec.output.filename_prefix contains an unsupported character.");
        }
    }

    private static bool IsReservedDeviceName(string value)
    {
        var stem = value.TrimEnd(' ').Split('-', StringSplitOptions.None)[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9');
    }

    private static JsonElement RequireObjectProperty(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value)) throw Invalid($"{property} is required.");
        RequireObject(value, property);
        return value;
    }

    private static string RequireNonBlankString(JsonElement parent, string property, string field)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            throw Invalid($"{field} must be a string.");
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || !string.Equals(text, text.Trim(), StringComparison.Ordinal))
            throw Invalid($"{field} must be a non-blank canonical string.");
        return text!;
    }

    private static void RequireString(JsonElement parent, string property, string expected, string field)
    {
        if (!string.Equals(RequireNonBlankString(parent, property, field), expected, StringComparison.Ordinal))
            throw Invalid($"{field} must be '{expected}'.");
    }

    private static int RequireInt(JsonElement parent, string property, string field)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed))
            throw Invalid($"{field} must be an integer.");
        return parsed;
    }

    private static void RequireObject(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid($"{field} must be a JSON object.");
    }

    private static void RequireProperties(JsonElement value, string field, params string[] expected)
    {
        var expectedSet = new HashSet<string>(expected, Comparer);
        var seen = new HashSet<string>(Comparer);
        foreach (var property in value.EnumerateObject())
        {
            if (!expectedSet.Contains(property.Name)) throw Invalid($"{field} contains unsupported property '{property.Name}'.");
            if (!seen.Add(property.Name)) throw Invalid($"{field} contains duplicate property '{property.Name}'.");
        }
        foreach (var property in expected)
            if (!value.TryGetProperty(property, out _)) throw Invalid($"{field}.{property} is required.");
    }

    private static ApiException Invalid(string message, object? details = null) =>
        new(400, "INVALID_ARGUMENT", message, details ?? new { suggested_action = "fix_request" });
}
