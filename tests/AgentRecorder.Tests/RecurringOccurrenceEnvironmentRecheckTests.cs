using System.Globalization;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringOccurrenceEnvironmentRecheckTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private const string UserSid = "S-1-5-21-262";
    private const string SessionBinding = "session-262";
    private const string StableDisplayFingerprint = "DISPLAY-FP-262";
    private const string TopologyDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void ExactHappyPathReturnsEligibleAndFreezesEverySpecificationField()
    {
        var fixture = CreateFixture();

        var decision = Evaluate(fixture);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckStatus.Eligible, decision.Status);
        Assert.Equal("", decision.ReasonCode);
        var specification = Assert.IsType<RecurringOccurrenceExecutionSpecification>(decision.ExecutionSpecification);
        Assert.Equal(fixture.Plan.Id, specification.PlanId);
        Assert.Equal(fixture.Occurrence.Id, specification.OccurrenceId);
        Assert.Equal(fixture.Candidate.Identity.Value, specification.OccurrenceIdentity);
        Assert.Equal(fixture.Lease.LeaseId, specification.LeaseId);
        Assert.Equal(fixture.Candidate.ScheduleRevision, specification.ScheduleRevision);
        Assert.Equal(fixture.Candidate.ScheduleDigest, specification.ScheduleDigest);
        Assert.Equal(fixture.Lease.ConfigurationRef.TimeZoneRulesDigest, specification.TimeZoneRulesDigest);
        Assert.Equal(fixture.Profile.ProfileId, specification.ProfileId);
        Assert.Equal(fixture.Profile.ProfileVersion, specification.ProfileVersion);
        Assert.Equal(fixture.Profile.ProfileDigest, specification.ProfileDigest);
        Assert.Equal(fixture.Lease.ConfigurationRef.ConfigurationDigest, specification.ConfigurationDigest);
        Assert.Equal(fixture.Lease.AuthorizationDigest, specification.LeaseAuthorizationDigest);
        Assert.Equal(fixture.Approval.ApprovalId, specification.LocalApprovalId);
        Assert.Equal(fixture.Approval.ApprovalDigest, specification.LocalApprovalDigest);
        Assert.Equal(fixture.Candidate.ScheduledStartUtc!.Value, specification.ScheduledStartUtc);
        Assert.Equal(fixture.Candidate.LatestStartUtc!.Value, specification.LatestStartUtc);
        Assert.Equal(fixture.Candidate.PlannedEndUtc!.Value, specification.PlannedEndUtc);
        Assert.Equal(fixture.Environment.NowUtc, specification.EvaluatedAtUtc);
        Assert.Equal(fixture.Profile.StableDisplayFingerprint, specification.StableDisplayFingerprint);
        Assert.Equal(fixture.Profile.DisplayBounds, specification.DisplayBounds);
        Assert.Equal(fixture.Profile.RegionWithinDisplay, specification.RegionWithinDisplay);
        Assert.Equal(fixture.Profile.VirtualScreenRegion, specification.VirtualScreenRegion);
        Assert.Equal(fixture.Profile.DpiX, specification.DpiX);
        Assert.Equal(fixture.Profile.DpiY, specification.DpiY);
        Assert.Equal(fixture.Profile.PhysicalWidth, specification.PhysicalWidth);
        Assert.Equal(fixture.Profile.PhysicalHeight, specification.PhysicalHeight);
        Assert.Equal(fixture.Profile.Orientation, specification.Orientation);
        Assert.Equal(fixture.Profile.TopologyDigest, specification.TopologyDigest);
        Assert.Equal(AuthorizedCaptureBackend.FfmpegRegion, specification.Backend);
        Assert.Equal(AuthorizedAudioMode.None, specification.AudioMode);
        Assert.Equal(fixture.Profile.Duration, specification.Duration);
        Assert.Equal(fixture.Profile.CountdownSeconds, specification.CountdownSeconds);
        Assert.Equal(StandingLeaseOutputPath.NormalizeDirectory(fixture.Profile.OutputDirectory), specification.NormalizedOutputDirectory);
        Assert.Equal(fixture.Profile.RenderOutputFileName(fixture.Candidate.Identity.Value, fixture.Candidate.ScheduledStartUtc.Value), specification.FrozenOutputFileName);
        Assert.Equal(fixture.Profile.ResolveOutputPath(fixture.Candidate.Identity.Value, fixture.Candidate.ScheduledStartUtc.Value), specification.FrozenOutputFilePath);
        Assert.Equal(AuthorizedOutputConflictPolicy.FailIfExists, specification.OutputConflictPolicy);
        Assert.Equal(UserSid, specification.ApprovedCurrentUserSid);
        Assert.Equal(SessionBinding, specification.ApprovedSessionBinding);
        Assert.Equal(specification.SpecificationDigest, RecurringOccurrenceExecutionSpecificationDigest.Compute(specification));
    }

    [Fact]
    public void SameInputIsDeterministicAndFilenameDoesNotFollowEvaluationNow()
    {
        var fixture = CreateFixture();
        var first = Evaluate(fixture);
        var second = Evaluate(fixture);
        var firstSpec = Assert.IsType<RecurringOccurrenceExecutionSpecification>(first.ExecutionSpecification);
        var secondSpec = Assert.IsType<RecurringOccurrenceExecutionSpecification>(second.ExecutionSpecification);

        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.ReasonCode, second.ReasonCode);
        Assert.Equal(firstSpec.SpecificationDigest, secondSpec.SpecificationDigest);
        Assert.Equal(firstSpec.FrozenOutputFileName, secondSpec.FrozenOutputFileName);
        Assert.Equal(firstSpec.FrozenOutputFilePath, secondSpec.FrozenOutputFilePath);

        var later = Evaluate(fixture with
        {
            Environment = CreateEnvironment(fixture, fixture.Candidate.LatestStartUtc!.Value),
        });
        var laterSpec = Assert.IsType<RecurringOccurrenceExecutionSpecification>(later.ExecutionSpecification);
        Assert.Equal(firstSpec.FrozenOutputFileName, laterSpec.FrozenOutputFileName);
        Assert.Equal(firstSpec.FrozenOutputFilePath, laterSpec.FrozenOutputFilePath);
        Assert.NotEqual(firstSpec.EvaluatedAtUtc, laterSpec.EvaluatedAtUtc);
        Assert.NotEqual(firstSpec.SpecificationDigest, laterSpec.SpecificationDigest);

        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            var chinese = Assert.IsType<RecurringOccurrenceExecutionSpecification>(
                Evaluate(fixture).ExecutionSpecification);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var english = Assert.IsType<RecurringOccurrenceExecutionSpecification>(
                Evaluate(fixture).ExecutionSpecification);
            Assert.Equal(chinese.SpecificationDigest, english.SpecificationDigest);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void WindowBoundariesUseInclusiveStartAndLatestAndBeforeWindowPrecedesEnvironment()
    {
        var fixture = CreateFixture();
        var scheduled = fixture.Candidate.ScheduledStartUtc!.Value;
        var latest = fixture.Candidate.LatestStartUtc!.Value;

        Assert.Equal(
            RecurringOccurrenceEnvironmentRecheckStatus.Eligible,
            Evaluate(fixture).Status);
        Assert.Equal(
            RecurringOccurrenceEnvironmentRecheckStatus.Eligible,
            Evaluate(fixture with { Environment = CreateEnvironment(fixture, latest) }).Status);

        var missed = Evaluate(fixture with
        {
            Environment = CreateEnvironment(fixture, latest.AddTicks(1)),
        });
        AssertDecision(
            missed,
            RecurringOccurrenceEnvironmentRecheckStatus.Missed,
            RecurringOccurrenceEnvironmentRecheckReasonCodes.MissedLatestStart);

        var beforeWindow = Evaluate(fixture with
        {
            Environment = new StandingLeaseExecutionEnvironment(
                scheduled.AddTicks(-1),
                UserSid,
                SessionBinding,
                isInteractiveDesktop: true),
        });
        AssertDecision(
            beforeWindow,
            RecurringOccurrenceEnvironmentRecheckStatus.BeforeWindow,
            RecurringOccurrenceEnvironmentRecheckReasonCodes.BeforeWindow);
    }

    [Fact]
    public void FutureValidApprovalIsAllowedOnceLeaseValidityAndOccurrenceWindowAreOpen()
    {
        var fixture = CreateFixture(
            validFromAt: BaseTime.AddMinutes(10),
            approvalAt: BaseTime.AddMinutes(5));

        Assert.True(fixture.Approval.ApprovedAtUtc < fixture.Lease.ValidFromUtc);
        Assert.Equal(
            RecurringOccurrenceEnvironmentRecheckStatus.Eligible,
            Evaluate(fixture with
            {
                Environment = CreateEnvironment(fixture, fixture.Candidate.ScheduledStartUtc!.Value),
            }).Status);

        var beforeApproval = Evaluate(fixture with
        {
            Environment = CreateEnvironment(fixture, fixture.Approval.ApprovedAtUtc.AddTicks(-1)),
        });
        AssertDecision(
            beforeApproval,
            RecurringOccurrenceEnvironmentRecheckStatus.Rejected,
            RecurringOccurrenceEnvironmentRecheckReasonCodes.TimeRelationInvalid);
    }

    [Fact]
    public void LateMaterializationAfterScheduledStartRemainsEligibleWithinGrace()
    {
        var fixture = CreateFixture();
        var scheduled = fixture.Candidate.ScheduledStartUtc!.Value;
        var latest = fixture.Candidate.LatestStartUtc!.Value;
        var lateOccurrence = PlanOccurrence.CreateFor(
            fixture.Plan,
            fixture.Candidate.Identity!.Value,
            fixture.Candidate.ScheduledStartUtc!.Value,
            fixture.Candidate.PlannedEndUtc!.Value,
            scheduled.AddMinutes(1));
        Assert.True(lateOccurrence.TryTransition(
            PlanOccurrenceStatus.Due,
            scheduled.AddMinutes(1).AddTicks(1)).Succeeded);
        Assert.True(lateOccurrence.TryTransition(PlanOccurrenceStatus.Rechecking, latest).Succeeded);

        var eligible = Evaluate(fixture with
        {
            Occurrence = lateOccurrence,
            Environment = CreateEnvironment(fixture, latest),
        });
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckStatus.Eligible, eligible.Status);

        var beforeOccurrenceSnapshot = Evaluate(fixture with
        {
            Occurrence = lateOccurrence,
            Environment = CreateEnvironment(fixture, latest.AddTicks(-1)),
        });
        AssertDecision(
            beforeOccurrenceSnapshot,
            RecurringOccurrenceEnvironmentRecheckStatus.Rejected,
            RecurringOccurrenceEnvironmentRecheckReasonCodes.TimeRelationInvalid);
    }

    [Fact]
    public void PlanResumedAfterScheduledStartRemainsEligibleWithinGrace()
    {
        var fixture = CreateFixture();
        var scheduled = fixture.Candidate.ScheduledStartUtc!.Value;
        var latest = fixture.Candidate.LatestStartUtc!.Value;
        Assert.True(fixture.Plan.TryTransition(PlanDefinitionStatus.Paused, scheduled.AddMinutes(1)).Succeeded);
        Assert.True(fixture.Plan.TryTransition(PlanDefinitionStatus.Enabled, scheduled.AddMinutes(1).AddTicks(1)).Succeeded);

        Assert.Equal(
            RecurringOccurrenceEnvironmentRecheckStatus.Eligible,
            Evaluate(fixture with
            {
                Environment = CreateEnvironment(fixture, latest),
            }).Status);

        var beforeResumptionSnapshot = Evaluate(fixture with
        {
            Environment = CreateEnvironment(fixture, scheduled.AddMinutes(1)),
        });
        AssertDecision(
            beforeResumptionSnapshot,
            RecurringOccurrenceEnvironmentRecheckStatus.Rejected,
            RecurringOccurrenceEnvironmentRecheckReasonCodes.TimeRelationInvalid);
    }

    [Fact]
    public void ApprovalMustFollowImmutableCreationAndStayBeforeLeaseEnd()
    {
        AssertRejected(
            CreateFixture(leaseCreatedAt: BaseTime.AddMinutes(6)),
            RecurringOccurrenceEnvironmentRecheckReasonCodes.TimeRelationInvalid);
        AssertRejected(
            CreateFixture(profileCreatedAt: BaseTime.AddMinutes(6)),
            RecurringOccurrenceEnvironmentRecheckReasonCodes.TimeRelationInvalid);
        AssertRejected(
            CreateFixture(bindingAt: BaseTime.AddMinutes(6)),
            RecurringOccurrenceEnvironmentRecheckReasonCodes.TimeRelationInvalid);

        var validUntil = new DateTimeOffset(2026, 9, 2, 11, 4, 0, TimeSpan.Zero);
        AssertRejected(
            CreateFixture(approvalAt: validUntil),
            RecurringOccurrenceEnvironmentRecheckReasonCodes.TimeRelationInvalid);
        AssertRejected(
            CreateFixture(approvalAt: validUntil.AddTicks(1)),
            RecurringOccurrenceEnvironmentRecheckReasonCodes.TimeRelationInvalid);
    }

    [Fact]
    public void ExactDurationBoundaryFitsAndLeaseValidityIsHalfOpen()
    {
        var fixture = CreateFixture();
        var atLatest = Evaluate(fixture with
        {
            Environment = CreateEnvironment(fixture, fixture.Candidate.LatestStartUtc!.Value),
        });
        Assert.Equal(RecurringOccurrenceEnvironmentRecheckStatus.Eligible, atLatest.Status);
        var specification = Assert.IsType<RecurringOccurrenceExecutionSpecification>(atLatest.ExecutionSpecification);
        Assert.Equal(specification.PlannedEndUtc, specification.LatestStartUtc.Add(specification.Duration));
        Assert.True(specification.PlannedEndUtc < fixture.Lease.ValidUntilUtc);

        var expired = Evaluate(fixture with
        {
            Environment = CreateEnvironment(fixture, fixture.Lease.ValidUntilUtc),
        });
        AssertDecision(
            expired,
            RecurringOccurrenceEnvironmentRecheckStatus.Blocked,
            RecurringOccurrenceEnvironmentRecheckReasonCodes.LeaseOutsideValidity);
    }

    [Fact]
    public void NonUtcRollbackAndAdditionOverflowFailClosed()
    {
        var fixture = CreateFixture();
        var nonUtc = Evaluate(fixture with
        {
            Environment = CreateEnvironment(
                fixture,
                fixture.Environment.NowUtc.ToOffset(TimeSpan.FromHours(8))),
        });
        AssertDecision(
            nonUtc,
            RecurringOccurrenceEnvironmentRecheckStatus.Rejected,
            RecurringOccurrenceEnvironmentRecheckReasonCodes.NonUtcTime);

        var rollback = Evaluate(fixture with
        {
            Environment = CreateEnvironment(fixture, fixture.Plan.CreatedAtUtc.AddTicks(-1)),
        });
        AssertDecision(
            rollback,
            RecurringOccurrenceEnvironmentRecheckStatus.Rejected,
            RecurringOccurrenceEnvironmentRecheckReasonCodes.TimeRelationInvalid);

        var overflow = CreateOverflowFixture();
        var overflowDecision = Evaluate(overflow);
        AssertDecision(
            overflowDecision,
            RecurringOccurrenceEnvironmentRecheckStatus.Rejected,
            RecurringOccurrenceEnvironmentRecheckReasonCodes.CandidateInvalid);
    }

    [Fact]
    public void ParentAndApprovalDriftAreRejectedBeforeEnvironmentChecks()
    {
        var fixture = CreateFixture();
        var wrongRevision = new RecurringOccurrenceCandidate(
            fixture.Candidate.PlanId,
            fixture.Candidate.ScheduleRevision + 1,
            fixture.Schedule,
            fixture.Candidate.LocalDate,
            fixture.Candidate.LocalWallClockTime,
            fixture.Candidate.Identity,
            fixture.Candidate.ScheduledStartUtc,
            fixture.Candidate.LatestStartUtc,
            fixture.Candidate.PlannedEndUtc,
            fixture.Candidate.ReasonCode,
            fixture.Candidate.ResolutionCode);
        AssertRejected(fixture with { Candidate = wrongRevision }, RecurringOccurrenceEnvironmentRecheckReasonCodes.ParentMismatch);

        var otherSchedule = RecurringPlanSchedule.CreateDaily(
            "UTC",
            fixture.Candidate.LocalDate,
            fixture.Candidate.LocalDate,
            fixture.Schedule.LocalWallClockTime,
            maximumOccurrences: 10,
            recordingDuration: fixture.Schedule.RecordingDuration,
            latestStartGrace: fixture.Schedule.LatestStartGrace + TimeSpan.FromSeconds(1));
        AssertRejected(fixture with { Schedule = otherSchedule }, RecurringOccurrenceEnvironmentRecheckReasonCodes.ParentMismatch);

        var nextProfile = RecurringFixedRegionProfileVersion.CreateNextVersion(
            fixture.Profile,
            CreateProfileSpecification(),
            fixture.Profile.CreatedAtUtc);
        AssertRejected(fixture with { Profile = nextProfile }, RecurringOccurrenceEnvironmentRecheckReasonCodes.ParentMismatch);

        var wrongTimeZoneDigest = new RecurringPlanConfigurationRef(
            fixture.Plan.Id,
            fixture.Lease.ConfigurationRef.ScheduleRevision,
            fixture.Lease.ConfigurationRef.ScheduleDigest,
            RecurringTimeZoneRulesDigest.Prefix + new string('b', 64),
            fixture.Profile.Reference);
        var wrongLease = RecurringConsentLease.CreatePending(
            fixture.Lease.LeaseId,
            wrongTimeZoneDigest,
            fixture.Lease.ValidFromUtc,
            fixture.Lease.ValidUntilUtc,
            fixture.Lease.AuthorizedPlanLatestEndUtc,
            fixture.Lease.PerRunDuration,
            fixture.Lease.MaxUses,
            fixture.Lease.MaxCumulativeDuration,
            fixture.Lease.CreatedAtUtc);
        Assert.True(wrongLease.TryTransition(ConsentLeaseStatus.Active, fixture.Lease.UpdatedAtUtc).Succeeded);
        AssertRejected(fixture with { Lease = wrongLease }, RecurringOccurrenceEnvironmentRecheckReasonCodes.ParentMismatch);

        var wrongApproval = RecurringLeaseLocalApprovalEvidence.Rehydrate(
            fixture.Approval.ApprovalId,
            "other-lease",
            fixture.Plan.Id,
            fixture.Lease.ConfigurationRef.ConfigurationDigest,
            fixture.Lease.AuthorizationDigest,
            UserSid,
            SessionBinding,
            fixture.Approval.ApprovedAtUtc,
            fixture.Approval.ApprovalKind,
            fixture.Approval.ApprovalVersion,
            RecurringLeaseLocalApprovalDigest.Compute(
                fixture.Approval.ApprovalId,
                "other-lease",
                fixture.Plan.Id,
                fixture.Lease.ConfigurationRef.ConfigurationDigest,
                fixture.Lease.AuthorizationDigest,
                UserSid,
                SessionBinding,
                fixture.Approval.ApprovedAtUtc,
                fixture.Approval.ApprovalKind,
                fixture.Approval.ApprovalVersion));
        AssertRejected(fixture with { Approval = wrongApproval }, RecurringOccurrenceEnvironmentRecheckReasonCodes.ApprovalInvalid);
    }

    [Fact]
    public void LifecycleAndTerminalStatesHaveStableDecisions()
    {
        var fixture = CreateFixture();
        var oneTimePlan = new PlanDefinition(fixture.Plan.Id, isOneTime: true, fixture.Plan.CreatedAtUtc);
        AssertDecision(
            Evaluate(fixture with { Plan = oneTimePlan }),
            RecurringOccurrenceEnvironmentRecheckStatus.Rejected,
            RecurringOccurrenceEnvironmentRecheckReasonCodes.PlanNotPeriodic);

        var draftPlan = new PlanDefinition(fixture.Plan.Id, isOneTime: false, fixture.Plan.CreatedAtUtc);
        AssertDecision(
            Evaluate(fixture with { Plan = draftPlan }),
            RecurringOccurrenceEnvironmentRecheckStatus.Blocked,
            RecurringOccurrenceEnvironmentRecheckReasonCodes.PlanNotEnabled);

        Assert.True(fixture.Plan.TryTransition(PlanDefinitionStatus.Paused, fixture.Lease.UpdatedAtUtc.AddTicks(1)).Succeeded);
        AssertDecision(
            Evaluate(fixture),
            RecurringOccurrenceEnvironmentRecheckStatus.Blocked,
            RecurringOccurrenceEnvironmentRecheckReasonCodes.PlanNotEnabled);

        Assert.True(fixture.Plan.TryTransition(PlanDefinitionStatus.Enabled, fixture.Lease.UpdatedAtUtc.AddTicks(2)).Succeeded);

        var revoked = fixture.Lease;
        Assert.True(revoked.TryTransition(ConsentLeaseStatus.Revoked, fixture.Lease.UpdatedAtUtc.AddTicks(3)).Succeeded);
        AssertDecision(
            Evaluate(fixture with { Lease = revoked }),
            RecurringOccurrenceEnvironmentRecheckStatus.Blocked,
            RecurringOccurrenceEnvironmentRecheckReasonCodes.LeaseNotActive);

        var scheduledOccurrence = new PlanOccurrence(
            fixture.Occurrence.Id,
            fixture.Plan.Id,
            fixture.Occurrence.WindowStartUtc,
            fixture.Occurrence.WindowEndUtc,
            fixture.Occurrence.CreatedAtUtc);
        AssertDecision(
            Evaluate(fixture with { Occurrence = scheduledOccurrence }),
            RecurringOccurrenceEnvironmentRecheckStatus.Blocked,
            RecurringOccurrenceEnvironmentRecheckReasonCodes.OccurrenceNotRechecking);

        var blockedOccurrence = new PlanOccurrence(
            fixture.Occurrence.Id,
            fixture.Plan.Id,
            fixture.Occurrence.WindowStartUtc,
            fixture.Occurrence.WindowEndUtc,
            fixture.Occurrence.CreatedAtUtc);
        Assert.True(blockedOccurrence.TryTransition(
            PlanOccurrenceStatus.Blocked,
            fixture.Occurrence.UpdatedAtUtc.AddTicks(1),
            terminalReasonCode: "safety_stop").Succeeded);
        AssertDecision(
            Evaluate(fixture with { Occurrence = blockedOccurrence }),
            RecurringOccurrenceEnvironmentRecheckStatus.AlreadyTerminal,
            RecurringOccurrenceEnvironmentRecheckReasonCodes.AlreadyTerminal);

        var completedOccurrence = new PlanOccurrence(
            fixture.Occurrence.Id,
            fixture.Plan.Id,
            fixture.Occurrence.WindowStartUtc,
            fixture.Occurrence.WindowEndUtc,
            fixture.Occurrence.CreatedAtUtc);
        Assert.True(completedOccurrence.TryTransition(PlanOccurrenceStatus.Due, fixture.Occurrence.UpdatedAtUtc.AddTicks(1)).Succeeded);
        Assert.True(completedOccurrence.TryTransition(PlanOccurrenceStatus.Rechecking, fixture.Occurrence.UpdatedAtUtc.AddTicks(2)).Succeeded);
        Assert.True(completedOccurrence.TryTransition(PlanOccurrenceStatus.Authorized, fixture.Occurrence.UpdatedAtUtc.AddTicks(3)).Succeeded);
        Assert.True(completedOccurrence.TryCreateRun("run-262", fixture.Occurrence.UpdatedAtUtc.AddTicks(4)).Succeeded);
        Assert.True(completedOccurrence.TryTransition(PlanOccurrenceStatus.Completed, fixture.Occurrence.UpdatedAtUtc.AddTicks(5)).Succeeded);
        AssertDecision(
            Evaluate(fixture with { Occurrence = completedOccurrence }),
            RecurringOccurrenceEnvironmentRecheckStatus.AlreadyTerminal,
            RecurringOccurrenceEnvironmentRecheckReasonCodes.AlreadyTerminal);
    }

    [Fact]
    public void RunCreatedShapeIsLegalButCannotRecheckAgain()
    {
        var fixture = CreateFixture();
        var runCreated = CreateRunCreatedOccurrence(fixture, "run-created-262");

        AssertDecision(
            Evaluate(fixture with { Occurrence = runCreated }),
            RecurringOccurrenceEnvironmentRecheckStatus.Blocked,
            RecurringOccurrenceEnvironmentRecheckReasonCodes.OccurrenceNotRechecking);
    }

    [Fact]
    public void AbnormalTerminalShapesWithOrWithoutRunIdsReplayAsAlreadyTerminal()
    {
        foreach (var status in new[]
                 {
                     PlanOccurrenceStatus.Blocked,
                     PlanOccurrenceStatus.Cancelled,
                     PlanOccurrenceStatus.Expired,
                 })
        {
            var withRun = CreateRunCreatedOccurrence(CreateFixture(), "run-" + status);
            Assert.True(withRun.TryTransition(
                status,
                withRun.UpdatedAtUtc.AddTicks(1),
                terminalReasonCode: "terminal-" + status).Succeeded);
            AssertDecision(
                Evaluate(CreateFixture() with { Occurrence = withRun }),
                RecurringOccurrenceEnvironmentRecheckStatus.AlreadyTerminal,
                RecurringOccurrenceEnvironmentRecheckReasonCodes.AlreadyTerminal);

            var withoutRunFixture = CreateFixture();
            var withoutRun = PlanOccurrence.CreateFor(
                withoutRunFixture.Plan,
                withoutRunFixture.Occurrence.Id,
                withoutRunFixture.Occurrence.WindowStartUtc,
                withoutRunFixture.Occurrence.WindowEndUtc,
                withoutRunFixture.Occurrence.CreatedAtUtc);
            Assert.True(withoutRun.TryTransition(
                status,
                withoutRunFixture.Occurrence.UpdatedAtUtc.AddTicks(1),
                terminalReasonCode: "terminal-" + status).Succeeded);
            AssertDecision(
                Evaluate(withoutRunFixture with { Occurrence = withoutRun }),
                RecurringOccurrenceEnvironmentRecheckStatus.AlreadyTerminal,
                RecurringOccurrenceEnvironmentRecheckReasonCodes.AlreadyTerminal);
        }
    }

    [Fact]
    public void InvalidOccurrenceShapesAreRejectedByDomainRehydrationOrPolicy()
    {
        var fixture = CreateFixture();
        AssertInvalidOccurrenceRehydration(fixture, PlanOccurrenceStatus.Missed, "run-invalid-262", "missed");
        AssertInvalidOccurrenceRehydration(fixture, PlanOccurrenceStatus.Completed, null, null);
        AssertInvalidOccurrenceRehydration(fixture, PlanOccurrenceStatus.RunCreated, null, null);
        AssertInvalidOccurrenceRehydration(fixture, (PlanOccurrenceStatus)999, null, null);
    }

    [Fact]
    public void TerminalReplayIgnoresLaterPlanPauseAndLeaseRevocation()
    {
        var fixture = CreateFixture();
        var terminalOccurrence = PlanOccurrence.CreateFor(
            fixture.Plan,
            fixture.Occurrence.Id,
            fixture.Occurrence.WindowStartUtc,
            fixture.Occurrence.WindowEndUtc,
            fixture.Occurrence.CreatedAtUtc);
        Assert.True(terminalOccurrence.TryTransition(
            PlanOccurrenceStatus.Blocked,
            fixture.Occurrence.UpdatedAtUtc.AddTicks(1),
            terminalReasonCode: "safety_stop").Succeeded);
        Assert.True(fixture.Plan.TryTransition(
            PlanDefinitionStatus.Paused,
            fixture.Environment.NowUtc.AddTicks(1)).Succeeded);
        Assert.True(fixture.Lease.TryTransition(
            ConsentLeaseStatus.Revoked,
            fixture.Environment.NowUtc.AddTicks(1)).Succeeded);

        AssertDecision(
            Evaluate(fixture with
            {
                Occurrence = terminalOccurrence,
                Environment = CreateEnvironment(fixture, fixture.Environment.NowUtc.AddTicks(1)),
            }),
            RecurringOccurrenceEnvironmentRecheckStatus.AlreadyTerminal,
            RecurringOccurrenceEnvironmentRecheckReasonCodes.AlreadyTerminal);
    }

    [Fact]
    public void EnvironmentMetadataFailuresUseSharedFixedRegionValidatorReasons()
    {
        var fixture = CreateFixture();
        var profile = fixture.Profile;
        var display = ValidDisplay(profile);
        var cases = new[]
        {
            (CreateEnvironment(fixture, fixture.Environment.NowUtc, sid: "S-1-5-21-other"), "execution_environment_mismatch"),
            (CreateEnvironment(fixture, fixture.Environment.NowUtc, session: "other-session"), "execution_environment_mismatch"),
            (CreateEnvironment(fixture, fixture.Environment.NowUtc, interactive: false), "execution_environment_mismatch"),
            (CreateEnvironment(fixture, fixture.Environment.NowUtc, displays: Array.Empty<StandingLeaseDisplayMetadata>()), "execution_display_identity_unavailable"),
            (CreateEnvironment(fixture, fixture.Environment.NowUtc, displays: new[] { display with { StableDisplayFingerprint = null, IdentityStatus = DisplayIdentityResolutionStatus.Unresolved } }), "execution_display_identity_unavailable"),
            (CreateEnvironment(fixture, fixture.Environment.NowUtc, displays: new[] { display with { StableDisplayFingerprint = "OTHER-DISPLAY" } }), "execution_display_missing"),
            (CreateEnvironment(fixture, fixture.Environment.NowUtc, displays: new[] { display, display with { PublicId = "display-2" } }), "execution_display_ambiguous"),
            (CreateEnvironment(fixture, fixture.Environment.NowUtc, displays: new[] { display with { PhysicalBounds = null } }), "execution_display_metadata_unavailable"),
            (CreateEnvironment(fixture, fixture.Environment.NowUtc, displays: new[] { display with { DpiX = display.DpiX + 1 } }), "execution_display_metadata_mismatch"),
            (CreateEnvironment(fixture, fixture.Environment.NowUtc, topology: ""), "execution_topology_unavailable"),
            (CreateEnvironment(fixture, fixture.Environment.NowUtc, topology: "other-topology"), "execution_topology_mismatch"),
        };

        foreach (var (environment, reason) in cases)
        {
            AssertDecision(
                Evaluate(fixture with { Environment = environment }),
                RecurringOccurrenceEnvironmentRecheckStatus.Blocked,
                reason);
        }
    }

    [Fact]
    public void OutputReadinessFailuresUseSharedValidatorReasonsWithoutFilesystemAccess()
    {
        var fixture = CreateFixture();
        var output = fixture.Environment.OutputFileSystem!;
        var cases = new[]
        {
            (output with { NormalizedOutputDirectory = "C:\\other\\" }, "execution_output_directory_mismatch"),
            (output with { FrozenOutputFilePath = "C:\\other\\capture.mp4" }, "execution_output_file_path_mismatch"),
            (output with { DirectoryExists = false }, "execution_output_directory_unavailable"),
            (output with { DirectoryWritable = false }, "execution_output_directory_unwritable"),
            (output with { FrozenFileExists = true }, "execution_output_file_exists"),
            (output with { FreeSpaceAvailable = false }, "execution_disk_space_unavailable"),
            (output with { RequiredFreeBytes = output.RequiredFreeBytes + 1 }, "execution_disk_space_unavailable"),
            (output with { AvailableFreeBytes = output.RequiredFreeBytes - 1 }, "execution_disk_space_insufficient"),
        };

        foreach (var (variant, reason) in cases)
        {
            AssertDecision(
                Evaluate(fixture with
                {
                    Environment = CreateEnvironment(fixture, fixture.Environment.NowUtc, output: variant),
                }),
                RecurringOccurrenceEnvironmentRecheckStatus.Blocked,
                reason);
        }
    }

    [Fact]
    public void RegionProjectionFailureIsCoveredByTheSharedValidator()
    {
        var fixture = CreateFixture();
        var profile = fixture.Profile;
        var requirements = new FixedRegionExecutionEnvironmentRequirements(
            UserSid,
            SessionBinding,
            profile.StableDisplayFingerprint,
            profile.DisplayBounds,
            profile.RegionWithinDisplay,
            new AuthorizedPhysicalRectangle(profile.VirtualScreenRegion.X + 1, profile.VirtualScreenRegion.Y, profile.VirtualScreenRegion.Width, profile.VirtualScreenRegion.Height),
            profile.DpiX,
            profile.DpiY,
            profile.PhysicalWidth,
            profile.PhysicalHeight,
            profile.Orientation,
            profile.TopologyDigest,
            StandingLeaseOutputPath.NormalizeDirectory(profile.OutputDirectory),
            profile.ResolveOutputPath(fixture.Candidate.Identity.Value, fixture.Candidate.ScheduledStartUtc!.Value),
            profile.OutputConflictPolicy,
            profile.Duration);

        Assert.False(StandingLeaseExecutionEnvironmentValidator.TryValidate(
            fixture.Environment,
            requirements,
            out var reason));
        Assert.Equal("execution_region_invalid", reason);
    }

    [Fact]
    public void SpecificationDigestHasGoldenVectorAndChangesForSecurityCriticalInputs()
    {
        var fixture = CreateFixture();
        var baseline = Assert.IsType<RecurringOccurrenceExecutionSpecification>(
            Evaluate(fixture).ExecutionSpecification);

        Assert.Equal(
            "recurring-occurrence-execution-spec/v1:91dad2721ae8c65450087f14d1e1f5e9fad0c2d5221f2012138f53c20f0cb7a5",
            baseline.SpecificationDigest);

        var changedEvaluationTime = Assert.IsType<RecurringOccurrenceExecutionSpecification>(
            Evaluate(fixture with
            {
                Environment = CreateEnvironment(fixture, fixture.Candidate.LatestStartUtc!.Value),
            }).ExecutionSpecification);
        Assert.NotEqual(baseline.SpecificationDigest, changedEvaluationTime.SpecificationDigest);
        Assert.Equal(baseline.FrozenOutputFileName, changedEvaluationTime.FrozenOutputFileName);

        var changedApproval = RecurringLeaseLocalApprovalEvidence.Rehydrate(
            "approval-262-other",
            fixture.Lease.LeaseId,
            fixture.Plan.Id,
            fixture.Lease.ConfigurationRef.ConfigurationDigest,
            fixture.Lease.AuthorizationDigest,
            UserSid,
            SessionBinding,
            fixture.Approval.ApprovedAtUtc,
            fixture.Approval.ApprovalKind,
            fixture.Approval.ApprovalVersion,
            RecurringLeaseLocalApprovalDigest.Compute(
                "approval-262-other",
                fixture.Lease.LeaseId,
                fixture.Plan.Id,
                fixture.Lease.ConfigurationRef.ConfigurationDigest,
                fixture.Lease.AuthorizationDigest,
                UserSid,
                SessionBinding,
                fixture.Approval.ApprovedAtUtc,
                fixture.Approval.ApprovalKind,
                fixture.Approval.ApprovalVersion));
        var approvalVariant = Assert.IsType<RecurringOccurrenceExecutionSpecification>(
            Evaluate(fixture with { Approval = changedApproval }).ExecutionSpecification);
        Assert.NotEqual(baseline.SpecificationDigest, approvalVariant.SpecificationDigest);
    }

    [Fact]
    public void PolicyDoesNotMutateAggregatesOrCreateExecutionSideEffects()
    {
        var fixture = CreateFixture();
        var before = (
            PlanStatus: fixture.Plan.Status,
            PlanVersion: fixture.Plan.Version,
            PlanUpdatedAtUtc: fixture.Plan.UpdatedAtUtc,
            OccurrenceStatus: fixture.Occurrence.Status,
            OccurrenceVersion: fixture.Occurrence.Version,
            OccurrenceUpdatedAtUtc: fixture.Occurrence.UpdatedAtUtc,
            OccurrenceRunId: fixture.Occurrence.RunId,
            LeaseStatus: fixture.Lease.Status,
            LeaseVersion: fixture.Lease.Version,
            LeaseUpdatedAtUtc: fixture.Lease.UpdatedAtUtc);

        var decision = Evaluate(fixture);

        Assert.Equal(RecurringOccurrenceEnvironmentRecheckStatus.Eligible, decision.Status);
        Assert.Equal(before.PlanStatus, fixture.Plan.Status);
        Assert.Equal(before.PlanVersion, fixture.Plan.Version);
        Assert.Equal(before.PlanUpdatedAtUtc, fixture.Plan.UpdatedAtUtc);
        Assert.Equal(before.OccurrenceStatus, fixture.Occurrence.Status);
        Assert.Equal(before.OccurrenceVersion, fixture.Occurrence.Version);
        Assert.Equal(before.OccurrenceUpdatedAtUtc, fixture.Occurrence.UpdatedAtUtc);
        Assert.Equal(before.OccurrenceRunId, fixture.Occurrence.RunId);
        Assert.Equal(before.LeaseStatus, fixture.Lease.Status);
        Assert.Equal(before.LeaseVersion, fixture.Lease.Version);
        Assert.Equal(before.LeaseUpdatedAtUtc, fixture.Lease.UpdatedAtUtc);
    }

    private static RecurringOccurrenceEnvironmentRecheckDecision Evaluate(TestFixture fixture) =>
        RecurringOccurrenceEnvironmentRecheckPolicy.Evaluate(fixture.ToRequest());

    private static void AssertRejected(TestFixture fixture, string reason) =>
        AssertDecision(
            Evaluate(fixture),
            RecurringOccurrenceEnvironmentRecheckStatus.Rejected,
            reason);

    private static void AssertDecision(
        RecurringOccurrenceEnvironmentRecheckDecision decision,
        RecurringOccurrenceEnvironmentRecheckStatus status,
        string reason)
    {
        Assert.Equal(status, decision.Status);
        Assert.Equal(reason, decision.ReasonCode);
        Assert.Null(decision.ExecutionSpecification);
    }

    private static PlanOccurrence CreateRunCreatedOccurrence(TestFixture fixture, string runId)
    {
        var occurrence = PlanOccurrence.CreateFor(
            fixture.Plan,
            fixture.Occurrence.Id,
            fixture.Occurrence.WindowStartUtc,
            fixture.Occurrence.WindowEndUtc,
            fixture.Occurrence.CreatedAtUtc);
        var transitionAt = fixture.Occurrence.UpdatedAtUtc.AddTicks(1);
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Due, transitionAt).Succeeded);
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Rechecking, transitionAt.AddTicks(1)).Succeeded);
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Authorized, transitionAt.AddTicks(2)).Succeeded);
        Assert.True(occurrence.TryCreateRun(runId, transitionAt.AddTicks(3)).Succeeded);
        return occurrence;
    }

    private static void AssertInvalidOccurrenceRehydration(
        TestFixture fixture,
        PlanOccurrenceStatus status,
        string? runId,
        string? terminalReasonCode)
    {
        Assert.Throws<Phase3DomainException>(() => PlanOccurrence.Rehydrate(
            fixture.Occurrence.Id,
            fixture.Plan.Id,
            fixture.Occurrence.WindowStartUtc,
            fixture.Occurrence.WindowEndUtc,
            fixture.Occurrence.CreatedAtUtc,
            status,
            runId,
            terminalReasonCode,
            fixture.Occurrence.UpdatedAtUtc,
            version: 0));
    }

    private static TestFixture CreateFixture(
        DateTimeOffset? profileCreatedAt = null,
        DateTimeOffset? bindingAt = null,
        DateTimeOffset? leaseCreatedAt = null,
        DateTimeOffset? validFromAt = null,
        DateTimeOffset? approvalAt = null)
    {
        var plan = new PlanDefinition("periodic-plan-262", isOneTime: false, BaseTime);
        Assert.True(plan.TryTransition(PlanDefinitionStatus.Enabled, BaseTime.AddMinutes(1)).Succeeded);

        var schedule = RecurringPlanSchedule.CreateDaily(
            "UTC",
            new DateOnly(2026, 9, 2),
            new DateOnly(2026, 9, 2),
            new TimeOnly(10, 0),
            maximumOccurrences: 10,
            recordingDuration: TimeSpan.FromMinutes(2),
            latestStartGrace: TimeSpan.FromMinutes(2));
        var candidate = new RecurringOccurrenceCalculator()
            .CalculateCandidateForAuthorization(plan.Id, 1, schedule, new DateOnly(2026, 9, 2));
        var occurrence = PlanOccurrence.CreateFor(
            plan,
            candidate.Identity.Value,
            candidate.ScheduledStartUtc!.Value,
            candidate.PlannedEndUtc!.Value,
            BaseTime.AddMinutes(2));
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Due, BaseTime.AddMinutes(2).AddTicks(1)).Succeeded);
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Rechecking, BaseTime.AddMinutes(3)).Succeeded);

        var profile = RecurringFixedRegionProfileVersion.CreateVersion1(
            "profile-262",
            profileCreatedAt ?? BaseTime,
            CreateProfileSpecification());
        var binding = new RecurringPlanProfileBinding(
            plan.Id,
            profile.Reference,
            bindingAt ?? BaseTime.AddMinutes(2));
        var configuration = new RecurringPlanConfigurationRef(
            plan.Id,
            1,
            schedule.CanonicalDigest,
            RecurringTimeZoneRulesDigest.Compute(schedule.TimeZoneInfo),
            profile.Reference);
        var lease = RecurringConsentLease.CreatePending(
            "lease-262",
            configuration,
            validFromAt ?? BaseTime.AddMinutes(3),
            candidate.PlannedEndUtc.Value.AddHours(1),
            candidate.PlannedEndUtc.Value,
            profile.Duration,
            maxUses: 10,
            maxCumulativeDuration: TimeSpan.FromMinutes(20),
            leaseCreatedAt ?? BaseTime.AddMinutes(3));
        Assert.True(lease.TryTransition(ConsentLeaseStatus.Active, lease.CreatedAtUtc.AddMinutes(1)).Succeeded);

        var approvalReceipt = RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter(
            "approval-262",
            lease.LeaseId,
            plan.Id,
            configuration.ConfigurationDigest,
            lease.AuthorizationDigest,
            UserSid,
            SessionBinding,
            approvalAt ?? BaseTime.AddMinutes(5));
        var approval = RecurringLeaseLocalApprovalEvidence.Rehydrate(
            approvalReceipt.ApprovalId,
            approvalReceipt.LeaseId,
            approvalReceipt.PlanId,
            approvalReceipt.ConfigurationDigest,
            approvalReceipt.AuthorizationDigest,
            approvalReceipt.CurrentUserSid,
            approvalReceipt.SessionBinding,
            approvalReceipt.ApprovedAtUtc,
            approvalReceipt.ApprovalKind,
            approvalReceipt.ApprovalVersion,
            approvalReceipt.ApprovalDigest);
        var environment = CreateEnvironmentCore(
            profile,
            candidate.Identity.Value,
            candidate.ScheduledStartUtc.Value,
            UserSid,
            SessionBinding,
            displays: null,
            topology: profile.TopologyDigest,
            output: null,
            isInteractiveDesktop: true);
        return new(
            plan,
            schedule,
            occurrence,
            lease,
            candidate,
            binding,
            profile,
            approval,
            environment);
    }

    private static TestFixture CreateOverflowFixture()
    {
        var planTime = new DateTimeOffset(9999, 12, 31, 20, 0, 0, TimeSpan.Zero);
        var plan = new PlanDefinition("periodic-overflow-262", isOneTime: false, planTime);
        Assert.True(plan.TryTransition(PlanDefinitionStatus.Enabled, planTime.AddMinutes(1)).Succeeded);
        var schedule = RecurringPlanSchedule.CreateDaily(
            "UTC",
            new DateOnly(9999, 12, 31),
            new DateOnly(9999, 12, 31),
            new TimeOnly(23, 56),
            maximumOccurrences: 1,
            recordingDuration: TimeSpan.FromMinutes(2),
            latestStartGrace: TimeSpan.FromMinutes(2));
        var local = new DateOnly(9999, 12, 31);
        var identity = RecurringOccurrenceIdentity.Create(plan.Id, 1, schedule, local, schedule.LocalWallClockTime);
        var scheduled = new DateTimeOffset(9999, 12, 31, 23, 56, 0, TimeSpan.Zero);
        var latest = scheduled.AddMinutes(2);
        var plannedEnd = DateTimeOffset.MaxValue;
        var candidate = new RecurringOccurrenceCandidate(
            plan.Id,
            1,
            schedule,
            local,
            schedule.LocalWallClockTime,
            identity,
            scheduled,
            latest,
            plannedEnd,
            "",
            "schedule_exact");
        var occurrence = PlanOccurrence.CreateFor(
            plan,
            identity.Value,
            scheduled,
            plannedEnd,
            planTime.AddMinutes(2));
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Due, planTime.AddMinutes(2).AddTicks(1)).Succeeded);
        Assert.True(occurrence.TryTransition(PlanOccurrenceStatus.Rechecking, planTime.AddMinutes(3)).Succeeded);
        var profile = RecurringFixedRegionProfileVersion.CreateVersion1(
            "profile-overflow-262",
            planTime,
            CreateProfileSpecification(duration: TimeSpan.FromMinutes(2)));
        var binding = new RecurringPlanProfileBinding(plan.Id, profile.Reference, planTime.AddMinutes(2));
        var configuration = new RecurringPlanConfigurationRef(
            plan.Id,
            1,
            schedule.CanonicalDigest,
            RecurringTimeZoneRulesDigest.Compute(schedule.TimeZoneInfo),
            profile.Reference);
        var lease = RecurringConsentLease.CreatePending(
            "lease-overflow-262",
            configuration,
            planTime.AddMinutes(3),
            DateTimeOffset.MaxValue,
            DateTimeOffset.MaxValue.AddTicks(-1),
            profile.Duration,
            1,
            profile.Duration,
            planTime.AddMinutes(3));
        Assert.True(lease.TryTransition(ConsentLeaseStatus.Active, planTime.AddMinutes(4)).Succeeded);
        var receipt = RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter(
            "approval-overflow-262",
            lease.LeaseId,
            plan.Id,
            configuration.ConfigurationDigest,
            lease.AuthorizationDigest,
            UserSid,
            SessionBinding,
            planTime.AddMinutes(5));
        var approval = RecurringLeaseLocalApprovalEvidence.Rehydrate(
            receipt.ApprovalId,
            receipt.LeaseId,
            receipt.PlanId,
            receipt.ConfigurationDigest,
            receipt.AuthorizationDigest,
            receipt.CurrentUserSid,
            receipt.SessionBinding,
            receipt.ApprovedAtUtc,
            receipt.ApprovalKind,
            receipt.ApprovalVersion,
            receipt.ApprovalDigest);
        return new(
            plan,
            schedule,
            occurrence,
            lease,
            candidate,
            binding,
            profile,
            approval,
            CreateEnvironmentCore(
                profile,
                candidate.Identity.Value,
                scheduled,
                UserSid,
                SessionBinding,
                displays: null,
                topology: profile.TopologyDigest,
                output: null,
                isInteractiveDesktop: true,
                now: DateTimeOffset.MaxValue.AddMinutes(-1)));
    }

    private static StandingLeaseExecutionEnvironment CreateEnvironment(
        TestFixture fixture,
        DateTimeOffset now,
        string? sid = null,
        string? session = null,
        bool interactive = true,
        IReadOnlyList<StandingLeaseDisplayMetadata>? displays = null,
        string? topology = null,
        StandingLeaseOutputFileSystemSnapshot? output = null)
    {
        return CreateEnvironmentCore(
            fixture.Profile,
            fixture.Candidate.Identity.Value,
            fixture.Candidate.ScheduledStartUtc!.Value,
            sid ?? UserSid,
            session ?? SessionBinding,
            displays,
            topology ?? fixture.Profile.TopologyDigest,
            output,
            interactive,
            now);
    }

    private static StandingLeaseExecutionEnvironment CreateEnvironmentCore(
        RecurringFixedRegionProfileVersion profile,
        string occurrenceIdentity,
        DateTimeOffset scheduledStartUtc,
        string sid,
        string session,
        IReadOnlyList<StandingLeaseDisplayMetadata>? displays,
        string? topology,
        StandingLeaseOutputFileSystemSnapshot? output,
        bool isInteractiveDesktop,
        DateTimeOffset? now = null)
    {
        var normalizedDirectory = StandingLeaseOutputPath.NormalizeDirectory(profile.OutputDirectory);
        var frozenPath = profile.ResolveOutputPath(occurrenceIdentity, scheduledStartUtc);
        var required = RecordingPreflightChecker.RequiredFreeSpaceBytes(profile.Duration);
        var defaultOutput = new StandingLeaseOutputFileSystemSnapshot(
            normalizedDirectory,
            frozenPath,
            directoryExists: true,
            frozenFileExists: false,
            directoryWritable: true,
            freeSpaceAvailable: true,
            availableFreeBytes: required,
            requiredFreeBytes: required);
        var defaultDisplay = ValidDisplay(profile);
        return new StandingLeaseExecutionEnvironment(
            now ?? scheduledStartUtc,
            sid,
            session,
            isInteractiveDesktop,
            displays ?? new[] { defaultDisplay },
            topology,
            output ?? defaultOutput);
    }

    private static StandingLeaseDisplayMetadata ValidDisplay(
        RecurringFixedRegionProfileVersion profile) =>
        new(
            "display-1",
            profile.StableDisplayFingerprint,
            DisplayIdentityResolutionStatus.Resolved,
            profile.DisplayBounds,
            profile.DpiX,
            profile.DpiY,
            profile.PhysicalWidth,
            profile.PhysicalHeight,
            profile.Orientation);

    private static RecurringFixedRegionProfileSpecification CreateProfileSpecification(
        TimeSpan? duration = null) =>
        new(
            AuthorizedScopeTargetType.FixedRegion,
            RecurringFixedRegionRebindPolicy.ExactMatchOnly,
            AuthorizedCaptureSemantics.DesktopRegion,
            AuthorizedCoordinateSpace.PhysicalVirtualScreen,
            AuthorizedDisplayIdentityStatus.Resolved,
            StableDisplayFingerprint,
            new AuthorizedPhysicalRectangle(-100, 50, 1920, 1080),
            new AuthorizedPhysicalRectangle(10, 20, 640, 480),
            96,
            144,
            1920,
            1080,
            AuthorizedDisplayOrientation.Landscape,
            TopologyDigest,
            AuthorizedCaptureBackend.FfmpegRegion,
            AuthorizedAudioMode.None,
            duration ?? TimeSpan.FromMinutes(2),
            3,
            Path.Combine(Path.GetTempPath(), "task262-output-" + StableDisplayFingerprint),
            "recurring-demo",
            AuthorizedOutputConflictPolicy.FailIfExists,
            AuthorizedWakePolicy.NaturalWakeOnly,
            AuthorizedDesktopRequirement.InteractiveDesktopRequired);

    private sealed record TestFixture(
        PlanDefinition Plan,
        RecurringPlanSchedule Schedule,
        PlanOccurrence Occurrence,
        RecurringConsentLease Lease,
        RecurringOccurrenceCandidate Candidate,
        RecurringPlanProfileBinding ProfileBinding,
        RecurringFixedRegionProfileVersion Profile,
        RecurringLeaseLocalApprovalEvidence Approval,
        StandingLeaseExecutionEnvironment Environment)
    {
        internal RecurringOccurrenceEnvironmentRecheckRequest ToRequest() => new(
            Plan,
            Schedule,
            Occurrence,
            Lease,
            Candidate,
            ProfileBinding,
            Profile,
            Approval,
            Environment);
    }
}
