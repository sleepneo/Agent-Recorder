using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringLeaseLifecycleTransactionTests
{
    [Fact]
    public void FirstFrameCaptureEndedAndNormalSettlementAreMonotonicAndIdempotent()
    {
        using var started = Start();
        var lifecycle = started.Lifecycle;
        var firstFrameAt = started.Context.Fixture.CreatedAt.AddMinutes(3);

        var first = lifecycle.ObserveFirstFrame(
            new FirstFrameObservation
            {
                EvidenceKind = "ffmpeg_progress_frame_and_output_bytes",
                FrameNumber = 0,
                TotalSizeBytes = 128,
            },
            firstFrameAt);
        var duplicate = lifecycle.ObserveFirstFrame(
            new FirstFrameObservation
            {
                EvidenceKind = "ffmpeg_progress_frame_and_output_bytes",
                FrameNumber = 1,
                TotalSizeBytes = 256,
            },
            firstFrameAt.AddSeconds(1));
        var ended = lifecycle.ObserveCaptureEnded(firstFrameAt.AddMinutes(1));
        var endedReplay = lifecycle.ObserveCaptureEnded(firstFrameAt.AddMinutes(1));
        var settled = lifecycle.CompleteTermination(
            RecurringLeaseLifecycleTerminationKind.NaturalExit,
            0,
            ValidMeta(started, 12.5),
            firstFrameAt.AddMinutes(2));
        var terminalRunVersion = new SqliteRecordingRunRepository(started.Context.Fixture.Store)
            .Get(started.Receipt.Run.Id).Version;
        var terminalUseVersion = started.Context.Fixture.ReadAccountingRow(started.Receipt.Use.Id).Version;
        var terminalOccurrenceVersion = new SqlitePlanOccurrenceRepository(started.Context.Fixture.Store)
            .Get(started.Context.Slot.OccurrenceId!).Version;
        var terminalReplay = lifecycle.CompleteTermination(
            RecurringLeaseLifecycleTerminationKind.NaturalExit,
            0,
            ValidMeta(started, 12.5),
            firstFrameAt.AddMinutes(3));

        Assert.True(first.Succeeded && first.Changed && !first.Terminal);
        Assert.True(duplicate.Succeeded && !duplicate.Changed);
        Assert.True(ended.Succeeded && ended.Changed);
        Assert.True(endedReplay.Succeeded && !endedReplay.Changed);
        Assert.True(settled.Succeeded && settled.Changed && settled.Terminal);
        Assert.True(terminalReplay.Succeeded && !terminalReplay.Changed && terminalReplay.Terminal);

        var occurrence = new SqlitePlanOccurrenceRepository(started.Context.Fixture.Store)
            .Get(started.Context.Slot.OccurrenceId!);
        var run = new SqliteRecordingRunRepository(started.Context.Fixture.Store)
            .Get(started.Receipt.Run.Id);
        var use = new SqliteRecurringLeaseUseAccountingReader(started.Context.Fixture.Store)
            .TryGetByOccurrence(started.Context.Slot.OccurrenceIdentity);

        Assert.Equal(PlanOccurrenceStatus.Completed, occurrence.Status);
        Assert.Equal(RecordingRunStatus.Settled, run.Status);
        Assert.NotNull(use);
        Assert.Equal(LeaseUseStatus.Settled, use!.Status);
        Assert.Equal(TimeSpan.FromSeconds(12.5), use.ActualSettledDuration);
        Assert.Equal(terminalRunVersion, run.Version);
        Assert.Equal(terminalUseVersion, started.Context.Fixture.ReadAccountingRow(use.UseId).Version);
        Assert.Equal(terminalOccurrenceVersion, occurrence.Version);
    }

    [Fact]
    public void CaptureEndedBeforeFirstFrameDoesNotInventRecording()
    {
        using var started = Start();

        var result = started.Lifecycle.ObserveCaptureEnded(started.Context.Fixture.CreatedAt.AddMinutes(3));

        Assert.True(result.Succeeded);
        Assert.False(result.Changed);
        Assert.Equal("capture_ended_before_first_frame", result.Reason);
        Assert.Equal(
            RecordingRunStatus.StartCommitted,
            new SqliteRecordingRunRepository(started.Context.Fixture.Store).Get(started.Receipt.Run.Id).Status);
    }

    [Theory]
    [InlineData("NaturalExit", "natural_exit_before_first_frame")]
    [InlineData("UserStop", "user_stop_before_first_frame")]
    [InlineData("SafetyStop", "safety_stop_before_first_frame")]
    [InlineData("SessionInterrupted", "session_interrupted_before_first_frame")]
    [InlineData("BackendStartFailure", "backend_start_failed_before_first_frame")]
    [InlineData("LifecyclePersistenceFailure", "lifecycle_persistence_failure_before_first_frame")]
    public void PreFirstFrameTerminationChargesTheReservedUseAndIsAtMostOnce(
        string kindCode,
        string expectedReason)
    {
        using var started = Start();
        var kind = Enum.Parse<RecurringLeaseLifecycleTerminationKind>(kindCode);

        var result = started.Lifecycle.CompleteTermination(
            kind,
            0,
            ValidMeta(started, 1),
            started.Context.Fixture.CreatedAt.AddMinutes(3));
        var replay = started.Lifecycle.CompleteTermination(
            kind,
            0,
            ValidMeta(started, 1),
            started.Context.Fixture.CreatedAt.AddMinutes(4));

        Assert.True(result.Succeeded && result.Changed && result.Terminal);
        Assert.Equal(expectedReason, result.Reason);
        Assert.True(replay.Succeeded && !replay.Changed && replay.Terminal);

        var run = new SqliteRecordingRunRepository(started.Context.Fixture.Store).Get(started.Receipt.Run.Id);
        var occurrence = new SqlitePlanOccurrenceRepository(started.Context.Fixture.Store).Get(started.Context.Slot.OccurrenceId!);
        var use = new SqliteRecurringLeaseUseAccountingReader(started.Context.Fixture.Store).TryGetByOccurrence(started.Context.Slot.OccurrenceIdentity);
        Assert.Equal(RecordingRunStatus.StartedUnknown, run.Status);
        Assert.Equal(expectedReason, run.TerminalReasonCode);
        Assert.Equal(PlanOccurrenceStatus.Blocked, occurrence.Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown, use!.Status);
        Assert.Null(use.ActualSettledDuration);
    }

    [Fact]
    public void ValidUserStopSettlesActualPartialDurationAndRetainsActiveLease()
    {
        using var started = Start();
        var firstFrameAt = started.Context.Fixture.CreatedAt.AddMinutes(3);
        Assert.True(started.Lifecycle.ObserveFirstFrame(
            new FirstFrameObservation { FrameNumber = 2, TotalSizeBytes = 200 },
            firstFrameAt).Succeeded);

        var result = started.Lifecycle.CompleteTermination(
            RecurringLeaseLifecycleTerminationKind.UserStop,
            0,
            ValidMeta(started, 1.25, "manual"),
            firstFrameAt.AddMinutes(1));

        Assert.True(result.Succeeded && result.Changed && result.Terminal);
        Assert.Equal("media_settled", result.Reason);
        var use = new SqliteRecurringLeaseUseAccountingReader(started.Context.Fixture.Store)
            .TryGetByOccurrence(started.Context.Slot.OccurrenceIdentity);
        var lease = new SqliteRecurringConsentLeaseRepository(started.Context.Fixture.Store)
            .Get(started.Context.Lease.LeaseId);
        Assert.Equal(LeaseUseStatus.Settled, use!.Status);
        Assert.Equal(TimeSpan.FromSeconds(1.25), use.ActualSettledDuration);
        Assert.Equal(ConsentLeaseStatus.Active, lease.Status);
    }

    [Fact]
    public void InvalidOutputAfterFirstFrameUsesClosedReasonAndNeverSettles()
    {
        using var started = Start();
        var firstFrameAt = started.Context.Fixture.CreatedAt.AddMinutes(3);
        Assert.True(started.Lifecycle.ObserveFirstFrame(
            new FirstFrameObservation { FrameNumber = 0, TotalSizeBytes = 100 },
            firstFrameAt).Succeeded);

        var result = started.Lifecycle.CompleteTermination(
            RecurringLeaseLifecycleTerminationKind.UserStop,
            0,
            new OutputMeta
            {
                SizeBytes = 0,
                DurationSeconds = 1,
                OutputPath = started.Receipt.Specification.FrozenOutputFilePath,
            },
            firstFrameAt.AddMinutes(1));

        Assert.True(result.Succeeded && result.Changed && result.Terminal);
        Assert.Equal("user_stop_output_invalid", result.Reason);
        var run = new SqliteRecordingRunRepository(started.Context.Fixture.Store).Get(started.Receipt.Run.Id);
        var use = new SqliteRecurringLeaseUseAccountingReader(started.Context.Fixture.Store).TryGetByOccurrence(started.Context.Slot.OccurrenceIdentity);
        Assert.Equal(RecordingRunStatus.SessionInterrupted, run.Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown, use!.Status);
        Assert.Null(use.ActualSettledDuration);
    }

    [Theory]
    [InlineData("null_meta", "capture_output_missing")]
    [InlineData("empty_path", "capture_output_path_invalid")]
    [InlineData("zero_size", "capture_output_missing")]
    public void MissingOrMalformedOutputEvidenceUsesClosedReason(string variant, string expectedReason)
    {
        using var started = Start();
        var at = started.Context.Fixture.CreatedAt.AddMinutes(3);
        Assert.True(started.Lifecycle.ObserveFirstFrame(
            new FirstFrameObservation { FrameNumber = 0, TotalSizeBytes = 100 }, at).Succeeded);
        OutputMeta? meta = variant == "null_meta" ? null : ValidMeta(started, 1);
        if (variant == "empty_path")
            meta!.OutputPath = "";
        if (variant == "zero_size")
            meta!.SizeBytes = 0;

        var result = started.Lifecycle.CompleteTermination(
            RecurringLeaseLifecycleTerminationKind.NaturalExit, 0, meta, at.AddMinutes(1));

        Assert.True(result.Succeeded && result.Terminal);
        Assert.Equal(expectedReason, result.Reason);
        var use = new SqliteRecurringLeaseUseAccountingReader(started.Context.Fixture.Store)
            .TryGetByOccurrence(started.Context.Slot.OccurrenceIdentity)!;
        Assert.Equal(LeaseUseStatus.StartedUnknown, use.Status);
        Assert.Null(use.ActualSettledDuration);
    }

    [Fact]
    public void SingleUseExhaustionIsCommittedWithSettledActualDurationAndNeverReopened()
    {
        using var started = Start(maxUses: 1);
        var leaseAfterStart = new SqliteRecurringConsentLeaseRepository(started.Context.Fixture.Store)
            .Get(started.Context.Lease.LeaseId);
        Assert.Equal(ConsentLeaseStatus.Exhausted, leaseAfterStart.Status);

        var result = started.Lifecycle.CompleteTermination(
            RecurringLeaseLifecycleTerminationKind.NaturalExit,
            0,
            ValidMeta(started, 2),
            started.Context.Fixture.CreatedAt.AddMinutes(3));

        Assert.True(result.Succeeded && result.Changed);
        var leaseAfterSettlement = new SqliteRecurringConsentLeaseRepository(started.Context.Fixture.Store)
            .Get(started.Context.Lease.LeaseId);
        Assert.Equal(ConsentLeaseStatus.Exhausted, leaseAfterSettlement.Status);
    }

    [Fact]
    public void InvalidFirstFrameEvidenceIsRejectedBeforeOpeningLifecycleTransaction()
    {
        using var started = Start();

        var result = started.Lifecycle.ObserveFirstFrame(
            new FirstFrameObservation
            {
                EvidenceKind = "other",
                FrameNumber = -1,
                TotalSizeBytes = 0,
            },
            started.Context.Fixture.CreatedAt.AddMinutes(3));

        Assert.False(result.Succeeded);
        Assert.False(result.Changed);
        Assert.Equal("first_frame_evidence_invalid", result.Reason);
    }

    [Fact]
    public void FailureAfterRunUpdateRollsBackTheEntireLifecycleTransaction()
    {
        using var started = Start();
        var hook = new Action<RecurringLeaseLifecycleFailurePoint>(point =>
        {
            if (point == RecurringLeaseLifecycleFailurePoint.AfterRunUpdate)
                throw new InvalidOperationException("task272 injected failure");
        });
        var lifecycle = new SqliteRecurringLeaseLifecycleTransaction(
            started.Context.Fixture.Store,
            started.Receipt,
            hook);

        Assert.Throws<Phase3PersistenceException>(() => lifecycle.ObserveFirstFrame(
            new FirstFrameObservation { FrameNumber = 0, TotalSizeBytes = 100 },
            started.Context.Fixture.CreatedAt.AddMinutes(3)));

        var run = new SqliteRecordingRunRepository(started.Context.Fixture.Store).Get(started.Receipt.Run.Id);
        var use = new SqliteRecurringLeaseUseAccountingReader(started.Context.Fixture.Store).TryGetByOccurrence(started.Context.Slot.OccurrenceIdentity);
        Assert.Equal(RecordingRunStatus.StartCommitted, run.Status);
        Assert.Equal(LeaseUseStatus.StartCommitted, use!.Status);
    }

    [Fact]
    public void RevokedLeaseCanFinishCurrentCommittedRunWithoutBeingOverwritten()
    {
        using var started = Start();
        var at = started.Context.Fixture.CreatedAt.AddMinutes(3);
        started.Context.Fixture.Execute(
            "UPDATE recurring_consent_leases SET status_code = 'revoked', version = version + 1, updated_at_utc = $at WHERE lease_id = $leaseId;",
            ("$at", at.UtcDateTime.Ticks),
            ("$leaseId", started.Context.Lease.LeaseId));

        var result = started.Lifecycle.CompleteTermination(
            RecurringLeaseLifecycleTerminationKind.NaturalExit,
            0,
            ValidMeta(started, 2),
            at);

        Assert.True(result.Succeeded && result.Changed);
        Assert.Equal(
            ConsentLeaseStatus.Revoked,
            new SqliteRecurringConsentLeaseRepository(started.Context.Fixture.Store).Get(started.Context.Lease.LeaseId).Status);
    }

    [Fact]
    public void ExpiredLeaseCanFinishCurrentCommittedRunWithoutBeingOverwritten()
    {
        using var started = Start();
        var at = started.Context.Fixture.CreatedAt.AddMinutes(3);
        started.Context.Fixture.Execute(
            "DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; UPDATE recurring_consent_leases SET status_code = 'expired', version = version + 1, updated_at_utc = $at WHERE lease_id = $leaseId;",
            ("$at", at.UtcDateTime.Ticks),
            ("$leaseId", started.Context.Lease.LeaseId));

        var result = started.Lifecycle.CompleteTermination(
            RecurringLeaseLifecycleTerminationKind.NaturalExit,
            0,
            ValidMeta(started, 2),
            at);

        Assert.True(result.Succeeded && result.Changed);
        Assert.Equal(ConsentLeaseStatus.Expired,
            new SqliteRecurringConsentLeaseRepository(started.Context.Fixture.Store)
                .Get(started.Context.Lease.LeaseId).Status);
    }

    [Theory]
    [InlineData("SafetyStop", "SessionInterrupted", "safety_stop")]
    [InlineData("SessionInterrupted", "SessionInterrupted", "session_interrupted")]
    [InlineData("BackendStartFailure", "Failed", "backend_start_failed_after_first_frame")]
    [InlineData("LifecyclePersistenceFailure", "Failed", "lifecycle_persistence_failure_after_first_frame")]
    public void PostFirstFrameAbnormalTerminationPersistsExactState(
        string kindCode,
        string expectedRunStatusCode,
        string expectedReason)
    {
        using var started = Start();
        var firstFrameAt = started.Context.Fixture.CreatedAt.AddMinutes(3);
        Assert.True(started.Lifecycle.ObserveFirstFrame(
            new FirstFrameObservation { FrameNumber = 0, TotalSizeBytes = 100 },
            firstFrameAt).Succeeded);

        var beforeRun = new SqliteRecordingRunRepository(started.Context.Fixture.Store).Get(started.Receipt.Run.Id);
        var beforeUse = new SqliteRecurringLeaseUseAccountingReader(started.Context.Fixture.Store)
            .TryGetByOccurrence(started.Context.Slot.OccurrenceIdentity)!;
        var beforeUseVersion = started.Context.Fixture.ReadAccountingRow(beforeUse.UseId).Version;
        var beforeOccurrence = new SqlitePlanOccurrenceRepository(started.Context.Fixture.Store)
            .Get(started.Context.Slot.OccurrenceId!);

        var result = started.Lifecycle.CompleteTermination(
            Enum.Parse<RecurringLeaseLifecycleTerminationKind>(kindCode),
            0,
            null,
            firstFrameAt.AddMinutes(1));

        Assert.True(result.Succeeded && result.Changed && result.Terminal);
        Assert.Equal(expectedReason, result.Reason);
        var run = new SqliteRecordingRunRepository(started.Context.Fixture.Store).Get(started.Receipt.Run.Id);
        var use = new SqliteRecurringLeaseUseAccountingReader(started.Context.Fixture.Store)
            .TryGetByOccurrence(started.Context.Slot.OccurrenceIdentity)!;
        var occurrence = new SqlitePlanOccurrenceRepository(started.Context.Fixture.Store)
            .Get(started.Context.Slot.OccurrenceId!);
        Assert.Equal(expectedRunStatusCode, run.Status.ToString());
        Assert.Equal(expectedReason, run.TerminalReasonCode);
        Assert.Equal(PlanOccurrenceStatus.Blocked, occurrence.Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown, use.Status);
        Assert.Equal(beforeRun.Version + 1, run.Version);
        Assert.Equal(beforeUseVersion + 1, started.Context.Fixture.ReadAccountingRow(use.UseId).Version);
        Assert.Equal(beforeOccurrence.Version + 1, occurrence.Version);
        Assert.Null(use.ActualSettledDuration);
    }

    [Fact]
    public void OutputFileMissingIsRejectedEvenWhenOtherMediaMetadataLooksValid()
    {
        using var started = Start();
        var at = started.Context.Fixture.CreatedAt.AddMinutes(3);
        Assert.True(started.Lifecycle.ObserveFirstFrame(
            new FirstFrameObservation { FrameNumber = 0, TotalSizeBytes = 100 }, at).Succeeded);
        var meta = ValidMeta(started, 1.25);
        meta.OutputFileExists = false;

        var result = started.Lifecycle.CompleteTermination(
            RecurringLeaseLifecycleTerminationKind.NaturalExit, 0, meta, at.AddMinutes(1));

        Assert.True(result.Succeeded && result.Changed && result.Terminal);
        Assert.Equal("capture_output_missing", result.Reason);
        var use = new SqliteRecurringLeaseUseAccountingReader(started.Context.Fixture.Store)
            .TryGetByOccurrence(started.Context.Slot.OccurrenceIdentity)!;
        Assert.Equal(LeaseUseStatus.StartedUnknown, use.Status);
        Assert.Null(use.ActualSettledDuration);
    }

    [Theory]
    [InlineData(double.NaN, "capture_duration_invalid")]
    [InlineData(double.PositiveInfinity, "capture_duration_invalid")]
    [InlineData(-1d, "capture_duration_negative")]
    [InlineData(999999d, "capture_duration_exceeds_authorization")]
    public void InvalidDurationEvidenceNeverSettles(double durationSeconds, string expectedReason)
    {
        using var started = Start();
        var at = started.Context.Fixture.CreatedAt.AddMinutes(3);
        Assert.True(started.Lifecycle.ObserveFirstFrame(
            new FirstFrameObservation { FrameNumber = 0, TotalSizeBytes = 100 }, at).Succeeded);

        var result = started.Lifecycle.CompleteTermination(
            RecurringLeaseLifecycleTerminationKind.NaturalExit,
            0,
            ValidMeta(started, durationSeconds),
            at.AddMinutes(1));

        Assert.True(result.Succeeded && result.Terminal);
        Assert.Equal(expectedReason, result.Reason);
        var use = new SqliteRecurringLeaseUseAccountingReader(started.Context.Fixture.Store)
            .TryGetByOccurrence(started.Context.Slot.OccurrenceIdentity)!;
        Assert.Equal(LeaseUseStatus.StartedUnknown, use.Status);
        Assert.Null(use.ActualSettledDuration);
    }

    [Fact]
    public void WindowsOutputPathComparisonIsCaseInsensitiveButDifferentPathIsRejected()
    {
        using (var matching = Start())
        {
            var at = matching.Context.Fixture.CreatedAt.AddMinutes(3);
            Assert.True(matching.Lifecycle.ObserveFirstFrame(
                new FirstFrameObservation { FrameNumber = 0, TotalSizeBytes = 100 }, at).Succeeded);
            var meta = ValidMeta(matching, 1.2345678);
            meta.OutputPath = meta.OutputPath!.ToUpperInvariant();
            var result = matching.Lifecycle.CompleteTermination(
                RecurringLeaseLifecycleTerminationKind.NaturalExit, 0, meta, at.AddMinutes(1));
            Assert.Equal("media_settled", result.Reason);
            var use = new SqliteRecurringLeaseUseAccountingReader(matching.Context.Fixture.Store)
                .TryGetByOccurrence(matching.Context.Slot.OccurrenceIdentity)!;
            Assert.Equal(TimeSpan.FromTicks((long)Math.Round(1.2345678 * TimeSpan.TicksPerSecond, MidpointRounding.ToEven)), use.ActualSettledDuration);
        }

        using var different = Start();
        var differentAt = different.Context.Fixture.CreatedAt.AddMinutes(3);
        Assert.True(different.Lifecycle.ObserveFirstFrame(
            new FirstFrameObservation { FrameNumber = 0, TotalSizeBytes = 100 }, differentAt).Succeeded);
        var differentMeta = ValidMeta(different, 1);
        differentMeta.OutputPath = Path.Combine(
            Path.GetDirectoryName(differentMeta.OutputPath!)!, "different-output.mp4");
        var differentResult = different.Lifecycle.CompleteTermination(
            RecurringLeaseLifecycleTerminationKind.UserStop, 0, differentMeta, differentAt.AddMinutes(1));
        Assert.Equal("user_stop_output_invalid", differentResult.Reason);
    }

    [Fact]
    public void SettledActualDurationReleasesCapacityForTheNextReservation()
    {
        using var started = Start(maxUses: 2, slotCount: 2);
        var at = started.Context.Fixture.CreatedAt.AddMinutes(3);
        Assert.True(started.Lifecycle.ObserveFirstFrame(
            new FirstFrameObservation { FrameNumber = 0, TotalSizeBytes = 100 }, at).Succeeded);
        Assert.True(started.Lifecycle.CompleteTermination(
            RecurringLeaseLifecycleTerminationKind.UserStop,
            0,
            ValidMeta(started, 1.25, "manual"),
            at.AddMinutes(1)).Succeeded);

        var lease = new SqliteRecurringConsentLeaseRepository(started.Context.Fixture.Store)
            .Get(started.Context.Lease.LeaseId);
        Assert.Equal(ConsentLeaseStatus.Active, lease.Status);
        var secondAt = started.Context.Slots[1].ScheduledStartUtc!.Value;
        var secondReservation = new RecurringOccurrenceReservationService(
            started.Context.Fixture.Store,
            () => secondAt,
            () => "task272r-second-run",
            () => "task272r-second-use")
            .Reserve(started.Context.Lease.LeaseId, started.Context.Slots[1].OccurrenceIdentity);
        Assert.True(
            secondReservation.Status == RecurringOccurrenceReservationStatus.Reserved,
            $"status={secondReservation.Status}; reason={secondReservation.ReasonCode}");
    }

    [Fact]
    public void CumulativeDurationExhaustionIsPersistedSeparatelyFromMaxUses()
    {
        using var started = Start(
            maxUses: 10,
            maxCumulativeDuration: TimeSpan.FromMinutes(2),
            slotCount: 1);
        var at = started.Context.Fixture.CreatedAt.AddMinutes(3);
        Assert.Equal(ConsentLeaseStatus.Active,
            new SqliteRecurringConsentLeaseRepository(started.Context.Fixture.Store)
                .Get(started.Context.Lease.LeaseId).Status);
        Assert.True(started.Lifecycle.ObserveFirstFrame(
            new FirstFrameObservation { FrameNumber = 0, TotalSizeBytes = 100 }, at).Succeeded);
        Assert.True(started.Lifecycle.CompleteTermination(
            RecurringLeaseLifecycleTerminationKind.NaturalExit,
            0,
            ValidMeta(started, 1.25),
            at.AddMinutes(1)).Succeeded);
        var lease = new SqliteRecurringConsentLeaseRepository(started.Context.Fixture.Store)
            .Get(started.Context.Lease.LeaseId);
        Assert.Equal(ConsentLeaseStatus.Exhausted, lease.Status);
    }

    [Fact]
    public void ExhaustedLeaseWithNonterminalQuotaFailsClosedAndRollsBackLifecycleRows()
    {
        using var started = Start();
        var beforeRun = new SqliteRecordingRunRepository(started.Context.Fixture.Store).Get(started.Receipt.Run.Id);
        var beforeUse = new SqliteRecurringLeaseUseAccountingReader(started.Context.Fixture.Store)
            .TryGetByOccurrence(started.Context.Slot.OccurrenceIdentity)!;
        var beforeUseVersion = started.Context.Fixture.ReadAccountingRow(beforeUse.UseId).Version;
        var beforeOccurrence = new SqlitePlanOccurrenceRepository(started.Context.Fixture.Store)
            .Get(started.Context.Slot.OccurrenceId!);
        started.Context.Fixture.Execute(
            "DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; UPDATE recurring_consent_leases SET status_code = 'exhausted', version = version + 1, updated_at_utc = $at WHERE lease_id = $leaseId;",
            ("$at", started.Context.Fixture.CreatedAt.AddTicks(1).UtcDateTime.Ticks),
            ("$leaseId", started.Context.Lease.LeaseId));

        Assert.Throws<Phase3PersistenceException>(() => started.Lifecycle.ObserveFirstFrame(
            new FirstFrameObservation { FrameNumber = 0, TotalSizeBytes = 100 },
            started.Context.Fixture.CreatedAt.AddMinutes(3)));

        var run = new SqliteRecordingRunRepository(started.Context.Fixture.Store).Get(started.Receipt.Run.Id);
        var use = new SqliteRecurringLeaseUseAccountingReader(started.Context.Fixture.Store)
            .TryGetByOccurrence(started.Context.Slot.OccurrenceIdentity)!;
        var occurrence = new SqlitePlanOccurrenceRepository(started.Context.Fixture.Store)
            .Get(started.Context.Slot.OccurrenceId!);
        Assert.Equal(beforeRun.Version, run.Version);
        Assert.Equal(beforeUseVersion, started.Context.Fixture.ReadAccountingRow(use.UseId).Version);
        Assert.Equal(beforeOccurrence.Version, occurrence.Version);
        Assert.Equal(RecordingRunStatus.StartCommitted, run.Status);
        Assert.Equal(LeaseUseStatus.StartCommitted, use.Status);
    }

    [Theory]
    [InlineData("AfterRunUpdate")]
    [InlineData("AfterUseUpdate")]
    [InlineData("AfterOccurrenceUpdate")]
    [InlineData("AfterLeaseExhaustionUpdate")]
    [InlineData("BeforeFinalReadback")]
    [InlineData("BeforeCommit")]
    public void FailureAtEveryLifecycleBoundaryRollsBackAllWrites(string failurePointCode)
    {
        using var started = Start(
            maxUses: 10,
            maxCumulativeDuration: failurePointCode == "AfterLeaseExhaustionUpdate" ? TimeSpan.FromMinutes(2) : null);
        var at = started.Context.Fixture.CreatedAt.AddMinutes(3);
        Assert.True(started.Lifecycle.ObserveFirstFrame(
            new FirstFrameObservation { FrameNumber = 0, TotalSizeBytes = 100 }, at).Succeeded);
        var beforeRun = new SqliteRecordingRunRepository(started.Context.Fixture.Store).Get(started.Receipt.Run.Id);
        var beforeUse = new SqliteRecurringLeaseUseAccountingReader(started.Context.Fixture.Store)
            .TryGetByOccurrence(started.Context.Slot.OccurrenceIdentity)!;
        var beforeUseVersion = started.Context.Fixture.ReadAccountingRow(beforeUse.UseId).Version;
        var beforeOccurrence = new SqlitePlanOccurrenceRepository(started.Context.Fixture.Store)
            .Get(started.Context.Slot.OccurrenceId!);
        var beforeLease = new SqliteRecurringConsentLeaseRepository(started.Context.Fixture.Store)
            .Get(started.Context.Lease.LeaseId);
        var hook = new Action<RecurringLeaseLifecycleFailurePoint>(point =>
        {
            if (point == Enum.Parse<RecurringLeaseLifecycleFailurePoint>(failurePointCode))
                throw new InvalidOperationException("task272r injected failure");
        });
        var lifecycle = new SqliteRecurringLeaseLifecycleTransaction(started.Context.Fixture.Store, started.Receipt, hook);

        Assert.Throws<Phase3PersistenceException>(() => lifecycle.CompleteTermination(
            RecurringLeaseLifecycleTerminationKind.NaturalExit,
            0,
            ValidMeta(started, 1.25),
            at.AddMinutes(1)));

        var run = new SqliteRecordingRunRepository(started.Context.Fixture.Store).Get(started.Receipt.Run.Id);
        var use = new SqliteRecurringLeaseUseAccountingReader(started.Context.Fixture.Store)
            .TryGetByOccurrence(started.Context.Slot.OccurrenceIdentity)!;
        var occurrence = new SqlitePlanOccurrenceRepository(started.Context.Fixture.Store)
            .Get(started.Context.Slot.OccurrenceId!);
        var lease = new SqliteRecurringConsentLeaseRepository(started.Context.Fixture.Store)
            .Get(started.Context.Lease.LeaseId);
        Assert.Equal(beforeRun.Version, run.Version);
        Assert.Equal(beforeUseVersion, started.Context.Fixture.ReadAccountingRow(use.UseId).Version);
        Assert.Equal(beforeOccurrence.Version, occurrence.Version);
        Assert.Equal(beforeLease.Version, lease.Version);
        Assert.Equal(RecordingRunStatus.Recording, run.Status);
        Assert.Equal(LeaseUseStatus.Consumed, use.Status);
        Assert.Equal(PlanOccurrenceStatus.RunCreated, occurrence.Status);
    }

    [Fact]
    public async Task ConcurrentFirstFrameWritersProduceOneChangedResult()
    {
        using var started = Start();
        var at = started.Context.Fixture.CreatedAt.AddMinutes(3);
        var firstFrame = new FirstFrameObservation { FrameNumber = 0, TotalSizeBytes = 100 };
        var first = new SqliteRecurringLeaseLifecycleTransaction(started.Context.Fixture.Store, started.Receipt);
        var second = new SqliteRecurringLeaseLifecycleTransaction(started.Context.Fixture.Store, started.Receipt);
        var results = await Task.WhenAll(
            Task.Run(() => first.ObserveFirstFrame(firstFrame, at)),
            Task.Run(() => second.ObserveFirstFrame(firstFrame, at.AddTicks(1))));
        Assert.Equal(1, results.Count(result => result.Changed));
        Assert.All(results, result => Assert.True(result.Succeeded));
        var run = new SqliteRecordingRunRepository(started.Context.Fixture.Store).Get(started.Receipt.Run.Id);
        var use = new SqliteRecurringLeaseUseAccountingReader(started.Context.Fixture.Store)
            .TryGetByOccurrence(started.Context.Slot.OccurrenceIdentity)!;
        Assert.Equal(RecordingRunStatus.Recording, run.Status);
        Assert.Equal(LeaseUseStatus.Consumed, use.Status);
    }

    [Fact]
    public async Task ConcurrentTerminationWritersProduceOneChangedResult()
    {
        using var started = Start();
        var firstFrameAt = started.Context.Fixture.CreatedAt.AddMinutes(3);
        Assert.True(started.Lifecycle.ObserveFirstFrame(
            new FirstFrameObservation { FrameNumber = 0, TotalSizeBytes = 100 }, firstFrameAt).Succeeded);
        var first = new SqliteRecurringLeaseLifecycleTransaction(started.Context.Fixture.Store, started.Receipt);
        var second = new SqliteRecurringLeaseLifecycleTransaction(started.Context.Fixture.Store, started.Receipt);
        var results = await Task.WhenAll(
            Task.Run(() => first.CompleteTermination(
                RecurringLeaseLifecycleTerminationKind.NaturalExit,
                0,
                ValidMeta(started, 1.25),
                firstFrameAt.AddMinutes(1))),
            Task.Run(() => second.CompleteTermination(
                RecurringLeaseLifecycleTerminationKind.NaturalExit,
                0,
                ValidMeta(started, 1.25),
                firstFrameAt.AddMinutes(2))));
        Assert.Equal(1, results.Count(result => result.Changed));
        Assert.All(results, result => Assert.True(result.Succeeded && result.Terminal));
        var run = new SqliteRecordingRunRepository(started.Context.Fixture.Store).Get(started.Receipt.Run.Id);
        var use = new SqliteRecurringLeaseUseAccountingReader(started.Context.Fixture.Store)
            .TryGetByOccurrence(started.Context.Slot.OccurrenceIdentity)!;
        Assert.Equal(RecordingRunStatus.Settled, run.Status);
        Assert.Equal(LeaseUseStatus.Settled, use.Status);
    }

    [Fact]
    public void PersistedSpecificationDigestMismatchRejectsReceiptAndSpecificationEvidence()
    {
        using var started = Start();
        started.Context.Fixture.Execute(
            "DROP TRIGGER trg_recurring_occurrence_execution_specs_immutable_update; UPDATE recurring_occurrence_execution_specs SET specification_digest = $digest WHERE occurrence_identity = $identity;",
            ("$digest", started.Receipt.Specification.SpecificationDigest[..^1] + (started.Receipt.Specification.SpecificationDigest[^1] == '0' ? '1' : '0')),
            ("$identity", started.Context.Slot.OccurrenceIdentity));

        var receiptLifecycle = new SqliteRecurringLeaseLifecycleTransaction(started.Context.Fixture.Store, started.Receipt);
        Assert.Throws<Phase3PersistenceException>(() => receiptLifecycle.ObserveFirstFrame(
            new FirstFrameObservation { FrameNumber = 0, TotalSizeBytes = 100 },
            started.Context.Fixture.CreatedAt.AddMinutes(3)));
        var specificationLifecycle = new SqliteRecurringLeaseLifecycleTransaction(
            started.Context.Fixture.Store, started.Receipt.Specification);
        Assert.Throws<Phase3PersistenceException>(() => specificationLifecycle.ObserveFirstFrame(
            new FirstFrameObservation { FrameNumber = 0, TotalSizeBytes = 100 },
            started.Context.Fixture.CreatedAt.AddMinutes(3)));
    }

    private static OutputMeta ValidMeta(StartedContext started, double durationSeconds, string? stopReason = null) =>
        new()
        {
            SizeBytes = 1024,
            OutputFileExists = true,
            DurationSeconds = durationSeconds,
            OutputPath = started.Receipt.Specification.FrozenOutputFilePath,
            StopReason = stopReason,
        };

    private static StartedContext Start(
        long maxUses = 10,
        int slotCount = 1,
        TimeSpan? maxCumulativeDuration = null)
    {
        var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            maxUses: maxUses,
            slotCount: slotCount,
            maxCumulativeDuration: maxCumulativeDuration);
        var reservation = new RecurringOccurrenceReservationService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt,
            () => "task272-run-" + Guid.NewGuid().ToString("N"),
            () => "task272-use-" + Guid.NewGuid().ToString("N"))
            .Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceReservationStatus.Reserved, reservation.Status);

        var startCommit = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt,
            new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider())
            .Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.True(startCommit.Succeeded, $"status={startCommit.Status}; reason={startCommit.ReasonCode}");
        Assert.NotNull(startCommit.FirstCommitReceipt);
        return new StartedContext(
            context,
            startCommit.FirstCommitReceipt!,
            new SqliteRecurringLeaseLifecycleTransaction(context.Fixture.Store, startCommit.FirstCommitReceipt!));
    }

    private sealed class StartedContext : IDisposable
    {
        internal StartedContext(
            RecurringOccurrenceReservationTests.ReservationContext context,
            RecurringStartCommitReceipt receipt,
            SqliteRecurringLeaseLifecycleTransaction lifecycle)
        {
            Context = context;
            Receipt = receipt;
            Lifecycle = lifecycle;
        }

        internal RecurringOccurrenceReservationTests.ReservationContext Context { get; }
        internal RecurringStartCommitReceipt Receipt { get; }
        internal SqliteRecurringLeaseLifecycleTransaction Lifecycle { get; }

        public void Dispose() => Context.Dispose();
    }
}
