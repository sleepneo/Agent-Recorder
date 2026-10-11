using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class UnifiedHistoryRecurringLifecycleTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void ApprovedRecurringPlanProjectsOnlyPersistentOccurrencesAndForeignOwnersStayHidden()
    {
        using var fixture=new RecurringPlanSetupTestFixture();
        fixture.Create("owned",prepared:true);fixture.Activate("owned");
        fixture.Create("foreign",prepared:true,sid:"foreign-sid",session:"foreign-session");fixture.Activate("foreign",sid:"foreign-sid",session:"foreign-session");
        var plan=fixture.Query.Get("owned",RecurringPlanSetupTestFixture.Sid,RecurringPlanSetupTestFixture.Session)!.PlanId!;
        var foreignPlan=fixture.Query.Get("foreign","foreign-sid","foreign-session")!.PlanId!;
        using var ordinary=new SqliteOrdinaryRecordingHistoryStore(fixture.Store);var query=new UnifiedHistoryQueryService(fixture.Store,ordinary);
        Assert.Empty(query.GetPage(RecurringPlanSetupTestFixture.Sid,RecurringPlanSetupTestFixture.Session,20,new()).Items);
        var advance=new SqliteRecurringAdvancementTransaction(fixture.Store);
        var first=advance.AdvanceOne(plan,1,"owned-operation-1",0,RecurringPlanSetupTestFixture.At(0),RecurringPlanSetupTestFixture.At(10));
        Assert.Equal("scheduled",first.ResultCode);
        advance.AdvanceOne(foreignPlan,1,"foreign-operation",0,RecurringPlanSetupTestFixture.At(0),RecurringPlanSetupTestFixture.At(10));
        var second=advance.AdvanceOne(plan,1,"owned-operation-2",first.ResultCursorVersion,RecurringPlanSetupTestFixture.At(0),RecurringPlanSetupTestFixture.At(11));
        Assert.Equal("scheduled",second.ResultCode);
        UnifiedHistoryIndexedSeekTests.ObserveOwnedPlanAccess(query, fixture.Store, "recurring", output);
        var before=fixture.State();var page=query.GetPage(RecurringPlanSetupTestFixture.Sid,RecurringPlanSetupTestFixture.Session,1,new("plan"));
        Assert.Equal(plan,Assert.Single(page.Items).PlanId);Assert.NotNull(page.NextCursor);
        var next=query.GetPage(RecurringPlanSetupTestFixture.Sid,RecurringPlanSetupTestFixture.Session,20,new("plan"),page.NextCursor);
        Assert.Equal(plan,Assert.Single(next.Items).PlanId);Assert.NotEqual(page.Items[0].OccurrenceId,next.Items[0].OccurrenceId);
        Assert.Equal(before,fixture.State());
    }
}
