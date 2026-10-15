using System.Net;
using System.Text;
using System.Text.Json;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class MediaOnlyPublicationTests
{
    [Fact]
    public async Task LiveActualPublisherCleanupKeepsClaimAndQueriesCannotRetireIt()
    {
        using var phase=new OriginalFileHoldFixture.Phase();using var f=new MediaOnlyFixture();f.BeforeVideoCleanup=phase.Block;var rec=await f.Create(-1);f.Tray.Confirm!(ConfirmationDecision.Approve());
        try
        {
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));Assert.False(rec.IsFinalized);Assert.True(RuntimeOutputTestEngine.Read(f.Engine,rec.OutputPath)!.Paths.Single().InUse);
            var id=await f.DeleteRequest(rec);Assert.True(await f.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(8)));await f.ReadRequest(id);Assert.Equal(0,f.Ui.Begins);Assert.Equal(0,f.NativeApi.Calls);Assert.True(RuntimeOutputTestEngine.Read(f.Engine,rec.OutputPath)!.Paths.Single().InUse);
        }
        finally{phase.Release();}
        await MediaOnlyFixture.Until(()=>rec.IsFinalized&&!RuntimeOutputTestEngine.Read(f.Engine,rec.OutputPath)!.Paths.Single().InUse);Assert.Equal("not_applicable",f.Writer.Read(rec.Id)!.Bundle!.Status);Assert.NotNull(rec.LastMeta!.PublicationReceipt);
    }
    [Fact]
    public async Task InvalidFormalFinalProbeCannotBecomeAnEligibleMediaOnlyFile()
    {
        using var f=new MediaOnlyFixture();f.FailMedia=true;var rec=await f.Create(-1);f.Tray.Confirm!(ConfirmationDecision.Approve());await MediaOnlyFixture.Until(()=>rec.IsFinalized);Assert.Equal(RecState.failed,rec.State);Assert.Null(rec.LastMeta!.PublicationReceipt);Assert.Equal(0,f.GeneratorCalls);
        var id=await f.DeleteRequest(rec);Assert.True(await f.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(8)));await f.ReadRequest(id);Assert.Equal(0,f.Ui.Begins);Assert.Equal(0,f.NativeApi.Calls);
    }
    [Theory][InlineData(false,false)][InlineData(true,true)]
    public async Task SameFormalPublishedMediaReachesActualApiOriginalHandleAndReplayPreservesNewObject(bool quick,bool manualAndDirectoryChange)
    {
        using var f=new MediaOnlyFixture();var rec=await f.Create(-1,quick,manualAndDirectoryChange);await f.Complete(rec,manualAndDirectoryChange,manualAndDirectoryChange);
        Assert.Equal(f.Videos,Path.GetDirectoryName(rec.OutputPath));var row=f.Writer.Read(rec.Id)!;Assert.Equal("not_applicable",row.Bundle!.Status);Assert.Null(row.MediaHash);Assert.Null(rec.OrdinaryBundleOperation);Assert.Equal(0,f.GeneratorCalls);Assert.False(Directory.Exists(Path.ChangeExtension(rec.OutputPath,".bundle")));Assert.DoesNotContain(Directory.GetFiles(f.Videos),p=>!p.EndsWith(".mp4",StringComparison.OrdinalIgnoreCase));
        var probe=FfmpegCaptureBackend.Probe(rec.OutputPath);Assert.Equal(64,probe.Width);Assert.Equal(64,probe.Height);Assert.True(probe.DurationSeconds>0);var original=rec.LastMeta!.PublicationReceipt!.Anchor;Assert.Equal(row.OutputSizeBytes,original.Length);Assert.Equal(original.Identity,WindowsReadOnlyFileMetadataProbe.Instance.Observe(rec.OutputPath).Before!.Identity);f.NativeApi.Expected=original;
        var before=Snapshot(f);var id=await f.DeleteRequest(rec);await f.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(0,f.NativeApi.Calls);Assert.Equal(0,f.Scalar("SELECT count(*) FROM ordinary_deletion_operations;"));
        f.NativeApi.Before=()=>
        {
            using var c=f.Store.OpenReadOnlyConnection();using var cmd=c.CreateCommand();cmd.CommandText="SELECT r.phase,r.approval_id,o.approval_id,r.recording_id,o.recording_id,o.phase,o.volume_id,o.file_id FROM ordinary_deletion_requests r JOIN ordinary_deletion_operations o ON o.approval_id=r.approval_id WHERE r.request_id=$id;";cmd.Parameters.AddWithValue("$id",id);
            using(var r=cmd.ExecuteReader()){Assert.True(r.Read());Assert.Equal("execution_reserved",r.GetString(0));Assert.Equal(r.GetString(1),r.GetString(2));Assert.Equal(rec.Id,r.GetString(3));Assert.Equal(rec.Id,r.GetString(4));Assert.Equal("prepared_unknown",r.GetString(5));Assert.Equal(8,((byte[])r.GetValue(6)).Length);Assert.Equal(16,((byte[])r.GetValue(7)).Length);Assert.False(r.Read());}
            OrdinaryPreparationFixture.AssertConflict(f.Engine,rec.OutputPath);
            using var writer=new SqliteConnection($"Data Source={f.Store.DatabasePath};Mode=ReadWrite;Pooling=False;Default Timeout=1");writer.Open();Assert.Equal(5,Assert.Throws<SqliteException>(()=>RetentionGuardFixture.Write(writer,"UPDATE ordinary_recording_history SET source_title='blocked';")).SqliteErrorCode);
        };
        f.Ui.Decide(AgentRecorder.Persistence.LocalRetentionUiChoice.Approve);await Task.Delay(70);Assert.Equal(0,f.NativeApi.Calls);f.Ui.Retire.TrySetResult(true);
        Assert.True(await f.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(9)));var final=await f.ReadRequest(id);Assert.True(final.GetProperty("disposition_progress").GetString()=="accepted",JsonSerializer.Serialize(new{response=final,actual=f.DeletionResult()}));Assert.True(final.GetProperty("file_action_performed").GetBoolean());Assert.True(final.GetProperty("original_resources_retired").GetBoolean());Assert.True(final.GetProperty("ledger_resources_retired").GetBoolean());Assert.True(final.GetProperty("our_resources_retired").GetBoolean());Assert.Equal("committed",final.GetProperty("ledger_settlement").GetString());Assert.True(f.NativeApi.DeletePending);Assert.Equal(1,f.NativeApi.Calls);Assert.Equal(before,Snapshot(f));
        using(var h=WindowsFileMetadataApi.Instance.Open(@"\\?\"+rec.OutputPath,0x80,7,3,0x00200000,out var error)){Assert.True(h.IsInvalid);Assert.Equal(2,error);}
        var replacement=Enumerable.Range(0,16).Select(i=>(byte)i).ToArray();using(var file=new FileStream(rec.OutputPath,FileMode.CreateNew,FileAccess.Write))file.Write(replacement);var newIdentity=WindowsReadOnlyFileMetadataProbe.Instance.Observe(rec.OutputPath).Before!.Identity;Assert.NotEqual(original.Identity,newIdentity);
        var replay=await f.DeleteRequest(rec);Assert.Equal(id,replay);await f.ReadRequest(id);Assert.Equal(1,f.Ui.Begins);Assert.Equal(1,f.NativeApi.Calls);Assert.Equal(replacement,File.ReadAllBytes(rec.OutputPath));Assert.Equal(newIdentity,WindowsReadOnlyFileMetadataProbe.Instance.Observe(rec.OutputPath).Before!.Identity);Assert.Equal(before,Snapshot(f));
        File.WriteAllText(Path.Combine(f.Root,"formal-media-deletion.json"),JsonSerializer.Serialize(new{root=f.Root,recording_id=rec.Id,request_id=id,operation_id=final.GetProperty("operation_id").GetString(),media=rec.OutputPath,original,generator= f.Generator.GetType().FullName,generator_calls=f.GeneratorCalls,formal_mp4=new{probe.Width,probe.Height,probe.DurationSeconds,original.Length},bundle=row.Bundle.Status,media_hash_recorded=false,manual=manualAndDirectoryChange,quick,local_directory_changed=manualAndDirectoryChange,native_calls=f.NativeApi.Calls,delete_pending=f.NativeApi.DeletePending,result=final,replacement_identity=newIdentity,replacement_bytes_preserved=true,history_and_anchor_unchanged=true}),new UTF8Encoding(false));
    }
    [Theory][InlineData(0,false)][InlineData(1,false)][InlineData(1,true)]
    public async Task SameProductionGeneratorDefaultAndTrueReadyOrFailedRemainProtected(int policy,bool fail)
    {
        using var f=new MediaOnlyFixture();f.FailBundle=fail;var rec=await f.Create(policy);await f.Complete(rec);await MediaOnlyFixture.Until(()=>f.Writer.Read(rec.Id)?.Bundle?.Status==(fail?"failed":"ready"));
        var row=f.Writer.Read(rec.Id)!;Assert.Equal(1,f.GeneratorCalls);Assert.NotNull(rec.OrdinaryBundleOperation);Assert.NotNull(rec.LastMeta!.PublicationReceipt);Assert.Equal("completed",row.Status);if(!fail){Assert.Equal(5,Directory.GetFiles(row.Bundle!.Path!).Length);Assert.NotNull(row.MediaHash);}
        var before=Snapshot(f);var bytes=File.ReadAllBytes(rec.OutputPath);var id=await f.DeleteRequest(rec);Assert.True(await f.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(8)));var query=await f.ReadRequest(id);Assert.Equal(0,f.Ui.Begins);Assert.Equal(0,f.NativeApi.Calls);Assert.Equal(JsonValueKind.Null,query.GetProperty("operation_id").ValueKind);Assert.Equal(bytes,File.ReadAllBytes(rec.OutputPath));Assert.Equal(before,Snapshot(f));
        File.WriteAllText(Path.Combine(f.Root,"formal-bundle-protection.json"),JsonSerializer.Serialize(new{recording_id=rec.Id,request_id=id,policy,fail,bundle=row.Bundle,generator=f.Generator.GetType().FullName,generator_calls=f.GeneratorCalls,native_calls=0,local_ui_calls=0,history_and_anchor_unchanged=true}),new UTF8Encoding(false));
    }
    [Fact]
    public async Task GeneratingBundleAndSequentialPoliciesDoNotBecomeNotApplicableOrContaminateNextRequest()
    {
        using var f=new MediaOnlyFixture();var first=await f.Create(-1);await f.Complete(first);Assert.Equal("not_applicable",f.Writer.Read(first.Id)!.Bundle!.Status);Assert.Equal(0,f.GeneratorCalls);
        f.GenerateRelease.Reset();var second=await f.Create(1,true);await f.Complete(second);await MediaOnlyFixture.Until(()=>f.GeneratorCalls==1);Assert.Equal("generating",f.Writer.Read(second.Id)!.Bundle!.Status);
        try{var id=await f.DeleteRequest(second);Assert.True(await f.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(8)));await f.ReadRequest(id);Assert.Equal(0,f.Ui.Begins);Assert.Equal(0,f.NativeApi.Calls);Assert.Equal("not_applicable",f.Writer.Read(first.Id)!.Bundle!.Status);}
        finally{f.GenerateRelease.Set();}
        await MediaOnlyFixture.Until(()=>f.Writer.Read(second.Id)?.Bundle?.Status=="ready");Assert.False(first.GenerateBundle);Assert.True(second.GenerateBundle);Assert.Equal(1,f.GeneratorCalls);
    }
    [Fact]
    public async Task FormalMediaCanBeLocallyRejectedWithoutAnyNativeDisposition()
    {
        using var f=new MediaOnlyFixture();var rec=await f.Create(-1);await f.Complete(rec);var bytes=File.ReadAllBytes(rec.OutputPath);var id=await f.DeleteRequest(rec);await f.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));f.Ui.Decide(AgentRecorder.Persistence.LocalRetentionUiChoice.Reject);f.Ui.Retire.TrySetResult(true);Assert.True(await f.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(8)));await f.ReadRequest(id);Assert.Equal(0,f.NativeApi.Calls);Assert.Equal(bytes,File.ReadAllBytes(rec.OutputPath));
    }
    private static string Snapshot(MediaOnlyFixture f)
    {
        using var c=f.Store.OpenReadOnlyConnection();using var cmd=c.CreateCommand();cmd.CommandText="SELECT group_concat(h.recording_id||':'||h.status_code||':'||h.output_path||':'||h.output_size_bytes||':'||hex(a.volume_id)||':'||hex(a.file_id)||':'||b.status_code) FROM ordinary_recording_history h JOIN ordinary_publication_anchors a USING(recording_id) JOIN ordinary_recording_bundles b USING(recording_id);";return(string)cmd.ExecuteScalar()!;
    }
}
