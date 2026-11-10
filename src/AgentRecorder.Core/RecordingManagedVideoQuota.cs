using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Core;

public sealed partial class RecordingEngine
{
    private ManagedVideoQuota? _managedVideoQuota;
    internal ManagedVideoQuotaSetting ManagedQuotaSetting => _managedVideoQuota?.Setting ?? ManagedVideoQuotaSetting.Disabled;

    internal void ConfigureManagedVideoQuota(ManagedVideoQuota quota)
    {
        if (Interlocked.CompareExchange(ref _managedVideoQuota, quota, null) is not null)
            throw new InvalidOperationException("Managed video quota has already been configured.");
    }

    private void CheckManagedQuotaBeforeConfirmation(Recording rec)
    {
        var quota = _managedVideoQuota;
        if (quota?.Applies(rec) != true) return;
        var rejection = quota.CheckAvailability(QuotaNoOutputRetired, _ordinaryRecordingHistory is IOrdinaryRecordingHistoryCommitObserver)
            ?? quota.CheckUsage(rec);
        if (rejection is not null)
            throw new ApiException(rejection.Status, rejection.Code, rejection.Message, rejection.Details("before_confirmation"));
    }

    private bool TryAdmitManagedQuotaAfterApproval(Recording rec, string traceId, ITrayContext tray)
    {
        var quota = _managedVideoQuota;
        if (quota?.Applies(rec) != true) return true;
        var rejection = quota.CheckAvailability(QuotaNoOutputRetired, _ordinaryRecordingHistory is IOrdinaryRecordingHistoryCommitObserver);
        if (rejection is null)
        {
            var admission = new ManagedVideoQuota.Admission(rec);
            if (!quota.TryOwn(admission))
                rejection = ManagedVideoQuota.Busy();
            else
            {
                // Neither the Engine, output nor per-recording lock spans the SQLite read.
                rejection = quota.CheckUsage(rec);
                lock (rec)
                {
                    if (rejection is null && !rec.IsFinalized && rec.State == RecState.pending_confirmation)
                    {
                        rec.ManagedQuotaAdmission = admission;
                        return true;
                    }
                }
                // The backend cannot be constructed until this method returns true.
                quota.Release(admission);
                if (rejection is null) return false;
            }
        }

        lock (rec)
        {
            if (rec.IsFinalized || rec.State != RecState.pending_confirmation) return false;
            rec.ManagedQuotaFailure = rejection;
            rec.Error = rejection.Code;
            rec.StopReason = rejection.Code;
            rec.CompletedAtUtc = DateTime.UtcNow;
            rec.State = RecState.failed;
            MarkBundleNotApplicable(rec);
            BumpStateVersion();
            rec.PublishFinalized();
        }
        _audit.Log("recording.preflight_failed", new
        {
            recording_id = rec.Id, stage = "before_start", error_code = rejection.Code,
            reason_code = rejection.Reason
        });
        _tracer.RecordingTerminal(traceId, rec.Id, status: "failed", errorCode: rejection.Code);
        tray.ShowError(rejection.Message);
        TrySetIdleOnAllDone(tray);
        return false;
    }

    private bool QuotaNoOutputRetired(Recording rec)
    {
        if (_ordinaryOutputCleanup.ContainsKey(rec.Id) || rec.LastMeta?.PublicationReceipt is not null)
            return false;
        var backend = rec.Backend;
        if (backend is null) return rec.OrdinaryBackendCreation?.NoResourcesRetired.IsCompletedSuccessfully == true;
        if (!BackendRetired(backend)) return false;
        var safety = rec.Config.StorageSafety;
        return safety is null ? !rec.BackendStartAttempted : !safety.FinalPublicationAttempted;
    }

    private static readonly JsonSerializerOptions QuotaStatusJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static object AddManagedQuotaFailure(object status, ManagedVideoQuotaRejection? rejection)
    {
        if (rejection is null) return status;
        var result = JsonSerializer.SerializeToNode(status, QuotaStatusJson)!.AsObject();
        result["managed_video_quota_failure"] = new JsonObject
        {
            ["code"] = rejection.Code,
            ["details"] = JsonSerializer.SerializeToNode(rejection.Details("before_start"), QuotaStatusJson)
        };
        return result;
    }
}
