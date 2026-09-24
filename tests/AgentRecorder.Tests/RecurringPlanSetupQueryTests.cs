using AgentRecorder.Core.Automation;
using AgentRecorder.Persistence;
using Xunit;
using static AgentRecorder.Tests.RecurringPlanSetupTestFixture;

namespace AgentRecorder.Tests;

public sealed class RecurringPlanSetupQueryTests
{
    [Theory]
    [InlineData("initial")]
    [InlineData("prepared")]
    [InlineData("activated")]
    [InlineData("rejected")]
    [InlineData("expired")]
    public void StrictProjectionIsReadOnlyAndPreparedDataIsExact(string shape)
    {
        using var db = new RecurringPlanSetupTestFixture();
        db.Create("one", shape != "initial");
        if (shape == "activated") db.Activate("one");
        if (shape == "rejected") db.Reject("one");
        if (shape == "expired")
        {
            db.Now = At(172800);
            new RecurringPlanSetupTerminalService(db.Store, () => db.Now).Settle("one", Sid, Session,
                RecurringSetupIntentStatus.Expired, "recurring_setup_intent_expired");
        }
        var before = db.State();
        var state = db.Query.Get("one", Sid, Session);
        Assert.NotNull(state);
        var prepared = db.Query.GetPrepared("one", Sid, Session);
        if (shape == "prepared")
        {
            Assert.NotNull(prepared);
            Assert.Equal("stable-display", prepared.StableDisplayFingerprint);
            Assert.Equal(new AuthorizedPhysicalRectangle(100, 200, 1920, 1080), prepared.DisplayBounds);
            Assert.Equal(new AuthorizedPhysicalRectangle(10, 20, 640, 480), prepared.RegionWithinDisplay);
            Assert.Equal((96, 96, 1920, 1080, AuthorizedDisplayOrientation.Landscape),
                (prepared.DpiX, prepared.DpiY, prepared.PhysicalWidth, prepared.PhysicalHeight, prepared.Orientation));
            Assert.Equal(Sid, prepared.CurrentUserSid); Assert.Equal(Session, prepared.SessionBinding);
            Assert.StartsWith("recurring-plan-configuration/v1:", prepared.ConfigurationDigest);
            Assert.False(string.IsNullOrEmpty(prepared.AuthorizationDigest));
            Assert.Equal(db.Request().Schedule.CanonicalDigest, prepared.Request.Schedule.CanonicalDigest);
            Assert.Equal(db.Request().OutputDirectory, prepared.Request.OutputDirectory);
            Assert.Equal("private-prefix", prepared.Request.FilenamePrefix);
            Assert.Equal("none", prepared.Request.AudioModeCode);
            Assert.Equal("natural_wake_only", prepared.Request.WakePolicyCode);
            Assert.Equal("interactive_desktop_required", prepared.Request.DesktopRequirementCode);
            Assert.Equal("fail_if_exists", prepared.Request.OutputConflictPolicyCode);
        }
        else Assert.Null(prepared);
        Assert.Equal(before, db.State()); db.AssertNoExecution();
    }

    [Theory]
    [InlineData("DELETE FROM recurring_setup_preparations;")]
    [InlineData("DELETE FROM recurring_fixed_region_profile_versions;")]
    [InlineData("UPDATE recurring_fixed_region_profile_versions SET dpi_x = 120;")]
    [InlineData("DELETE FROM recurring_schedule_versions;")]
    [InlineData("UPDATE recurring_schedule_versions SET schedule_digest = 'corrupt';")]
    [InlineData("DELETE FROM recurring_plan_profile_bindings;")]
    [InlineData("UPDATE recurring_plan_profile_bindings SET bound_at_utc = bound_at_utc + 1;")]
    [InlineData("DELETE FROM plans;")]
    [InlineData("UPDATE plans SET status_code = 'enabled', version = 1;")]
    [InlineData("DELETE FROM recurring_consent_leases;")]
    [InlineData("UPDATE recurring_consent_leases SET status_code = 'active', version = 1;")]
    [InlineData("UPDATE recurring_consent_leases SET updated_at_utc = updated_at_utc + 1;")]
    [InlineData("UPDATE recurring_consent_leases SET authorization_digest = 'corrupt';")]
    [InlineData("UPDATE recurring_setup_preparations SET configuration_digest = 'corrupt';")]
    [InlineData("UPDATE recurring_setup_preparations SET selection_digest = 'corrupt';")]
    [InlineData("UPDATE setup_intents SET updated_at_utc = updated_at_utc + 1;")]
    [InlineData("UPDATE setup_intents SET version = 2;")]
    [InlineData("UPDATE setup_intents SET recurring_filename_prefix = 'other';")]
    [InlineData("UPDATE setup_intents SET status_code = 'rejected', version = 2, terminal_reason_code = 'setup_conflict';")]
    public void CorruptPreparedChainNeverProjectsAsRecoverable(string sql)
    {
        using var db = new RecurringPlanSetupTestFixture(); db.Create("one", true); db.Corrupt(sql);
        var before = db.State();
        var logs = new List<string>();
        var query = new RecurringPlanSetupQueryService(db.Store, (_, reason) => logs.Add(reason));
        Assert.Null(query.Get("one", Sid, Session)); Assert.Null(query.GetPrepared("one", Sid, Session));
        Assert.Empty(query.ListRecoverable(Sid, Session));
        Assert.NotEmpty(logs); Assert.All(logs, reason => Assert.Equal("recurring_setup_query_unavailable", reason));
        Assert.Equal(before, db.State());
    }

    [Fact]
    public void ListIsBoundedOrderedPrincipalBoundAndSkipsCorruptRowsWithoutWriting()
    {
        using var db = new RecurringPlanSetupTestFixture();
        db.Create("b"); db.Create("a"); db.Create("foreign", sid: "different-sid"); db.Create("session", session: "different-session");
        db.Now = At(11); db.Create("c", true); db.Create("done", true); db.Activate("done");
        Assert.Equal(new[] { "a", "b", "c" }, db.Query.ListRecoverable(Sid, Session).Select(r => r.IntentId));
        Assert.Equal("a", Assert.Single(db.Query.ListRecoverable(Sid, Session, 1)).IntentId);
        Assert.Empty(db.Query.ListRecoverable(Sid, Session, 0)); Assert.Empty(db.Query.ListRecoverable(Sid, Session, 257));
        Assert.Null(db.Query.Get("a", "wrong", Session)); Assert.Null(db.Query.Get("a", Sid, "wrong"));
        Assert.Null(db.Query.Get("missing", Sid, Session));
        db.Corrupt("UPDATE setup_intents SET request_digest = 'corrupt' WHERE intent_id = 'a';");
        var before = db.State();
        Assert.Empty(db.Query.ListRecoverable(Sid, Session, 1));
        Assert.Equal("b", Assert.Single(db.Query.ListRecoverable(Sid, Session, 2)).IntentId);
        Assert.Equal(before, db.State());
    }

    [Theory]
    [InlineData("UPDATE setup_intents SET intent_kind_code = 'standing_once_fixed_region';")]
    [InlineData("UPDATE setup_intents SET status_code = 'unknown';")]
    [InlineData("UPDATE setup_intents SET updated_at_utc = created_at_utc - 1;")]
    [InlineData("UPDATE setup_intents SET plan_id = 'orphan-plan';")]
    public void WrongKindAndMalformedInitialRowAreUnavailable(string mutation)
    {
        using var db = new RecurringPlanSetupTestFixture(); db.Create("one"); db.Corrupt(mutation);
        var before = db.State(); Assert.Null(db.Query.Get("one", Sid, Session));
        Assert.Empty(db.Query.ListRecoverable(Sid, Session)); Assert.Equal(before, db.State());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TerminalAndActivatedProjectionStillRequireTheirStrictChain(bool activated)
    {
        using var db = new RecurringPlanSetupTestFixture(); db.Create("one", true);
        if (activated) db.Activate("one"); else db.Reject("one");
        Assert.NotNull(db.Query.Get("one", Sid, Session));
        db.Corrupt("UPDATE recurring_consent_leases SET version = version + 7;");
        var before = db.State(); Assert.Null(db.Query.Get("one", Sid, Session));
        Assert.Null(db.Query.GetPrepared("one", Sid, Session)); Assert.Equal(before, db.State());
    }
}
