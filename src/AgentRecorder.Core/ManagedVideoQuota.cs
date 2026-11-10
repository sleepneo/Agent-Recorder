using System.Globalization;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Core;

internal sealed record ManagedVideoQuotaSetting(long? EffectiveBytes, string Source, string? InvalidReason = null)
{
    internal const string EnvironmentVariable = "AGENT_RECORDER_MANAGED_VIDEO_QUOTA_MIB";
    internal const int MaximumMiB = 1_048_576;
    internal const string Scope = "locally_approved_storage_protected_managed_main_mp4";
    internal static readonly ManagedVideoQuotaSetting Disabled = new(0, "default");
    internal bool Enabled => EffectiveBytes > 0;

    internal static ManagedVideoQuotaSetting Parse(string? value)
    {
        if (value is null) return Disabled;
        if (value.Length == 0 || value.Any(c => c is < '0' or > '9'))
            return new(null, "environment", "invalid_integer");
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var mib) || mib > MaximumMiB)
            return new(null, "environment", "out_of_range");
        return new(mib * 1024L * 1024, "environment");
    }
}

internal sealed record ManagedVideoQuotaUsage(long? Bytes, string? UnavailableReason = null);
internal sealed record ManagedVideoQuotaShortfall(long ConfiguredBytes, long RecordedBytes, long EstimatedBytes, decimal ShortfallBytes);

internal sealed record ManagedVideoQuotaRejection(int Status, string Code, string Reason, string Message,
    ManagedVideoQuotaShortfall? Shortfall = null)
{
    internal object Details(string stage) => Shortfall is null
        ? new { reason_code = Reason, stage }
        : new
        {
            reason_code = Reason, stage,
            configured_bytes = Shortfall.ConfiguredBytes, recorded_bytes = Shortfall.RecordedBytes,
            estimated_bytes = Shortfall.EstimatedBytes, shortfall_bytes = Shortfall.ShortfallBytes
        };
}

internal sealed class ManagedVideoQuota(ManagedVideoQuotaSetting setting, string dataDirectory,
    Func<Task<ManagedVideoQuotaUsage>> readUsage)
{
    private readonly string _videosRoot = Path.Combine(Path.GetFullPath(dataDirectory), "Videos");
    private Admission? _owner;
    internal ManagedVideoQuotaSetting Setting => setting;

    internal sealed class Admission(Recording recording)
    {
        internal Recording Recording { get; } = recording;
        internal string Path { get; } = recording.OutputPath;
        internal int HistoryCommitted;
    }

    internal bool Applies(Recording rec) =>
        setting.EffectiveBytes != 0 && rec.IsOrdinaryLocalRequest && rec.Mode == "video" &&
        !rec.IsStandingLeaseExecution && !rec.IsRecurringLeaseExecution && !rec.IsRequiredOnceExecution &&
        !rec.IsFutureWindowOneShotExecution && RecordingStoragePolicy.RequiresSafety(rec) &&
        rec.ApprovedCapturePlan?.PlannedBackend is "wgc-continuous" or "wgc-window-av-split" or
            "ffmpeg" or "ffmpeg-region" or "ffmpeg-av-split" or "ffmpeg-region-av-split" &&
        OriginalFileHoldService.ValidScopePath(_videosRoot, rec.OutputPath);

    internal ManagedVideoQuotaRejection? CheckAvailability(Func<Recording, bool> noOutputRetired, bool commitObserverAvailable)
    {
        if (setting.InvalidReason is { } invalid)
            return new(503, "MANAGED_VIDEO_QUOTA_CONFIGURATION_INVALID", invalid,
                "Managed video quota configuration is invalid. Set AGENT_RECORDER_MANAGED_VIDEO_QUOTA_MIB and restart.");
        if (!commitObserverAvailable) return Unavailable("history_commit_observer_unavailable");

        var owner = Volatile.Read(ref _owner);
        if (owner is null) return null;
        if (owner.Recording.IsFinalized && Volatile.Read(ref owner.HistoryCommitted) != 0 &&
            owner.Recording.State != RecState.completed && noOutputRetired(owner.Recording))
        {
            Release(owner);
            return Volatile.Read(ref _owner) is null ? null : Busy();
        }
        if (!ReferenceEquals(Volatile.Read(ref _owner), owner))
            return Volatile.Read(ref _owner) is null ? null : Busy();
        return owner.Recording.IsFinalized ? Unavailable("previous_run_unsettled") : Busy();
    }

    internal bool TryOwn(Admission admission) => Interlocked.CompareExchange(ref _owner, admission, null) is null;
    internal void Release(Admission admission) => Interlocked.CompareExchange(ref _owner, null, admission);

    internal void Acknowledge(Admission admission, OrdinaryRecordingHistorySnapshot snapshot)
    {
        if (snapshot.RecordingId != admission.Recording.Id ||
            !string.Equals(snapshot.OutputPath, admission.Path, StringComparison.OrdinalIgnoreCase))
            return;

        if (snapshot.Status == "completed")
        {
            if (snapshot.PublicationReceipt?.Anchor is { Valid: true } anchor &&
                snapshot.CompletedAtUtc is not null && snapshot.OutputSizeBytes == anchor.Length &&
                string.Equals(anchor.Path, admission.Path, StringComparison.OrdinalIgnoreCase))
                Release(admission);
        }
        else if (snapshot.Status is "failed" or "cancelled" or "rejected" or "expired")
            Volatile.Write(ref admission.HistoryCommitted, 1);
    }

    internal ManagedVideoQuotaRejection? CheckUsage(Recording rec)
    {
        ManagedVideoQuotaUsage usage;
        try { usage = readUsage().GetAwaiter().GetResult(); }
        catch { return Unavailable("failed"); }
        if (usage.UnavailableReason is not null || usage.Bytes is not (>= 0))
            return Unavailable(usage.UnavailableReason ?? "recorded_usage_incomplete");
        var recorded = usage.Bytes.Value;
        var estimated = WindowStorageSafety.VideoEstimate(rec.Config.DurationSeconds ?? 0);
        var limit = setting.EffectiveBytes!.Value;
        var shortfall = (decimal)recorded + estimated - limit;
        return shortfall <= 0 ? null : new(409, "MANAGED_VIDEO_QUOTA_EXCEEDED", "estimated_usage_exceeds_quota",
            "The estimated recording exceeds the managed video quota.", new(limit, recorded, estimated, shortfall));
    }

    internal static ManagedVideoQuotaRejection Busy() =>
        new(409, "MANAGED_VIDEO_QUOTA_BUSY", "protected_run_active", "Another protected recording owns the managed video quota slot.");

    private static ManagedVideoQuotaRejection Unavailable(string reason) =>
        new(503, "MANAGED_VIDEO_QUOTA_USAGE_UNAVAILABLE", reason, "Managed video quota usage could not be confirmed.");
}
