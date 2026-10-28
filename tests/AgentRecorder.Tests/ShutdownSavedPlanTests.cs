using AgentRecorder.Core;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class RequiredOncePlanSetupCoordinatorTests
{
    [Fact]
    public async Task IdleShutdownPreservesSavedPlanAndBlocksDueClaimWithoutApprovalOrCapture()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var setup = CreateFixture(database, clock, new FakeUi());
        using var setupCoordinator = setup.Coordinator;
        var created = setupCoordinator.CreateOrGet(setup.ApiRequest("shutdown-preserves-future-plan"));
        await setupCoordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var state = setupCoordinator.Get(created.SetupIntentId!)!;
        var backend = new FakeRequiredOnceBackend();
        using var engine = CreateRequiredOnceEngine(setup, backend);
        var ui = new FakeExecutionUi();
        using var execution = CreateExecutionCoordinator(database, setup, engine, ui);
        var before = new PlanExecutionStatusQueryService(database.Store).Get(state.PlanId!, Sid, Session)!;
        Assert.Null(engine.HostAdmission.TryClose(() => engine.TryReserveIdleHostShutdown(() => null)));
        clock.Set(setup.Request.ScheduledStartUtc);
        var repository = new SqliteRequiredOnceExecutionRepository(database.Store);
        var candidate = Assert.Single(repository.ListDue(Sid, Session, clock.Now));
        Assert.Equal(0, await execution.ProcessDueOnceAsync());
        var after = Assert.Single(repository.ListDue(Sid, Session, clock.Now));
        Assert.Equal(candidate.OccurrenceVersion, after.OccurrenceVersion);
        Assert.Equal(candidate.Specification.SpecificationDigest, after.Specification.SpecificationDigest);
        Assert.Equal(candidate.Specification.CreationApprovalId, after.Specification.CreationApprovalId);
        Assert.Equal(0, ui.ShowCount);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0, Count(database.Store, "recording_runs"));
        Assert.Equal(0, Count(database.Store, "required_once_execution_authorizations"));
        Assert.NotNull(new PlanExecutionStatusQueryService(database.Store).Get(state.PlanId!, Sid, Session));
        Assert.Equal(before.LatestOccurrence!.Status,
            new PlanExecutionStatusQueryService(database.Store).Get(state.PlanId!, Sid, Session)!.LatestOccurrence!.Status);
    }
}
