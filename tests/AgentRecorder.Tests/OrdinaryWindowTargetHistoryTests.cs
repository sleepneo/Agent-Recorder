using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

public sealed class OrdinaryWindowTargetHistoryTests
{
    internal static UnifiedHistoryTestDatabase Database()=>new(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task336",Guid.NewGuid().ToString("N")));
    internal static string Key=>OrdinaryWindowTargetKeyTests.Key;
    internal static string OtherKey=>OrdinaryWindowTargetKey.Create(OrdinaryWindowTargetKeyTests.Plan(hwnd:43),OrdinaryWindowTargetKeyTests.Facts(hwnd:43))!;
    internal static OrdinaryRecordingHistorySnapshot Row(UnifiedHistoryTestDatabase d,string id,bool completed=false)=>d.Recording(id,completed:completed) with {SourceType="window",CaptureSemantics="window_surface",TargetKey=Key};

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FirstRegistrationAndReplaysAreImmutableAndThreeReopenedProjectionsAgree(bool known)
    {
        using var d=Database();var row=Row(d,"r") with {TargetKey=known?Key:null};d.Ordinary.Register(row);d.Ordinary.Register(row);
        Assert.Throws<InvalidOperationException>(()=>d.Ordinary.Register(row with {TargetKey=known?OtherKey:Key}));
        Assert.True(d.Ordinary.TryPublish(row with {Status="recording"}));
        var end=PublicationAnchorTestFixture.Anchored(Row(d,"r",true) with {TargetKey=row.TargetKey,BundleRegistration=new(null)});
        Assert.True(d.Ordinary.TryPublish(end));Assert.Equal(row.TargetKey,d.Ordinary.Read("r")!.TargetKey);
        using var restart=new SqliteOrdinaryRecordingHistoryStore(new SqliteOperationalStore(d.Store.DatabasePath));
        Assert.Equal(row.TargetKey,restart.Read("r")!.TargetKey);Assert.Equal(row.TargetKey,Assert.Single(restart.ReadPage(20,null).Items).TargetKey);
        var query=new UnifiedHistoryQueryService(d.Store,restart);Assert.Equal(row.TargetKey,Assert.Single(query.GetPage(Sid,Session,20,new("ordinary")).Items).TargetKey);
        Assert.Equal(known?1:0,d.Scalar("SELECT COUNT(*) FROM ordinary_recording_window_targets WHERE target_key IS NOT NULL;"));
        Assert.Equal(0,d.Scalar("SELECT COUNT(*) FROM ordinary_recording_targets WHERE target_key IS NOT NULL;"));
    }

    [Fact]
    public void FirstWindowFactFailureRollsBackEntireRegisterAndSameStoreRetries()
    {
        using var d=Database();d.Execute("CREATE TRIGGER fail_window BEFORE UPDATE ON ordinary_recording_window_targets BEGIN SELECT RAISE(ABORT,'task336 injected'); END;");
        var row=PublicationAnchorTestFixture.Anchored(Row(d,"r",true) with {BundleRegistration=new(null)});
        Assert.Throws<SqliteException>(()=>d.Ordinary.Register(row));
        foreach(var t in new[]{"ordinary_recording_history","ordinary_recording_targets","ordinary_recording_window_targets","ordinary_publication_anchors","ordinary_recording_bundles","ordinary_bundle_contents"})Assert.Equal(0,d.Scalar("SELECT COUNT(*) FROM "+t+";"));
        d.Execute("DROP TRIGGER fail_window;");d.Ordinary.Register(row);Assert.Equal(Key,d.Ordinary.Read("r")!.TargetKey);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LateAsyncUpdatesCannotReplaceNullOrKnownWindowFacts(bool known)
    {
        using var d=Database();var row=Row(d,"r") with {TargetKey=known?Key:null};d.Ordinary.Register(row);
        Assert.True(d.Ordinary.TryPublish(row with {Status="failed",TargetKey=known?OtherKey:Key}));
        Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>d.Ordinary.Read("r"));
        using var restarted=new SqliteOrdinaryRecordingHistoryStore(d.Store);Assert.Equal(row.TargetKey,restarted.Read("r")!.TargetKey);
    }

    [Theory]
    [InlineData("source")] [InlineData("semantics")] [InlineData("nested")]
    public void ExternalOrConflictingBindingsCannotBeRegistered(string fault)
    {
        using var d=Database();var row=Row(d,"r");
        row=fault switch {"source"=>row with {SourceType="display"},"semantics"=>row with {CaptureSemantics="screen_rectangle"},_=>row with {NestedRole="outer"}};
        Assert.Throws<InvalidDataException>(()=>d.Ordinary.Register(row));Assert.Equal(0,d.Scalar("SELECT COUNT(*) FROM ordinary_recording_history;"));
    }

    private static SqliteConnection Raw(string path) {var c=new SqliteConnection($"Data Source={path};Pooling=False");c.Open();SqliteOutputPathCollation.Register(c);return c;}

    internal static SqliteOperationalStore V33(string path,string root)
    {
        using var c=new SqliteConnection($"Data Source={path};Pooling=False");c.Open();SqliteOutputPathCollation.Register(c);using var cmd=c.CreateCommand();
        foreach(var m in SqliteSchemaCatalog.Migrations.Where(m=>m.Version<=33)) {
            cmd.CommandText=m.Definition;cmd.ExecuteNonQuery();cmd.CommandText="INSERT INTO schema_migrations VALUES($v,$n,$c,$t);";
            cmd.Parameters.AddWithValue("$v",m.Version);cmd.Parameters.AddWithValue("$n",m.Name);cmd.Parameters.AddWithValue("$c",m.Checksum);cmd.Parameters.AddWithValue("$t",m.Version+1000);cmd.ExecuteNonQuery();cmd.Parameters.Clear();
        }
        cmd.CommandText="INSERT INTO ordinary_recording_history(recording_id,history_sequence,created_at_utc,status_code,source_type,capture_semantics,source_title,audio_mode,output_path,updated_at_utc) VALUES('old-window',1,$at,'failed','window','window_surface','Synthetic','none',$p,$at),('old-screen',2,$at,'failed','display','display_capture','Synthetic','none',$p,$at); UPDATE ordinary_recording_targets SET target_key=$key WHERE recording_id='old-screen';";
        cmd.Parameters.AddWithValue("$at",At.UtcTicks);cmd.Parameters.AddWithValue("$p",Path.Combine(root,"old.mp4"));cmd.Parameters.AddWithValue("$key",OrdinaryTargetHistoryTests.Key);cmd.ExecuteNonQuery();
        return new(path);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void V33UpgradeIsAtomicRetryableAndKeepsReceiptsScreenKeysAndOldWindowNull(bool fail)
    {
        using var d=Database();var store=V33(Path.Combine(d.Root,"v33.db"),d.Root);
        var before=UnifiedHistoryProfileFilterTests.RawSnapshot(store.DatabasePath,schemaThrough:33);
        if(fail) {
            using(var c=Raw(store.DatabasePath))using(var cmd=c.CreateCommand()) {cmd.CommandText="CREATE TRIGGER fail_v34 BEFORE INSERT ON schema_migrations WHEN NEW.version=34 BEGIN SELECT RAISE(ABORT,'injected'); END;";cmd.ExecuteNonQuery();}
            Assert.Throws<SqliteOperationalStoreException>(()=>store.Initialize());
            using(var c=Raw(store.DatabasePath))using(var cmd=c.CreateCommand()) {
                cmd.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE name IN ('ordinary_recording_window_targets','ordinary_window_target_boundary','ordinary_window_target_initial','idx_ordinary_window_targets_key_created');";Assert.Equal(0L,cmd.ExecuteScalar());
                cmd.CommandText="DROP TRIGGER fail_v34;";cmd.ExecuteNonQuery();
            }
        }
        store.Initialize();store.Initialize();Assert.Equal(before,UnifiedHistoryProfileFilterTests.RawSnapshot(store.DatabasePath,schemaThrough:33));
        using var writer=new SqliteOrdinaryRecordingHistoryStore(store);Assert.Null(writer.Read("old-window")!.TargetKey);Assert.Equal(OrdinaryTargetHistoryTests.Key,writer.Read("old-screen")!.TargetKey);
        Assert.Throws<InvalidOperationException>(()=>writer.Register(writer.Read("old-window")! with {TargetKey=Key}));
        using var read=store.OpenReadOnlyConnection();using var count=read.CreateCommand();count.CommandText="SELECT COUNT(*) FROM ordinary_recording_window_targets;";Assert.Equal(0L,count.ExecuteScalar());
        writer.Register(Row(d,"new"));Assert.Equal(Key,writer.Read("new")!.TargetKey);
    }

    [Theory]
    [InlineData("DELETE FROM ordinary_recording_window_targets;")]
    [InlineData("UPDATE ordinary_recording_window_targets SET created_at_utc=created_at_utc+1;")]
    [InlineData("UPDATE ordinary_recording_window_targets SET source_type='display';")]
    [InlineData("UPDATE ordinary_recording_window_targets SET capture_semantics='screen_rectangle';")]
    [InlineData("UPDATE ordinary_recording_window_targets SET target_key='target:v1:window:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB';")]
    [InlineData("UPDATE ordinary_recording_targets SET target_key='target:v1:display:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';")]
    [InlineData("DROP INDEX idx_ordinary_window_targets_key_created;")]
    [InlineData("ALTER TABLE ordinary_recording_window_targets ADD COLUMN extra TEXT;")]
    [InlineData("UPDATE schema_migrations SET definition_checksum='ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff' WHERE version=34;")]
    [InlineData("DELETE FROM ordinary_window_target_boundary;")]
    public void DamagedWindowFactsFailHistoryAndRetentionBeforeFileProbe(string sql)
    {
        using var d=Database();d.Ordinary.Register(Row(d,"r",true));d.RawExecute("PRAGMA ignore_check_constraints=ON;"+sql);
        var files=new RetentionPreviewQueryTests.MetadataFake();var retention=new RetentionPreviewQueryService(d.Query.GetOrdinaryEvidencePage,d.Root,files,files,()=>At.AddDays(2));
        var probes=0;d.Query.OrdinaryFileProbeForTests=_=>{probes++;return true;};
        Assert.Throws<UnifiedHistoryUnavailableException>(()=>d.Page());Assert.Throws<RetentionPreviewUnavailableException>(()=>retention.GetPage(Sid,Session,1,At.AddHours(1)));
        Assert.ThrowsAny<Exception>(()=>d.Ordinary.Read("r"));Assert.Equal(0,probes);Assert.Empty(files.Observations);
    }

    [Fact]
    public void WindowMediaAndBundleAreOneSnapshotWhileOtherWritesCommit()
    {
        using var d=Database();var row=Row(d,"r");d.Ordinary.Register(row);
        var end=PublicationAnchorTestFixture.Anchored(Row(d,"r",true));var op=OrdinaryRecordingBundleOperation.Create("r",end.OutputPath);end=end with {BundleRegistration=new(op)};
        d.Query.SnapshotEstablishedForTests=()=> {
            Assert.True(d.Ordinary.TryPublish(end));Assert.True(d.Ordinary.TryPublishBundle(new(op,new("ready",op.BundlePath,null))));
            Assert.Equal("ready",d.Ordinary.Read("r")!.Bundle!.Status);d.Ordinary.Register(Row(d,"late") with {TargetKey=OtherKey});d.Query.SnapshotEstablishedForTests=null;
        };
        var first=Assert.Single(d.Page(filters:new("ordinary",TargetKey:Key)).Items);Assert.Equal(Key,first.TargetKey);Assert.Equal("preparing",first.Status);Assert.Null(first.Bundle);
        var next=Assert.Single(d.Page(filters:new("ordinary",TargetKey:Key)).Items);Assert.Equal("completed",next.Status);Assert.Equal("ready",next.Bundle!.Status);Assert.Equal(Key,next.TargetKey);
    }

    [Fact]
    public void WindowCursorBindsExactKeyAndOldMissingTargetPropertyRemainsCompatible()
    {
        using var d=Database();foreach(var id in new[]{"a","b","c"})d.Ordinary.Register(Row(d,id));
        var filters=new UnifiedHistoryFilters("ordinary",TargetKey:Key);var cursor=d.Page(1,filters).NextCursor!;
        foreach(var changed in new[]{filters with{TargetKey=null},filters with{TargetKey=OtherKey},filters with{TargetKey=OrdinaryTargetHistoryTests.Key}})
            Assert.Throws<UnifiedHistoryArgumentException>(()=>d.Page(1,changed,cursor));
        var old=d.Page(1,new("ordinary")).NextCursor!;var body=old.Split('.')[0];var n=JsonNode.Parse(Convert.FromBase64String(body.Replace('-','+').Replace('_','/')+new string('=',(4-body.Length%4)%4)))!;
        n["filters"]!.AsObject().Remove("targetKey");body=Convert.ToBase64String(Encoding.UTF8.GetBytes(n.ToJsonString())).TrimEnd('=').Replace('+','-').Replace('/','_');
        old=body+"."+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        Assert.Equal(new[]{"b","c"},d.Page(100,new("ordinary"),old).Items.Select(i=>i.RecordingId));
    }
}
