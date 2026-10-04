using System.Globalization;
using System.Text.Json;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Api;

internal static class FutureWindowOneShotApiRequestParser
{
    internal static FutureWindowOneShotCreateRequest ParseCreate(string body, string? idempotencyHeader)
    {
        var key = StandingPlanApiRequestParser.NormalizeIdempotencyKey(idempotencyHeader);
        using var document = ParseDocument(body);
        var root = document.RootElement;
        RequireObject(root, "request");
        RequireOnly(root, "executable_path", "audio", "maximum_duration_seconds", "validity_seconds", "output_directory");
        var executable = RequiredString(root, "executable_path");
        var directory = RequiredString(root, "output_directory");
        if (!Path.IsPathFullyQualified(executable))
            throw Invalid("executable_path must be an absolute local .exe path.", "executable_path");
        if (!Path.IsPathFullyQualified(directory))
            throw Invalid("output_directory must be an absolute path.", "output_directory");
        var duration = RequiredInt(root, "maximum_duration_seconds");
        var validity = RequiredInt(root, "validity_seconds");
        if (duration is < 1 or > 1800)
            throw Invalid("maximum_duration_seconds must be between 1 and 1800.", "maximum_duration_seconds");
        if (validity is < 1 or > 3600)
            throw Invalid("validity_seconds must be between 1 and 3600.", "validity_seconds");
        if (duration > validity)
            throw Invalid("The full maximum run duration must fit within authorization validity.", "maximum_duration_seconds");

        if (!root.TryGetProperty("audio", out var audio))
            throw Invalid("audio is required and must explicitly select none or system_loopback.", "audio");
        RequireObject(audio, "audio");
        var mode = RequiredString(audio, "mode");
        string? endpoint = null;
        if (mode == "none") RequireOnly(audio, "mode");
        else if (mode == "system_loopback")
        {
            RequireOnly(audio, "mode", "endpoint_id");
            endpoint = RequiredString(audio, "endpoint_id");
        }
        else throw Invalid("audio.mode must be 'none' or 'system_loopback'.", "audio.mode");

        return new FutureWindowOneShotCreateRequest(key, executable, endpoint, duration, validity, directory);
    }

    internal static string ParseStartWindowId(string body)
    {
        using var document = ParseDocument(body);
        var root = document.RootElement;
        RequireObject(root, "request");
        RequireOnly(root, "window_id");
        var value = RequiredString(root, "window_id");
        if (value.Length > 64 || !value.StartsWith("window_", StringComparison.Ordinal) ||
            !long.TryParse(value.AsSpan("window_".Length), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var hwnd) || hwnd == 0 ||
            value != "window_" + hwnd.ToString(CultureInfo.InvariantCulture))
            throw Invalid("window_id must be one exact locally enumerated window identifier.", "window_id");
        return value;
    }

    private static JsonDocument ParseDocument(string body)
    {
        try { return JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException exception) { throw Invalid("Invalid JSON body.", "body", exception.Message); }
    }

    private static void RequireObject(JsonElement element, string field)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid($"{field} must be a JSON object.", field);
    }

    private static void RequireOnly(JsonElement element, params string[] allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw Invalid($"Duplicate field '{property.Name}'.", property.Name);
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) throw Invalid($"Unknown field '{property.Name}'.", property.Name);
        }
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw Invalid($"{name} must be a non-empty string.", name);
        return value.GetString()!.Trim();
    }

    private static int RequiredInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result))
            throw Invalid($"{name} must be an integer.", name);
        return result;
    }

    private static ApiException Invalid(string message, string field, string? detail = null) =>
        new(400, "INVALID_ARGUMENT", message, detail is null ? new { field } : new { field, detail });
}
