using System.Text.Json;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Api;

// Validate the original token stream, before JsonNode can fold duplicate keys.
internal static class ProfileRegionQuickRequest
{
    internal static ProfileRef? Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        bool region = root.EnumerateObject().Where(p=>p.Name=="target" && p.Value.ValueKind==JsonValueKind.Object)
            .Any(p=>p.Value.EnumerateObject().Any(t=>t.Name=="type" && t.Value.ValueKind==JsonValueKind.String && t.Value.GetString()=="profile_region"));
        if (!region)
        {
            if(root.EnumerateObject().Any(p=>p.Name=="target" && p.Value.ValueKind==JsonValueKind.Object && p.Value.TryGetProperty("profile_ref",out _)))throw Invalid("target.profile_ref");
            return null;
        }
        Check(root, "body", new[]{"target","duration_seconds","countdown_seconds","audio","video","output","mode"});
        var target = root.GetProperty("target");
        Check(target,"target",new[]{"type","profile_ref"});
        if (!target.TryGetProperty("profile_ref",out var reference)) throw Invalid("target.profile_ref");
        var result=FixedRegionProfileRecordingRequest.Parse("{\"profile_ref\":"+reference.GetRawText()+"}");
        if(!root.TryGetProperty("duration_seconds",out var duration) || !Integer(duration,1,600))throw Invalid("duration_seconds");
        foreach(var p in root.EnumerateObject())
        {
            switch(p.Name)
            {
                case "countdown_seconds":
                    if(p.Value.ValueKind!=JsonValueKind.Number || !p.Value.TryGetInt32(out var countdown) || countdown is <0 or >10)throw Invalid(p.Name);
                    break;
                case "mode": if(p.Value.ValueKind!=JsonValueKind.String || p.Value.GetString()!="video")throw Invalid(p.Name);break;
                case "audio":
                    Check(p.Value,"audio",new[]{"microphone","system_audio"});
                    foreach(var a in p.Value.EnumerateObject())
                    {
                        Check(a.Value,"audio."+a.Name,new[]{"enabled","device_id"});
                        foreach(var f in a.Value.EnumerateObject())
                            if(f.Name=="enabled" ? f.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) :
                                f.Value.ValueKind!=JsonValueKind.String || string.IsNullOrWhiteSpace(f.Value.GetString()))throw Invalid("audio."+a.Name+"."+f.Name);
                        if(a.Value.TryGetProperty("device_id",out _) && (!a.Value.TryGetProperty("enabled",out var enabled) || enabled.ValueKind!=JsonValueKind.True))throw Invalid("audio."+a.Name);
                    }
                    if(p.Value.TryGetProperty("microphone",out var mic) && mic.TryGetProperty("enabled",out var me) && me.ValueKind==JsonValueKind.True &&
                        p.Value.TryGetProperty("system_audio",out var system) && system.TryGetProperty("enabled",out var se) && se.ValueKind==JsonValueKind.True)
                        throw new ApiException(400,"UNSUPPORTED_FEATURE","Microphone and system audio cannot be enabled together in this controlled flow.",new{suggested_action="choose_one_audio_source"});
                    break;
                case "output":
                    Check(p.Value,"output",new[]{"directory","filename","filename_template","conflict_policy"});
                    if(p.Value.TryGetProperty("filename",out _) && p.Value.TryGetProperty("filename_template",out _))throw Invalid("output");
                    foreach(var f in p.Value.EnumerateObject())
                        if(f.Value.ValueKind!=JsonValueKind.String || string.IsNullOrWhiteSpace(f.Value.GetString()) ||
                            (f.Name=="conflict_policy" && f.Value.GetString() is not ("rename" or "fail" or "overwrite")))throw Invalid("output."+f.Name);
                    break;
                case "video":
                    Check(p.Value,"video",new[]{"fps","quality"});
                    foreach(var f in p.Value.EnumerateObject())
                        if(f.Name=="fps" ? !Integer(f.Value,1,60) || !new[]{15,24,30,60}.Contains(f.Value.GetInt32()) :
                            f.Value.ValueKind!=JsonValueKind.String)throw Invalid("video."+f.Name);
                    break;
            }
        }
        return result;
    }
    private static bool Integer(JsonElement e,int min,int max)=>e.ValueKind==JsonValueKind.Number &&
        e.GetRawText().All(char.IsAsciiDigit) && e.TryGetInt32(out var n) && n>=min && n<=max;
    private static void Check(JsonElement e,string field,string[] allowed)
    {
        if(e.ValueKind!=JsonValueKind.Object)throw Invalid(field);
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var p in e.EnumerateObject())if(!seen.Add(p.Name) || !allowed.Contains(p.Name,StringComparer.Ordinal))throw Invalid(field+"."+p.Name);
    }
    private static ApiException Invalid(string field)=>new(400,"INVALID_PROFILE_RECORDING_REQUEST",
        "Invalid profile_region quick request.",new{field,reason_code="invalid_or_unsupported_field"});
}
