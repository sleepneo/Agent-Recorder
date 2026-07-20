namespace AgentRecorder.Capture;

/// <summary>
/// Describes a microphone input device that FFmpeg dshow can open.
/// </summary>
/// <param name="Id">Stable identifier used in API requests (maps to dshow device name).</param>
/// <param name="Name">Human-readable display name. May contain Unicode or special characters.</param>
/// <param name="IsDefault"><c>true</c> only when the provider can reliably identify this as the default device; <c>null</c> when unknown.</param>
/// <param name="State">Device state, e.g. "active" or "inactive"; <c>null</c> when unknown.</param>
/// <param name="IsMuted">Read-only CoreAudio mute state. Null when the state cannot be read.</param>
/// <param name="VolumePercent">Read-only CoreAudio master volume scalar in 0..100. Null when unavailable.</param>
public sealed record MicrophoneDeviceInfo(string Id, string Name, bool? IsDefault = null, string? State = null, bool? IsMuted = null, int? VolumePercent = null);
