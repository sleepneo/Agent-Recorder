namespace AgentRecorder.Infrastructure;

// Read-only transport DTO for the v0.1.25 ready contract. No readiness writer or product DB.
public sealed class ReadySnapshot
{
    public string InstanceId { get; set; } = "";
    public long ProcessStartedAtUtcTicks { get; set; }
    public string ExecutablePath { get; set; } = "";
    public string ProcessUserSid { get; set; } = "";
    public bool Ready { get; set; }
    public int Pid { get; set; }
    public int Port { get; set; }
    public string ApiVersion { get; set; } = "";
    public string Mode { get; set; } = "";
    public string DataDir { get; set; } = "";
    public string ApiKeyFile { get; set; } = "";
    public string ReadyFile { get; set; } = "";
    public bool InteractiveDesktopReady { get; set; }
    public int SessionId { get; set; } = -1;
    public string WindowStation { get; set; } = "";
    public string Desktop { get; set; } = "";
}
