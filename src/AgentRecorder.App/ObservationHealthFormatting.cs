using System.Globalization;
using AgentRecorder.Api;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.App;

internal sealed record ObservationHealthRow(string Id, string Name, HealthState State, string Details);
internal static class ObservationHealthFormatting
{
    internal static string Text(IUiTextProvider text,string key)=>text.Get("Health_"+key);
    internal static string State(IUiTextProvider text,HealthState state)=>Text(text,state.ToString());
    private static string Time(DateTimeOffset? time)=>time?.ToString("O",CultureInfo.InvariantCulture)??"—";
    private static string? SafePath(string? path)=>ObservationHealthFacts.SafePath(path);
    // Summary is constructed from these same visible rows. Never serialize a host,
    // readiness file, exception, capabilities response or desktop identity record.
    internal static IReadOnlyList<ObservationHealthRow> Rows(ObservationHealthSnapshot snapshot,IUiTextProvider text)
    {
        var rows=new List<ObservationHealthRow>(); var owner=snapshot.Owner; var api=owner.Api; var ready=owner.Readiness;
        string T(string key)=>Text(text,key);
        void Add(string id,string name,HealthState state,string details)=>rows.Add(new(id,name is "FFmpeg" or "ffprobe"?name:T(name),state,details));
        var safeVersion=ObservationHealthFacts.Version(api?.Version);
        var version=safeVersion=="unknown"?T("Unknown"):safeVersion;
        var safeMode=ObservationHealthFacts.Mode(api?.HostMode);
        var mode=safeMode=="unknown"?T("Unknown"):safeMode;
        Add("build","Build",api is null?HealthState.Unknown:HealthState.Available,
            $"{version} / {mode} / PID: {api?.Pid.ToString(CultureInfo.InvariantCulture)??"—"}\r\n{T("Time")}: {Time(owner.SampledAt)}");
        var directory=SafePath(owner.DataDirectory);
        var readyPath=directory is null?null:Path.Combine(directory,"runtime","ready.json");
        var apiState=ObservationHealthFacts.ApiState(owner);
        Add("api","Api",apiState,$"{T("Runtime")}\r\nPort: {api?.ListeningPort?.ToString(CultureInfo.InvariantCulture)??"—"}\r\n{T("ReadyPath")}: {readyPath??"—"}\r\n{T("Time")}: {Time(owner.SampledAt)}");
        Add("data","Data",directory is null?HealthState.Unknown:HealthState.Available,$"{directory??"—"}\r\n{T("Frozen")}");
        void Tool(string id,string name,HealthTool tool)
        {
            var reason=ObservationHealthFacts.ToolReason(tool.Reason);
            var source=ObservationHealthFacts.Source(tool.Source);
            Add(id,name,tool.State,$"{T("Path")}: {SafePath(tool.Path)??"—"}\r\n{T("Source")}: {source}\r\n{T("Reason")}: {reason} — {T(reason)}\r\n{T("PathOnly")}\r\n{T("Time")}: {Time(tool.SampledAt)}");
        }
        Tool("ffmpeg","FFmpeg",snapshot.Ffmpeg); Tool("ffprobe","ffprobe",snapshot.Ffprobe);
        Add("startup_desktop","StartupDesktop",ready?.StartupInteractiveDesktopReady switch { true=>HealthState.Available,false=>HealthState.Unavailable,_=>HealthState.Unknown },
            $"{T("Static")}\r\n{T("Receipt")}: {Time(ready?.StartupEvidenceReceivedAt)}");
        var desktopReason=ObservationHealthFacts.DesktopReason(snapshot.Desktop.Reason);
        Add("desktop","Desktop",snapshot.Desktop.State,$"{T("Dynamic")}\r\n{T("Reason")}: {desktopReason} — {T(desktopReason)}\r\n{T("Time")}: {Time(snapshot.Desktop.SampledAt)}");
        void Hotkey(string id,string name,HealthHotkey hotkey,string expectedGesture)
        {
            var state=ObservationHealthFacts.HotkeyState(hotkey);
            var policy=ObservationHealthFacts.HotkeyPolicy(hotkey);
            var gesture=hotkey.Gesture==expectedGesture?hotkey.Gesture:T("Unknown");
            Add(id,name,state,$"{gesture}\r\n{T("Policy")}: {policy}\r\n{T(hotkey.Registered?"Registered":"Unregistered")}"+
                (hotkey.Expected && !hotkey.Registered?$"\r\n{T("Reason")}: registration_failed — {T("registration_failed")} (Win32: {hotkey.ErrorCode})":"")+
                $"\r\n{T("Time")}: {Time(owner.SampledAt)}");
        }
        Hotkey("stop","Stop",owner.Stop,"Ctrl+Shift+F10"); Hotkey("chapter","Chapter",owner.Chapter,"Ctrl+Shift+F11");
        string Policy(string value)=>value is "default" or "wgc-continuous"?$"{value} — {T("Policy_"+value)}":T("Unknown");
        Add("wgc","Wgc",HealthState.NotChecked,$"{T("Experimental")}\r\n{T("Window")}: {Policy(owner.WindowPolicy)}\r\n{T("Display")}: {Policy(owner.DisplayPolicy)}\r\n{T("Region")}: {Policy(owner.RegionPolicy)}");
        Tool("wgc_helper","WgcHelper",snapshot.WgcHelper); Tool("audio_helper","AudioHelper",snapshot.AudioHelper);
        void Audio(string id,string name,bool? supported)=>Add(id,name,supported switch { true=>HealthState.Available,false=>HealthState.Unsupported,_=>HealthState.Unknown },supported==true?T("SupportedOnly"):T("NotChecked"));
        Audio("microphone","Microphone",api?.MicrophoneBackendSupported); Audio("system_audio","SystemAudio",api?.SystemAudioBackendSupported);
        Add("update","Update",HealthState.NotChecked,T("NotChecked"));
        return rows;
    }
    internal static string Summary(ObservationHealthSnapshot snapshot,IUiTextProvider text,IReadOnlyList<ObservationHealthRow> rows)=>
        Text(text,snapshot.ProbeState.ToString())+"\r\n\r\n"+string.Join("\r\n\r\n",rows.Select(row=>$"{row.Name}: {State(text,row.State)}\r\n{row.Details}"));
}
