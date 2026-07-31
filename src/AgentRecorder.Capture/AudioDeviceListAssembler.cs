using System;
using System.Collections.Generic;

namespace AgentRecorder.Capture;

/// <summary>
/// Result of merging an enumerated (possibly cached) dshow device list with
/// fresh per-device CoreAudio status.
/// </summary>
public sealed record AudioDeviceListAssembly(
    IReadOnlyList<MicrophoneDeviceInfo> Devices,
    bool RemovedStaleDevices);

/// <summary>
/// Merges enumerated microphone device IDs with fresh CoreAudio status. The
/// dshow enumeration may come from a short TTL cache, so an entry can refer to
/// a device that has since disconnected. A fresh CoreAudio lookup that
/// definitively reports the endpoint as not present is positive evidence the
/// entry is stale; such entries are dropped from the merged list. Inconclusive
/// lookups (null status fields after a transient COM failure) never drop a
/// device.
/// </summary>
public static class AudioDeviceListAssembler
{
    /// <summary>Fresh-state value produced when the endpoint id cannot be resolved at all.</summary>
    public const string NotPresentState = "not_present";

    public static AudioDeviceListAssembly Assemble(
        IReadOnlyList<MicrophoneDeviceInfo> devices,
        Func<string, MicrophoneStatus> freshStatusQuery)
    {
        if (devices == null) throw new ArgumentNullException(nameof(devices));
        if (freshStatusQuery == null) throw new ArgumentNullException(nameof(freshStatusQuery));

        if (devices.Count == 0)
            return new AudioDeviceListAssembly(devices, false);

        var merged = new List<MicrophoneDeviceInfo>(devices.Count);
        bool removedStale = false;

        foreach (var device in devices)
        {
            MicrophoneStatus status;
            try
            {
                status = freshStatusQuery(device.Id) ?? new MicrophoneStatus(null, null, null, null);
            }
            catch
            {
                status = new MicrophoneStatus(null, null, null, null);
            }

            if (string.Equals(status.State, NotPresentState, StringComparison.OrdinalIgnoreCase))
            {
                removedStale = true;
                continue;
            }

            merged.Add(device with
            {
                IsMuted = status.IsMuted,
                VolumePercent = status.VolumePercent,
                IsDefault = status.IsDefault ?? device.IsDefault,
                State = status.State ?? device.State
            });
        }

        return new AudioDeviceListAssembly(merged, removedStale);
    }
}
