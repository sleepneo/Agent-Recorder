namespace AgentRecorder.Api;

public enum ApiListenerState { NotStarted, Listening, Stopped }
public sealed record ApiRuntimeObservation(string Version, string HostMode, int Pid,
    ApiListenerState ListenerState, int? ListeningPort, bool MicrophoneBackendSupported,
    bool SystemAudioBackendSupported);
