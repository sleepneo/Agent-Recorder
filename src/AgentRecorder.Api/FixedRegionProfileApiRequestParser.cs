using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Api;

internal sealed record FixedRegionProfileCreateApiRequest(
    string Name, ProfileRef? SourceRef, string? SourceSelectionRef, FixedRegionProfileChanges Changes,
    int? SelectionDurationSeconds, int? SelectionCountdownSeconds, string? SelectionOutputDirectory,
    string? SelectionFilenamePrefix, string IdempotencyKey, string RequestHash);

internal sealed record FixedRegionProfilePatchApiRequest(string? Name, FixedRegionProfileChanges Changes);

internal static class FixedRegionProfileApiRequestParser
{
    private static readonly StringComparer PropertyComparer = StringComparer.Ordinal;

    public static FixedRegionProfileCreateApiRequest ParseCreate(string body, string? idempotencyHeader)
    {
        var key = StandingPlanApiRequestParser.NormalizeIdempotencyKey(idempotencyHeader);
        using var document = ParseDocument(body);
        var root = RequireObject(document.RootElement, "request");
        RequireAllowed(root, "name", "source_profile_ref", "source_selection_ref", "changes");
        var name = ReadName(Required(root, "name"));
        var hasProfileRef = root.TryGetProperty("source_profile_ref", out var profileRefElement);
        var hasSelectionRef = root.TryGetProperty("source_selection_ref", out var selectionRefElement);
        if (hasProfileRef == hasSelectionRef)
            throw Invalid("Exactly one source_profile_ref or source_selection_ref is required.");

        if (hasProfileRef)
        {
            var reference = ParseProfileRef(profileRefElement);
            var changes = root.TryGetProperty("changes", out var changesElement)
                ? ParseChanges(changesElement, allowEmpty: true)
                : new FixedRegionProfileChanges();
            // Keep the established copy request hash byte-for-byte compatible.
            var canonical = Canonicalize(name, reference, changes);
            var hash = ComputeHash(canonical);
            return new FixedRegionProfileCreateApiRequest(name, reference, null, changes,
                null, null, null, null, key, hash);
        }

        var selectionRef = ReadRequiredString(selectionRefElement, "source_selection_ref");
        var selectionChanges = root.TryGetProperty("changes", out var selectionChangesElement)
            ? ParseSelectionChanges(selectionChangesElement)
            : throw Invalid("changes.duration_seconds is required for selection-based profile creation.");
        var selectionCanonical = string.Concat("create_from_local_selection\n", CanonicalPart(name),
            CanonicalPart(selectionRef), CanonicalPart(selectionChanges.DurationSeconds!.Value.ToString(CultureInfo.InvariantCulture)),
            CanonicalPart((selectionChanges.CountdownSeconds ?? 3).ToString(CultureInfo.InvariantCulture)),
            CanonicalPart(selectionChanges.OutputDirectory),
            CanonicalPart(selectionChanges.FilenamePrefix ?? "recording"));
        var selectionHash = ComputeHash(selectionCanonical);
        return new FixedRegionProfileCreateApiRequest(name, null, selectionRef, selectionChanges,
            selectionChanges.DurationSeconds, selectionChanges.CountdownSeconds ?? 3,
            selectionChanges.OutputDirectory, selectionChanges.FilenamePrefix ?? "recording", key, selectionHash);
    }

    private static string ComputeHash(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("POST\n/api/v1/profiles\n" + canonical))).ToLowerInvariant();

    private static FixedRegionProfileChanges ParseSelectionChanges(JsonElement element)
    {
        var value = RequireObject(element, "changes");
        RequireAllowed(value, "duration_seconds", "countdown_seconds", "output_directory", "filename_prefix");
        var duration = ReadInteger(Required(value, "duration_seconds"), "duration_seconds");
        if (duration is < 1 or > 600) throw Invalid("duration_seconds must be between 1 and 600.");
        int? countdown = null;
        if (value.TryGetProperty("countdown_seconds", out var countdownElement))
        {
            countdown = ReadInteger(countdownElement, "countdown_seconds");
            if (countdown is < 0 or > 10) throw Invalid("countdown_seconds must be between 0 and 10.");
        }
        var parsed = ParseChanges(value, allowEmpty: true);
        return parsed with { DurationSeconds = duration, CountdownSeconds = countdown };
    }

    public static FixedRegionProfilePatchApiRequest ParsePatch(string body)
    {
        using var document = ParseDocument(body);
        var root = RequireObject(document.RootElement, "patch");
        RequireAllowed(root, "name", "duration_seconds", "countdown_seconds", "output_directory", "filename_prefix");
        if (!root.EnumerateObject().Any()) throw Invalid("PATCH body must change at least one supported field.");
        string? name = null;
        if (root.TryGetProperty("name", out var nameElement)) name = ReadName(nameElement);
        return new FixedRegionProfilePatchApiRequest(name, ParseChanges(root, allowEmpty: true, allowName: true));
    }

    private static JsonDocument ParseDocument(string body)
    {
        try
        {
            return JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
        }
        catch (JsonException exception)
        {
            throw new ApiException(400, "INVALID_ARGUMENT", "The profile request body is not valid JSON.",
                new { reason_code = "invalid_json", exception_type = exception.GetType().Name });
        }
    }

    private static FixedRegionProfileChanges ParseChanges(JsonElement element, bool allowEmpty, bool allowName = false)
    {
        var value = RequireObject(element, "changes");
        if (allowName)
            RequireAllowed(value, "name", "duration_seconds", "countdown_seconds", "output_directory", "filename_prefix");
        else
            RequireAllowed(value, "duration_seconds", "countdown_seconds", "output_directory", "filename_prefix");
        if (!allowEmpty && !value.EnumerateObject().Any(property => property.Name != "name"))
            throw Invalid("At least one supported change is required.");
        int? duration = null;
        int? countdown = null;
        string? directory = null;
        string? prefix = null;
        if (value.TryGetProperty("duration_seconds", out var durationElement))
        {
            duration = ReadInteger(durationElement, "duration_seconds");
            if (duration is < 1 or > 600) throw Invalid("duration_seconds must be between 1 and 600.");
        }
        if (value.TryGetProperty("countdown_seconds", out var countdownElement))
        {
            countdown = ReadInteger(countdownElement, "countdown_seconds");
            if (countdown is < 0 or > 60) throw Invalid("countdown_seconds is outside the supported product range.");
        }
        if (value.TryGetProperty("output_directory", out var directoryElement))
        {
            var suppliedDirectory = ReadRequiredString(directoryElement, "output_directory");
            if (suppliedDirectory.Any(char.IsControl) || ContainsTraversal(suppliedDirectory))
                throw Invalid("output_directory must be a valid path without traversal segments.");
            try { directory = AuthorizedFixedRegionScope.NormalizeOutputDirectoryForAuthorization(suppliedDirectory); }
            catch (Phase3DomainException) { throw Invalid("output_directory must be an absolute supported path."); }
        }
        if (value.TryGetProperty("filename_prefix", out var prefixElement))
            prefix = ReadRequiredString(prefixElement, "filename_prefix");
        return new FixedRegionProfileChanges(duration, countdown, directory, prefix);
    }

    private static bool ContainsTraversal(string path) => path.Replace('\\', '/').Split('/').Any(segment => segment == "..");

    private static ProfileRef ParseProfileRef(JsonElement element)
    {
        var reference = RequireObject(element, "source_profile_ref");
        RequireAllowed(reference, "id", "version", "digest");
        var id = ReadRequiredString(Required(reference, "id"), "source_profile_ref.id");
        var version = ReadLong(Required(reference, "version"), "source_profile_ref.version");
        var digest = ReadRequiredString(Required(reference, "digest"), "source_profile_ref.digest");
        try { return new ProfileRef(id, version, digest); }
        catch (Exception exception) when (exception is ArgumentException or Phase3DomainException)
        {
            throw Invalid("source_profile_ref is not a valid exact immutable reference.");
        }
    }

    private static string Canonicalize(string name, ProfileRef source, FixedRegionProfileChanges changes) => string.Concat(
        CanonicalPart(name), CanonicalPart(source.ProfileId), CanonicalPart(source.ProfileVersion.ToString(CultureInfo.InvariantCulture)),
        CanonicalPart(source.ProfileDigest), CanonicalPart(changes.DurationSeconds?.ToString(CultureInfo.InvariantCulture)),
        CanonicalPart(changes.CountdownSeconds?.ToString(CultureInfo.InvariantCulture)), CanonicalPart(changes.OutputDirectory),
        CanonicalPart(changes.FilenamePrefix));

    private static string CanonicalPart(string? value) => value is null
        ? "-1:"
        : value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;

    private static string ReadName(JsonElement element)
    {
        var name = ReadRequiredString(element, "name").Trim();
        if (name.EnumerateRunes().Count() is < 1 or > 80 || name.Any(char.IsControl))
            throw Invalid("name must be a non-empty readable value of at most 80 characters.");
        return name;
    }

    private static string ReadRequiredString(JsonElement element, string field)
    {
        if (element.ValueKind != JsonValueKind.String) throw Invalid($"{field} must be a string.");
        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value)) throw Invalid($"{field} must not be empty.");
        if (value.Any(char.IsControl)) throw Invalid($"{field} contains a control character.");
        return value;
    }

    private static int ReadInteger(JsonElement element, string field)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
            throw Invalid($"{field} must be an in-range integer.");
        return value;
    }

    private static long ReadLong(JsonElement element, string field)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value) || value <= 0)
            throw Invalid($"{field} must be a positive in-range integer.");
        return value;
    }

    private static JsonElement RequireObject(JsonElement element, string field)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid($"{field} must be an object.");
        var seen = new HashSet<string>(PropertyComparer);
        foreach (var property in element.EnumerateObject())
            if (!seen.Add(property.Name)) throw Invalid($"{field} contains a duplicate JSON property.");
        return element;
    }

    private static void RequireAllowed(JsonElement element, params string[] allowed)
    {
        var set = new HashSet<string>(allowed, PropertyComparer);
        foreach (var property in element.EnumerateObject())
            if (!set.Contains(property.Name)) throw Invalid($"Unsupported profile field '{property.Name}'.");
    }

    private static JsonElement Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value : throw Invalid($"{name} is required.");

    private static ApiException Invalid(string message) => new(400, "INVALID_ARGUMENT", message,
        new { reason_code = "profile_request_invalid" });
}
