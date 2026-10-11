using AgentRecorder.Capture;

namespace AgentRecorder.Core;

// Only the locally confirmed ordinary FFmpeg video path joins the existing
// strict-window storage contract. The approved plan determines the write layout.
internal static class RecordingStoragePolicy
{
    internal static bool IsOrdinaryVideoOnly(Recording rec) =>
        rec.IsOrdinaryLocalRequest && !rec.IsStandingLeaseExecution && !rec.IsRecurringLeaseExecution &&
        !rec.IsRequiredOnceExecution && !rec.IsFutureWindowOneShotExecution &&
        rec.Config.SourceKind is "display" or "region" &&
        FfmpegCaptureBackend.UsesVideoOnlyPublication(rec.Config) &&
        rec.ApprovedCapturePlan?.PlannedBackend is "ffmpeg" or "ffmpeg-region";

    internal static bool IsOrdinaryAv(Recording rec) =>
        rec.IsOrdinaryLocalRequest && !rec.IsStandingLeaseExecution && !rec.IsRecurringLeaseExecution &&
        !rec.IsRequiredOnceExecution && !rec.IsFutureWindowOneShotExecution &&
        rec.Config.Mode == "video" && rec.Config.SourceKind is "display" or "region" &&
        rec.Config.AudioSourceKind is AudioCaptureSourceKind.SystemLoopback or AudioCaptureSourceKind.Microphone &&
        rec.ApprovedCapturePlan?.PlannedBackend is "ffmpeg-av-split" or "ffmpeg-region-av-split";

    internal static bool RequiresSafety(Recording rec) => rec.Config.RequireWindowSurface || IsOrdinaryVideoOnly(rec) || IsOrdinaryAv(rec);

    internal static CaptureWritePaths Freeze(Recording rec, string? approvedOutputPath = null) =>
        rec.Config.RequireWindowSurface ? CaptureWritePaths.Freeze(rec.Config, approvedOutputPath)
            : IsOrdinaryAv(rec) ? CaptureWritePaths.FreezeFfmpegAv(rec.Config, approvedOutputPath)
            : CaptureWritePaths.FreezeFfmpegVideoOnly(rec.Config, approvedOutputPath);

    internal static WindowStorageSafety CreateSafety(Recording rec, IStorageCapacityProvider provider,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var safety = new WindowStorageSafety(rec.Config.WritePaths!, rec.Config.DurationSeconds ?? 0, provider)
            { Delay = delay ?? Task.Delay };
        // Admission must include the final copy even before backend.Prepare.
        if (IsOrdinaryVideoOnly(rec)) safety.ReserveFfmpegVideoOnlyPublication();
        else if (IsOrdinaryAv(rec)) safety.EnableFfmpegAvPublication();
        return safety;
    }
}
