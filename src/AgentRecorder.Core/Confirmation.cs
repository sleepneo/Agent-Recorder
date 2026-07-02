using System;
namespace AgentRecorder.Core;
public sealed class Confirmation
{
    public string Id { get; } = "confirm_" + Guid.NewGuid().ToString("N")[..12];
    public string Status { get; set; } = "pending";
    public string RecordingId { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public int TimeoutSeconds { get; set; } = 60;
}
