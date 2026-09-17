using System.Security.Cryptography;

namespace AgentRecorder.Core.Automation;

/// <summary>
/// Canonical request fingerprint for one persisted recurring advancement.
/// The operation id and arrival time are deliberately excluded so a normal
/// retry with a different observation time remains the same logical request.
/// </summary>
public static class RecurringAdvancementRequestDigest
{
    public const int CanonicalVersion = 1;
    public const string Prefix = "recurring-advancement/v1:";

    public static string Compute(
        string planId,
        long scheduleRevision,
        long expectedCursorVersion,
        DateTimeOffset initialAfterUtc)
    {
        var canonicalPlanId = Phase3Validation.RequiredId(planId, nameof(planId));
        if (scheduleRevision <= 0)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.RevisionInvalid, "The schedule revision must be positive.");
        }

        if (expectedCursorVersion < 0)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.CursorInvalid, "The expected recurring cursor version must not be negative.");
        }

        if (initialAfterUtc.Offset != TimeSpan.Zero)
        {
            throw new Phase3DomainException(RecurringScheduleReasonCodes.QueryCursorNotUtc, "The recurring advancement initial boundary must be UTC.");
        }

        var bytes = RecurringScheduleCanonicalization.Start("recurring-advancement/v1");
        RecurringScheduleCanonicalization.AppendString(bytes, canonicalPlanId);
        RecurringScheduleCanonicalization.AppendInt64(bytes, scheduleRevision);
        RecurringScheduleCanonicalization.AppendInt64(bytes, expectedCursorVersion);
        RecurringScheduleCanonicalization.AppendInt64(bytes, initialAfterUtc.UtcDateTime.Ticks);
        return Prefix + Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
    }
}
