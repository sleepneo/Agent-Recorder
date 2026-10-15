using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;
using static AgentRecorder.Tests.RetentionGuardFixture;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RetentionGuardRelationTests
{
    [Theory]
    [InlineData("ordinary")][InlineData("run")][InlineData("required")][InlineData("future")]
    public async Task AllSharedOutputSourcesAcrossForeignIdentity(string source)
    {
        using var f = new RetentionSharedOutputPathTests.Fixture(root: Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task356", Guid.NewGuid().ToString("N")));
        var path = source == "run" ? f.Run() : f.PathFor(); var candidate = f.Seed(path: path);
        if (source == "ordinary") f.Seed("FOREIGN", path: path, status: "failed", at: f.Before.AddDays(1));
        if (source == "required") await f.Required(path);
        if (source == "future") f.Future(path);
        using var g = new RetentionReferenceGuard(f.Db.Store, f.Db.Ordinary, Sid, Session, new()); var calls = 0;
        Assert.Equal(RetentionGuardReason.SharedPathProtected, await Finish(g.Execute(candidate.RecordingId, candidate.PublicationReceipt!.Anchor, f.Before, _ => { calls++; return RetentionGuardContinuationResult.Completed; })));
        Assert.Equal(0, calls); f.NoProbes();
    }
    [Theory]
    [InlineData("standing")][InlineData("recurring")][InlineData("required")][InlineData("future")]
    public async Task AllFrozenPlanSourcesAreCheckedInsideGuard(string source)
    {
        using var f = new RetentionSharedOutputPathTests.Fixture(root: Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task356", Guid.NewGuid().ToString("N")));
        var path = await RetentionPlannedOutputPathTests.Purpose(f, source); var candidate = f.Seed(path: path);
        using var g = new RetentionReferenceGuard(f.Db.Store, f.Db.Ordinary, Sid, Session, new()); var calls = 0;
        Assert.Equal(RetentionGuardReason.PlannedPathProtected, await Finish(g.Execute(candidate.RecordingId, candidate.PublicationReceipt!.Anchor, f.Before, _ => { calls++; return RetentionGuardContinuationResult.Completed; })));
        Assert.Equal(0, calls); f.NoProbes();
    }
    [Theory]
    [InlineData("parent")][InlineData("child")][InlineData("role")][InlineData("invalid")]
    public async Task NestedBidirectionalRolesAndBadFactsNeverContinue(string relation)
    {
        using var f = new RetentionGuardFixture();
        if (relation == "child") f.Db.Ordinary.Register(f.Db.Recording("other") with { ParentRecordingId = f.Row.RecordingId });
        else f.Db.Corrupt(relation == "parent" ? "UPDATE ordinary_recording_history SET parent_recording_id='foreign';" :
            relation == "role" ? "UPDATE ordinary_recording_history SET nested_role='outer';" : "UPDATE ordinary_recording_history SET nested_role='invalid';");
        using var g = f.Guard(); var calls = 0;
        Assert.Equal(relation == "invalid" ? RetentionGuardReason.RelationUnknown : RetentionGuardReason.NestedProtected,
            await Finish(f.Execute(g, _ => { calls++; return RetentionGuardContinuationResult.Completed; })));
        Assert.Equal(0, calls);
    }
    [Theory]
    [InlineData("ordinary")][InlineData("future")][InlineData("standing")][InlineData("recurring")][InlineData("required_once")]
    public async Task AllThreeSavedBinaryIdentitySourcesProtectIndependentPaths(string source)
    {
        using var f = new RetentionGuardFixture("same-id");
        if (source == "ordinary")
        {
            var other = f.Db.Recording("SAME-ID", at: f.Cutoff.AddDays(1), completed: true) with { BundleRegistration = new(null) };
            other = other with { PublicationReceipt = PublicationReceipt.IssueForTests(other.OutputPath, other.OutputSizeBytes!.Value, f.Anchor.Identity) };
            f.Db.Ordinary.Register(other);
        }
        else RetentionPublicationIdentityTests.PrivateForeign(f.Db, f.Anchor, source, f.Row.RecordingId, malformed: true);
        using var g = f.Guard(); var calls = 0;
        Assert.Equal(RetentionGuardReason.IdentityProtected, await Finish(f.Execute(g, _ => { calls++; return RetentionGuardContinuationResult.Completed; })));
        Assert.Equal(0, calls);
    }
    [Theory]
    [InlineData("case", true)][InlineData("unicode_case", true)][InlineData("normalization", false)][InlineData("different", false)]
    public async Task RawPathCollationAndBinarySelfExclusionRemainUnchanged(string variant, bool protectedPath)
    {
        using var f = new RetentionGuardFixture(); var other = variant switch
        { "case" => f.Row.OutputPath.ToUpperInvariant(), "unicode_case" => f.Row.OutputPath.Replace("Ä", "ä"), "normalization" => f.Row.OutputPath.Replace("Ä", "A\u0308"), _ => f.Row.OutputPath + ".other" };
        f.Db.Ordinary.Register(f.Db.Recording("other") with { OutputPath = other }); using var g = f.Guard();
        Assert.Equal(protectedPath ? RetentionGuardReason.SharedPathProtected : RetentionGuardReason.Checked, await Finish(f.Execute(g)));
    }
    [Theory]
    [InlineData("fixed")][InlineData("required")][InlineData("recurring")][InlineData("profile")]
    public async Task AllFourPendingSourcesExactTemplateAndUnknown(string source)
    {
        var name = source is "profile" or "recurring" ? RecurringFixedRegionProfileOutput.RenderOutputFileName(source == "profile" ? "synthetic" : "test", RecurringOccurrenceIdentity.Prefix + new string('a', 64), At) : "test.mp4";
        using var f = new RetentionGuardFixture(name: name); var directory = Path.GetDirectoryName(f.Row.OutputPath)!;
        if (source == "fixed") f.Fixed("foreign", name);
        if (source == "required")
        {
            var request = new RequiredOncePlanSetupRequestSnapshot("foreign", "key", "foreign-sid", "foreign-session", At, At.AddMinutes(12), At.AddMinutes(10), At.AddMinutes(11), At.AddMinutes(13), TimeSpan.FromSeconds(30), directory, name);
            Assert.Equal(RequiredOncePlanSetupWriteStatus.Created, new SqliteRequiredOncePlanSetupRepository(f.Db.Store).CreateOrGet(request, At).Status);
        }
        if (source == "recurring")
        {
            var schedule = RecurringPlanSchedule.CreateDaily(TimeZoneInfo.Utc.Id, new(2026, 10, 5), new(2026, 10, 6), new(1, 0), 2, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
            var request = RecurringSetupIntentSnapshot.CreateForTrustedSetupAdapter("foreign", "key", "foreign-sid", "foreign-session", At, At.AddDays(2), schedule, 2, TimeSpan.FromSeconds(60), At.AddDays(2), directory, "test");
            Assert.Equal(RecurringSetupIntentResultStatus.Created, new RecurringSetupIntentService(f.Db.Store, () => At).CreateOrGet(request).Result);
        }
        if (source == "profile") new SqliteRecurringFixedRegionProfileRepository(f.Db.Store).CreateVersion1("foreign", At, UnifiedHistoryProfileFilterTests.Specification(directory));
        // Preserve the existing helper's immutable "synthetic" prefix.
        using var g = f.Guard(); var known = await Finish(f.Execute(g));
        Assert.Equal(source is "fixed" or "required" ? RetentionGuardReason.PendingExactProtected : RetentionGuardReason.PendingTemplateProtected, known);
        var table = source == "profile" ? "recurring_fixed_region_profile_versions" : source == "required" ? "required_once_setup_intents" : "setup_intents";
        var column = source == "profile" ? "filename_template" : source == "recurring" ? "recurring_filename_prefix" : "frozen_file_name";
        f.Db.Corrupt($"UPDATE {table} SET {column}={column}||char(0)||'PRIVATE';");
        Assert.Equal(RetentionGuardReason.RelationUnknown, await Finish(f.Execute(g)));
    }
    [Theory]
    [InlineData(500)][InlineData(501)]
    public async Task SingleCandidateStillHonorsTotalFiveHundredRelatedRows(int count)
    {
        using var f = new RetentionGuardFixture(); f.Fixed("foreign", "other.mp4");
        using (var c = f.Writer())
        {
            using var columns = c.CreateCommand(); columns.CommandText = "PRAGMA table_info(setup_intents);";
            var names = new List<string>(); using (var r = columns.ExecuteReader()) while (r.Read()) names.Add(r.GetString(1));
            using var t = c.BeginTransaction();
            for (var i = 1; i < count; i++)
            {
                using var copy = c.CreateCommand(); copy.Transaction = t;
                copy.CommandText = "INSERT INTO setup_intents SELECT " + string.Join(",", names.Select(n => n is "intent_id" or "idempotency_key" ? "$id" : n)) + " FROM setup_intents WHERE intent_id='foreign';";
                copy.Parameters.AddWithValue("$id", "copy-" + i); copy.ExecuteNonQuery();
            }
            t.Commit();
        }
        using var g = f.Guard(); var calls = 0;
        Assert.Equal(count == 500 ? RetentionGuardReason.Checked : RetentionGuardReason.Unavailable, await Finish(f.Execute(g, _ => { calls++; return RetentionGuardContinuationResult.Completed; })));
        Assert.Equal(count == 500 ? 1 : 0, calls);
    }
}
