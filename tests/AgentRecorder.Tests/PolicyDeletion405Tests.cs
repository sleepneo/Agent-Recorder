using System.Net;
using System.Text.Json;
using AgentRecorder.Persistence;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;
namespace AgentRecorder.Tests;
[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class PolicyDeletion405Tests
{
    private static RetentionDeletionPolicy Policy(DeletionRequestFixture f,int n=1)=>new(f.F.Clock.GetUtcNow().AddSeconds(-1),n);
    private static string Body(DeletionRequestFixture f,RetentionDeletionPolicy? policy)=>JsonSerializer.Serialize(new {recording_id=f.F.Row.RecordingId,policy=policy is null?null:new {completed_before=policy.CompletedBefore.ToString("O"),keep_latest_count=policy.KeepLatestCount}});
    private static void Newer(DeletionRequestFixture f,string id="newer") {
        var row=f.F.Db.Recording(id,completed:true) with {OutputPath=Path.Combine(f.F.Videos,id+".mp4"),CompletedAtUtc=f.F.Clock.GetUtcNow().AddSeconds(-2)};
        f.F.Db.Ordinary.Register(PublicationAnchorTestFixture.Anchored(row) with {BundleRegistration=new(null)});
    }
    private static async Task<string> Create(DeletionRequestFixture f,RetentionDeletionPolicy p,string key="policy-key") {
        var r=await f.Post(key,Body(f,p));Assert.Equal(HttpStatusCode.Accepted,r.Status);return r.Json.GetProperty("data").GetProperty("deletion_request_id").GetString()!;
    }
    [Theory][InlineData(1)][InlineData(1000)] public async Task PrecheckKeepsWithoutUiPermitOrNativeAndReplayIsDurable(int n) {
        using var f=new DeletionRequestFixture();var p=Policy(f,n);var id=await Create(f,p);var done=await f.Finish(id);
        Assert.Equal("within_keep_latest",done.GetProperty("reason_code").GetString());Assert.False(done.GetProperty("file_action_performed").GetBoolean());Assert.Equal(0,f.F.Native.Calls);Assert.Equal(0,f.F.Ui.Begins);Assert.Equal(0,f.Scalar("SELECT count(*) FROM ordinary_deletion_operations;"));
        var replay=await Create(f,p);Assert.Equal(id,replay);Assert.Equal(0,f.F.Ui.Begins);Assert.Equal(1,f.Scalar("SELECT count(*) FROM ordinary_deletion_request_policies;"));
    }
    [Fact] public async Task TimePrecheckAndPolicyHashConflict() {
        using var f=new DeletionRequestFixture();Newer(f);var p=new RetentionDeletionPolicy(f.F.Row.CompletedAtUtc!.Value,1);var id=await Create(f,p);var done=await f.Finish(id);
        Assert.Equal("completion_not_before_cutoff",done.GetProperty("reason_code").GetString());Assert.Equal(0,f.F.Ui.Begins);Assert.Equal(0,f.F.Native.Calls);
        Assert.Equal(HttpStatusCode.Conflict,(await f.Post("policy-key",Body(f,Policy(f,2)))).Status);
        Assert.Equal(HttpStatusCode.Conflict,(await f.Post("policy-key",JsonSerializer.Serialize(new{recording_id=f.F.Row.RecordingId}))).Status);
    }
    [Theory][InlineData(false)][InlineData(true)] public async Task ApprovedOrRejectedPolicyIsBoundThroughIntentAndLedger(bool approve) {
        using var f=new DeletionRequestFixture();Newer(f);var p=Policy(f);var id=await Create(f,p);await f.F.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(p,f.F.Ui.Intent!.Facts.Policy);f.F.Ui.Decide(approve?LocalRetentionUiChoice.Approve:LocalRetentionUiChoice.Reject);f.F.Ui.Retire.TrySetResult(true);
        var result=await f.Finish(id);Assert.Equal(approve?1:0,f.F.Native.Calls);Assert.Equal(p.KeepLatestCount,result.GetProperty("policy").GetProperty("keep_latest_count").GetInt32());
        Assert.Equal(approve?1:0,f.Scalar("SELECT count(*) FROM ordinary_deletion_operation_policies;"));
        Assert.Equal(id,await Create(f,p));Assert.Equal(1,f.F.Ui.Begins);Assert.Equal(approve?1:0,f.F.Native.Calls);
    }
    [Fact] public async Task ApprovalCannotOverrideLatestSetChange() {
        using var f=new DeletionRequestFixture();Newer(f);var p=Policy(f);var id=await Create(f,p);await f.F.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The newer row ceases to be completed while human confirmation is pending.
        f.F.Db.Corrupt("UPDATE ordinary_recording_history SET status_code='failed' WHERE recording_id='newer';");
        await f.Approve();var done=await f.Finish(id);Assert.Equal("within_keep_latest",done.GetProperty("reason_code").GetString());Assert.False(done.GetProperty("file_action_performed").GetBoolean());Assert.Equal(0,f.F.Native.Calls);
        Assert.Equal(1,f.Scalar("SELECT count(*) FROM ordinary_deletion_operation_policies;"));
    }
    [Theory][InlineData(false)][InlineData(true)] public async Task NewReferenceOrMissingOperationPolicyNeverOverridesProtection(bool corrupt) {
        using var f=new DeletionRequestFixture();Newer(f);var id=await Create(f,Policy(f));await f.F.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if(!corrupt)f.F.Db.SeedFuture("new-plan",outputPath:f.F.Media);
        else f.Database.OnStep=(name,c,t)=>{if(name=="reserve_after_close")f.F.Db.Corrupt("UPDATE ordinary_deletion_request_policies SET policy_keep_count=0;");};
        await f.Approve();
        if(corrupt){Assert.True(await f.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(6)));using var response=await f.Client.GetAsync(DeletionRequestFixture.Endpoint+"/"+id);Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);}
        else await f.Finish(id);
        Assert.Equal(0,f.F.Native.Calls);
    }
    [Fact] public async Task UpgradeV48PreservesLegacyRowsAndAddsNoInventedPolicy() {
        using var f=new DeletionRequestFixture();var id=await f.Create();await f.Approve();await f.Finish(id);
        var old=SqlitePublicationIdentityReferenceTests.OldBytes(f.F.Db.Store.DatabasePath,true,48);
        f.F.Db.RawExecute("DROP TABLE ordinary_deletion_host_rejections; DELETE FROM schema_migrations WHERE version=51; DROP TABLE ordinary_deletion_batch_policy_children; DROP TABLE ordinary_deletion_batch_policies; DELETE FROM schema_migrations WHERE version=50; DROP TABLE ordinary_deletion_operation_policies; DROP TABLE ordinary_deletion_request_policies; DELETE FROM schema_migrations WHERE version=49;");
        f.F.Db.Store.Initialize();Assert.Equal(old,SqlitePublicationIdentityReferenceTests.OldBytes(f.F.Db.Store.DatabasePath,true,48));
        Assert.Equal(51,SqliteOperationalStore.CurrentSchemaVersion);Assert.Equal(SqliteSchemaV49.Checksum,SqliteSchemaV1.ComputeChecksum(SqliteSchemaV49.Definition));Assert.Equal(0,f.Scalar("SELECT count(*) FROM ordinary_deletion_request_policies;"));
        Assert.Equal(id,(await f.Post()).Json.GetProperty("data").GetProperty("deletion_request_id").GetString());Assert.Equal(1,f.F.Native.Calls);
    }
    private sealed class Gate:RetentionGuardDatabase {
        internal Action<SqliteConnection,SqliteTransaction>? Enter;
        internal override SqliteTransaction Begin(SqliteConnection c){var t=base.Begin(c);Enter?.Invoke(c,t);return t;}
    }
    [Fact] public async Task ImmediateExecutionTransactionBlocksPolicyAffectingWriterUntilDisposition() {
        var gate=new Gate();using var f=new DeletionRequestFixture(guard:gate);Newer(f);var id=await Create(f,Policy(f));
        using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.Enter=(_,_)=>{entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));};
        await f.Approve();await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string writerSql="";
        try { using var c=f.F.Writer();SqliteOutputPathCollation.Register(c);c.DefaultTimeout=1;using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA busy_timeout=50;";cmd.ExecuteNonQuery();var columns=new List<string>();using(var schema=c.CreateCommand()){schema.CommandText="PRAGMA table_info(ordinary_recording_history);";using var reader=schema.ExecuteReader();while(reader.Read())columns.Add(reader.GetString(1));}
            var values=columns.Select(name=>name=="recording_id"?"'policy-writer-newer'":name=="history_sequence"?"(SELECT MAX(history_sequence)+1 FROM ordinary_recording_history)":name=="output_path"?"output_path||'.new.mp4'":name);
            cmd.CommandText=writerSql="INSERT INTO ordinary_recording_history("+string.Join(',',columns)+") SELECT "+string.Join(',',values)+" FROM ordinary_recording_history WHERE recording_id='newer';";var error=Assert.Throws<SqliteException>(()=>cmd.ExecuteNonQuery());Assert.True(error.SqliteErrorCode==5,error.ToString());Assert.Equal(0,f.F.Native.Calls); }
        finally{release.Set();}
        await f.Finish(id);Assert.Equal(1,f.F.Native.Calls);
        using var after=f.F.Writer();SqliteOutputPathCollation.Register(after);using var write=after.CreateCommand();write.CommandText=writerSql;Assert.Equal(1,write.ExecuteNonQuery());
    }
    [Theory][InlineData("null")][InlineData("{}")][InlineData("{\"completed_before\":null,\"keep_latest_count\":1}")][InlineData("{\"completed_before\":\"2026-10-06T00:00:00Z\",\"keep_latest_count\":1.0}")][InlineData("{\"completed_before\":\"2026-10-06T00:00:00Z\",\"keep_latest_count\":1001}")][InlineData("{\"completed_before\":\"2026-10-06T00:00:00Z\",\"keep_latest_count\":1,\"keep_latest_count\":2}")]
    public async Task InvalidPolicyFailsBeforePersistence(string json) {
        using var f=new DeletionRequestFixture();var body="{\"recording_id\":\"candidate\",\"policy\":"+json+"}";Assert.Equal(HttpStatusCode.BadRequest,(await f.Post("bad-key",body)).Status);Assert.Equal(0,f.Scalar("SELECT count(*) FROM ordinary_deletion_requests;"));Assert.Equal(0,f.F.Ui.Begins);Assert.Equal(0,f.F.Native.Calls);
    }
    [Theory][InlineData("0")][InlineData("-1")][InlineData("null")][InlineData("true")][InlineData("\"1\"")][InlineData("1e0")][InlineData("9223372036854775808")]
    public async Task InvalidCountCannotCreateAnyRequest(string n) {
        using var f=new DeletionRequestFixture();var body="{\"recording_id\":\"candidate\",\"policy\":{\"completed_before\":\"2026-10-06T00:00:00Z\",\"keep_latest_count\":"+n+"}}";
        Assert.Equal(HttpStatusCode.BadRequest,(await f.Post("bad-count",body)).Status);Assert.Equal(0,f.Scalar("SELECT count(*) FROM ordinary_deletion_requests;"));
    }
    [Fact] public async Task CanonicalUtcAndJsonWhitespaceReplaySameRequestButBatchRejectsPolicy() {
        using var f=new DeletionRequestFixture();var p=Policy(f);var id=await Create(f,p);await f.Finish(id);
        var body=Body(f,p).Replace("+00:00","Z").Replace("\\u002B00:00","Z");Assert.Contains("Z",body);body="  "+body+" ";Assert.Equal(id,(await f.Post("policy-key",body)).Json.GetProperty("data").GetProperty("deletion_request_id").GetString());
        using var response=await f.Client.PostAsync("/api/v1/retention/batch-deletion-requests",new System.Net.Http.StringContent("{\"recording_ids\":[\"candidate\"],\"policy\":{}}",System.Text.Encoding.UTF8,"application/json"));Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
        Assert.Equal(0,f.F.Native.Calls);Assert.Equal(0,f.F.Ui.Begins);
    }
    [Theory][InlineData(2)][InlineData(3)]
    public async Task CloseAndEscapeWithPolicyNeverDelete(int choice) {
        using var f=new DeletionRequestFixture();Newer(f);var id=await Create(f,Policy(f));await f.F.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));f.F.Ui.Decide((LocalRetentionUiChoice)choice);f.F.Ui.Retire.TrySetResult(true);await f.Finish(id);Assert.Equal(0,f.F.Native.Calls);
    }
    [Theory][InlineData("expire")][InlineData("cancel")][InlineData("unknown_ui")]
    public async Task PolicyRetainsOriginalStopExpiryAndUnknownRetirementRules(string ending) {
        using var f=new DeletionRequestFixture();Newer(f);var id=await Create(f,Policy(f));await f.F.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if(ending=="expire")f.F.Clock.Advance(61);else if(ending=="cancel")f.Coordinator.Dispose();else f.F.Ui.Decide(LocalRetentionUiChoice.Approve);
        f.F.Ui.Retire.TrySetResult(ending!="unknown_ui");var retired=await f.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(6));Assert.Equal(ending!="unknown_ui",retired);Assert.Equal(0,f.F.Native.Calls);
        if(ending=="unknown_ui")Assert.Equal(HttpStatusCode.ServiceUnavailable,(await f.Post("another",Body(f,Policy(f)))).Status);
    }
    [Fact] public async Task CorruptPolicyResultCannotHideAnAcceptedDisposition() {
        using var f=new DeletionRequestFixture();Newer(f);var id=await Create(f,Policy(f));await f.Approve();var done=await f.Finish(id);Assert.True(done.GetProperty("file_action_performed").GetBoolean());
        f.F.Db.Corrupt("UPDATE ordinary_deletion_request_policies SET policy_reason='within_keep_latest';");
        using var response=await f.Client.GetAsync(DeletionRequestFixture.Endpoint+"/"+id);Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);Assert.Equal(1,f.F.Native.Calls);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task MissingOrCorruptPreparedOperationPolicyCannotExecute(bool corrupt) {
        var ledger=new DeletionRequestFixture.Io();using var f=new DeletionRequestFixture(ledger:ledger);Newer(f);
        var id=await Create(f,Policy(f));await f.F.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ledger.OnStep=(name,_,_)=>{if(name=="prepare_after_close")f.F.Db.Corrupt(corrupt
            ?"UPDATE ordinary_deletion_operation_policies SET policy_digest='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';"
            :"DELETE FROM ordinary_deletion_operation_policies;");};
        await f.Approve();Assert.True(await f.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(9)));Assert.Equal(0,f.F.Native.Calls);
        using var response=await f.Client.GetAsync(DeletionRequestFixture.Endpoint+"/"+id);Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);
    }
    [Theory][InlineData("9999-01-01T00:00:00Z")][InlineData("2026-10-06T00:00:00")]
    [InlineData("2026-10-06T08:00:00+08:00")][InlineData(" 2026-10-06T00:00:00Z")][InlineData("invalid")]
    public async Task InvalidPolicyCutoffCannotCreateRequest(string cutoff) {
        using var f=new DeletionRequestFixture();var body=JsonSerializer.Serialize(new {recording_id="candidate",policy=new {completed_before=cutoff,keep_latest_count=1}});
        Assert.Equal(HttpStatusCode.BadRequest,(await f.Post("invalid-cutoff",body)).Status);Assert.Equal(0,f.Scalar("SELECT count(*) FROM ordinary_deletion_requests;"));Assert.Equal(0,f.F.Ui.Begins);Assert.Equal(0,f.F.Native.Calls);
    }
    [Fact] public async Task MissingPolicyCannotDowngradeToLegacy() {
        using var f=new DeletionRequestFixture();var id=await Create(f,Policy(f));await f.Finish(id);
        f.F.Db.Corrupt("DELETE FROM ordinary_deletion_request_policies;");
        using var response=await f.Client.GetAsync(DeletionRequestFixture.Endpoint+"/"+id);Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);Assert.Equal(0,f.F.Native.Calls);
    }
}
