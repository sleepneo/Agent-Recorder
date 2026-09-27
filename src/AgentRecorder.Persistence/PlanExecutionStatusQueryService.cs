using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

public sealed record PlanExecutionStatusRunSnapshot(
    string RunId,
    string Status,
    string? TerminalReasonCode);

public sealed record PlanExecutionStatusOccurrenceSnapshot(
    string OccurrenceId,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    string Status,
    string? TerminalReasonCode,
    string? RunId,
    PlanExecutionStatusRunSnapshot? Run,
    string? OutputPath,
    bool OutputPathRecorded,
    bool? OutputFileExists,
    string? ExecutionStatusCode = null);

public sealed record PlanExecutionStatusSnapshot(
    string PlanId,
    string Kind,
    string PlanStatus,
    bool? ScheduleExhausted,
    long OccurrenceCount,
    PlanExecutionStatusOccurrenceSnapshot? NextOccurrence,
    PlanExecutionStatusOccurrenceSnapshot? LatestOccurrence);

/// <summary>
/// Read-only, identity-bound projection of durable plan execution state.
/// This service never advances scheduling or invokes execution/recovery.
/// </summary>
public sealed class PlanExecutionStatusQueryService : SqliteRepositoryBase
{
    private const int MaximumOccurrencesPerPlan = 512;
    private const string OccurrenceColumns =
        "id, plan_id, status_code, window_start_utc, window_end_utc, run_id, terminal_reason_code, created_at_utc, updated_at_utc, version";

    public PlanExecutionStatusQueryService(SqliteOperationalStore store) : base(store) { }

    public PlanExecutionStatusSnapshot? Get(string? planId, string? currentUserSid, string? sessionBinding)
    {
        if (!Canonical(planId, 128))
            throw new Phase3PersistenceException("invalid_argument", "The plan identifier is invalid.");
        if (!Canonical(currentUserSid, 256) || !Canonical(sessionBinding, 256))
            return null;

        using var connection = OpenBusinessConnection();
        using (var readOnly = connection.CreateCommand())
        {
            readOnly.CommandText = "PRAGMA query_only = ON;";
            readOnly.ExecuteNonQuery();
        }

        using var transaction = BeginReadTransaction(connection);
        try
        {
            // Establish the caller's identity-bound ownership before reading
            // any mutable Plan or approval payload. A foreign corrupt row is
            // therefore indistinguishable from an unknown identifier.
            if (!HasOwnershipForIdentity(connection, transaction, planId!, currentUserSid!, sessionBinding!))
            {
                transaction.Commit();
                return null;
            }

            var plan = ReadPlan(connection, transaction, planId!);
            if (plan is null)
                throw InvalidPersistedData("Owned setup evidence points to a missing plan.");

            string? ownerScheduleKind = null;
            var isRequiredOnceOwner = false;
            if (!IsOwnedByCurrentIdentity(connection, transaction, plan, currentUserSid!, sessionBinding!,
                    out ownerScheduleKind, out isRequiredOnceOwner))
                throw InvalidPersistedData("Plan ownership evidence changed shape during identity validation.");

            var kind = plan.IsOneTime ? "once" : "daily";
            bool? scheduleExhausted = null;
            if (plan.IsOneTime)
            {
                if (ownerScheduleKind is not null)
                    throw InvalidPersistedData("A one-time plan has recurring schedule ownership evidence.");
            }
            else
            {
                var schedule = SqliteRecurringScheduleVersionRepository.ReadLatestSchedule(connection, transaction, plan.Id)
                    ?? throw InvalidPersistedData("The recurring plan has no persisted schedule version.");
                kind = schedule.Schedule.Kind == RecurringScheduleKind.Daily ? "daily" : "weekly";
                if (!string.Equals(kind, ownerScheduleKind, StringComparison.Ordinal))
                    throw InvalidPersistedData("The recurring setup schedule kind disagrees with the persisted schedule.");
                var cursor = SqliteRecurringAdvancementTransaction.ReadValidatedCursorWithinTransaction(
                    connection, transaction, plan.Id, schedule.ScheduleRevision, schedule);
                if (cursor is null)
                {
                    EnsureNoAdvancementHistory(connection, transaction, plan.Id);
                    scheduleExhausted = false;
                }
                else
                {
                    scheduleExhausted = cursor.IsExhausted;
                }
            }

            var occurrences = ReadOccurrences(connection, transaction, plan.Id, plan.IsOneTime,
                isRequiredOnceOwner, out var occurrenceCount);
            var latest = occurrences.Count == 0 ? null : occurrences[^1];
            var next = occurrences.FirstOrDefault(item => !Phase3TransitionGuards.IsTerminal(
                Phase3StateCodes.ParsePlanOccurrence(item.Status)));
            var result = new PlanExecutionStatusSnapshot(
                plan.Id,
                kind,
                plan.StatusCode,
                scheduleExhausted,
                occurrenceCount,
                next,
                latest);
            transaction.Commit();
            return result;
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException("plan_status_unavailable", "The persisted plan status could not be read.", exception);
        }
        catch (Exception exception) when (exception is PersistedSnapshotException or Phase3DomainException or InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException("plan_status_data_invalid", "The persisted plan status is inconsistent or invalid.", exception);
        }
        catch (Exception exception)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException("plan_status_unavailable", "The persisted plan status could not be read.", exception);
        }
    }

    private static PlanDefinition? ReadPlan(SqliteConnection connection, SqliteTransaction transaction, string planId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, is_one_time, status_code, created_at_utc, updated_at_utc, version FROM plans WHERE id = $id LIMIT 2;";
        Add(command, "$id", planId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var plan = ReadPlanDefinitionSnapshot(reader);
        if (reader.Read()) throw InvalidPersistedData("The plan identifier resolved to duplicate rows.");
        return plan;
    }

    private static bool HasOwnershipForIdentity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId,
        string currentUserSid,
        string sessionBinding)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
              EXISTS (
                SELECT 1 FROM setup_intents
                WHERE plan_id = $plan_id AND current_user_sid = $sid AND session_binding = $session
              )
              OR EXISTS (
                SELECT 1 FROM recurring_lease_local_approvals
                WHERE plan_id = $plan_id AND current_user_sid = $sid AND session_binding = $session
              )
              OR EXISTS (
                SELECT 1 FROM required_once_setup_intents
                WHERE plan_id = $plan_id AND current_user_sid = $sid AND session_binding = $session
              );
            """;
        Add(command, "$plan_id", planId);
        Add(command, "$sid", currentUserSid);
        Add(command, "$session", sessionBinding);
        return command.ExecuteScalar() is long result && result == 1;
    }

    private static bool IsOwnedByCurrentIdentity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlanDefinition plan,
        string currentUserSid,
        string sessionBinding,
        out string? scheduleKind,
        out bool isRequiredOnceOwner)
    {
        scheduleKind = null;
        isRequiredOnceOwner = false;
        if (plan.IsOneTime)
        {
            long standingOwnerCount;
            long requiredOwnerCount;
            using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT (SELECT COUNT(*) FROM setup_intents WHERE plan_id = $plan_id), (SELECT COUNT(*) FROM required_once_setup_intents WHERE plan_id = $plan_id);";
                Add(count, "$plan_id", plan.Id);
                using var countReader = count.ExecuteReader();
                if (!countReader.Read()) throw InvalidPersistedData("The one-time plan ownership counts are missing.");
                standingOwnerCount = ReadInt64(countReader, 0);
                requiredOwnerCount = ReadInt64(countReader, 1);
                if (countReader.Read()) throw InvalidPersistedData("The one-time plan ownership counts are duplicated.");
            }
            if (standingOwnerCount + requiredOwnerCount == 0) return false;
            if (standingOwnerCount + requiredOwnerCount != 1)
                throw InvalidPersistedData("The one-time plan has ambiguous setup ownership evidence.");

            if (requiredOwnerCount == 1)
            {
                var occurrenceId = ReadRequiredOnceOccurrenceId(connection, transaction, plan.Id);
                var required = SqliteRequiredOncePlanSetupRepository.ReadAndValidateExecutionProgressPlanWithinTransaction(
                    connection, transaction, plan.Id, occurrenceId)
                    ?? throw InvalidPersistedData("Required setup ownership evidence disappeared during plan validation.");
                if (!string.Equals(required.Request.CurrentUserSid, currentUserSid, StringComparison.Ordinal) ||
                    !string.Equals(required.Request.SessionBinding, sessionBinding, StringComparison.Ordinal))
                    return false;
                isRequiredOnceOwner = true;
                return true;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT intent_kind_code, current_user_sid, session_binding
                FROM setup_intents WHERE plan_id = $plan_id LIMIT 2;
                """;
            Add(command, "$plan_id", plan.Id);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return false;
            var intentKind = ReadRequiredText(reader, 0);
            var sid = ReadRequiredText(reader, 1);
            var session = ReadRequiredText(reader, 2);
            if (reader.Read()) throw InvalidPersistedData("The one-time plan has duplicate setup ownership evidence.");
            if (!string.Equals(intentKind, "standing_once_fixed_region", StringComparison.Ordinal))
                throw InvalidPersistedData("The one-time plan is bound to an unexpected setup kind.");
            return string.Equals(sid, currentUserSid, StringComparison.Ordinal) &&
                string.Equals(session, sessionBinding, StringComparison.Ordinal);
        }

        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM recurring_lease_local_approvals WHERE plan_id = $plan_id;";
            Add(count, "$plan_id", plan.Id);
            var countValue = count.ExecuteScalar();
            if (countValue is not long ownerCount)
                throw InvalidPersistedData("The recurring plan ownership count is malformed.");
            if (ownerCount == 0) return false;
            if (ownerCount != 1)
                throw InvalidPersistedData("The recurring plan has ambiguous local-approval ownership evidence.");
        }

        string leaseId;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT lease_id FROM recurring_lease_local_approvals WHERE plan_id = $plan_id LIMIT 2;";
            Add(command, "$plan_id", plan.Id);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return false;
            leaseId = ReadRequiredText(reader, 0);
            if (reader.Read()) throw InvalidPersistedData("The recurring plan has duplicate local-approval evidence.");
        }

        var approval = SqliteRecurringLeaseLocalApprovalEvidenceReader.ReadWithinTransaction(connection, transaction, leaseId)
            ?? throw InvalidPersistedData("The recurring plan approval evidence is missing.");
        var lease = SqliteRecurringConsentLeaseRepository.ReadWithinTransaction(connection, transaction, leaseId)
            ?? throw InvalidPersistedData("The recurring plan approval references a missing lease.");
        if (!string.Equals(approval.PlanId, plan.Id, StringComparison.Ordinal) ||
            !string.Equals(lease.PlanId, plan.Id, StringComparison.Ordinal) ||
            !string.Equals(approval.ConfigurationDigest, lease.ConfigurationRef.ConfigurationDigest, StringComparison.Ordinal) ||
            !string.Equals(approval.AuthorizationDigest, lease.AuthorizationDigest, StringComparison.Ordinal))
            throw InvalidPersistedData("The recurring plan approval is not bound to its exact lease and plan.");

        if (!string.Equals(approval.CurrentUserSid, currentUserSid, StringComparison.Ordinal) ||
            !string.Equals(approval.SessionBinding, sessionBinding, StringComparison.Ordinal))
            return false;

        scheduleKind = SqliteRecurringScheduleVersionRepository.ReadLatestSchedule(connection, transaction, plan.Id)?.Schedule.Kind switch
        {
            RecurringScheduleKind.Daily => "daily",
            RecurringScheduleKind.Weekly => "weekly",
            _ => null,
        };
        if (scheduleKind is null)
            throw InvalidPersistedData("The recurring plan has no supported persisted schedule kind.");
        return true;
    }

    private static List<PlanExecutionStatusOccurrenceSnapshot> ReadOccurrences(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId,
        bool isOneTime,
        bool isRequiredOnceOwner,
        out long occurrenceCount)
    {
        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM plan_occurrences WHERE plan_id = $plan_id;";
            Add(count, "$plan_id", planId);
            var value = count.ExecuteScalar();
            if (value is not long total || total < 0)
                throw InvalidPersistedData("The plan occurrence count is malformed.");
            if (total > MaximumOccurrencesPerPlan)
                throw InvalidPersistedData("The plan occurrence count exceeds the supported query bound.");
            occurrenceCount = total;
        }

        var result = new List<PlanExecutionStatusOccurrenceSnapshot>(checked((int)occurrenceCount));
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT o.id, o.plan_id, o.status_code, o.window_start_utc, o.window_end_utc,
                   o.run_id, o.terminal_reason_code, o.created_at_utc, o.updated_at_utc, o.version,
                   r.id, r.occurrence_id, r.status_code, r.has_crossed_start_commit,
                   r.media_artifact_id, r.bundle_id, r.terminal_reason_code,
                   r.created_at_utc, r.updated_at_utc, r.version,
                   e.run_id, e.occurrence_id, e.evidence_kind_code, e.output_path, e.recorded_at_utc,
                   x.execution_id, x.status_code, x.reason_code, x.run_id, x.specification_digest,
                   x.output_path, x.output_size_bytes, x.actual_duration_ms, x.execution_approval_id,
                   x.proof_id, x.proof_nonce, x.current_user_sid, x.session_binding,
                   x.latest_start_utc, x.committed_at_utc, x.updated_at_utc, x.approved_at_utc
            FROM plan_occurrences AS o
            LEFT JOIN recording_runs AS r ON r.occurrence_id = o.id
            LEFT JOIN recording_run_output_evidence AS e ON e.run_id = r.id
            LEFT JOIN required_once_execution_authorizations AS x ON x.plan_id = o.plan_id AND x.occurrence_id = o.id
            WHERE o.plan_id = $plan_id
            ORDER BY o.window_start_utc ASC, o.id COLLATE BINARY ASC
            LIMIT $limit;
            """;
        Add(command, "$plan_id", planId);
        Add(command, "$limit", MaximumOccurrencesPerPlan + 1);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (result.Count >= MaximumOccurrencesPerPlan)
                throw InvalidPersistedData("The plan occurrence query exceeded its hard bound.");
            var occurrence = PlanOccurrence.Rehydrate(
                ReadRequiredText(reader, 0),
                ReadRequiredText(reader, 1),
                ReadUtcDateTimeOffset(reader, 3),
                ReadUtcDateTimeOffset(reader, 4),
                ReadUtcDateTimeOffset(reader, 7),
                Phase3StateCodes.ParsePlanOccurrence(ReadRequiredText(reader, 2)),
                ReadNullableText(reader, 5),
                ReadNullableText(reader, 6),
                ReadUtcDateTimeOffset(reader, 8),
                ReadInt64(reader, 9));
            RecordingRun? run = null;
            if (!reader.IsDBNull(10))
            {
                run = RecordingRun.Rehydrate(
                    ReadRequiredText(reader, 10), ReadRequiredText(reader, 11),
                    ReadUtcDateTimeOffset(reader, 17),
                    Phase3StateCodes.ParseRecordingRun(ReadRequiredText(reader, 12)),
                    ReadBoolean(reader, 13), ReadNullableText(reader, 14), ReadNullableText(reader, 15),
                    ReadNullableText(reader, 16), ReadUtcDateTimeOffset(reader, 18), ReadInt64(reader, 19));
            }
            if (!IsLegalOccurrenceRunCombination(occurrence, run))
                throw InvalidPersistedData("The occurrence and run states do not form a reachable lifecycle combination.");

            string? outputPath = null;
            var outputPathRecorded = false;
            bool? outputFileExists = null;
            var hasEvidence = !reader.IsDBNull(20);
            var hasRequiredExecution = !reader.IsDBNull(25);
            var isSettledSuccess = occurrence.Status == PlanOccurrenceStatus.Completed &&
                run?.Status == RecordingRunStatus.Settled &&
                occurrence.TerminalReasonCode is null && run.TerminalReasonCode is null;
            string? executionStatusCode = null;
            if (hasRequiredExecution)
            {
                if (!isRequiredOnceOwner)
                    throw InvalidPersistedData("A non-required plan has required-once execution evidence.");
                executionStatusCode = ValidateRequiredOnceExecutionRow(
                    connection, transaction, planId, occurrence, run, reader);
                if (hasEvidence)
                    throw InvalidPersistedData("A required-once execution unexpectedly has Lease-style output evidence.");
            }
            else if (isRequiredOnceOwner && occurrence.RunId is not null)
                throw InvalidPersistedData("A required-once run has no execution authorization receipt.");

            if (isSettledSuccess && hasRequiredExecution)
            {
                outputPath = ReadNullableText(reader, 30);
                outputPathRecorded = true;
                var sizeBytes = ReadInt64(reader, 31);
                outputFileExists = File.Exists(outputPath) && new FileInfo(outputPath!).Length == sizeBytes;
            }
            else if (isSettledSuccess)
            {
                if (!hasEvidence)
                    throw InvalidPersistedData("A settled run has no durable output-path evidence or legacy marker.");

                var evidenceRunId = ReadRequiredText(reader, 20);
                var evidenceOccurrenceId = ReadRequiredText(reader, 21);
                var evidenceKind = ReadRequiredText(reader, 22);
                var evidencePath = ReadNullableText(reader, 23);
                var recordedAtUtc = ReadUtcDateTimeOffset(reader, 24);
                if (!string.Equals(evidenceRunId, run!.Id, StringComparison.Ordinal) ||
                    !string.Equals(evidenceOccurrenceId, occurrence.Id, StringComparison.Ordinal) ||
                    recordedAtUtc != run.UpdatedAtUtc)
                    throw InvalidPersistedData("The output-path evidence is cross-linked or has an invalid settlement time.");

                if (string.Equals(evidenceKind, "legacy_path_unavailable", StringComparison.Ordinal))
                {
                    if (evidencePath is not null)
                        throw InvalidPersistedData("Legacy output evidence unexpectedly contains an output path.");
                }
                else if (string.Equals(evidenceKind, "verified_output_path", StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(evidencePath))
                        throw InvalidPersistedData("Verified output evidence is missing its actual path.");

                    EnsureSettledLeaseUse(connection, transaction, planId, occurrence.Id, run.Id, isOneTime);
                    var approvedPath = ReadApprovedOutputPath(connection, transaction, planId, occurrence.Id, isOneTime);
                    var normalizedRecordedPath = Path.GetFullPath(evidencePath);
                    if (!string.Equals(evidencePath, normalizedRecordedPath, StringComparison.Ordinal) ||
                        !string.Equals(normalizedRecordedPath, approvedPath, StringComparison.OrdinalIgnoreCase))
                        throw InvalidPersistedData("The actual output path is not the exact approved frozen target.");

                    outputPath = evidencePath;
                    outputPathRecorded = true;
                    outputFileExists = File.Exists(evidencePath);
                }
                else
                {
                    throw InvalidPersistedData("The output-path evidence kind is unknown.");
                }
            }
            else if (hasEvidence)
            {
                throw InvalidPersistedData("Output-path evidence exists without a completed settled run.");
            }

            result.Add(new PlanExecutionStatusOccurrenceSnapshot(
                occurrence.Id,
                occurrence.WindowStartUtc,
                occurrence.WindowEndUtc,
                occurrence.StatusCode,
                occurrence.TerminalReasonCode,
                occurrence.RunId,
                run is null ? null : new PlanExecutionStatusRunSnapshot(run.Id, run.StatusCode, run.TerminalReasonCode),
                OutputPath: outputPath,
                OutputPathRecorded: outputPathRecorded,
                OutputFileExists: outputFileExists,
                ExecutionStatusCode: executionStatusCode));
        }

        if (result.Count != occurrenceCount)
            throw InvalidPersistedData("The occurrence query and count disagree or a run relation is duplicated.");
        return result;
    }

    private static string ReadRequiredOnceOccurrenceId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT occurrence_id FROM required_once_setup_intents WHERE plan_id = $plan LIMIT 2;";
        Add(command, "$plan", planId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw InvalidPersistedData("Required-once ownership has no occurrence identifier.");
        var occurrenceId = ReadRequiredText(reader, 0);
        if (reader.Read()) throw InvalidPersistedData("Required-once ownership has duplicate occurrence identifiers.");
        return occurrenceId;
    }

    private static string ValidateRequiredOnceExecutionRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId,
        PlanOccurrence occurrence,
        RecordingRun? run,
        SqliteDataReader reader)
    {
        var executionId = ReadRequiredText(reader, 25);
        var status = ReadRequiredText(reader, 26);
        var reason = ReadNullableText(reader, 27);
        var executionRunId = ReadNullableText(reader, 28);
        var digest = ReadRequiredText(reader, 29);
        var outputPath = ReadNullableText(reader, 30);
        var outputSize = reader.IsDBNull(31) ? (long?)null : ReadInt64(reader, 31);
        var actualDuration = reader.IsDBNull(32) ? (long?)null : ReadInt64(reader, 32);
        var approvalId = ReadNullableText(reader, 33);
        var proofId = ReadNullableText(reader, 34);
        var nonce = ReadNullableText(reader, 35);
        var sid = ReadRequiredText(reader, 36);
        var session = ReadRequiredText(reader, 37);
        var latestStart = ReadUtcDateTimeOffset(reader, 38);
        var committedAt = ReadNullableUtcDateTimeOffset(reader, 39);
        var executionUpdatedAt = ReadUtcDateTimeOffset(reader, 40);
        var approvedAt = ReadNullableUtcDateTimeOffset(reader, 41);

        if (executionId.Length is 0 or > 128 || digest.Length != 64 ||
            digest.Any(character => !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw InvalidPersistedData("The required-once execution identity or digest is malformed.");
        var state = SqliteRequiredOncePlanSetupRepository.ReadAndValidateExecutionProgressPlanWithinTransaction(
            connection, transaction, planId, occurrence.Id)
            ?? throw InvalidPersistedData("The required-once execution setup owner is missing.");
        var creationApprovalId = ReadRequiredCreationApprovalId(connection, transaction, planId, occurrence.Id);
        var selection = state.Selection ?? throw InvalidPersistedData("The required-once approved region is missing.");
        var specification = new RequiredOnceCaptureExecutionSpecification(
            state.PlanId!, state.OccurrenceId!, state.Request.SetupIntentId, creationApprovalId,
            state.Request.CurrentUserSid, state.Request.SessionBinding,
            state.Request.ScheduledStartUtc, state.Request.LatestStartUtc, state.Request.PlannedEndUtc,
            state.Request.Duration, state.Request.OutputDirectory, state.Request.FrozenFileName,
            selection.StableDisplayFingerprint, selection.DisplayBounds, selection.RegionWithinDisplay,
            selection.DpiX, selection.DpiY, selection.PhysicalWidth, selection.PhysicalHeight,
            selection.Orientation, selection.TopologyDigest);
        if (digest != specification.SpecificationDigest || sid != specification.CurrentUserSid ||
            session != specification.SessionBinding || latestStart != specification.LatestStartUtc)
            throw InvalidPersistedData("The execution receipt does not match the immutable required-once specification.");

        var committedState = status is "start_committed" or "recording" or "finalizing" or "settled" or
            "started_unknown" or "session_interrupted" or "failed";
        if (!committedState)
        {
            if (executionRunId is not null || approvalId is not null || proofId is not null || nonce is not null ||
                committedAt is not null || approvedAt is not null || outputPath is not null ||
                outputSize is not null || actualDuration is not null)
                throw InvalidPersistedData("A pre-start required-once outcome contains post-commit evidence.");
            var expectedPrecommitOccurrence = status switch
            {
                "pending_confirmation" => PlanOccurrenceStatus.PendingConfirmation,
                "rejected" => PlanOccurrenceStatus.Cancelled,
                "expired" => PlanOccurrenceStatus.Expired,
                "blocked" => PlanOccurrenceStatus.Blocked,
                _ => (PlanOccurrenceStatus?)null,
            };
            if (expectedPrecommitOccurrence is null || occurrence.Status != expectedPrecommitOccurrence || occurrence.RunId is not null ||
                (status == "pending_confirmation" && (reason is not null || occurrence.TerminalReasonCode is not null)) ||
                (status != "pending_confirmation" && (reason is null || reason != occurrence.TerminalReasonCode)))
                throw InvalidPersistedData("The pre-start execution status disagrees with its occurrence state.");
            return status;
        }

        if (run is null || executionRunId != run.Id || approvalId is null || proofId is null ||
            nonce is not { Length: 32 } || nonce.Any(character => !(character is >= '0' and <= '9' or >= 'a' and <= 'f')) ||
            committedAt is null || approvedAt is null || approvedAt.Value > committedAt.Value ||
            committedAt.Value < specification.ScheduledStartUtc || committedAt.Value >= specification.LatestStartUtc ||
            executionUpdatedAt < committedAt.Value)
            throw InvalidPersistedData("The committed execution proof, run, or time binding is invalid.");
        var expectedRunStatus = status switch
        {
            "start_committed" => RecordingRunStatus.StartCommitted,
            "recording" => RecordingRunStatus.Recording,
            "finalizing" => RecordingRunStatus.Finalizing,
            "settled" => RecordingRunStatus.Settled,
            "started_unknown" => RecordingRunStatus.StartedUnknown,
            "session_interrupted" => RecordingRunStatus.SessionInterrupted,
            "failed" => RecordingRunStatus.Failed,
            _ => (RecordingRunStatus?)null,
        };
        var expectedOccurrence = status switch
        {
            "settled" => PlanOccurrenceStatus.Completed,
            "started_unknown" or "session_interrupted" or "failed" => PlanOccurrenceStatus.Blocked,
            _ => PlanOccurrenceStatus.RunCreated,
        };
        var mediaValid = status != "settled" ||
            (reason is null && occurrence.TerminalReasonCode is null &&
             outputPath == specification.FrozenOutputFilePath && outputSize is > 0 && actualDuration is > 0 &&
             actualDuration <= specification.Duration.TotalMilliseconds * 1.5 && run.UpdatedAtUtc == executionUpdatedAt);
        if (expectedRunStatus is null || run.Status != expectedRunStatus || occurrence.Status != expectedOccurrence ||
            run.TerminalReasonCode != reason ||
            (expectedOccurrence == PlanOccurrenceStatus.Blocked && occurrence.TerminalReasonCode != reason) ||
            !mediaValid || (status != "settled" && (outputPath is not null || outputSize is not null || actualDuration is not null)))
            throw InvalidPersistedData("The committed execution lifecycle or media receipt is inconsistent.");
        return status;
    }

    private static string ReadRequiredCreationApprovalId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId,
        string occurrenceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT creation_approval_id FROM required_once_authorized_specs WHERE plan_id = $plan AND occurrence_id = $occurrence LIMIT 2;";
        Add(command, "$plan", planId); Add(command, "$occurrence", occurrenceId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw InvalidPersistedData("The immutable creation approval evidence is missing.");
        var approvalId = ReadRequiredText(reader, 0);
        if (reader.Read()) throw InvalidPersistedData("The immutable creation approval evidence is duplicated.");
        return approvalId;
    }

    private static string ReadApprovedOutputPath(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId,
        string occurrenceId,
        bool isOneTime)
    {
        string outputDirectory;
        string frozenOutputFilePath;
        if (isOneTime)
        {
            var scope = SqliteAuthorizedCaptureScopeRepository.TryReadOutputScopeByPlanOccurrence(
                connection,
                transaction,
                planId,
                occurrenceId) ?? throw InvalidPersistedData("The settled one-time run has no persisted approved output scope.");
            if (!string.Equals(scope.PlanId, planId, StringComparison.Ordinal) ||
                !string.Equals(scope.OccurrenceId, occurrenceId, StringComparison.Ordinal))
                throw InvalidPersistedData("The approved one-time output scope is cross-linked.");
            outputDirectory = scope.OutputDirectory;
            frozenOutputFilePath = scope.OutputFilePath;
        }
        else
        {
            var specification = SqliteRecurringOccurrenceExecutionSpecificationReader.ReadByOccurrence(
                connection,
                transaction,
                string.Empty,
                occurrenceId) ?? throw InvalidPersistedData("The settled recurring run has no persisted execution specification.");
            if (!string.Equals(specification.PlanId, planId, StringComparison.Ordinal) ||
                !string.Equals(specification.OccurrenceId, occurrenceId, StringComparison.Ordinal))
                throw InvalidPersistedData("The recurring output specification is cross-linked.");
            outputDirectory = specification.NormalizedOutputDirectory;
            frozenOutputFilePath = specification.FrozenOutputFilePath;
        }

        var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var normalizedTarget = Path.GetFullPath(frozenOutputFilePath);
        var targetDirectory = Path.GetDirectoryName(normalizedTarget);
        if (!Path.IsPathFullyQualified(normalizedDirectory) ||
            !Path.IsPathFullyQualified(normalizedTarget) ||
            !string.Equals(
                targetDirectory is null ? null : Path.TrimEndingDirectorySeparator(targetDirectory),
                normalizedDirectory,
                StringComparison.OrdinalIgnoreCase))
            throw InvalidPersistedData("The frozen output target is outside its approved directory.");

        return normalizedTarget;
    }

    private static void EnsureSettledLeaseUse(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId,
        string occurrenceId,
        string runId,
        bool isOneTime)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = isOneTime
            ? "SELECT COUNT(*) FROM lease_uses WHERE occurrence_id = $occurrence_id AND run_id = $run_id AND status_code = 'settled' AND actual_settled_duration_ms IS NOT NULL;"
            : "SELECT COUNT(*) FROM recurring_lease_uses WHERE plan_id = $plan_id AND occurrence_id = $occurrence_id AND run_id = $run_id AND status_code = 'settled' AND actual_settled_duration_ticks IS NOT NULL;";
        Add(command, "$plan_id", planId);
        Add(command, "$occurrence_id", occurrenceId);
        Add(command, "$run_id", runId);
        if (command.ExecuteScalar() is not long count || count != 1)
            throw InvalidPersistedData("The settled run has no exact settled lease-use accounting row.");
    }

    private static Phase3PersistenceException InvalidPersistedData(string message) =>
        new("plan_status_data_invalid", message);

    private static bool IsLegalOccurrenceRunCombination(PlanOccurrence occurrence, RecordingRun? run)
    {
        if (occurrence.RunId is null)
            return run is null;
        if (run is null ||
            !string.Equals(occurrence.RunId, run.Id, StringComparison.Ordinal) ||
            !string.Equals(occurrence.Id, run.OccurrenceId, StringComparison.Ordinal))
            return false;

        return occurrence.Status switch
        {
            PlanOccurrenceStatus.RunCreated => run.Status is
                RecordingRunStatus.Created or RecordingRunStatus.Preparing or RecordingRunStatus.StartCommitted or
                RecordingRunStatus.Recording or RecordingRunStatus.Finalizing or RecordingRunStatus.MediaReady,
            PlanOccurrenceStatus.Completed => run.Status == RecordingRunStatus.Settled &&
                occurrence.TerminalReasonCode is null && run.TerminalReasonCode is null,
            PlanOccurrenceStatus.Blocked =>
                (run.Status is RecordingRunStatus.StartedUnknown or RecordingRunStatus.SessionInterrupted or RecordingRunStatus.Failed) &&
                occurrence.TerminalReasonCode is not null &&
                string.Equals(occurrence.TerminalReasonCode, run.TerminalReasonCode, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static void EnsureNoAdvancementHistory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string planId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM recurring_advancement_operations WHERE plan_id = $plan) +
              (SELECT COUNT(*) FROM recurring_occurrence_slots WHERE plan_id = $plan) +
              (SELECT COUNT(*) FROM plan_occurrences WHERE plan_id = $plan);
            """;
        Add(command, "$plan", planId);
        if (command.ExecuteScalar() is not long count || count != 0)
            throw InvalidPersistedData("Recurring advancement history exists without its durable cursor.");
    }

    private static bool Canonical(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= maximum &&
        value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.');

    private static void TryRollback(SqliteTransaction transaction)
    {
        try { transaction.Rollback(); } catch { }
    }
}
