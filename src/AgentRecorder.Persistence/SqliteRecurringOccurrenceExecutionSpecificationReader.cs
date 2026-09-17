using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal sealed record RecurringOccurrenceExecutionSpecificationSummary(
    string OccurrenceIdentity,
    string OccurrenceId,
    string LeaseId,
    int SpecificationVersion,
    string SpecificationDigest,
    DateTimeOffset ScheduledStartUtc,
    DateTimeOffset LatestStartUtc,
    DateTimeOffset PlannedEndUtc,
    DateTimeOffset EvaluatedAtUtc,
    string NormalizedOutputDirectory,
    string FrozenOutputFileName,
    string FrozenOutputFilePath,
    AuthorizedCaptureBackend Backend,
    AuthorizedAudioMode AudioMode,
    TimeSpan Duration,
    int CountdownSeconds)
{
    internal static RecurringOccurrenceExecutionSpecificationSummary From(
        RecurringOccurrenceExecutionSpecification specification) =>
        new(
            specification.OccurrenceIdentity,
            specification.OccurrenceId,
            specification.LeaseId,
            RecurringOccurrenceExecutionSpecification.CanonicalVersion,
            specification.SpecificationDigest,
            specification.ScheduledStartUtc,
            specification.LatestStartUtc,
            specification.PlannedEndUtc,
            specification.EvaluatedAtUtc,
            specification.NormalizedOutputDirectory,
            specification.FrozenOutputFileName,
            specification.FrozenOutputFilePath,
            specification.Backend,
            specification.AudioMode,
            specification.Duration,
            specification.CountdownSeconds);
}

/// <summary>
/// Shared strict reader and parent validator for the immutable recurring
/// execution specification.  Both environment recheck and reservation use
/// this exact parser and validation boundary.
/// </summary>
internal sealed class SqliteRecurringOccurrenceExecutionSpecificationReader : SqlitePeriodicOccurrenceDueRepositoryBase
{
    internal const string SpecColumns = "occurrence_identity, occurrence_id, plan_id, lease_id, schedule_revision, schedule_digest, time_zone_rules_digest, profile_id, profile_version, profile_digest, configuration_digest, lease_authorization_digest, local_approval_id, local_approval_digest, scheduled_start_utc, latest_start_utc, planned_end_utc, evaluated_at_utc, stable_display_fingerprint, display_bounds_x, display_bounds_y, display_bounds_width, display_bounds_height, region_x, region_y, region_width, region_height, virtual_region_x, virtual_region_y, virtual_region_width, virtual_region_height, dpi_x, dpi_y, physical_width, physical_height, orientation_code, topology_digest, backend_code, audio_mode_code, duration_ticks, countdown_seconds, normalized_output_directory, frozen_output_file_name, frozen_output_file_path, output_conflict_policy_code, approved_current_user_sid, approved_session_binding, specification_version, specification_digest";

    private SqliteRecurringOccurrenceExecutionSpecificationReader(SqliteOperationalStore store)
        : base(store)
    {
    }

    internal static RecurringOccurrenceExecutionSpecification? ReadByOccurrence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string occurrenceIdentity,
        string occurrenceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SpecColumns} FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $occurrence_identity OR occurrence_id = $occurrence_id;";
        Add(command, "$occurrence_identity", occurrenceIdentity);
        Add(command, "$occurrence_id", occurrenceId);
        using var reader = command.ExecuteReader();
        RecurringOccurrenceExecutionSpecification? result = null;
        while (reader.Read())
        {
            if (result is not null)
                throw SnapshotFailure("Multiple execution specifications point to one recurring occurrence.");
            result = ReadSpecification(reader);
        }

        return result;
    }

    internal static void RegisterNaturalWakeSqlFunctions(SqliteConnection connection)
    {
        connection.CreateFunction<int>(
            "recurring_specification_is_canonical",
            arguments => IsCanonicalSpecification(arguments),
            isDeterministic: true);
        connection.CreateFunction<string?>(
            "recurring_specification_output_file_name",
            arguments => RenderOutputFileName(arguments),
            isDeterministic: true);
        connection.CreateFunction<string?>(
            "recurring_specification_output_file_path",
            arguments => ResolveOutputPath(arguments),
            isDeterministic: true);
    }

    private static int IsCanonicalSpecification(object?[] arguments)
    {
        try
        {
            _ = RehydrateSpecification(arguments);
            return 1;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static RecurringOccurrenceExecutionSpecification RehydrateSpecification(object?[] arguments)
    {
        if (arguments.Length != 49)
            throw new PersistedSnapshotException("The persisted specification column count is invalid.");

        var index = 4;
        return RecurringOccurrenceExecutionSpecification.Rehydrate(
            Text(arguments, 2),
            Text(arguments, 1),
            Text(arguments, 0),
            Text(arguments, 3),
            Int64(arguments, index++),
            Text(arguments, index++),
            Text(arguments, index++),
            Text(arguments, index++),
            Int64(arguments, index++),
            Text(arguments, index++),
            Text(arguments, index++),
            Text(arguments, index++),
            Text(arguments, index++),
            Text(arguments, index++),
            Utc(arguments, index++),
            Utc(arguments, index++),
            Utc(arguments, index++),
            Utc(arguments, index++),
            Text(arguments, index++),
            Rectangle(arguments, ref index),
            Rectangle(arguments, ref index),
            Rectangle(arguments, ref index),
            Int32(arguments, index++),
            Int32(arguments, index++),
            Int32(arguments, index++),
            Int32(arguments, index++),
            ParseOrientation(Text(arguments, index++)),
            Text(arguments, index++),
            ParseBackend(Text(arguments, index++)),
            ParseAudioMode(Text(arguments, index++)),
            TimeSpan.FromTicks(Int64(arguments, index++)),
            Int32(arguments, index++),
            Text(arguments, index++),
            Text(arguments, index++),
            Text(arguments, index++),
            ParseOutputConflictPolicy(Text(arguments, index++)),
            Text(arguments, index++),
            Text(arguments, index++),
            checked((int)Int64(arguments, index++)),
            Text(arguments, index));
    }

    private static string? RenderOutputFileName(object?[] arguments)
    {
        try
        {
            if (arguments.Length != 3)
                return null;
            return RecurringFixedRegionProfileOutput.RenderOutputFileName(
                Text(arguments, 0),
                Text(arguments, 1),
                Utc(arguments, 2));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ResolveOutputPath(object?[] arguments)
    {
        try
        {
            if (arguments.Length != 4)
                return null;
            return RecurringFixedRegionProfileOutput.ResolveOutputPath(
                Text(arguments, 0),
                Text(arguments, 1),
                Text(arguments, 2),
                Utc(arguments, 3));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static AuthorizedPhysicalRectangle Rectangle(object?[] arguments, ref int index) =>
        new(Int32(arguments, index++), Int32(arguments, index++), Int32(arguments, index++), Int32(arguments, index++));

    private static string Text(object?[] arguments, int index) =>
        arguments[index] as string
        ?? throw new PersistedSnapshotException("The persisted specification text value is invalid.");

    private static long Int64(object?[] arguments, int index) =>
        arguments[index] switch
        {
            long value => value,
            int value => value,
            _ => throw new PersistedSnapshotException("The persisted specification integer value is invalid."),
        };

    private static int Int32(object?[] arguments, int index)
    {
        var value = Int64(arguments, index);
        return checked((int)value);
    }

    private static DateTimeOffset Utc(object?[] arguments, int index) =>
        new(new DateTime(checked(Int64(arguments, index)), DateTimeKind.Utc));

    internal static void ValidateAgainstParents(
        RecurringOccurrenceExecutionSpecification specification,
        PlanDefinition plan,
        RecurringScheduleVersionSnapshot schedule,
        RecurringPlanProfileBinding binding,
        RecurringFixedRegionProfileVersion profile,
        RecurringConsentLease lease,
        RecurringLeaseLocalApprovalEvidence approval,
        RecurringOccurrenceCandidate candidate,
        PlanOccurrence occurrence)
    {
        var expectedFileName = profile.RenderOutputFileName(candidate.Identity.Value, candidate.ScheduledStartUtc!.Value);
        var expectedFilePath = profile.ResolveOutputPath(candidate.Identity.Value, candidate.ScheduledStartUtc.Value);
        if (!string.Equals(specification.PlanId, plan.Id, StringComparison.Ordinal) ||
            !string.Equals(specification.OccurrenceId, occurrence.Id, StringComparison.Ordinal) ||
            !string.Equals(specification.OccurrenceIdentity, candidate.Identity.Value, StringComparison.Ordinal) ||
            !string.Equals(specification.LeaseId, lease.LeaseId, StringComparison.Ordinal) ||
            specification.ScheduleRevision != schedule.ScheduleRevision ||
            !string.Equals(specification.ScheduleDigest, schedule.ScheduleDigest, StringComparison.Ordinal) ||
            !string.Equals(specification.TimeZoneRulesDigest, schedule.TimeZoneRulesDigest, StringComparison.Ordinal) ||
            !string.Equals(specification.TimeZoneRulesDigest, lease.ConfigurationRef.TimeZoneRulesDigest, StringComparison.Ordinal) ||
            !string.Equals(specification.ProfileId, profile.ProfileId, StringComparison.Ordinal) ||
            specification.ProfileVersion != profile.ProfileVersion ||
            !string.Equals(specification.ProfileDigest, profile.ProfileDigest, StringComparison.Ordinal) ||
            !string.Equals(specification.ConfigurationDigest, lease.ConfigurationRef.ConfigurationDigest, StringComparison.Ordinal) ||
            !string.Equals(specification.LeaseAuthorizationDigest, lease.AuthorizationDigest, StringComparison.Ordinal) ||
            !string.Equals(specification.LocalApprovalId, approval.ApprovalId, StringComparison.Ordinal) ||
            !string.Equals(specification.LocalApprovalDigest, approval.ApprovalDigest, StringComparison.Ordinal) ||
            specification.ScheduledStartUtc != candidate.ScheduledStartUtc ||
            specification.LatestStartUtc != candidate.LatestStartUtc ||
            specification.PlannedEndUtc != candidate.PlannedEndUtc ||
            !string.Equals(specification.StableDisplayFingerprint, profile.StableDisplayFingerprint, StringComparison.Ordinal) ||
            specification.DisplayBounds != profile.DisplayBounds ||
            specification.RegionWithinDisplay != profile.RegionWithinDisplay ||
            specification.VirtualScreenRegion != profile.VirtualScreenRegion ||
            specification.DpiX != profile.DpiX || specification.DpiY != profile.DpiY ||
            specification.PhysicalWidth != profile.PhysicalWidth || specification.PhysicalHeight != profile.PhysicalHeight ||
            specification.Orientation != profile.Orientation ||
            !string.Equals(specification.TopologyDigest, profile.TopologyDigest, StringComparison.Ordinal) ||
            specification.Backend != AuthorizedCaptureBackend.FfmpegRegion ||
            specification.AudioMode != AuthorizedAudioMode.None ||
            specification.Duration != profile.Duration ||
            specification.CountdownSeconds != profile.CountdownSeconds ||
            !string.Equals(specification.NormalizedOutputDirectory, StandingLeaseOutputPath.NormalizeDirectory(profile.OutputDirectory), StringComparison.Ordinal) ||
            !string.Equals(specification.FrozenOutputFileName, expectedFileName, StringComparison.Ordinal) ||
            !string.Equals(specification.FrozenOutputFilePath, expectedFilePath, StringComparison.Ordinal) ||
            specification.OutputConflictPolicy != AuthorizedOutputConflictPolicy.FailIfExists ||
            !string.Equals(specification.ApprovedCurrentUserSid, approval.CurrentUserSid, StringComparison.Ordinal) ||
            !string.Equals(specification.ApprovedSessionBinding, approval.SessionBinding, StringComparison.Ordinal) ||
            !IsValidTimeRelation(specification, lease, occurrence))
        {
            throw SnapshotFailure("The execution specification is not bound to the exact recurring parents.");
        }
    }

    internal static bool IsValidTimeRelation(
        RecurringOccurrenceExecutionSpecification specification,
        RecurringConsentLease lease,
        PlanOccurrence occurrence)
    {
        if (specification.EvaluatedAtUtc < specification.ScheduledStartUtc ||
            specification.EvaluatedAtUtc > specification.LatestStartUtc ||
            specification.EvaluatedAtUtc < lease.ValidFromUtc ||
            specification.EvaluatedAtUtc >= lease.ValidUntilUtc)
        {
            return false;
        }

        DateTimeOffset captureEndUtc;
        try
        {
            captureEndUtc = specification.EvaluatedAtUtc.Add(specification.Duration);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if (captureEndUtc > specification.PlannedEndUtc || captureEndUtc > lease.ValidUntilUtc)
            return false;

        if (occurrence.Status == PlanOccurrenceStatus.Authorized)
            return specification.EvaluatedAtUtc == occurrence.UpdatedAtUtc;

        if (Phase3TransitionGuards.IsTerminal(occurrence.Status) ||
            occurrence.Status is PlanOccurrenceStatus.RunCreated or PlanOccurrenceStatus.Completed)
        {
            return specification.EvaluatedAtUtc <= occurrence.UpdatedAtUtc;
        }

        return true;
    }

    private static RecurringOccurrenceExecutionSpecification ReadSpecification(SqliteDataReader reader)
    {
        var index = 4;
        return RecurringOccurrenceExecutionSpecification.Rehydrate(
            ReadRequiredText(reader, 2),
            ReadRequiredText(reader, 1),
            ReadRequiredText(reader, 0),
            ReadRequiredText(reader, 3),
            ReadInt64(reader, index++),
            ReadRequiredText(reader, index++),
            ReadRequiredText(reader, index++),
            ReadRequiredText(reader, index++),
            ReadInt64(reader, index++),
            ReadRequiredText(reader, index++),
            ReadRequiredText(reader, index++),
            ReadRequiredText(reader, index++),
            ReadRequiredText(reader, index++),
            ReadRequiredText(reader, index++),
            ReadUtcDateTimeOffset(reader, index++),
            ReadUtcDateTimeOffset(reader, index++),
            ReadUtcDateTimeOffset(reader, index++),
            ReadUtcDateTimeOffset(reader, index++),
            ReadRequiredText(reader, index++),
            ReadRectangle(reader, ref index),
            ReadRectangle(reader, ref index),
            ReadRectangle(reader, ref index),
            ReadInt32(reader, index++),
            ReadInt32(reader, index++),
            ReadInt32(reader, index++),
            ReadInt32(reader, index++),
            ParseOrientation(ReadRequiredText(reader, index++)),
            ReadRequiredText(reader, index++),
            ParseBackend(ReadRequiredText(reader, index++)),
            ParseAudioMode(ReadRequiredText(reader, index++)),
            TimeSpan.FromTicks(ReadInt64(reader, index++)),
            ReadInt32(reader, index++),
            ReadRequiredText(reader, index++),
            ReadRequiredText(reader, index++),
            ReadRequiredText(reader, index++),
            ParseOutputConflictPolicy(ReadRequiredText(reader, index++)),
            ReadRequiredText(reader, index++),
            ReadRequiredText(reader, index++),
            checked((int)ReadInt64(reader, index++)),
            ReadRequiredText(reader, index));
    }

    private static AuthorizedPhysicalRectangle ReadRectangle(SqliteDataReader reader, ref int index) =>
        new(ReadInt32(reader, index++), ReadInt32(reader, index++), ReadInt32(reader, index++), ReadInt32(reader, index++));

    private static AuthorizedDisplayOrientation ParseOrientation(string code) => code switch
    {
        "landscape" => AuthorizedDisplayOrientation.Landscape,
        "portrait" => AuthorizedDisplayOrientation.Portrait,
        "landscape_flipped" => AuthorizedDisplayOrientation.LandscapeFlipped,
        "portrait_flipped" => AuthorizedDisplayOrientation.PortraitFlipped,
        _ => throw new PersistedSnapshotException("The persisted specification orientation code is invalid."),
    };

    private static AuthorizedCaptureBackend ParseBackend(string code) => code switch
    {
        "ffmpeg-region" => AuthorizedCaptureBackend.FfmpegRegion,
        "wgc" => AuthorizedCaptureBackend.Wgc,
        _ => throw new PersistedSnapshotException("The persisted specification backend code is invalid."),
    };

    private static AuthorizedAudioMode ParseAudioMode(string code) => code switch
    {
        "none" => AuthorizedAudioMode.None,
        "microphone" => AuthorizedAudioMode.Microphone,
        "system_audio" => AuthorizedAudioMode.SystemAudio,
        _ => throw new PersistedSnapshotException("The persisted specification audio mode code is invalid."),
    };

    private static AuthorizedOutputConflictPolicy ParseOutputConflictPolicy(string code) => code switch
    {
        "fail_if_exists" => AuthorizedOutputConflictPolicy.FailIfExists,
        "rename" => AuthorizedOutputConflictPolicy.Rename,
        _ => throw new PersistedSnapshotException("The persisted specification conflict policy code is invalid."),
    };

    private static Phase3PersistenceException SnapshotFailure(string message) =>
        new(RecurringPersistenceReasonCodes.PersistedDataInvalid, message);
}
