using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;
[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class PendingAliasQuery396Tests(ITestOutputHelper output)
{
    private static void Seed(UnifiedHistoryTestDatabase db, string path, string source, string id = "seed", string state = "region_selection_pending")
    {
        if (source == "standing")
        {
            var snapshot = StandingSetupIntentSnapshot.CreateForTrustedSetupAdapter(id, id + "-key", "foreign-sid", "foreign-session", At,
                At.AddMinutes(5), At.AddSeconds(10), At.AddSeconds(20), At.AddSeconds(80), TimeSpan.FromSeconds(30), At.AddHours(1),
                Path.GetDirectoryName(path)!, Path.GetFileName(path));
            Assert.Equal(StandingSetupIntentResultStatus.Created, new StandingSetupIntentService(db.Store, () => At).CreateOrGet(snapshot).Result);
            // Legacy states which no current coordinator emits still belong to the original unfiltered read contract.
            if (state != "region_selection_pending") db.Corrupt("UPDATE setup_intents SET status_code=$state;", ("$state", state));
            return;
        }
        var repo = new SqliteRequiredOncePlanSetupRepository(db.Store);
        var request = new RequiredOncePlanSetupRequestSnapshot(id, id + "-key", "foreign-sid", "foreign-session", At,
            At.AddMinutes(12), At.AddMinutes(10), At.AddMinutes(11), At.AddMinutes(13), TimeSpan.FromSeconds(30), Path.GetDirectoryName(path)!, Path.GetFileName(path));
        var result = repo.CreateOrGet(request, state == "initial_expired" ? At.AddMinutes(12) : At);
        if (state is "creation_approval_pending" or "scheduled")
        {
            result = repo.SaveSelection(id, request.CurrentUserSid, request.SessionBinding,
                new(At.AddSeconds(1), "DISPLAY-SYNTHETIC", new(0,0,1920,1080), new(10,20,640,480), 96,96,1920,1080,
                    AuthorizedDisplayOrientation.Landscape, new string('a',64)), At.AddSeconds(1));
            if (state == "scheduled") result = repo.Activate(id, request.CurrentUserSid, request.SessionBinding, result.State!.Version, "approval", At.AddSeconds(2));
        }
        else if (state is "rejected" or "expired")
            result = repo.SetTerminal(id, request.CurrentUserSid, request.SessionBinding, state,
                state == "rejected" ? "plan_creation_rejected_by_user" : "setup_intent_expired", At.AddSeconds(1));
        Assert.Equal(state == "initial_expired" ? "expired" : state, result.State!.StatusCode);
    }
    private static Dictionary<string,IReadOnlyList<string>> Paths(params string[] paths) => new() { ["candidate"] = paths };
    [Theory]
    [InlineData("standing", "region_selection_pending")][InlineData("standing", "confirmation_pending")]
    [InlineData("standing", "lease_approval_pending")][InlineData("standing", "activated")]
    [InlineData("standing", "rejected")][InlineData("standing", "expired")][InlineData("standing", "cancelled")]
    [InlineData("required", "region_selection_pending")][InlineData("required", "creation_approval_pending")]
    [InlineData("required", "scheduled")][InlineData("required", "rejected")][InlineData("required", "expired")][InlineData("required", "initial_expired")]
    public void ExactBranchPreservesAllOriginalLifecycleRows(string source, string state)
    {
        using var db = new UnifiedHistoryTestDatabase(); var path = Path.Combine(db.Root, "Videos", "PENDIN~1.MP4");
        Seed(db, path, source, state: state);
        Assert.Equal(PendingOutputUseState.SetupExact, db.Query.ReadShortNameReferences(Paths(path), default)["candidate"].Pending);
    }
    [Theory]
    [InlineData("standing", "nul")][InlineData("required", "nul")]
    [InlineData("standing", "oversized")][InlineData("required", "oversized")]
    [InlineData("standing", "blob")][InlineData("required", "blob")]
    [InlineData("standing", "slash")][InlineData("required", "slash")]
    public void DirtySelectedMetadataIsUnknownAndKnownExactWins(string source, string kind)
    {
        using var db = new UnifiedHistoryTestDatabase(); var path = Path.Combine(db.Root, "Videos", "PENDIN~1.MP4"); Seed(db, path, source);
        var table = source == "standing" ? "setup_intents" : "required_once_setup_intents";
        object value = kind switch { "nul" => System.Text.Encoding.UTF8.GetBytes("PENDIN~1.MP4\0suffix"), "oversized" => new string('x',1000000), "blob" => new byte[] {1,2}, _ => "bad/name.mp4" };
        db.Corrupt("UPDATE " + table + " SET frozen_file_name=" + (kind == "nul" ? "CAST($bad AS TEXT)" : "$bad") + ";", ("$bad", value));
        Assert.Equal(PendingOutputUseState.Unknown, db.Query.ReadShortNameReferences(Paths(path), default)["candidate"].Pending);
        Seed(db, path, source, "known");
        Assert.Equal(PendingOutputUseState.SetupExact, db.Query.ReadShortNameReferences(Paths(path), default)["candidate"].Pending);
    }
    [Theory]
    [InlineData(500)][InlineData(501)]
    public void AliasesAndCandidatesShareOneGlobalExactBudget(int count)
    {
        using var db = new UnifiedHistoryTestDatabase();
        var a = Path.Combine(db.Root, "Videos", "A~1.MP4"); var b = Path.Combine(db.Root, "ROOT~1", "Videos", "B~1.MP4");
        // Distinct legal repository rows in two directory spellings and two tables.
        for (var i = 0; i < count; i++) Seed(db, i % 2 == 0 ? a : b, i % 2 == 0 ? "standing" : "required", "seed" + i);
        var paths = Enumerable.Range(0,100).ToDictionary(i => "id"+i, _ => (IReadOnlyList<string>)new[] { a, b, a }, StringComparer.Ordinal);
        var calls = 0;
        db.Query.RetentionPendingOutputCommandForTests = (c,t,sql,args) =>
        {
            calls++; Assert.Equal(4,args.Count); Assert.Contains("LIMIT 501",sql);
            Assert.DoesNotContain("recurring_output_directory",sql); Assert.DoesNotContain("filename_template",sql);
            Assert.DoesNotContain("status_code",sql); Assert.DoesNotContain("LIKE",sql);
            using var explain = c.CreateCommand(); explain.Transaction = t; explain.CommandText = "EXPLAIN QUERY PLAN " + sql;
            foreach (var arg in args) explain.Parameters.AddWithValue(arg.Key,arg.Value);
            using var r = explain.ExecuteReader(); var plan = new List<string>(); while (r.Read()) plan.Add(r.GetString(3));
            foreach (var index in new[] { SqliteSchemaV38.Paths[0].Index, SqliteSchemaV39.Paths[0].Index })
                Assert.Contains(plan,p => p.Contains("SEARCH uses USING") && p.Contains(index));
            Assert.DoesNotContain(plan,p => p.Contains("SCAN uses")); output.WriteLine(string.Join("\n",plan));
        };
        if (count == 501) Assert.Throws<InvalidDataException>(() => db.Query.ReadShortNameReferences(paths,default));
        else Assert.All(db.Query.ReadShortNameReferences(paths,default).Values,p => Assert.Equal(PendingOutputUseState.SetupExact,p.Pending));
        Assert.Equal(1,calls);
    }
    [Theory]
    [InlineData("directory")][InlineData("name")][InlineData("template")]
    public void UnrelatedExactTuplesAndTemplatesDoNotExpandAliasSemantics(string kind)
    {
        using var db = new UnifiedHistoryTestDatabase(); var path = Path.Combine(db.Root,"Videos","PENDIN~1.MP4");
        Seed(db,kind == "directory" ? Path.Combine(db.Root,"Videos2","PENDIN~1.MP4") : kind == "name" ? path + "-other" : path,"standing");
        if (kind == "template") db.Corrupt("UPDATE setup_intents SET output_directory=NULL,frozen_file_name=NULL,recurring_output_directory=$dir,recurring_filename_prefix='PENDIN';",("$dir",Path.GetDirectoryName(path)!));
        Assert.Equal(PendingOutputUseState.None,db.Query.ReadShortNameReferences(Paths(path),default)["candidate"].Pending);
    }
    [Fact]
    public void MaximumAliasRequestHasAtMostSixHundredDirectoryParameters()
    {
        using var db = new UnifiedHistoryTestDatabase();
        var paths = Enumerable.Range(0,100).ToDictionary(i => "id"+i,
            i => (IReadOnlyList<string>)Enumerable.Range(0,3).Select(j => Path.Combine(db.Root,"owned-"+i+"-"+j,"SHORT.MP4")).ToArray());
        var calls = 0;
        db.Query.RetentionPendingOutputCommandForTests = (_,_,sql,args) => { calls++; Assert.Equal(600,args.Count); Assert.Contains("LIMIT 501",sql); };
        Assert.All(db.Query.ReadShortNameReferences(paths,default).Values,p => Assert.Equal(PendingOutputUseState.None,p.Pending));
        Assert.Equal(1,calls);
    }
    [Theory]
    [InlineData("v38")][InlineData("v39")][InlineData("query")][InlineData("cancelled")]
    public void FailureCannotReturnNoReference(string fault)
    {
        using var db = new UnifiedHistoryTestDatabase(); var path = Path.Combine(db.Root,"Videos","PENDIN~1.MP4");
        if (fault == "v38") db.Execute("DROP INDEX " + SqliteSchemaV38.Paths[0].Index);
        if (fault == "v39") db.Execute("DROP INDEX " + SqliteSchemaV39.Paths[0].Index);
        if (fault == "query") db.Query.RetentionPendingOutputCommandForTests = (_,_,_,_) => throw new IOException("owned query failure");
        Assert.ThrowsAny<Exception>(() => db.Query.ReadShortNameReferences(Paths(path),new CancellationToken(fault == "cancelled")));
    }
}
