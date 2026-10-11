using System.Text.RegularExpressions;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Api;

// Shared judgments used by both the localized UI and the machine-readable projection.
internal static class ObservationHealthFacts
{
    internal static string? SafePath(string? path)
    {
        try { return path is not null && !path.Any(char.IsControl) && Path.IsPathFullyQualified(path)?Path.GetFullPath(path):null; }
        catch { return null; }
    }
    internal static string Version(string? value)=>value is not null && Regex.IsMatch(value,@"^[0-9]{1,5}(\.[0-9]{1,5}){1,3}(-[A-Za-z0-9.-]{1,50})?$")?value:"unknown";
    internal static string Mode(string? value)=>value is "tray" or "headless"?value:"unknown";
    internal static string Code(HealthState state)=>state switch
    {
        HealthState.Ready=>"ready",HealthState.Available=>"available",HealthState.Unavailable=>"unavailable",
        HealthState.Unsupported=>"unsupported",HealthState.NotChecked=>"not_checked",HealthState.Idle=>"idle",
        HealthState.Listening=>"listening",HealthState.Stopped=>"stopped",HealthState.NotStarted=>"not_started",_=>"unknown"
    };
    internal static string Policy(string? value)=>value is "default" or "wgc-continuous"?value:"unknown";
    internal static string ToolReason(string? value)=>value is "path_only" or "tool_not_found" or "tool_resolution_failed" or "not_checked"?value:"unknown";
    internal static string Source(string? value)=>value is "env:AGENT_RECORDER_FFMPEG_DIR" or "app_dir" or "project_tools" or "path" or "WgcHelperExePathResolver" or "AudioHelperExePathResolver"?value:"unknown";
    internal static string DesktopReason(string? value)=>value is "not_checked" or "interactive_desktop" or "desktop_locked" or "windows_required" or "input_desktop_unavailable" or "session_unavailable" or "interactive_desktop_required" or "desktop_observation_failed"?value:"desktop_observation_failed";
    internal static string HotkeyPolicy(HealthHotkey key)=>key.Policy is "always" or "while_recording"?key.Policy:"unknown";
    internal static HealthState HotkeyState(HealthHotkey key)=>HotkeyPolicy(key)=="unknown"?HealthState.Unknown:
        !key.Supported?HealthState.Unsupported:key.Registered?HealthState.Available:!key.Expected?HealthState.Idle:HealthState.Unavailable;
    internal static HealthState ApiState(ObservationHealthOwner owner)
    {
        var api=owner.Api;var ready=owner.Readiness;var directory=SafePath(owner.DataDirectory);
        var same=api is not null && ready is not null && api.Pid==ready.Pid && api.HostMode==ready.Mode && !ready.Disposed &&
            directory is not null && string.Equals(Path.Combine(directory,"runtime","ready.json"),ready.PublishedReadyFile,StringComparison.OrdinalIgnoreCase);
        return api?.ListenerState switch { ApiListenerState.NotStarted=>HealthState.NotStarted,ApiListenerState.Stopped=>HealthState.Stopped,
            ApiListenerState.Listening when same=>HealthState.Ready,ApiListenerState.Listening=>HealthState.Listening,_=>HealthState.Unknown };
    }
}
internal sealed record HealthInstanceDto(string Version,string Mode,int? Pid,int? ListeningPort,string ReadyState,string? DataDirectory,DateTimeOffset SampledAt,string EvidenceType);
internal sealed record HealthDesktopEvidenceDto(string State,string ReasonCode,DateTimeOffset? SampledAt,string EvidenceType,string Limitation);
internal sealed record HealthDesktopDto(bool? UiRequired,HealthDesktopEvidenceDto Startup,HealthDesktopEvidenceDto Current);
internal sealed record HealthToolDto(string State,string? Path,string Source,string ReasonCode,DateTimeOffset? SampledAt,string EvidenceType);
internal sealed record HealthToolsDto(HealthToolDto Ffmpeg,HealthToolDto Ffprobe,HealthToolDto WgcHelper,HealthToolDto AudioHelper);
internal sealed record HealthHotkeyDto(bool Supported,bool Registered,string? Gesture,string Policy,string State,string ReasonCode,DateTimeOffset SampledAt,string EvidenceType);
internal sealed record HealthHotkeysDto(HealthHotkeyDto Stop,HealthHotkeyDto Chapter);
internal sealed record HealthVisualBackendDto(string Policy,bool Experimental,string CaptureCheck,string EvidenceType);
internal sealed record HealthAudioBackendDto(bool? Supported,string DeviceCheck,string EvidenceType);
internal sealed record HealthBackendsDto(HealthVisualBackendDto Window,HealthVisualBackendDto Display,HealthVisualBackendDto Region,HealthAudioBackendDto Microphone,HealthAudioBackendDto SystemAudio);
internal sealed record ObservationHealthResponse(string SampleStatus,DateTimeOffset SampledAt,HealthInstanceDto Instance,HealthDesktopDto Desktop,HealthToolsDto Tools,HealthHotkeysDto Hotkeys,HealthBackendsDto Backends)
{
    internal static ObservationHealthResponse From(ObservationHealthSnapshot snapshot)
    {
        var owner=snapshot.Owner;var api=owner.Api;var ready=owner.Readiness;
        var status=snapshot.ProbeState switch {HealthProbeState.Complete=>"complete",HealthProbeState.Busy=>"busy",HealthProbeState.Timeout=>"timeout",HealthProbeState.NotChecked=>"not_checked",_=>"failed"};
        HealthToolDto Tool(HealthTool t)=>new(ObservationHealthFacts.Code(t.State),ObservationHealthFacts.SafePath(t.Path),ObservationHealthFacts.Source(t.Source),ObservationHealthFacts.ToolReason(t.Reason),t.SampledAt,"path_only");
        HealthHotkeyDto Key(HealthHotkey k,string gesture)
        {
            var state=ObservationHealthFacts.HotkeyState(k);var reason=state switch {HealthState.Unsupported=>"unsupported",HealthState.Idle=>"idle",HealthState.Available=>"registered",HealthState.Unavailable=>"registration_failed",_=>"not_checked"};
            return new(k.Supported,k.Registered,k.Gesture==gesture?gesture:null,ObservationHealthFacts.HotkeyPolicy(k),ObservationHealthFacts.Code(state),reason,owner.SampledAt,"owner_snapshot");
        }
        HealthVisualBackendDto Visual(string policy)=>new(ObservationHealthFacts.Policy(policy),policy=="wgc-continuous","not_checked","configuration_only");
        var mode=ObservationHealthFacts.Mode(api?.HostMode);
        return new(status,owner.SampledAt,
            new(ObservationHealthFacts.Version(api?.Version),mode,api?.Pid,api?.ListeningPort,ObservationHealthFacts.Code(ObservationHealthFacts.ApiState(owner)),ObservationHealthFacts.SafePath(owner.DataDirectory),owner.SampledAt,"runtime_snapshot"),
            new(mode=="unknown"?null:mode=="tray",
                new(ready?.StartupInteractiveDesktopReady switch {true=>"available",false=>"unavailable",_=>"unknown"},"startup_receipt",ready?.StartupEvidenceReceivedAt,"static_startup_receipt","does_not_describe_current_desktop"),
                new(ObservationHealthFacts.Code(snapshot.Desktop.State),ObservationHealthFacts.DesktopReason(snapshot.Desktop.Reason),snapshot.Desktop.SampledAt,"dynamic_read_only_probe","does_not_verify_capture")),
            new(Tool(snapshot.Ffmpeg),Tool(snapshot.Ffprobe),Tool(snapshot.WgcHelper),Tool(snapshot.AudioHelper)),
            new(Key(owner.Stop,"Ctrl+Shift+F10"),Key(owner.Chapter,"Ctrl+Shift+F11")),
            new(Visual(owner.WindowPolicy),Visual(owner.DisplayPolicy),Visual(owner.RegionPolicy),new(api?.MicrophoneBackendSupported,"not_checked","backend_support_only"),new(api?.SystemAudioBackendSupported,"not_checked","backend_support_only")));
    }
}
