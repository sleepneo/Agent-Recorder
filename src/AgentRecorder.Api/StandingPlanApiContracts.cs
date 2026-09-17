using System;
using System.Linq;

namespace AgentRecorder.Api;

/// <summary>
/// The API-facing seam for the first one-shot standing-lease flow.  The API
/// owns syntax and strict request validation; the tray host owns identity,
/// persistence, local selection, and approval.
/// </summary>
public interface IStandingPlanSetupGateway
{
    bool IsInteractiveDesktopAvailable { get; }
    bool IsUnattendedEnabled { get; }

    /// <summary>
    /// True only when the full Tray production execution host is running.
    /// Test/headless gateways retain the safe default false.
    /// </summary>
    bool IsExecutionSupported => false;

    StandingPlanSetupCreateResult CreateOrGet(StandingPlanApiRequest request);

    StandingPlanSetupState? Get(string setupIntentId);
}

public sealed record StandingPlanApiRequest(
    string IdempotencyKey,
    DateTimeOffset ScheduledStartUtc,
    DateTimeOffset LatestStartUtc,
    DateTimeOffset PlannedEndUtc,
    DateTimeOffset LeaseExpiresUtc,
    TimeSpan Duration,
    string OutputDirectory,
    string FrozenFileName);

public enum StandingPlanSetupCreateStatus
{
    Created,
    Existing,
    Conflict,
    Rejected,
    Expired,
}

public sealed record StandingPlanSetupCreateResult(
    StandingPlanSetupCreateStatus Status,
    string? SetupIntentId,
    string StatusCode,
    long StatusVersion,
    string? PlanId,
    string? OccurrenceId,
    string? LeaseId,
    string? ReasonCode,
    string? StatusVersionCursor = null);

public sealed record StandingPlanSetupState(
    string SetupIntentId,
    string StatusCode,
    long StatusVersion,
    string? PlanId,
    string? OccurrenceId,
    string? LeaseId,
    bool RequiresLocalAction,
    string? NextAction,
    string? ReasonCode,
    string? RunId = null,
    string? RecordingStatusUrl = null,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null,
    string? StatusVersionCursor = null);

/// <summary>
/// A lexicographically comparable durable snapshot cursor.  The values are
/// aggregate versions from one SQLite read, in dependency order.  Because
/// versions never decrease, changing any related aggregate strictly advances
/// this cursor without packing unrelated values into an overflow-prone long.
/// </summary>
internal static class StandingPlanStatusVersionCursor
{
    private const string Prefix = "v1";

    internal static string Create(
        long setupVersion,
        long? planVersion,
        long? occurrenceVersion,
        long? leaseVersion,
        long? useVersion,
        long? runVersion)
    {
        var values = new[]
        {
            setupVersion,
            planVersion ?? 0,
            occurrenceVersion ?? 0,
            leaseVersion ?? 0,
            useVersion ?? 0,
            runVersion ?? 0,
        };
        if (values.Any(value => value < 0 || value == long.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(setupVersion));
        return Prefix + ":" + string.Join(":", values);
    }

    internal static long CreateNumeric(
        long setupVersion,
        long? planVersion,
        long? occurrenceVersion,
        long? leaseVersion,
        long? useVersion,
        long? runVersion)
    {
        var values = new[]
        {
            setupVersion,
            planVersion ?? 0,
            occurrenceVersion ?? 0,
            leaseVersion ?? 0,
            useVersion ?? 0,
            runVersion ?? 0,
        };
        if (values.Any(value => value < 0 || value == long.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(setupVersion));

        long total = 0;
        try
        {
            foreach (var value in values)
                total = checked(total + value);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentOutOfRangeException(nameof(setupVersion), exception);
        }

        return total;
    }

    internal static bool TryParse(string? value, out long[] versions)
    {
        versions = Array.Empty<long>();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split(':');
        if (parts.Length != 7 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
            return false;

        var parsed = new long[6];
        for (var index = 0; index < parsed.Length; index++)
        {
            if (!long.TryParse(parts[index + 1], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed[index]) ||
                parsed[index] < 0)
                return false;
        }

        versions = parsed;
        return true;
    }

    internal static int Compare(string current, string since)
    {
        if (!TryParse(current, out var currentValues) || !TryParse(since, out var sinceValues))
            throw new ArgumentException("Invalid standing plan status cursor.");

        for (var index = 0; index < currentValues.Length; index++)
        {
            var comparison = currentValues[index].CompareTo(sinceValues[index]);
            if (comparison != 0)
                return comparison;
        }

        return 0;
    }
}
