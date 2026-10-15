using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Core;

public static partial class ConfigParser
{
    private static ApiException InvalidArtifacts() => new(400,"INVALID_ARGUMENT",
        "artifacts accepts only a boolean bundle for ordinary non-nested video recordings.",new{field="artifacts"});

    // Preserve duplicate names before JsonNode can collapse or throw on them.
    // Validation precedes target/provider/UI/file operations.
    public static bool ValidateArtifactRequestJson(string json,bool ordinary)
    {
        using var doc=JsonDocument.Parse(json);
        var root=doc.RootElement;
        static bool Contains(JsonElement e)=>e.ValueKind==JsonValueKind.Object
            ?e.EnumerateObject().Any(p=>p.Name=="artifacts"||Contains(p.Value))
            :e.ValueKind==JsonValueKind.Array&&e.EnumerateArray().Any(Contains);
        if(!Contains(root))return true;
        if(!ordinary||root.ValueKind!=JsonValueKind.Object)throw InvalidArtifacts();
        var fields=root.EnumerateObject().ToArray();var policies=fields.Where(p=>p.Name=="artifacts").ToArray();
        if(policies.Length!=1||fields.Any(p=>p.Name!="artifacts"&&Contains(p.Value))||
            fields.Any(p=>p.Name is "nested" or "profile_ref" or "schedule" or "plan" or "lease" or "authorization_mode")||
            fields.Any(p=>p.Name=="mode"&&(p.Value.ValueKind!=JsonValueKind.String||p.Value.GetString()!="video")))throw InvalidArtifacts();
        var policy=policies[0].Value;if(policy.ValueKind!=JsonValueKind.Object)throw InvalidArtifacts();
        var options=policy.EnumerateObject().ToArray();
        if(options.Length>1||options.Any(p=>p.Name!="bundle"||p.Value.ValueKind is not(JsonValueKind.True or JsonValueKind.False)))throw InvalidArtifacts();
        return options.Length==0||options[0].Value.GetBoolean();
    }
    public static bool NormalizeArtifactBundle(JsonNode cfg)
    {
        try{return ValidateArtifactRequestJson(cfg.ToJsonString(),true);}
        catch(Exception e) when(e is JsonException or ArgumentException or InvalidOperationException){throw InvalidArtifacts();}
    }
}
