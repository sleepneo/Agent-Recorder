using System.Data;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class OrdinaryBundleContentsHistoryTests(ITestOutputHelper output)
{
    internal sealed class Fixture : IDisposable
    {
        internal readonly string Root=Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task332",Guid.NewGuid().ToString("N")));
        internal SqliteOperationalStore Store;
        internal SqliteOrdinaryRecordingHistoryStore Writer;
        internal Fixture()
        { Directory.CreateDirectory(Root);Store=new(Path.Combine(Root,"history.db"));Store.Initialize();Writer=new(Store); }
        internal UnifiedHistoryQueryService Query => new(Store,Writer);
        internal OrdinaryRecordingHistorySnapshot Row(string id="r") => new(id,At,"completed","display","display_capture","Synthetic",1,"none",null,null,null,null,
            Path.Combine(Root,"Videos",id+".mp4"),null,null,null,null,null,null,At.AddSeconds(1),At.AddSeconds(2),"duration_reached",100,1)
            { TargetKey=OrdinaryTargetHistoryTests.Key };
        internal (OrdinaryRecordingHistorySnapshot Row,OrdinaryRecordingBundleOperation Op) Seed(string id="r",bool anchor=false)
        {
            var row=Row(id);if(anchor)row=PublicationAnchorTestFixture.Anchored(row);
            var op=OrdinaryRecordingBundleOperation.Create(id,row.OutputPath);row=row with {BundleRegistration=new(op)};Writer.Register(row);return(row,op);
        }
        internal OrdinaryRecordingBundleUpdate Ready(OrdinaryRecordingBundleOperation op,long size=11,bool known=true)
            =>new(op,new("ready",op.BundlePath,null,known?new(BundleContentsGenerationTests.Items(size)):null));
        internal void Complete(OrdinaryRecordingBundleOperation op,long size=11,bool known=true)
        { Assert.True(Writer.TryPublishBundle(Ready(op,size,known)));Assert.Equal("ready",Writer.Read(op.RecordingId)!.Bundle!.Status); }
        internal void Execute(string sql,bool raw=false,params (string Name,object Value)[] values)
        {
            using var c=raw?new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Store.DatabasePath,Pooling=false}.ToString()):Store.OpenConnection();
            if(raw){c.Open();SqliteOutputPathCollation.Register(c);}using var cmd=c.CreateCommand();cmd.CommandText=sql;
            foreach(var value in values)cmd.Parameters.AddWithValue(value.Name,value.Value);cmd.ExecuteNonQuery();
        }
        internal long Scalar(string sql)
        { using var c=Store.OpenReadOnlyConnection();using var cmd=c.CreateCommand();cmd.CommandText=sql;return Convert.ToInt64(cmd.ExecuteScalar()); }
        internal void Reopen()
        { Assert.True(Writer.DrainAndDispose(TimeSpan.FromSeconds(3)));Store=new(Store.DatabasePath);Store.Initialize();Writer=new(Store); }
        public void Dispose(){Writer.Dispose();SqliteConnection.ClearAllPools();} // Keep owned evidence/media for this task.
    }

    [Fact]
    public void AtomicReadyDetachesByValueReopensAndDoesNotMutateMediaTargetOrAnchor()
    {
        using var f=new Fixture();var(row,op)=f.Seed(anchor:true);var before=f.Writer.Read("r")!;
        f.Complete(op);var ready=f.Writer.Read("r")!;Assert.Equal(before with {Bundle=null},ready with {Bundle=null});
        Assert.Equal(5,f.Scalar("SELECT COUNT(*) FROM ordinary_bundle_contents_items;"));
        Assert.True(f.Writer.TryPublishBundle(f.Ready(op)));Assert.Equal(ready,f.Writer.Read("r"));
        Assert.True(f.Writer.TryPublish(row));Assert.Equal(ready,f.Writer.Read("r"));
        f.Reopen();f.Writer.RecoverIncomplete(At.AddHours(1));Assert.Equal(ready,f.Writer.Read("r"));
        Assert.Equal(ready.Bundle,Assert.Single(f.Query.GetPage(Sid,Session,20,new("ordinary")).Items).Bundle);
        Assert.Equal(ready.Bundle,Assert.Single(f.Writer.ReadPage(20,null).Items).Bundle);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TerminalContentsConflictAndUnknownLateFillAreRejectedAndObservable(bool unknown)
    {
        using var f=new Fixture();var(row,op)=f.Seed();f.Complete(op,known:!unknown);
        var before=f.Writer.Read("r")!;Assert.True(f.Writer.TryPublishBundle(f.Ready(op,21)));
        Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.Read("r"));
        Assert.True(f.Writer.TryPublishBundle(f.Ready(op,known:!unknown)));
        Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.Read("r"));
        using var restarted=new SqliteOrdinaryRecordingHistoryStore(f.Store);Assert.Equal(before,restarted.Read("r"));
        Assert.Equal(unknown?0:5,f.Scalar("SELECT COUNT(*) FROM ordinary_bundle_contents_items;"));
    }

    [Theory]
    [InlineData("recording")] [InlineData("operation")] [InlineData("media")] [InlineData("path")] [InlineData("failed")]
    public void ForeignBindingsAndEvidenceOnFailureCannotCreateReady(string fault)
    {
        using var f=new Fixture();var(row,op)=f.Seed();var before=f.Writer.Read("r")!;var update=f.Ready(op);
        if(fault=="recording")update=update with {Operation=op with {RecordingId="other"}};
        if(fault=="operation")update=update with {Operation=op with {OperationId=Guid.NewGuid().ToString("N")}};
        if(fault=="media"){var other=OrdinaryRecordingBundleOperation.Create("r",Path.Combine(f.Root,"other.mp4"));update=f.Ready(other);}
        if(fault=="path")update=update with {Result=update.Result with {Path=Path.Combine(f.Root,"other.bundle")}};
        if(fault=="failed")update=update with {Result=update.Result with {Status="failed",ErrorCode=RecordingBundleErrorCodes.GenerationFailed}};
        Assert.Equal(fault is "recording" or "operation" or "media",f.Writer.TryPublishBundle(update));
        Assert.Equal(before,f.Writer.Read("r"));Assert.Equal(0,f.Scalar("SELECT COUNT(*) FROM ordinary_bundle_contents_items;"));
    }

    [Fact]
    public void InitialMarkerFailureRollsBackMediaTargetAnchorAndBundle()
    {
        using var f=new Fixture();f.Execute("CREATE TRIGGER fail_contents BEFORE INSERT ON ordinary_bundle_contents BEGIN SELECT RAISE(ABORT,'owned fault'); END;");
        var row=PublicationAnchorTestFixture.Anchored(f.Row());var op=OrdinaryRecordingBundleOperation.Create("r",row.OutputPath);
        Assert.Throws<SqliteException>(()=>f.Writer.Register(row with {BundleRegistration=new(op)}));
        foreach(var table in new[]{"ordinary_recording_history","ordinary_recording_targets","ordinary_publication_anchors","ordinary_recording_bundles","ordinary_bundle_contents","ordinary_bundle_contents_items"})
            Assert.Equal(0,f.Scalar("SELECT COUNT(*) FROM "+table+";"));
        f.Execute("DROP TRIGGER fail_contents;");f.Writer.Register(row with {BundleRegistration=new(op)});f.Complete(op);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ReadySqlOrActualDeferredCommitFailureRollsBackAndEqualReplayRepairs(bool commit)
    {
        using var f=new Fixture();var(row,op)=f.Seed(anchor:true);var before=f.Writer.Read("r")!;
        if(commit)
        {
            f.Execute("CREATE TABLE owned_commit_fault(id TEXT REFERENCES ordinary_recording_history(recording_id) DEFERRABLE INITIALLY DEFERRED);");
            f.Writer.BeforeBundleCommitForTests=(c,t)=>{using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="INSERT INTO owned_commit_fault VALUES('missing');";cmd.ExecuteNonQuery();};
        }
        else f.Execute("CREATE TRIGGER fail_item BEFORE INSERT ON ordinary_bundle_contents_items WHEN NEW.ordinal=3 BEGIN SELECT RAISE(ABORT,'owned item fault'); END;");
        var update=f.Ready(op);Assert.True(f.Writer.TryPublishBundle(update));Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.Read("r"));
        Assert.Equal(0,f.Scalar("SELECT COUNT(*) FROM ordinary_bundle_contents_items;"));Assert.Equal(0,f.Scalar("SELECT COUNT(*) FROM ordinary_bundle_contents WHERE evidence_version=1;"));
        using(var check=new SqliteOrdinaryRecordingHistoryStore(f.Store))Assert.Equal(before,check.Read("r"));
        if(commit)f.Writer.BeforeBundleCommitForTests=null;else f.Execute("DROP TRIGGER fail_item;");
        // A separately copied but value-equal collection clears the original obligation.
        Assert.True(f.Writer.TryPublishBundle(f.Ready(op)));Assert.Equal("ready",f.Writer.Read("r")!.Bundle!.Status);
        Assert.True(f.Writer.DrainAndDispose(TimeSpan.FromSeconds(3)));
        output.WriteLine(commit?"Actual SQLite Commit failed due to deferred FK; status/header/five items rolled back.":"Fourth item INSERT fault rolled back status/header/all prior items.");
    }

    [Fact]
    public void RecoveryFailureKeepsRetryEligibilityRevokesOldProducerAndPreservesReadyContents()
    {
        using var f=new Fixture();var(_,readyOp)=f.Seed("ready");f.Complete(readyOp);var ready=f.Writer.Read("ready")!;
        var(row,op)=f.Seed("pending");Assert.True(f.Writer.DrainAndDispose(TimeSpan.FromSeconds(3)));f.Writer=new(f.Store);
        f.Execute("CREATE TRIGGER fail_recovery BEFORE UPDATE ON ordinary_recording_bundles BEGIN SELECT RAISE(ABORT,'owned recovery fault'); END;");
        Assert.Throws<SqliteException>(()=>f.Writer.RecoverIncomplete(At.AddHours(1)));
        f.Execute("DROP TRIGGER fail_recovery;");f.Writer.RecoverIncomplete(At.AddHours(1));f.Writer.RecoverIncomplete(At.AddHours(2));
        Assert.Equal(ready,f.Writer.Read("ready"));var recovered=f.Writer.Read("pending")!;
        Assert.Equal("completed",recovered.Status);Assert.Equal("interrupted_unknown",recovered.Bundle!.Status);Assert.Null(recovered.Bundle.ContentsEvidence);
        Assert.True(f.Writer.TryPublishBundle(f.Ready(op)));Assert.Equal(recovered,f.Writer.Read("pending"));
        Assert.Equal(5,f.Scalar("SELECT COUNT(*) FROM ordinary_bundle_contents_items;"));
    }

    [Fact]
    public void TrueV32UpgradeKeepsOldReadyUnknownAndDoesNotBackfillAttachments()
    {
        using var f=new Fixture();var path=Path.Combine(f.Root,"legacy-v32.db");var row=f.Row("legacy") with {TargetKey=null};
        var op=OrdinaryRecordingBundleOperation.Create(row.RecordingId,row.OutputPath);
        using(var c=new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();SqliteOutputPathCollation.Register(c);using var cmd=c.CreateCommand();
            foreach(var m in SqliteSchemaCatalog.Migrations.Where(m=>m.Version<=32))
            {
                cmd.CommandText=m.Definition;cmd.ExecuteNonQuery();cmd.CommandText="INSERT INTO schema_migrations VALUES($v,$name,$hash,0);";
                cmd.Parameters.AddWithValue("$v",m.Version);cmd.Parameters.AddWithValue("$name",m.Name);cmd.Parameters.AddWithValue("$hash",m.Checksum);cmd.ExecuteNonQuery();cmd.Parameters.Clear();
            }
            cmd.CommandText="""
                INSERT INTO ordinary_recording_history(recording_id,history_sequence,created_at_utc,status_code,source_type,capture_semantics,source_title,audio_mode,output_path,started_at_utc,completed_at_utc,output_size_bytes,output_duration_ms,updated_at_utc)
                VALUES('legacy',1,$at,'completed','display','display_capture','Synthetic','none',$media,$at,$at,100,1000,$at);
                INSERT INTO ordinary_recording_bundles VALUES('legacy',$media,$op,'ready',$bundle,NULL);
                """;
            cmd.Parameters.AddWithValue("$at",At.UtcTicks);cmd.Parameters.AddWithValue("$media",op.MediaPath);cmd.Parameters.AddWithValue("$op",op.OperationId);cmd.Parameters.AddWithValue("$bundle",op.BundlePath);cmd.ExecuteNonQuery();
        }
        var before=LegacyBytes(path);var store=new SqliteOperationalStore(path);store.Initialize();store.Initialize();Assert.Equal(before,LegacyBytes(path));
        using var writer=new SqliteOrdinaryRecordingHistoryStore(store);var old=writer.Read("legacy")!;Assert.Equal("ready",old.Bundle!.Status);Assert.Null(old.Bundle.ContentsEvidence);
        using(var c=store.OpenReadOnlyConnection()){using var cmd=c.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM ordinary_bundle_contents;";Assert.Equal(0L,cmd.ExecuteScalar());}
        Assert.True(writer.TryPublishBundle(new(op,new("ready",op.BundlePath,null,new(BundleContentsGenerationTests.Items())))));
        Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>writer.Read("legacy"));
        using var restarted=new SqliteOrdinaryRecordingHistoryStore(store);Assert.Equal(old,restarted.Read("legacy"));
        var fresh=f.Row("fresh");var freshOp=OrdinaryRecordingBundleOperation.Create("fresh",fresh.OutputPath);
        restarted.Register(fresh with {BundleRegistration=new(freshOp)});
        Assert.True(restarted.TryPublishBundle(new(freshOp,new("ready",freshOp.BundlePath,null,new(BundleContentsGenerationTests.Items())))));
        Assert.NotNull(restarted.Read("fresh")!.Bundle!.ContentsEvidence);
        using(var c=new SqliteConnection($"Data Source={path};Pooling=False"))
        { c.Open();using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA foreign_keys=OFF; DELETE FROM ordinary_bundle_contents_items WHERE recording_id='fresh'; DELETE FROM ordinary_bundle_contents WHERE recording_id='fresh';";cmd.ExecuteNonQuery(); }
        Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>restarted.Read("fresh"));
        Assert.Equal(old,restarted.Read("legacy"));
        output.WriteLine("All existing v1-v32 table values/DDL/receipts unchanged; no old contents marker/items, old ready remains unknown.");
    }

    private static string LegacyBytes(string path)
    {
        using var c=new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");c.Open();
        var result=new List<string>();var tables=new List<string>();using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT name,type,sql FROM sqlite_master WHERE name NOT LIKE 'future_window_publication%' AND name NOT LIKE 'idx_future_window_publication%' AND tbl_name NOT LIKE 'plan_media_publication%' AND name NOT LIKE 'plan_media_publication%' AND name NOT LIKE 'idx_plan_media_publication%' AND name NOT IN ('idx_setup_intents_pending_output_path','idx_setup_intents_pending_output_template','idx_recurring_profiles_pending_output_template','idx_required_once_setup_pending_output_path') AND name NOT LIKE 'ordinary_bundle_attachment_hash%' AND name NOT LIKE 'idx_ordinary_bundle_attachment_hash%' AND name NOT LIKE 'ordinary_media_hash%' AND name NOT LIKE 'idx_ordinary_media_hash%' AND name NOT LIKE 'ordinary_bundle_identity%' AND name NOT LIKE 'idx_ordinary_bundle_identity%' AND name NOT LIKE 'ordinary_bundle_contents%' AND name NOT LIKE 'idx_ordinary_bundle_contents%' AND name NOT LIKE 'sqlite_%' AND name NOT IN ('ordinary_bundle_identity','ordinary_bundle_identity_boundary','ordinary_bundle_identity_items','ordinary_recording_window_targets','ordinary_window_target_boundary','ordinary_window_target_initial','idx_ordinary_window_targets_key_created') ORDER BY name;";
        using(var r=cmd.ExecuteReader())while(r.Read()){result.Add(string.Join("|",Enumerable.Range(0,3).Select(i=>r.GetValue(i).ToString())));if(r.GetString(1)=="table")tables.Add(r.GetString(0));}
        foreach(var table in tables)
        {
            cmd.CommandText="SELECT * FROM \""+table+"\""+(table=="schema_migrations"?" WHERE version<=32":"")+" ORDER BY rowid;";
            using var r=cmd.ExecuteReader();while(r.Read())result.Add(table+":"+System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(0,r.FieldCount)
                .Select(i=>r.GetValue(i) is byte[] bytes?Convert.ToHexString(bytes):Convert.ToString(r.GetValue(i),System.Globalization.CultureInfo.InvariantCulture))));
        }
        return string.Join("\n",result);
    }

    [Theory]
    [InlineData("ALTER TABLE ordinary_bundle_contents ADD COLUMN extra TEXT;")]
    [InlineData("DROP INDEX idx_ordinary_bundle_contents_ordinal;")]
    [InlineData("DROP INDEX idx_ordinary_bundle_contents_ordinal; CREATE UNIQUE INDEX idx_ordinary_bundle_contents_ordinal ON ordinary_bundle_contents_items(recording_id COLLATE NOCASE,ordinal ASC);")]
    [InlineData("DROP TRIGGER ordinary_bundle_contents_initial;")]
    [InlineData("UPDATE schema_migrations SET definition_checksum='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' WHERE version=33;")]
    [InlineData("DELETE FROM ordinary_bundle_contents_boundary;")]
    [InlineData("DELETE FROM ordinary_bundle_contents;")]
    [InlineData("DELETE FROM ordinary_bundle_contents_items WHERE ordinal=2;")]
    [InlineData("DELETE FROM ordinary_bundle_contents_items;")]
    [InlineData("UPDATE ordinary_bundle_contents SET evidence_version=2;")]
    [InlineData("UPDATE ordinary_bundle_contents SET evidence_version=1.5;")]
    [InlineData("UPDATE ordinary_bundle_contents SET contents_count=4;")]
    [InlineData("UPDATE ordinary_bundle_contents SET contents_count=5.5;")]
    [InlineData("UPDATE ordinary_bundle_contents SET evidence_version=NULL,contents_count=0;")]
    [InlineData("UPDATE ordinary_bundle_contents SET media_output_path='C:\\wrong.mp4';")]
    [InlineData("UPDATE ordinary_bundle_contents SET bundle_path='C:\\wrong.bundle';")]
    [InlineData("UPDATE ordinary_bundle_contents SET operation_id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';")]
    [InlineData("UPDATE ordinary_bundle_contents_items SET size_bytes=0 WHERE ordinal=1;")]
    [InlineData("UPDATE ordinary_bundle_contents_items SET ordinal=1.5 WHERE ordinal=1;")]
    [InlineData("UPDATE ordinary_bundle_contents_items SET media_type='text/plain' WHERE ordinal=1;")]
    [InlineData("UPDATE ordinary_bundle_contents_items SET name='../metadata.json' WHERE ordinal=0;")]
    public void DamageIsUnavailableBeforeAnyRetentionFileProbe(string sql)
    {
        using var f=new Fixture();var(_,op)=f.Seed(anchor:true);f.Complete(op);
        f.Execute("PRAGMA foreign_keys=OFF;PRAGMA ignore_check_constraints=ON;"+sql,raw:true);
        var query=f.Query;var probes=0;query.OrdinaryFileProbeForTests=_=>{probes++;return true;};
        Assert.Throws<UnifiedHistoryUnavailableException>(()=>query.GetPage(Sid,Session,20,new("ordinary")));
        var error=Record.Exception(()=>f.Writer.Read("r"));Assert.True(error is OrdinaryRecordingHistoryUnavailableException or SqliteOperationalStoreException);
        var files=new RetentionPreviewQueryTests.MetadataFake();var preview=new RetentionPreviewQueryService(query.GetOrdinaryEvidencePage,f.Root,files,files,()=>At.AddDays(5));
        Assert.Throws<RetentionPreviewUnavailableException>(()=>preview.GetPage(Sid,Session,20,At.AddHours(1),null));
        Assert.Equal(0,probes);Assert.Empty(files.Attributes);Assert.Empty(files.Observations);Assert.Empty(files.Lengths);
    }

    [Fact]
    public void KnownReadyRetentionKeepsProtectionAndUnknownObservedBytesWithoutAttachmentIo()
    {
        using var f=new Fixture();var(_,op)=f.Seed(anchor:true);f.Complete(op);var query=f.Query;
        query.OrdinaryFileProbeForTests=_=>throw new InvalidOperationException("No early probe");
        var files=new RetentionPreviewQueryTests.MetadataFake();var preview=new RetentionPreviewQueryService(query.GetOrdinaryEvidencePage,f.Root,files,files,()=>At.AddDays(5));
        var page=preview.GetPage(Sid,Session,20,At.AddHours(1),null);var item=Assert.Single(page.Items);
        Assert.Equal("bundle_artifact_reference",item.ReasonCode);Assert.Null(item.ObservedFileBytes);
        Assert.True(item.AgeCandidate);Assert.Equal(1,page.UnknownCandidateFileCount);Assert.Equal(0,page.ObservedCandidateFileCount);Assert.Equal(0,page.ObservedCandidateBytes);
        Assert.Empty(files.Attributes);Assert.Empty(files.Observations);Assert.Empty(files.Lengths);
        Assert.False(Directory.Exists(op.BundlePath)); // The stored five facts are not queried as files.
    }

    [Theory]
    [InlineData(1)] [InlineData(20)] [InlineData(100)]
    public void PageBatchesAtMostFiveHundredContentsUsingIndexedPointReads(int limit)
    {
        using var f=new Fixture();for(var i=0;i<101;i++){var(_,op)=f.Seed("r"+i.ToString("D4"));f.Complete(op);}
        var query=f.Query;query.OrdinaryFileProbeForTests=_=>false;var calls=0;var plans=new List<string>();var selected=0;
        query.CandidatesSelectedForTests=(_,n)=>{Assert.InRange(n,0,limit+1);selected+=n;};
        query.BundleContentsCommandForTests=(c,t,sql,args)=>
        {
            calls++;Assert.InRange(args.Count,1,100);Assert.Equal(limit,args.Count);Assert.Contains("LIMIT 501",sql);Assert.DoesNotContain("OFFSET",sql);
            using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="EXPLAIN QUERY PLAN "+sql;foreach(var a in args)cmd.Parameters.AddWithValue(a.Key,a.Value);
            using var r=cmd.ExecuteReader();while(r.Read())plans.Add(r.GetString(3));
        };
        var page=query.GetPage(Sid,Session,limit,new("ordinary",TargetKey:OrdinaryTargetHistoryTests.Key));
        Assert.Equal(limit,page.Items.Count);Assert.Equal(1,calls);Assert.InRange(selected,limit,limit+1);
        Assert.Equal(limit*5,page.Items.Sum(item=>item.Bundle!.ContentsEvidence!.Contents.Count));
        Assert.Contains(plans,p=>p.Contains("SEARCH c USING INDEX sqlite_autoindex_ordinary_bundle_contents_1"));
        Assert.Contains(plans,p=>p.Contains("SEARCH i USING INDEX "+SqliteSchemaV33.IndexName));
        output.WriteLine(string.Join("\n",plans));
    }

    [Fact]
    public async Task SnapshotBarrierNeverMixesReadyWithUnknownOrPartialContents()
    {
        using var f=new Fixture();var(_,a)=f.Seed("a");var(_,b)=f.Seed("b");
        var query=f.Query;query.OrdinaryFileProbeForTests=_=>false;using var release=new ManualResetEventSlim();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        query.SnapshotEstablishedForTests=()=>{entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));};
        var pending=Task.Run(()=>query.GetPage(Sid,Session,1,new("ordinary")));
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));f.Complete(a);f.Complete(b);f.Seed("newmember"); }
        finally {release.Set();}
        var old=await pending;var first=Assert.Single(old.Items);Assert.Equal("generating",first.Bundle!.Status);Assert.Null(first.Bundle.ContentsEvidence);
        query.SnapshotEstablishedForTests=null;var next=query.GetPage(Sid,Session,1,new("ordinary"),old.NextCursor);
        var second=Assert.Single(next.Items);Assert.Equal("b",second.RecordingId);Assert.Equal("ready",second.Bundle!.Status);Assert.Equal(5,second.Bundle.ContentsEvidence!.Contents.Count);
        Assert.Null(next.NextCursor);Assert.Equal(3,query.GetPage(Sid,Session,100,new("ordinary")).Items.Count);
    }

    [Fact]
    public void SameHostRecoveryDoesNotInterruptRegisteredProducerOrItsContents()
    {
        using var f=new Fixture();var(_,op)=f.Seed();f.Writer.RecoverIncomplete(At.AddHours(1));
        Assert.Equal("generating",f.Writer.Read("r")!.Bundle!.Status);f.Complete(op);
        Assert.NotNull(f.Writer.Read("r")!.Bundle!.ContentsEvidence);
    }
}
