namespace AgentRecorder.Core.Automation;

/// <summary>
/// Conservative limits for the pre-profile recurring setup intent.  These
/// limits are deliberately shared by request parsing and trusted persistence
/// validation so a later adapter cannot widen the uncommitted contract.
/// </summary>
public static class RecurringPlanSetupLimits
{
    public const int MinimumDurationSeconds = 1;
    public const int MaximumDurationSeconds = 600;
    public const int MaximumDateSpanDays = 366;
    public const int MaximumOccurrences = 366;
    public const int MaximumFilenamePrefixLength = 64;
    public const int MaximumTimeZoneIdLength = 128;
    public const int MaximumOutputDirectoryLength = 4096;
    public const int MaximumTotalDurationSeconds = MaximumOccurrences * MaximumDurationSeconds;
    public const int MaximumAuthorizationRuns = MaximumOccurrences;
    public const int MaximumGraceSeconds = 300;

    public static bool ContainsTraversalPathSegment(string value) =>
        value.Split(new[] { '\\', '/' }, StringSplitOptions.None)
            .Any(segment => segment is "." or "..");
}
