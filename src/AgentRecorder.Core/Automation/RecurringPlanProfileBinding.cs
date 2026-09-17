namespace AgentRecorder.Core.Automation;

/// <summary>
/// Immutable binding from one recurring plan to one exact immutable profile
/// version. The reference is intentionally not a latest-version pointer.
/// </summary>
public sealed class RecurringPlanProfileBinding
{
    public RecurringPlanProfileBinding(
        string planId,
        ProfileRef profileRef,
        DateTimeOffset boundAtUtc)
    {
        PlanId = Phase3Validation.RequiredId(planId, nameof(planId));
        profileRef.Validate();
        ProfileRef = profileRef;
        BoundAtUtc = Phase3Validation.Utc(boundAtUtc, nameof(boundAtUtc));
    }

    internal static RecurringPlanProfileBinding Rehydrate(
        string planId,
        ProfileRef profileRef,
        DateTimeOffset boundAtUtc) =>
        new(planId, profileRef, boundAtUtc);

    public string PlanId { get; }

    public ProfileRef ProfileRef { get; }

    public ProfileRef ProfileReference => ProfileRef;

    public DateTimeOffset BoundAtUtc { get; }
}
