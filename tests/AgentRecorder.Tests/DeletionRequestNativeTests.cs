using System.Net;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class DeletionRequestNativeTests
{
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public async Task ActualApiLocalDecisionAndOriginalHandleNativeKeepDurableBindingAndNeverTouchReplacement(int mode)
    {
        using var f=new DeletionRequestFixture(real:true);var readOnly=mode==1;if(readOnly)File.SetAttributes(f.F.Media,FileAttributes.ReadOnly);
        try
        {
            var id=await f.Create();await f.F.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(0,f.F.Native.Calls);
            f.F.Native.BeforeRequest=_=>
            {
                using var c=new SqliteConnection($"Data Source={f.F.Db.Store.DatabasePath};Mode=ReadOnly;Pooling=False");c.Open();using var cmd=c.CreateCommand();
                cmd.CommandText="SELECT r.phase,r.recording_id,r.approval_id,o.approval_id,o.phase,o.volume_id,o.file_id,o.output_path FROM ordinary_deletion_requests r JOIN ordinary_deletion_operations o ON o.approval_id=r.approval_id WHERE r.request_id=$id;";cmd.Parameters.AddWithValue("$id",id);
                using(var row=cmd.ExecuteReader()){Assert.True(row.Read());Assert.Equal("execution_reserved",row.GetString(0));Assert.Equal(f.F.Row.RecordingId,row.GetString(1));Assert.Equal(row.GetString(2),row.GetString(3));Assert.Equal("prepared_unknown",row.GetString(4));Assert.Equal(8,((byte[])row.GetValue(5)).Length);Assert.Equal(16,((byte[])row.GetValue(6)).Length);Assert.Equal(f.F.Media,row.GetString(7));Assert.False(row.Read());}
                var ledger=Assert.Single(DeletionLedgerFixture.Reader(f.F).Page());Assert.Equal(f.F.Anchor,ledger.Binding.Anchor);f.F.AssertProtected();
                using var writer=f.F.Writer();Assert.Equal(5,Assert.Throws<SqliteException>(()=>RetentionGuardFixture.Write(writer,"UPDATE ordinary_recording_history SET source_title='blocked';")).SqliteErrorCode);
            };
            await f.Approve();var final=await f.Finish(id);Assert.Equal(1,f.F.Native.Calls);Assert.Equal(1,f.F.Ui.Begins);Assert.Equal("committed",final.GetProperty("ledger_settlement").GetString());
            if(readOnly){Assert.Equal("rejected",final.GetProperty("disposition_progress").GetString());Assert.Equal(5,final.GetProperty("win32_error").GetInt32());Assert.False(final.GetProperty("file_action_performed").GetBoolean());}
            else
            {
                Assert.Equal("accepted",final.GetProperty("disposition_progress").GetString());Assert.True(f.F.Native.DeletePending);f.F.AssertNotAddressable();
                if(mode==2)
                {
                    using(var replacement=new FileStream(f.F.Media,FileMode.CreateNew,FileAccess.Write))replacement.Write(f.F.Bytes);f.F.Replacement=true;
                    var identity=WindowsReadOnlyFileMetadataProbe.Instance.Observe(f.F.Media).Before!.Identity;Assert.NotEqual(f.F.Anchor.Identity,identity);
                    var again=await f.Read(id);Assert.Equal("accepted",again.GetProperty("disposition_progress").GetString());var replay=await f.Post();Assert.Equal(HttpStatusCode.Accepted,replay.Status);
                    Assert.Equal(f.F.Bytes,File.ReadAllBytes(f.F.Media));Assert.Equal(identity,WindowsReadOnlyFileMetadataProbe.Instance.Observe(f.F.Media).Before!.Identity);Assert.Equal(1,f.F.Native.Calls);
                }
            }
            Assert.Equal(1,f.Scalar("SELECT count(*) FROM ordinary_recording_history WHERE status_code='completed';"));Assert.Equal(1,f.Scalar("SELECT count(*) FROM ordinary_publication_anchors;"));
        }
        finally{if(readOnly)File.SetAttributes(f.F.Media,FileAttributes.Normal);}
    }
}
