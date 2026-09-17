using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringLeaseCaptureExecutionSessionTests
{
    [Fact]
    public void AutoCallbacksSettleNaturalCaptureAndDisposeExactlyOnce()
    {
        using var harness = CreateHarness();
        harness.Backend.StopMeta = ValidMeta(harness, 1, "natural");

        harness.Backend.RaiseFirstFrame();
        Assert.Equal("first_frame_observed", harness.Session.LastCallbackResult.Reason);

        harness.Backend.RaiseCaptureEnded(new DateTime(2000, 1, 1), 0, "natural");
        var finalizing = new SqliteRecordingRunRepository(harness.Context.Fixture.Store)
            .Get(harness.Receipt.Run.Id);
        Assert.Equal(RecordingRunStatus.Finalizing, finalizing.Status);
        Assert.Equal(harness.Context.Fixture.CreatedAt.AddMinutes(3), finalizing.UpdatedAtUtc);

        harness.Backend.RaiseNaturalExit(0, ValidMeta(harness, 1, "natural"));

        Assert.True(harness.Session.LastCallbackResult.Succeeded);
        Assert.True(harness.Session.LastCallbackResult.Terminal);
        Assert.Equal("media_settled", harness.Session.LastCallbackResult.Reason);
        Assert.Equal(0, harness.Backend.StopCalls);
        Assert.Equal(1, harness.Backend.DisposeCalls);
        Assert.Equal(RecordingRunStatus.Settled, ReadRun(harness).Status);
        Assert.Equal(LeaseUseStatus.Settled, ReadUse(harness)!.Status);
        Assert.Equal(PlanOccurrenceStatus.Completed, ReadOccurrence(harness).Status);
    }

    [Fact]
    public void StartFailureAfterSynchronousFirstFrameUsesDurablePostStartTruth()
    {
        using var harness = CreateHarness();
        harness.Backend.RaiseFirstFrameDuringStart = true;
        harness.Backend.ThrowAfterStartCallback = true;

        Assert.Throws<InvalidOperationException>(() =>
            harness.Backend.Start(new CaptureConfig(), null!));

        var result = harness.Lifecycle.StartFailed();

        Assert.True(result.Succeeded);
        Assert.True(result.Terminal);
        Assert.Equal("backend_start_failed_after_first_frame", result.Reason);
        Assert.Equal(RecordingRunStatus.Failed, ReadRun(harness).Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown, ReadUse(harness)!.Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, ReadOccurrence(harness).Status);
    }

    [Fact]
    public void StartFailureBeforeFirstFrameDoesNotInventRecording()
    {
        using var harness = CreateHarness();

        var result = harness.Lifecycle.StartFailed();

        Assert.True(result.Succeeded);
        Assert.Equal("backend_start_failed_before_first_frame", result.Reason);
        Assert.Equal(RecordingRunStatus.StartedUnknown, ReadRun(harness).Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown, ReadUse(harness)!.Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, ReadOccurrence(harness).Status);
    }

    [Fact]
    public void NaturalExitBeforeFirstFrameIsDurableUnknown()
    {
        using var harness = CreateHarness();

        harness.Backend.RaiseNaturalExit(0, ValidMeta(harness, 1, "natural"));

        Assert.Equal("natural_exit_before_first_frame", harness.Session.LastCallbackResult.Reason);
        Assert.Equal(RecordingRunStatus.StartedUnknown, ReadRun(harness).Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown, ReadUse(harness)!.Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, ReadOccurrence(harness).Status);
    }

    [Theory]
    [InlineData(null, "user_stop_output_invalid", RecordingRunStatus.SessionInterrupted)]
    [InlineData("recurring_lease_safety_control", "safety_stop", RecordingRunStatus.SessionInterrupted)]
    [InlineData("session_interrupted", "session_interrupted", RecordingRunStatus.SessionInterrupted)]
    public void StopReasonMappingProducesDurableTerminalShape(
        string? reason,
        string expectedReason,
        RecordingRunStatus expectedRunStatus)
    {
        using var harness = CreateHarness();
        harness.Backend.StopMeta = reason is null
            ? new OutputMeta()
            : ValidMeta(harness, 1, "manual");
        harness.Backend.RaiseFirstFrame();

        var result = reason is null
            ? harness.Lifecycle.Stop()
            : harness.Driver.StopForEngine(reason).Lifecycle;

        Assert.True(result.Succeeded);
        Assert.True(result.Terminal);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Equal(expectedRunStatus, ReadRun(harness).Status);
        Assert.Equal(1, harness.Backend.StopCalls);
        Assert.Equal(1, harness.Backend.DisposeCalls);
    }

    [Fact]
    public void StopExceptionFailsClosedWithoutFabricatingSuccess()
    {
        using var harness = CreateHarness();
        harness.Backend.ThrowOnStop = true;
        harness.Backend.RaiseFirstFrame();

        var result = harness.Lifecycle.Stop();

        Assert.True(result.Succeeded);
        Assert.True(result.Terminal);
        Assert.Equal("user_stop_output_invalid", result.Reason);
        Assert.Equal(1, harness.Backend.StopCalls);
        Assert.Equal(1, harness.Backend.DisposeCalls);
        Assert.Equal(RecordingRunStatus.SessionInterrupted, ReadRun(harness).Status);
    }

    [Fact]
    public void UserStopWithValidPartialOutputSettles()
    {
        using var harness = CreateHarness();
        harness.Backend.StopMeta = ValidMeta(harness, 1, "manual");
        harness.Backend.RaiseFirstFrame();

        var result = harness.Lifecycle.Stop();

        Assert.True(result.Succeeded && result.Terminal);
        Assert.Equal("media_settled", result.Reason);
        Assert.Equal(RecordingRunStatus.Settled, ReadRun(harness).Status);
        Assert.Equal(LeaseUseStatus.Settled, ReadUse(harness)!.Status);
        Assert.Equal(PlanOccurrenceStatus.Completed, ReadOccurrence(harness).Status);
    }

    [Fact]
    public void StopIgnoresSynchronousCallbacksAndLateCallbacksAreIdempotent()
    {
        using var harness = CreateHarness();
        harness.Backend.StopMeta = ValidMeta(harness, 1, "manual");
        harness.Backend.RaiseCallbacksDuringStop = true;

        var result = harness.Lifecycle.Stop();
        var runVersion = ReadRun(harness).Version;
        var useVersion = harness.Context.Fixture.ReadAccountingRow(harness.Receipt.Use.Id).Version;

        Assert.True(result.Terminal);
        Assert.Equal(1, harness.Backend.StopCalls);
        harness.Backend.RaiseFirstFrame();
        harness.Backend.RaiseCaptureEnded(DateTime.MinValue, 0, "natural");
        harness.Backend.RaiseNaturalExit(0, ValidMeta(harness, 1, "natural"));
        var replay = harness.Lifecycle.Stop();

        Assert.True(replay.Succeeded);
        Assert.False(replay.Changed);
        Assert.True(replay.Terminal);
        Assert.Equal(runVersion, ReadRun(harness).Version);
        Assert.Equal(useVersion, harness.Context.Fixture.ReadAccountingRow(harness.Receipt.Use.Id).Version);
        Assert.Equal(1, harness.Backend.StopCalls);
    }

    [Fact]
    public async Task ConcurrentStopCallsIssueOnePhysicalStop()
    {
        using var harness = CreateHarness();
        harness.Backend.StopMeta = new OutputMeta();
        harness.Backend.RaiseFirstFrame();
        harness.Backend.BlockStop = true;

        var first = Task.Run(() => harness.Lifecycle.Stop());
        Assert.True(harness.Backend.StopEntered.Wait(TimeSpan.FromSeconds(5)));
        var second = Task.Run(() => harness.Lifecycle.Stop());
        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        harness.Backend.ReleaseStop.Set();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(1, harness.Backend.StopCalls);
        Assert.Equal(1, harness.Backend.DisposeCalls);
        Assert.Equal(RecordingRunStatus.SessionInterrupted, ReadRun(harness).Status);
    }

    [Fact]
    public async Task StopNaturalExitAndDisposeShareOneSerializedOwner()
    {
        using var harness = CreateHarness();
        harness.Backend.StopMeta = new OutputMeta();
        harness.Backend.RaiseFirstFrame();
        harness.Backend.BlockStop = true;

        var stop = Task.Run(() => harness.Lifecycle.Stop());
        Assert.True(harness.Backend.StopEntered.Wait(TimeSpan.FromSeconds(5)));
        var natural = Task.Run(() => harness.Backend.RaiseNaturalExit(0, ValidMeta(harness, 1, "natural")));
        var dispose = Task.Run(() => ((IDisposable)harness.Session).Dispose());
        await Task.Delay(50);
        Assert.False(stop.IsCompleted);

        harness.Backend.ReleaseStop.Set();
        await Task.WhenAll(stop, natural, dispose);

        Assert.Equal(1, harness.Backend.StopCalls);
        Assert.Equal(1, harness.Backend.DisposeCalls);
        Assert.Equal(RecordingRunStatus.SessionInterrupted, ReadRun(harness).Status);
    }

    [Fact]
    public void AttachRequiresExactBackendAndRegistrationFailureLeavesCallerOwnership()
    {
        using var context = CreateStartedContext();
        var expected = new FakeBackend();
        var wrong = new FakeBackend();
        using var session = new RecurringLeaseCaptureExecutionSession(
            context.Context.Fixture.Store,
            context.Receipt,
            expected,
            () => context.Context.Fixture.CreatedAt.AddMinutes(3));
        var lifecycle = (IRecurringLeaseCaptureLifecycleSession)session;

        Assert.False(lifecycle.TryAttach(wrong, out _));
        Assert.True(lifecycle.TryAttach(expected, out var reason), reason);
        Assert.False(lifecycle.TryAttach(expected, out _));
        Assert.Equal(0, wrong.FirstFrameHandlerCount);
        Assert.Equal(1, expected.FirstFrameHandlerCount);

        using var failedContext = CreateStartedContext();
        var failing = new FakeBackend { ThrowOnCaptureEndedAdd = true };
        using var failedSession = new RecurringLeaseCaptureExecutionSession(
            failedContext.Context.Fixture.Store,
            failedContext.Receipt,
            failing,
            () => failedContext.Context.Fixture.CreatedAt.AddMinutes(3));
        var failedLifecycle = (IRecurringLeaseCaptureLifecycleSession)failedSession;

        Assert.False(failedLifecycle.TryAttach(failing, out _));
        Assert.Equal(0, failing.FirstFrameHandlerCount);
        Assert.Equal(0, failing.CaptureEndedHandlerCount);
        Assert.Equal(0, failing.DisposeCalls);
        Assert.Equal(0, failing.StartCalls);
    }

    [Fact]
    public void StartHandoffIsActiveUntilTerminalCallbackThenReportsAlreadyTerminal()
    {
        using var harness = CreateHarness();

        Assert.Equal(
            RecurringLeaseCaptureLifecycleHandoffStatus.Active,
            harness.Lifecycle.CompleteStartHandoff().Status);

        harness.Backend.RaiseNaturalExit(0, ValidMeta(harness, 1, "natural"));

        Assert.Equal(
            RecurringLeaseCaptureLifecycleHandoffStatus.AlreadyTerminal,
            harness.Lifecycle.CompleteStartHandoff().Status);
        Assert.Equal(1, harness.Backend.DisposeCalls);
    }

    [Fact]
    public void FailBeforeStartMapsSessionInterruptionWithoutPhysicalStop()
    {
        using var harness = CreateHarness();

        var result = harness.Driver.FailBeforeStart("session_interrupted");

        Assert.True(result.Succeeded && result.Terminal);
        Assert.Equal("session_interrupted_before_first_frame", result.Reason);
        Assert.Equal(0, harness.Backend.StopCalls);
        Assert.Equal(1, harness.Backend.DisposeCalls);
        Assert.Equal(RecordingRunStatus.StartedUnknown, ReadRun(harness).Status);
        Assert.Equal("session_interrupted_before_first_frame", ReadRun(harness).TerminalReasonCode);
    }

    [Fact]
    public void FirstTerminalResultIsFrozenAcrossLateCallbacksAndRepeatedHandoff()
    {
        using var harness = CreateHarness();
        harness.Backend.RaiseFirstFrame();
        harness.Backend.RaiseNaturalExit(0, ValidMeta(harness, 1, "natural"));

        var firstHandoff = harness.Lifecycle.CompleteStartHandoff();
        var runVersion = ReadRun(harness).Version;
        var useVersion = harness.Context.Fixture.ReadAccountingRow(harness.Receipt.Use.Id).Version;
        var occurrenceVersion = ReadOccurrence(harness).Version;

        Assert.Equal(RecurringLeaseCaptureLifecycleHandoffStatus.AlreadyTerminal, firstHandoff.Status);
        Assert.True(firstHandoff.FirstTerminalResult is
        {
            Succeeded: true,
            Terminal: true,
            Reason: "media_settled",
        });

        _ = harness.Driver.ObserveFirstFrame(new FirstFrameObservation
        {
            EvidenceKind = "ffmpeg_progress_frame_and_output_bytes",
            FrameNumber = 2,
            TotalSizeBytes = 2048,
        });
        _ = harness.Driver.ObserveNaturalExit(0, ValidMeta(harness, 1, "late"));
        _ = harness.Lifecycle.StartFailed();
        _ = harness.Lifecycle.Stop();
        var secondHandoff = harness.Lifecycle.CompleteStartHandoff();

        Assert.Equal(firstHandoff, secondHandoff);
        Assert.Equal(runVersion, ReadRun(harness).Version);
        Assert.Equal(useVersion, harness.Context.Fixture.ReadAccountingRow(harness.Receipt.Use.Id).Version);
        Assert.Equal(occurrenceVersion, ReadOccurrence(harness).Version);
        Assert.Equal(1, harness.Backend.DisposeCalls);
    }

    [Fact]
    public void DriverModeDoesNotInstallCompetingBackendCallbacks()
    {
        using var harness = CreateHarness(attachBackendCallbacks: false);

        Assert.Equal(0, harness.Backend.FirstFrameHandlerCount);
        Assert.Equal(0, harness.Backend.CaptureEndedHandlerCount);
        Assert.Equal(0, harness.Backend.NaturalCallbackRegistrations);

        var first = harness.Driver.ObserveFirstFrame(new FirstFrameObservation
        {
            EvidenceKind = "ffmpeg_progress_frame_and_output_bytes",
            FrameNumber = 0,
            TotalSizeBytes = 100,
        });
        var ended = harness.Driver.ObserveCaptureEnded(new CaptureEndedObservation
        {
            EndedAtUtc = DateTime.MinValue,
            ExitCode = 0,
            Reason = "natural",
        });
        var natural = harness.Driver.ObserveNaturalExit(0, ValidMeta(harness, 1, "natural"));

        Assert.True(first.Succeeded);
        Assert.True(ended.Succeeded);
        Assert.True(natural.Succeeded && natural.Terminal);
        Assert.Equal(RecordingRunStatus.Settled, ReadRun(harness).Status);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("throw")]
    [InlineData("non_utc")]
    [InlineData("backwards")]
    public void InvalidTrustedUtcFailsClosedAndStopsOnce(string clockMode)
    {
        using var context = CreateStartedContext();
        var backend = new FakeBackend();
        Func<DateTimeOffset?> clock = clockMode switch
        {
            "null" => () => null,
            "throw" => () => throw new InvalidOperationException("clock failure"),
            "non_utc" => () => context.Context.Fixture.CreatedAt.AddMinutes(3).ToOffset(TimeSpan.FromHours(1)),
            "backwards" => () => context.Context.Fixture.CreatedAt.AddMinutes(-1),
            _ => throw new ArgumentOutOfRangeException(nameof(clockMode)),
        };
        using var session = new RecurringLeaseCaptureExecutionSession(
            context.Context.Fixture.Store,
            context.Receipt,
            backend,
            clock);
        var lifecycle = (IRecurringLeaseCaptureLifecycleSession)session;
        Assert.True(lifecycle.TryAttach(backend, out var reason), reason);

        var result = lifecycle.StartFailed();

        Assert.False(result.Succeeded);
        Assert.Equal(1, backend.StopCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(RecordingRunStatus.StartCommitted, ReadRun(context).Status);
    }

    [Fact]
    public void PersistenceFailureAtFirstFrameStopsAndBoundedTerminalizationSettlesFailure()
    {
        var hookCalls = 0;
        using var harness = CreateHarness(
            failureHook: point =>
            {
                if (point == RecurringLeaseLifecycleFailurePoint.AfterRunUpdate &&
                    Interlocked.Increment(ref hookCalls) == 1)
                    throw new InvalidOperationException("injected persistence failure");
            });

        var result = harness.Driver.ObserveFirstFrame(new FirstFrameObservation
        {
            EvidenceKind = "ffmpeg_progress_frame_and_output_bytes",
            FrameNumber = 0,
            TotalSizeBytes = 100,
        });

        Assert.False(result.Succeeded);
        Assert.Equal(1, harness.Backend.StopCalls);
        Assert.Equal(1, harness.Backend.DisposeCalls);
        Assert.Equal(RecordingRunStatus.StartedUnknown, ReadRun(harness).Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown, ReadUse(harness)!.Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, ReadOccurrence(harness).Status);

        var replay = harness.Driver.ObserveFirstFrame(new FirstFrameObservation
        {
            EvidenceKind = "ffmpeg_progress_frame_and_output_bytes",
            FrameNumber = 1,
            TotalSizeBytes = 101,
        });
        Assert.True(replay.Succeeded);
        Assert.False(replay.Changed);
        Assert.True(replay.Terminal);
        Assert.Equal(1, harness.Backend.StopCalls);
    }

    [Fact]
    public void TerminalizationFailureDoesNotRetryOrFabricateSuccess()
    {
        var hookCalls = 0;
        using var harness = CreateHarness(
            failureHook: point =>
            {
                if (point == RecurringLeaseLifecycleFailurePoint.AfterRunUpdate)
                {
                    Interlocked.Increment(ref hookCalls);
                    throw new InvalidOperationException("persistent failure");
                }
            });

        var result = harness.Driver.ObserveFirstFrame(new FirstFrameObservation
        {
            EvidenceKind = "ffmpeg_progress_frame_and_output_bytes",
            FrameNumber = 0,
            TotalSizeBytes = 100,
        });
        var replay = harness.Driver.ObserveFirstFrame(new FirstFrameObservation
        {
            EvidenceKind = "ffmpeg_progress_frame_and_output_bytes",
            FrameNumber = 1,
            TotalSizeBytes = 101,
        });

        Assert.False(result.Succeeded);
        Assert.False(replay.Succeeded);
        Assert.Equal(1, harness.Backend.StopCalls);
        Assert.True(hookCalls <= 2);
        Assert.Equal(RecordingRunStatus.StartCommitted, ReadRun(harness).Status);
    }

    [Fact]
    public void PersistenceFailureAtCaptureEndedStopsAndDoesNotSettleMedia()
    {
        var hookCalls = 0;
        using var harness = CreateHarness(
            failureHook: point =>
            {
                if (point == RecurringLeaseLifecycleFailurePoint.AfterRunUpdate &&
                    Interlocked.Increment(ref hookCalls) == 2)
                    throw new InvalidOperationException("capture-ended persistence failure");
            });

        harness.Backend.RaiseFirstFrame();
        harness.Backend.RaiseCaptureEnded(DateTime.MinValue, 0, "natural");

        Assert.Equal(1, harness.Backend.StopCalls);
        Assert.Equal(1, harness.Backend.DisposeCalls);
        Assert.Equal(RecordingRunStatus.Failed, ReadRun(harness).Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown, ReadUse(harness)!.Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, ReadOccurrence(harness).Status);
    }

    [Fact]
    public void ExhaustedLeaseStillSettlesWithoutChangingDurableQuotaRule()
    {
        using var harness = CreateHarness(maxUses: 1);
        harness.Backend.RaiseFirstFrame();
        harness.Backend.RaiseNaturalExit(0, ValidMeta(harness, 1, "natural"));

        Assert.Equal(ConsentLeaseStatus.Exhausted, ReadLease(harness).Status);
        Assert.Equal(RecordingRunStatus.Settled, ReadRun(harness).Status);
        Assert.Equal(LeaseUseStatus.Settled, ReadUse(harness)!.Status);
    }

    [Theory]
    [InlineData("revoked", ConsentLeaseStatus.Revoked)]
    [InlineData("expired", ConsentLeaseStatus.Expired)]
    public void RevokedOrExpiredLeaseIsNotOverwrittenByCurrentRunSettlement(
        string statusCode,
        ConsentLeaseStatus expectedStatus)
    {
        using var harness = CreateHarness();
        var at = harness.Context.Fixture.CreatedAt.AddMinutes(3);
        harness.Context.Fixture.Execute(
            "DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; UPDATE recurring_consent_leases SET status_code = $status, version = version + 1, updated_at_utc = $at WHERE lease_id = $leaseId;",
            ("$status", statusCode),
            ("$at", at.UtcDateTime.Ticks),
            ("$leaseId", harness.Context.Lease.LeaseId));

        harness.Backend.RaiseFirstFrame();
        harness.Backend.RaiseNaturalExit(0, ValidMeta(harness, 1, "natural"));

        Assert.Equal(expectedStatus, ReadLease(harness).Status);
        Assert.Equal(RecordingRunStatus.Settled, ReadRun(harness).Status);
    }

    [Fact]
    public void ResultContractsDoNotExposeProofTicketOrScope()
    {
        var resultTypes = new[]
        {
            typeof(RecurringLeaseLifecycleActionResult),
            typeof(RecurringLeaseCaptureStopResult),
        };

        foreach (var property in resultTypes.SelectMany(type => type.GetProperties()))
        {
            Assert.DoesNotContain("proof", property.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ticket", property.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("scope", property.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static Harness CreateHarness(
        long maxUses = 10,
        Action<RecurringLeaseLifecycleFailurePoint>? failureHook = null,
        bool attachBackendCallbacks = true)
    {
        var started = CreateStartedContext(maxUses);
        var backend = new FakeBackend();
        var session = new RecurringLeaseCaptureExecutionSession(
            started.Context.Fixture.Store,
            started.Receipt,
            backend,
            () => started.Context.Fixture.CreatedAt.AddMinutes(3),
            failureHookForTest: failureHook,
            attachBackendCallbacks: attachBackendCallbacks);
        var lifecycle = (IRecurringLeaseCaptureLifecycleSession)session;
        Assert.True(lifecycle.TryAttach(backend, out var reason), reason);
        return new Harness(started.Context, started.Receipt, session, lifecycle, (IRecurringLeaseCaptureLifecycleDriver)session, backend);
    }

    private static StartedContext CreateStartedContext(long maxUses = 10)
    {
        var context = RecurringOccurrenceReservationTests.ReservationContext.Create(maxUses: maxUses);
        var reservation = new RecurringOccurrenceReservationService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt,
            () => "task273-run-" + Guid.NewGuid().ToString("N"),
            () => "task273-use-" + Guid.NewGuid().ToString("N"))
            .Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceReservationStatus.Reserved, reservation.Status);

        var startCommit = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt,
            new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider())
            .Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.True(startCommit.Succeeded, $"status={startCommit.Status}; reason={startCommit.ReasonCode}");
        Assert.NotNull(startCommit.FirstCommitReceipt);
        return new StartedContext(context, startCommit.FirstCommitReceipt!);
    }

    private static OutputMeta ValidMeta(Harness harness, double durationSeconds, string reason) => new()
    {
        SizeBytes = 1024,
        OutputFileExists = true,
        DurationSeconds = durationSeconds,
        OutputPath = harness.Receipt.Specification.FrozenOutputFilePath,
        StopReason = reason,
    };

    private static RecordingRun ReadRun(Harness harness) =>
        new SqliteRecordingRunRepository(harness.Context.Fixture.Store).Get(harness.Receipt.Run.Id);

    private static RecordingRun ReadRun(StartedContext context) =>
        new SqliteRecordingRunRepository(context.Context.Fixture.Store).Get(context.Receipt.Run.Id);

    private static PlanOccurrence ReadOccurrence(Harness harness) =>
        new SqlitePlanOccurrenceRepository(harness.Context.Fixture.Store).Get(harness.Context.Slot.OccurrenceId!);

    private static RecurringLeaseUseAccountingEntry? ReadUse(Harness harness) =>
        new SqliteRecurringLeaseUseAccountingReader(harness.Context.Fixture.Store)
            .TryGetByOccurrence(harness.Context.Slot.OccurrenceIdentity);

    private static RecurringConsentLease ReadLease(Harness harness) =>
        new SqliteRecurringConsentLeaseRepository(harness.Context.Fixture.Store).Get(harness.Context.Lease.LeaseId);

    private sealed class Harness : IDisposable
    {
        internal Harness(
            RecurringOccurrenceReservationTests.ReservationContext context,
            RecurringStartCommitReceipt receipt,
            RecurringLeaseCaptureExecutionSession session,
            IRecurringLeaseCaptureLifecycleSession lifecycle,
            IRecurringLeaseCaptureLifecycleDriver driver,
            FakeBackend backend)
        {
            Context = context;
            Receipt = receipt;
            Session = session;
            Lifecycle = lifecycle;
            Driver = driver;
            Backend = backend;
        }

        internal RecurringOccurrenceReservationTests.ReservationContext Context { get; }
        internal RecurringStartCommitReceipt Receipt { get; }
        internal RecurringLeaseCaptureExecutionSession Session { get; }
        internal IRecurringLeaseCaptureLifecycleSession Lifecycle { get; }
        internal IRecurringLeaseCaptureLifecycleDriver Driver { get; }
        internal FakeBackend Backend { get; }

        public void Dispose()
        {
            ((IDisposable)Session).Dispose();
            Context.Dispose();
        }
    }

    private sealed class StartedContext : IDisposable
    {
        internal StartedContext(RecurringOccurrenceReservationTests.ReservationContext context, RecurringStartCommitReceipt receipt)
        {
            Context = context;
            Receipt = receipt;
        }

        internal RecurringOccurrenceReservationTests.ReservationContext Context { get; }
        internal RecurringStartCommitReceipt Receipt { get; }

        public void Dispose() => Context.Dispose();
    }

    private sealed class FakeBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend, ICaptureEndedObservableBackend
    {
        private Action<FirstFrameObservation>? _firstFrameObserved;
        private Action<CaptureEndedObservation>? _captureEnded;
        private Action<int, OutputMeta>? _naturalExit;

        public bool RaiseFirstFrameDuringStart { get; set; }
        public bool ThrowAfterStartCallback { get; set; }
        public bool ThrowOnStop { get; set; }
        public bool RaiseCallbacksDuringStop { get; set; }
        public bool BlockStop { get; set; }
        public bool ThrowOnCaptureEndedAdd { get; set; }
        public OutputMeta? StopMeta { get; set; }
        public int ExitCodeValue { get; set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public int FirstFrameHandlerCount { get; private set; }
        public int CaptureEndedHandlerCount { get; private set; }
        public int NaturalCallbackRegistrations { get; private set; }
        public ManualResetEventSlim StopEntered { get; } = new(false);
        public ManualResetEventSlim ReleaseStop { get; } = new(false);

        public event Action<FirstFrameObservation>? FirstFrameObserved
        {
            add
            {
                _firstFrameObserved += value;
                FirstFrameHandlerCount++;
            }
            remove
            {
                if (_firstFrameObserved is not null)
                {
                    _firstFrameObserved -= value;
                    FirstFrameHandlerCount--;
                }
            }
        }

        public event Action<CaptureEndedObservation>? CaptureEnded
        {
            add
            {
                if (ThrowOnCaptureEndedAdd)
                    throw new InvalidOperationException("capture-ended registration failed");
                _captureEnded += value;
                CaptureEndedHandlerCount++;
            }
            remove
            {
                if (_captureEnded is not null)
                {
                    _captureEnded -= value;
                    CaptureEndedHandlerCount--;
                }
            }
        }

        public void Start(CaptureConfig cfg, CaptureAuthorizationProof authorizationProof)
        {
            StartCalls++;
            if (RaiseFirstFrameDuringStart)
                RaiseFirstFrame();
            if (ThrowAfterStartCallback)
                throw new InvalidOperationException("start failed");
        }

        public OutputMeta Stop()
        {
            StopCalls++;
            StopEntered.Set();
            if (BlockStop)
                ReleaseStop.Wait(TimeSpan.FromSeconds(10));
            if (RaiseCallbacksDuringStop)
            {
                RaiseCaptureEnded(DateTime.MinValue, -1, "manual");
                RaiseNaturalExit(0, StopMeta ?? new OutputMeta());
            }
            if (ThrowOnStop)
                throw new InvalidOperationException("stop failed");
            return StopMeta ?? new OutputMeta();
        }

        public void OnNaturalExit(Action<int, OutputMeta> callback)
        {
            NaturalCallbackRegistrations++;
            _naturalExit = callback;
        }

        public int ExitCode => ExitCodeValue;

        public void RaiseFirstFrame() =>
            _firstFrameObserved?.Invoke(new FirstFrameObservation
            {
                EvidenceKind = "ffmpeg_progress_frame_and_output_bytes",
                FrameNumber = 0,
                TotalSizeBytes = 128,
            });

        public void RaiseCaptureEnded(DateTime endedAtUtc, int exitCode, string reason) =>
            _captureEnded?.Invoke(new CaptureEndedObservation
            {
                EndedAtUtc = endedAtUtc,
                ExitCode = exitCode,
                Reason = reason,
            });

        public void RaiseNaturalExit(int exitCode, OutputMeta meta) => _naturalExit?.Invoke(exitCode, meta);

        public void Dispose() => DisposeCalls++;
    }
}
