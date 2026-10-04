using System.Text.Json;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Api;

internal static class FixedRegionProfileRecordingRequest
{
    internal static ProfileRef Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Invalid("body", "object_required");
            var rootValues = UniqueProperties(root, "body");
            if (rootValues.Count != 1 || !rootValues.TryGetValue("profile_ref", out var profile))
                throw Invalid("body", "profile_ref_request_must_not_mix_fields");
            if (profile.ValueKind != JsonValueKind.Object)
                throw Invalid("profile_ref", "object_required");
            var values = UniqueProperties(profile, "profile_ref");
            if (values.Count != 3 || !values.TryGetValue("id", out var id) ||
                !values.TryGetValue("version", out var version) || !values.TryGetValue("digest", out var digest))
                throw Invalid("profile_ref", "exact_id_version_digest_required");
            if (id.ValueKind != JsonValueKind.String || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt64(out var versionNumber) || digest.ValueKind != JsonValueKind.String)
                throw Invalid("profile_ref", "invalid_field_type");
            return new ProfileRef(id.GetString()!, versionNumber, digest.GetString()!);
        }
        catch (ApiException) { throw; }
        catch (Exception ex) when (ex is JsonException or Phase3DomainException or ArgumentException or OverflowException)
        {
            throw Invalid("profile_ref", "invalid_value");
        }
    }

    private static Dictionary<string, JsonElement> UniqueProperties(JsonElement element, string field)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!result.TryAdd(property.Name, property.Value))
                throw Invalid(field + "." + property.Name, "duplicate_property");
        }
        return result;
    }

    private static ApiException Invalid(string field, string reason) =>
        new(400, "INVALID_PROFILE_RECORDING_REQUEST",
            "The fixed-region profile recording request is invalid.",
            new { field, reason_code = reason });
}
