using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Api;

internal static class StandingPlanApiRequestParser
{
    private static readonly StringComparer Comparer = StringComparer.Ordinal;

    internal static string NormalizeIdempotencyKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw Invalid("Idempotency-Key is required.");

        var normalized = value.Trim();
        if (normalized.Length is < 1 or > 128 ||
            normalized.Any(char.IsControl) ||
            normalized.Contains('/') || normalized.Contains('\\'))
        {
            throw Invalid("Idempotency-Key must be 1-128 characters without control characters or path separators.");
        }

        return normalized;
    }

    internal static StandingPlanApiRequest Parse(string requestBody, string idempotencyKey)
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
            if (durationSeconds is < 1 or > 600)
                throw Invalid("recording_spec.duration_seconds must be between 1 and 600.");

            if (RequireInt(spec, "countdown_seconds", "recording_spec.countdown_seconds") != 0)
                throw Invalid("countdown_seconds must be 0 for a standing setup.");

            RequireString(spec, "backend", "ffmpeg-region", "recording_spec.backend");

            var output = RequireObjectProperty(spec, "output");
            RequireProperties(output, "recording_spec.output", "directory", "filename");
            var outputDirectory = RequireNonBlankString(output, "directory", "recording_spec.output.directory");
            var frozenFileName = RequireNonBlankString(output, "filename", "recording_spec.output.filename");
            ValidateOutputShape(outputDirectory, frozenFileName);

            var schedule = RequireObjectProperty(root, "schedule");
            RequireProperties(schedule, "schedule", "kind", "start_at", "latest_start_at", "planned_end_at");
            RequireString(schedule, "kind", "once", "schedule.kind");
            var scheduledStartUtc = RequireUtc(schedule, "start_at", "schedule.start_at");
            var latestStartUtc = RequireUtc(schedule, "latest_start_at", "schedule.latest_start_at");
            var plannedEndUtc = RequireUtc(schedule, "planned_end_at", "schedule.planned_end_at");
            if (scheduledStartUtc >= latestStartUtc || latestStartUtc > plannedEndUtc)
                throw Invalid("schedule must satisfy start_at < latest_start_at <= planned_end_at.");
            if (latestStartUtc - scheduledStartUtc > TimeSpan.FromMinutes(5))
                throw Invalid("latest_start_at may be no more than five minutes after start_at.");
            TimeSpan duration = TimeSpan.FromSeconds(durationSeconds);
            DateTimeOffset latestEndUtc;
            try
            {
                latestEndUtc = latestStartUtc.Add(duration);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw Invalid("The requested execution window overflows the supported UTC range.", exception.Message);
            }

            if (plannedEndUtc - scheduledStartUtc < duration || latestEndUtc > plannedEndUtc)
                throw Invalid("planned_end_at must contain the full duration after the latest allowed start.");

            var authorization = RequireObjectProperty(root, "requested_authorization");
            RequireProperties(authorization, "requested_authorization", "mode", "expires_at", "max_runs", "max_duration_seconds");
            RequireString(authorization, "mode", "standing_lease", "requested_authorization.mode");
            var leaseExpiresUtc = RequireUtc(authorization, "expires_at", "requested_authorization.expires_at");
            if (leaseExpiresUtc <= plannedEndUtc)
                throw Invalid("requested_authorization.expires_at must be after planned_end_at.");
            if (leaseExpiresUtc - DateTimeOffset.UtcNow > TimeSpan.FromHours(1))
                throw Invalid("requested_authorization.expires_at must be within one hour of request time.");
            if (RequireInt(authorization, "max_runs", "requested_authorization.max_runs") != 1)
                throw Invalid("requested_authorization.max_runs must be 1.");
            if (RequireInt(authorization, "max_duration_seconds", "requested_authorization.max_duration_seconds") != durationSeconds)
                throw Invalid("requested_authorization.max_duration_seconds must equal duration_seconds.");

            return new StandingPlanApiRequest(
                idempotencyKey,
                scheduledStartUtc,
                latestStartUtc,
                plannedEndUtc,
                leaseExpiresUtc,
                duration,
                NormalizeOutputDirectory(outputDirectory),
                frozenFileName);
        }
    }

    private static void ValidateOutputShape(string directory, string fileName)
    {
        if (directory.Any(char.IsControl) || fileName.Any(char.IsControl) ||
            fileName is "." or ".." || fileName.Contains('/') || fileName.Contains('\\'))
        {
            throw Invalid("The output path is not a valid frozen output target.");
        }

        if (!Path.IsPathFullyQualified(directory))
            throw Invalid("recording_spec.output.directory must be an absolute path.");
    }

    private static string NormalizeOutputDirectory(string directory)
    {
        try
        {
            var fullPath = Path.GetFullPath(directory);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException();
            return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? root
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            throw Invalid("recording_spec.output.directory is invalid.", exception.Message);
        }
    }

    private static DateTimeOffset RequireUtc(JsonElement parent, string propertyName, string fieldName)
    {
        var value = RequireNonBlankString(parent, propertyName, fieldName);
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ||
            !HasExplicitOffset(value))
        {
            throw Invalid($"{fieldName} must be an ISO-8601 timestamp with an explicit UTC offset.");
        }

        return parsed.ToUniversalTime();
    }

    private static bool HasExplicitOffset(string value)
    {
        var separator = value.IndexOf('T');
        if (separator < 0)
            separator = value.IndexOf('t');
        if (separator < 0)
            return false;
        var time = value[(separator + 1)..];
        return time.EndsWith("Z", StringComparison.OrdinalIgnoreCase) ||
            time.IndexOf('+', 1) >= 0 || time.IndexOf('-', 1) >= 0;
    }

    private static JsonElement RequireObjectProperty(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
            throw Invalid($"{propertyName} is required.");
        RequireObject(value, propertyName);
        return value;
    }

    private static string RequireNonBlankString(JsonElement parent, string propertyName, string fieldName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            throw Invalid($"{fieldName} must be a string.");
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || !string.Equals(text, text.Trim(), StringComparison.Ordinal))
            throw Invalid($"{fieldName} must be a non-blank canonical string.");
        return text;
    }

    private static void RequireString(JsonElement parent, string propertyName, string expected, string fieldName)
    {
        var actual = RequireNonBlankString(parent, propertyName, fieldName);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw Invalid($"{fieldName} must be '{expected}'.");
    }

    private static int RequireInt(JsonElement parent, string propertyName, string fieldName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed))
        {
            throw Invalid($"{fieldName} must be an integer.");
        }
        return parsed;
    }

    private static void RequireObject(JsonElement value, string fieldName)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Invalid($"{fieldName} must be a JSON object.");
    }

    private static void RequireProperties(JsonElement value, string fieldName, params string[] expected)
    {
        var expectedSet = new HashSet<string>(expected, Comparer);
        foreach (var property in value.EnumerateObject())
        {
            if (!expectedSet.Contains(property.Name))
                throw Invalid($"{fieldName} contains unsupported property '{property.Name}'.");
        }

        foreach (var property in expected)
        {
            if (!value.TryGetProperty(property, out _))
                throw Invalid($"{fieldName}.{property} is required.");
        }
    }

    private static ApiException Invalid(string message, object? details = null) =>
        new(400, "INVALID_ARGUMENT", message, details ?? new { suggested_action = "fix_request" });
}
