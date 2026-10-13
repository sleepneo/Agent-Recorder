using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class DurableBundleAttachmentHashTests(ITestOutputHelper output)
{
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = NewRoot();
        internal readonly SqliteOperationalStore Store;
        internal SqliteOrdinaryRecordingHistoryStore Writer;
        internal readonly OrdinaryRecordingBundleOperation Op;
        internal readonly RecordingBundleRequest Request;
        internal readonly PublicationReceipt Receipt;
        internal readonly FfmpegRecordingBundleGenerator Generator = new(new Frames(), ffmpegPathProvider: () => "owned-fake-ffmpeg.exe");
        internal Fixture(bool anchor = true, bool upperReceiptPath = false)
        {
            Directory.CreateDirectory(Path.Combine(Root,"Videos")); var media = Path.Combine(Root,"Videos","media.mp4"); File.WriteAllBytes(media, Enumerable.Range(0, 160003).Select(i => (byte)i).ToArray());
            using (var stream = File.OpenRead(media))
            {
                var native = PublicationHandleObservation.Read(WindowsFileMetadataApi.Instance,stream.SafeFileHandle,media)!;
                Assert.NotNull(native); Receipt = PublicationReceipt.IssueForTests(upperReceiptPath ? media.ToUpperInvariant() : media,stream.Length,native.Snapshot.Identity!);
            }
            Store = new(Path.Combine(Root,"history.db")); Store.Initialize(); Writer = new(Store);
            Op = OrdinaryRecordingBundleOperation.Create("r",media); Request = BundlePublicationIdentityTests.RequestFor(media,"r",Op.OperationId);
            if (anchor) Request.BindMediaPublication(Receipt);
            Writer.Register(Row("r",media,Receipt.Anchor.Length) with { PublicationReceipt = anchor ? Receipt : null, BundleRegistration = new(Op) });
        }
        internal async Task<OrdinaryRecordingBundleUpdate> Produce()
        {
            var result = await Generator.GenerateAsync(Request); Assert.True(result.Success,result.ErrorDetail);
            return new(Op,new("ready",Op.BundlePath,null,result.ContentsEvidence) { IdentityEvidence = result.PublicationReceipt?.Evidence, MediaHashEvidence = result.MediaHashEvidence, ContentHashEvidence = result.PublicationReceipt?.ContentHashes });
        }
        internal void Accept(OrdinaryRecordingBundleUpdate update)
        { Assert.True(Writer.TryPublishBundle(update)); Assert.Equal(update.Result,Writer.Read("r")!.Bundle); }
        internal void Execute(string sql)
        { using var c = new SqliteConnection($"Data Source={Store.DatabasePath};Pooling=False"); c.Open(); SqliteOutputPathCollation.Register(c); using var cmd=c.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery(); }
        internal long Scalar(string sql)
        { using var c=Store.OpenReadOnlyConnection();using var cmd=c.CreateCommand();cmd.CommandText=sql;return Convert.ToInt64(cmd.ExecuteScalar()); }
        internal void Reopen() { Assert.True(Writer.DrainAndDispose(TimeSpan.FromSeconds(3))); Store.Initialize(); Writer=new(Store); }
        public void Dispose() => Writer.Dispose();
    }
    private static string NewRoot() { var p=Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task342",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(p);return p; }
    private static OrdinaryRecordingHistorySnapshot Row(string id,string media,long size) => new(id,At,"completed","display","display_capture","Synthetic",1,"none",null,null,null,null,media,null,null,null,null,null,null,At.AddSeconds(1),At.AddSeconds(2),"duration_reached",size,1);
    private sealed class Frames : IExternalProcessRunner
    {
        public Task<ExternalProcessResult> RunAsync(string filename,IReadOnlyList<string> args,TimeSpan timeout,bool captureStderr=true,Encoding? stderrEncoding=null,CancellationToken cancellationToken=default)
        { cancellationToken.ThrowIfCancellationRequested(); var path=args.Last();File.WriteAllBytes(path,(path.EndsWith(".png") ? new byte[]{137,80,78,71,13,10,26,10} : new byte[]{255,216}).Concat(new byte[100]).ToArray());return Task.FromResult(new ExternalProcessResult(0,false,"")); }
    }
    private static string Independent(string path) { using var f=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant(); }

    private static void AssertHashes(OrdinaryRecordingBundleSnapshot bundle)
    {
        Assert.NotNull(bundle.ContentHashEvidence); Assert.Equal(5,bundle.ContentHashEvidence.Values.Count);
        Assert.Equal(bundle.IdentityEvidence,bundle.ContentHashEvidence.Identity);
        for(var i=0;i<5;i++)
        {
            var item=bundle.ContentsEvidence!.Contents[i];var hash=bundle.ContentHash(i)!;
            Assert.Equal(new OrdinaryMediaHashSnapshot("sha256",Independent(Path.Combine(bundle.Path!,item.Name)),"bundle_publication",item.SizeBytes),hash);
        }
    }
    private static void AssertContentJson(JsonElement bundle,OrdinaryRecordingBundleSnapshot expected)
    {
        var items=bundle.GetProperty("contents").EnumerateArray().ToArray();Assert.Equal(5,items.Length);
        for(var i=0;i<5;i++)
        {
            Assert.Equal(expected.ContentsEvidence!.Contents[i].Name,items[i].GetProperty("name").GetString());
            Assert.Equal(4,items[i].EnumerateObject().Count());var hash=items[i].GetProperty("content_hash");var original=expected.ContentHash(i);
            if(original is null)Assert.Equal(JsonValueKind.Null,hash.ValueKind);
            else { Assert.Equal(4,hash.EnumerateObject().Count());Assert.Equal("sha256",hash.GetProperty("algorithm").GetString());Assert.Equal(original.Value,hash.GetProperty("value").GetString());Assert.Equal("bundle_publication",hash.GetProperty("basis").GetString());Assert.Equal(original.SizeBytes,hash.GetProperty("size_bytes").GetInt64()); }
        }
    }
    [Fact]
    public async Task FiveFormalCopiesMatchIndependentDigestsRestartAndAllHistoricalProjectionsWithoutReads()
    {
        using var f=new Fixture();var update=await f.Produce();AssertHashes(update.Result);f.Accept(update);f.Accept(update);f.Reopen();
        Assert.Equal(update.Result,f.Writer.Read("r")!.Bundle);AssertHashes(f.Writer.Read("r")!.Bundle!);
        Assert.Equal(update.Result,Assert.Single(f.Writer.ReadPage(100,null).Items).Bundle);
        Assert.Equal(update.Result,Assert.Single(new UnifiedHistoryQueryService(f.Store,f.Writer).GetPage(Sid,Session,100,new("ordinary")).Items).Bundle);
        Assert.Equal(update.Result,f.Writer.ReadArtifactFacts("r")!.Media.Bundle);
        foreach(var item in update.Result.ContentsEvidence!.Contents)File.Delete(Path.Combine(f.Op.BundlePath,item.Name));
        using var blocked=new FileStream(f.Op.MediaPath,FileMode.Open,FileAccess.Read,FileShare.None);
        Assert.Equal(update.Result,f.Writer.Read("r")!.Bundle);Assert.Equal(update.Result,Assert.Single(f.Writer.ReadPage(100,null).Items).Bundle);
        Assert.Equal(update.Result,Assert.Single(new UnifiedHistoryQueryService(f.Store,f.Writer).GetPage(Sid,Session,100,new("ordinary")).Items).Bundle);
        Assert.Equal(update.Result,f.Writer.ReadArtifactFacts("r")!.Media.Bundle);
    }
    private static string OriginalPath(FileStream stream) {var path=new StringBuilder(32768);Assert.NotEqual(0u,WindowsFileMetadataApi.Instance.FinalPath(stream.SafeFileHandle,path,path.Capacity,out _));return path.ToString();}
    private sealed class Copies
    {
        internal readonly string Root=NewRoot();internal string Stage=>Path.Combine(Root,"staging");internal string Media=>Path.Combine(Root,"media.mp4");
        internal string Final=>OrdinaryRecordingBundleOperation.DerivePath(Media);
        internal readonly RecordingBundleContentsEvidence Contents=new(new[]{new RecordingBundleContentItem("metadata.json","application/json",160003),new("thumbnail.jpg","image/jpeg",160003),new("first_frame.png","image/png",160003),new("last_frame.png","image/png",160003),new("marks.json","application/json",160003)});
        internal Copies() {Directory.CreateDirectory(Stage);File.WriteAllBytes(Media,new byte[100]);foreach(var item in Contents.Contents)File.WriteAllBytes(Path.Combine(Stage,item.Name),Enumerable.Range(0,(int)item.SizeBytes).Select(i=>(byte)i).ToArray());}
        internal Task<RecordingBundleGenerationResult> Publish(BundlePublicationReceipt.Publisher publisher,CancellationToken token=default)=>publisher.PublishAsync(BundlePublicationIdentityTests.RequestFor(Media,"r",new string('a',32)),Stage,Final,Contents,token);
    }
    [Fact]
    public async Task DigestUsesActuallyCopiedSameLengthStagingBytesAndRealOriginalHandlesStayPinned()
    {
        var f=new Copies();var expected=new List<string>();var shares=0;var identityCalls=0;
        var publisher=new BundlePublicationReceipt.Publisher {IdentityApi=new Metadata(()=>identityCalls++),StageForTests=(stage,stream)=> {
            if(stage=="prepare") foreach(var item in f.Contents.Contents){var path=Path.Combine(f.Stage,item.Name);File.WriteAllBytes(path,Enumerable.Repeat((byte)42,(int)item.SizeBytes).ToArray());expected.Add(Independent(path));}
            if(stage=="copy" && stream is not null){Assert.False(stream.SafeFileHandle.IsClosed);Assert.Throws<IOException>(()=>File.WriteAllText(OriginalPath(stream),"foreign"));Assert.Throws<IOException>(()=>File.Delete(OriginalPath(stream)));shares++;}
            if(stage=="after_commit")foreach(var item in f.Contents.Contents){var path=Path.Combine(f.Final,item.Name);Assert.Throws<IOException>(()=>File.WriteAllText(path,"foreign"));Assert.Throws<IOException>(()=>File.Move(path,path+".foreign"));}
        }};
        var result=await f.Publish(publisher);Assert.True(result.Success);Assert.Equal(expected,result.PublicationReceipt!.ContentHashes.Values);Assert.Equal(10,identityCalls);Assert.Equal(10,shares);
        Assert.Equal(800015,publisher.CopiedBytes);foreach(var item in f.Contents.Contents)Assert.Equal(expected[f.Contents.Contents.ToList().IndexOf(item)],Independent(Path.Combine(f.Final,item.Name)));
    }
    private sealed class CopyFailure(string fault):IPublicationCopyReservation
    {
        private int checks;
        public Task EnsureCapacityAsync(CancellationToken token){token.ThrowIfCancellationRequested();if(fault=="capacity" && ++checks==2)throw new IOException("owned capacity failure");return Task.CompletedTask;}
        public async Task WriteAsync(FileStream output,ReadOnlyMemory<byte> bytes,CancellationToken token){await output.WriteAsync(bytes,token);if(fault=="copy")throw new IOException("owned post-write failure");}
        public void Dispose(){}
    }
    [Theory]
    [InlineData("short")] [InlineData("growth")] [InlineData("copy")] [InlineData("flush")] [InlineData("capacity")]
    [InlineData("cancel_copy")] [InlineData("cancel_flush")] [InlineData("cancel_namespace")] [InlineData("namespace")] [InlineData("post_commit")]
    public async Task FormalFaultsNeverReturnPartialDigestAndPreserveIndependentMedia(string fault)
    {
        var f=new Copies();using var cancel=new CancellationTokenSource();var old=Independent(f.Media);var reservation=new CopyFailure(fault);
        if(fault is "short" or "growth")File.WriteAllBytes(Path.Combine(f.Stage,"metadata.json"),new byte[fault=="short"?1:160004]);
        var publisher=new BundlePublicationReceipt.Publisher {BeginCopy=(_,_)=>Task.FromResult<IPublicationCopyReservation?>(reservation),StageForTests=(stage,_)=>{
            if((fault=="flush" && stage=="flush") || (fault=="post_commit" && stage=="after_commit"))throw new IOException("owned boundary failure");
            if(fault=="namespace" && stage=="before_commit")File.WriteAllText(f.Final,"owned namespace blocker");
            if((fault=="cancel_copy" && stage=="copy") || (fault=="cancel_flush" && stage=="flush") || (fault=="cancel_namespace" && stage=="namespace_prepared"))cancel.Cancel();
        }};
        if(fault.StartsWith("cancel"))await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Publish(publisher,cancel.Token));else await Assert.ThrowsAnyAsync<IOException>(()=>f.Publish(publisher));
        Assert.Equal(old,Independent(f.Media));Assert.Equal(fault=="post_commit",Directory.Exists(f.Final));
        if(fault=="namespace")Assert.Equal("owned namespace blocker",File.ReadAllText(f.Final));
        output.WriteLine("Fault injection into actual original-object copy/transaction: "+fault+"; no result/evidence escapes.");
    }
    [Theory] [InlineData("missing")] [InlineData("post_identity")] [InlineData("unbound")]
    public async Task OptionalIdentityUnavailableOrChangedCannotIssueAttachmentDigest(string fault)
    {
        var f=new Copies();var api=new PostIdentity();var publisher=new BundlePublicationReceipt.Publisher {IdentityApi=fault=="missing"?new BundlePublicationIdentityTests.NoIdentityApi():api};
        var request=BundlePublicationIdentityTests.RequestFor(f.Media,"r",fault=="unbound"?null:new string('a',32));
        if(fault!="post_identity")api.Drift=false;
        var result=await publisher.PublishAsync(request,f.Stage,f.Final,f.Contents,default);Assert.True(result.Success);Assert.Null(result.PublicationReceipt);Assert.NotNull(result.ContentsEvidence);Assert.True(File.Exists(f.Media));
    }
    private sealed class PostIdentity:IWindowsFileMetadataApi
    {
        private readonly IWindowsFileMetadataApi real=WindowsFileMetadataApi.Instance;private int calls;internal bool Drift=true;
        public bool Supported=>real.Supported;public uint DriveType(string r)=>real.DriveType(r);
        public SafeFileHandle Open(string p,uint a,uint s,uint c,uint f,out int e)=>throw new InvalidOperationException("No hash reopen");
        public bool Basic(SafeFileHandle h,out FileBasicInformation v,out int e)=>real.Basic(h,out v,out e);
        public bool Standard(SafeFileHandle h,out FileStandardInformation v,out int e)=>real.Standard(h,out v,out e);
        public bool Identity(SafeFileHandle h,out FileIdentityInformation v,out int e){var ok=real.Identity(h,out v,out e);if(Drift && ++calls>5)v.IdLow^=1;return ok;}
        public uint FinalPath(SafeFileHandle h,StringBuilder b,int c,out int e)=>real.FinalPath(h,b,c,out e);
    }
    [Fact]
    public async Task CancellationAfterWholeDirectoryCommitStillCarriesAllFiveHashes()
    {
        var f=new Copies();using var cancel=new CancellationTokenSource();var result=await f.Publish(new(){StageForTests=(stage,_)=>{if(stage=="after_commit")cancel.Cancel();}},cancel.Token);
        Assert.True(result.Success);Assert.Equal(5,result.PublicationReceipt!.ContentHashes.Values.Count);
        for(var i=0;i<5;i++)Assert.Equal(Independent(Path.Combine(f.Final,f.Contents.Contents[i].Name)),result.PublicationReceipt.ContentHashes.Values[i]);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PartialItemInsertAndActualCommitFailureRollbackEntireTupleThenReplayWins(bool commit)
    {
        using var f=new Fixture();var update=await f.Produce();var before=f.Writer.Read("r");
        if(commit){f.Execute("CREATE TABLE owned_fault(id TEXT REFERENCES ordinary_recording_history(recording_id) DEFERRABLE INITIALLY DEFERRED);");f.Writer.BeforeBundleCommitForTests=(c,t)=>{using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="INSERT INTO owned_fault VALUES('missing');";cmd.ExecuteNonQuery();};}
        else f.Execute("CREATE TRIGGER owned_attachment_fault BEFORE INSERT ON ordinary_bundle_attachment_hash_items WHEN NEW.ordinal=2 BEGIN SELECT RAISE(ABORT,'owned third item failure');END;");
        Assert.True(f.Writer.TryPublishBundle(update));Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.Read("r"));
        foreach(var table in new[]{"ordinary_bundle_attachment_hash_items","ordinary_bundle_identity_items","ordinary_bundle_contents_items"})Assert.Equal(0,f.Scalar("SELECT COUNT(*) FROM "+table));
        Assert.Equal(0,f.Scalar("SELECT COUNT(*) FROM ordinary_media_hash WHERE evidence_version IS NOT NULL"));
        Assert.Equal(0,f.Scalar("SELECT COUNT(*) FROM ordinary_bundle_attachment_hash WHERE evidence_version IS NOT NULL"));
        using(var other=new SqliteOrdinaryRecordingHistoryStore(f.Store))Assert.Equal(before,other.Read("r"));
        if(commit)f.Writer.BeforeBundleCommitForTests=null;else f.Execute("DROP TRIGGER owned_attachment_fault;");
        f.Accept(update);f.Accept(update);f.Reopen();AssertHashes(f.Writer.Read("r")!.Bundle!);
    }
    [Theory] [InlineData("foreign")] [InlineData("recovery")] [InlineData("unknown_winner")] [InlineData("trusted_winner")]
    public async Task ForeignLateAndUnknownResultsCannotOverwriteAcceptedWinner(string mode)
    {
        using var f=new Fixture();var update=await f.Produce();
        if(mode=="foreign") {var op=f.Op with {OperationId=new string('f',32)};Assert.Throws<ArgumentException>(()=>OrdinaryRecordingBundleStorage.ValidateUpdate(new(op,update.Result)));Assert.True(f.Writer.TryPublishBundle(new(op,new("ready",op.BundlePath,null))));Assert.Equal("generating",f.Writer.Read("r")!.Bundle!.Status);}
        if(mode=="recovery"){f.Reopen();f.Writer.RecoverIncomplete(At.AddHours(1));Assert.True(f.Writer.TryPublishBundle(update));Assert.Equal("interrupted_unknown",f.Writer.Read("r")!.Bundle!.Status);Assert.Null(f.Writer.Read("r")!.Bundle!.ContentHashEvidence);}
        if(mode is "unknown_winner" or "trusted_winner")
        {
            var unknown=update with {Result=update.Result with {ContentHashEvidence=null}};var winner=mode=="unknown_winner"?unknown:update;f.Accept(winner);
            Assert.True(f.Writer.TryPublishBundle(mode=="unknown_winner"?update:unknown));Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.Read("r"));
            using var other=new SqliteOrdinaryRecordingHistoryStore(f.Store);Assert.Equal(winner.Result,other.Read("r")!.Bundle);
        }
    }
    [Fact]
    public async Task PublicReadyAndFailedPublicationCannotMintHashesFromContentsOrMetadata()
    {
        using var f=new Fixture();var result=RecordingBundleGenerationResult.Ready(f.Op.BundlePath,BundleContentsGenerationTests.Items());Assert.Null(result.PublicationReceipt);
        f.Accept(new(f.Op,new("ready",f.Op.BundlePath,null,result.ContentsEvidence)));Assert.Null(f.Writer.Read("r")!.Bundle!.ContentHashEvidence);
        using var failed=new Fixture();File.WriteAllText(failed.Op.BundlePath,"owned blocker");var generated=await failed.Generator.GenerateAsync(failed.Request);
        Assert.False(generated.Success);Assert.Null(generated.PublicationReceipt);Assert.True(failed.Writer.TryPublishBundle(new(failed.Op,new("failed",failed.Op.BundlePath,generated.ErrorCode))));Assert.Equal("completed",failed.Writer.Read("r")!.Status);Assert.Null(failed.Writer.Read("r")!.Bundle!.ContentHashEvidence);
    }
    [Theory]
    [InlineData("DROP INDEX idx_ordinary_bundle_attachment_hash_ordinal; UPDATE ordinary_bundle_attachment_hash_items SET ordinal=0 WHERE ordinal=2;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash_items SET ordinal=99 WHERE ordinal=1; UPDATE ordinary_bundle_attachment_hash_items SET ordinal=1 WHERE ordinal=2; UPDATE ordinary_bundle_attachment_hash_items SET ordinal=2 WHERE ordinal=99;")]
    [InlineData("DELETE FROM ordinary_bundle_attachment_hash;")]
    [InlineData("DELETE FROM ordinary_bundle_attachment_hash_items WHERE ordinal=2;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash SET evidence_version=2;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash SET evidence_version='wrong';")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash SET item_count=4;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash SET item_count='wrong';")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash SET operation_id='ffffffffffffffffffffffffffffffff';")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash SET media_output_path=media_output_path||'.foreign';")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash SET bundle_path=bundle_path||'.foreign';")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash SET evidence_version=NULL,item_count=0;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash_items SET ordinal=8 WHERE ordinal=2;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash_items SET name='foreign' WHERE ordinal=2;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash_items SET output_path=output_path||'.foreign' WHERE ordinal=2;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash_items SET output_size_bytes=1 WHERE ordinal=2;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash_items SET output_size_bytes='wrong' WHERE ordinal=2;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash_items SET algorithm='sha512' WHERE ordinal=2;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash_items SET algorithm=x'0102' WHERE ordinal=2;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash_items SET hash_value=upper(hash_value) WHERE ordinal=2;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash_items SET hash_value='abcd' WHERE ordinal=2;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash_items SET hash_value=x'0102' WHERE ordinal=2;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash_items SET basis='capture' WHERE ordinal=2;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash_items SET volume_id=zeroblob(8) WHERE ordinal=2;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash_items SET file_id=zeroblob(16) WHERE ordinal=2;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash_items SET file_id=x'0102' WHERE ordinal=2;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash_items SET file_id='wrong' WHERE ordinal=2;")]
    [InlineData("DELETE FROM ordinary_recording_bundles;")]
    [InlineData("DELETE FROM ordinary_bundle_identity_items WHERE ordinal=2;")]
    [InlineData("UPDATE ordinary_bundle_identity_items SET file_id=zeroblob(16) WHERE ordinal=2;")]
    [InlineData("UPDATE ordinary_bundle_contents_items SET size_bytes=1 WHERE ordinal=2;")]
    public async Task CorruptFiveItemSetFailsEveryProjectionIncludingArtifactSnapshot(string sql)
    {
        using var f=new Fixture();f.Accept(await f.Produce());f.Execute("PRAGMA foreign_keys=OFF;PRAGMA ignore_check_constraints=ON;"+sql);
        Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.Read("r"));Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.ReadPage(100,null));Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.ReadArtifactFacts("r"));
        Assert.Throws<UnifiedHistoryUnavailableException>(()=>new UnifiedHistoryQueryService(f.Store,f.Writer).GetPage(Sid,Session,100,new("ordinary")));
    }
    [Theory] [InlineData(1)] [InlineData(20)] [InlineData(100)]
    public async Task IndexedHundredParentFiveHundredItemBatchUsesOneWalSnapshotWithoutAttachmentReads(int limit)
    {
        using var f=new Fixture();var update=await f.Produce();f.Accept(update);
        for(var i=0;i<100;i++)
        {
            var id="r"+i.ToString("D3");var media=Path.Combine(f.Root,id+".mp4");var op=OrdinaryRecordingBundleOperation.Create(id,media);f.Writer.Register(Row(id,media,100) with {BundleRegistration=new(op)});
            var stage=Path.Combine(f.Root,"batch-stage");Directory.CreateDirectory(stage);var contents=new RecordingBundleContentsEvidence(BundleContentsGenerationTests.Items());foreach(var item in contents.Contents)File.WriteAllBytes(Path.Combine(stage,item.Name),new byte[item.SizeBytes]);
            var result=await new BundlePublicationReceipt.Publisher().PublishAsync(BundlePublicationIdentityTests.RequestFor(media,id,op.OperationId),stage,op.BundlePath,contents,default);
            Assert.True(f.Writer.TryPublishBundle(new(op,new("ready",op.BundlePath,null,contents){IdentityEvidence=result.PublicationReceipt!.Evidence,ContentHashEvidence=result.PublicationReceipt.ContentHashes})));Assert.NotNull(f.Writer.Read(id)!.Bundle!.ContentHashEvidence);
            foreach(var item in contents.Contents)File.Delete(Path.Combine(op.BundlePath,item.Name));
        }
        var query=new UnifiedHistoryQueryService(f.Store,f.Writer);var calls=0;var plans=new List<string>();query.OrdinaryFileProbeForTests=_=>false;
        query.AttachmentHashCommandForTests=(c,t,sql,args)=>{calls++;Assert.Equal(limit,args.Count);Assert.Contains("LIMIT 501",sql);using var explain=c.CreateCommand();explain.Transaction=t;explain.CommandText="EXPLAIN QUERY PLAN "+sql;foreach(var a in args)explain.Parameters.AddWithValue(a.Key,a.Value);using(var reader=explain.ExecuteReader())while(reader.Read())plans.Add(reader.GetString(3));f.Execute("PRAGMA foreign_keys=OFF;DELETE FROM ordinary_bundle_attachment_hash_items;");};
        var page=query.GetPage(Sid,Session,limit,new("ordinary"));Assert.Equal(limit,page.Items.Count);Assert.Equal(1,calls);Assert.NotNull(page.NextCursor);Assert.All(page.Items,r=>Assert.Equal(5,r.Bundle!.ContentHashEvidence!.Values.Count));
        Assert.Contains(plans,p=>p.Contains("SEARCH i USING INDEX "+SqliteSchemaV37.IndexName));Assert.Contains(plans,p=>p.Contains("SEARCH h USING INDEX"));query.AttachmentHashCommandForTests=null;
        Assert.Throws<UnifiedHistoryUnavailableException>(()=>query.GetPage(Sid,Session,limit,new("ordinary")));output.WriteLine(string.Join("\n",plans));
    }
    [Fact]
    public void GenuineV36UpgradePreservesEveryOldByteReceiptAndNeverBackfillsMetadataHashes()
    {
        var root=NewRoot();var db=Path.Combine(root,"legacy.db");var media=Path.Combine(root,"absent.mp4");var bundle=OrdinaryRecordingBundleOperation.DerivePath(media);
        Directory.CreateDirectory(bundle);File.WriteAllText(Path.Combine(bundle,"metadata.json"),"{\"media\":{\"sha256\":\""+new string('a',64)+"\"}}");
        using(var c=new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();SqliteOutputPathCollation.Register(c);using var cmd=c.CreateCommand();foreach(var m in SqliteSchemaCatalog.Migrations.Where(m=>m.Version<=36)){cmd.CommandText=m.Definition;cmd.ExecuteNonQuery();cmd.CommandText="INSERT INTO schema_migrations VALUES($v,$name,$hash,0)";cmd.Parameters.AddWithValue("$v",m.Version);cmd.Parameters.AddWithValue("$name",m.Name);cmd.Parameters.AddWithValue("$hash",m.Checksum);cmd.ExecuteNonQuery();cmd.Parameters.Clear();}
            cmd.CommandText="INSERT INTO ordinary_recording_history(recording_id,history_sequence,created_at_utc,status_code,source_type,capture_semantics,source_title,audio_mode,output_path,started_at_utc,completed_at_utc,output_size_bytes,output_duration_ms,updated_at_utc) VALUES('legacy',1,$at,'completed','display','display_capture','Synthetic','none',$media,$at,$at,100,1000,$at);INSERT INTO ordinary_recording_bundles VALUES('legacy',$media,'ffffffffffffffffffffffffffffffff','ready',$bundle,NULL);";cmd.Parameters.AddWithValue("$at",At.UtcTicks);cmd.Parameters.AddWithValue("$media",media);cmd.Parameters.AddWithValue("$bundle",bundle);cmd.ExecuteNonQuery();
        }
        var before=UnifiedHistoryProfileFilterTests.RawSnapshot(db,schemaThrough:36);var store=new SqliteOperationalStore(db);using var blocked=new FileStream(Path.Combine(bundle,"metadata.json"),FileMode.Open,FileAccess.Read,FileShare.None);
        store.Initialize();store.Initialize();Assert.Equal(before,UnifiedHistoryProfileFilterTests.RawSnapshot(db,schemaThrough:36));using var writer=new SqliteOrdinaryRecordingHistoryStore(store);Assert.Null(writer.Read("legacy")!.Bundle!.ContentHashEvidence);
        using(var c=store.OpenReadOnlyConnection()){using var cmd=c.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM ordinary_bundle_attachment_hash;";Assert.Equal(0L,cmd.ExecuteScalar());}
        var row=Row("fresh",Path.Combine(root,"fresh.mp4"),100);writer.Register(row with {BundleRegistration=new(OrdinaryRecordingBundleOperation.Create("fresh",row.OutputPath))});Assert.Null(writer.Read("fresh")!.Bundle!.ContentHashEvidence);
        using(var c=store.OpenConnection()){using var cmd=c.CreateCommand();cmd.CommandText="DELETE FROM ordinary_bundle_attachment_hash WHERE recording_id='fresh';";cmd.ExecuteNonQuery();}
        Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>writer.Read("fresh"));Assert.Null(writer.Read("legacy")!.Bundle!.ContentHashEvidence);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SilentFormalFfmpegFinalizerAndRestartedAuthenticatedThreeApiProjections(bool headless)
    {
        using var f=new Fixture();var priorDir=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");var priorMode=Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
        RecordingEngine? engine=null;FfmpegCaptureBackend? backend=null;
        try
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",f.Root);Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE","1");DataDirResolver.SetOverride(f.Root);ApiKeyAuth.InitializeForTesting(f.Root);
            SystemQuery.SetDisplayTopologyProvider(()=>new() {new("display_1","Synthetic",true,new(0,0,1920,1080),1.0,"synthetic-test-display:display_1",DisplayIdentityResolutionStatus.Resolved)});
            var audit=new RetentionSharedOutputPathTests.QuietAudit();var tray=new Tray(headless);var generator=new FfmpegRecordingBundleGenerator();
            engine=new(audit,bundleGenerator:generator);engine.ConfigureOrdinaryRecordingHistory(f.Writer);engine.SetTray(tray);
            backend=new() { VideoInputArgumentsForTests=_=>new[]{"-re","-f","lavfi","-t","1","-i","color=c=black:s=64x64:r=15"} };engine.BackendFactory=_=>(backend,"ffmpeg");
            engine.CreateRecording(new JsonObject { ["source"]=new JsonObject {["type"]="display",["display_id"]="display_1"},["video"]=new JsonObject {["fps"]=15},["duration_seconds"]=1,["countdown_seconds"]=0,
                ["output"]=new JsonObject {["directory"]=Path.GetDirectoryName(f.Op.MediaPath),["filename"]="formal.mp4",["conflict_policy"]="fail"}},"task342-synthetic",tray);
            tray.Confirm!(ConfirmationDecision.Approve());var rec=Assert.Single(engine._recs.Values);
            await Until(()=>f.Writer.Read(rec.Id)?.Bundle?.Status is "ready" or "failed");Assert.Equal(RecState.completed,rec.State);var row=f.Writer.Read(rec.Id)!;AssertHashes(row.Bundle!);
            Assert.Equal("ready",row.Bundle!.Status);Assert.NotNull(rec.LastMeta!.PublicationReceipt);Assert.NotNull(row.MediaHash);
            Assert.Equal(Independent(rec.OutputPath),row.MediaHash!.Value);Assert.False(new FfmpegOutputProber().Probe(rec.OutputPath).HasAudioStream);
            using(var metadata=JsonDocument.Parse(File.ReadAllText(Path.Combine(row.Bundle.Path!,"metadata.json"))))Assert.Equal(row.MediaHash.Value,metadata.RootElement.GetProperty("media").GetProperty("sha256").GetString());
            await AssertApis(engine,f.Store,f.Writer,row,headless);engine.Dispose();engine=null;backend.Dispose();backend=null;f.Reopen();
            engine=new(audit);engine.ConfigureOrdinaryRecordingHistory(f.Writer);engine.SetTray(tray);Assert.Equal(row.MediaHash,f.Writer.Read(rec.Id)!.MediaHash);await AssertApis(engine,f.Store,f.Writer,row,headless);
        }
        finally
        {
            engine?.Dispose();backend?.Dispose();ApiKeyAuth.ResetForTesting(null);SystemQuery.SetDisplayTopologyProvider(null);DataDirResolver.ClearOverride();
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",priorDir);Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE",priorMode);
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AvFormalMuxManualAndNaturalFinalizersCarryPrivateMediaReceipt(bool natural)
    {
        var root=NewRoot();var video=Path.Combine(root,"video.mp4");var audio=Path.Combine(root,"audio.wav");
        await Generate(new[]{"-y","-f","lavfi","-i","color=c=blue:s=64x64:r=15","-t","1","-c:v","libx264","-pix_fmt","yuv420p",video});
        await Generate(new[]{"-y","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","4","-c:a","pcm_s16le",audio});
        // Reuse the accepted exception-safe fixture without editing its code, budgets or restoration contract.
        var type=typeof(OrdinaryAvStorageSafetyTests).GetNestedType("Fixture",BindingFlags.NonPublic)!;
        using var owned=(IDisposable)type.GetConstructor(BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(bool),typeof(string),typeof(string),typeof(int)},null)!.Invoke(new object?[]{true,audio,video,0});
        var history=Field<SqliteOrdinaryRecordingHistoryStore>(owned,"History");var engine=Field<RecordingEngine>(owned,"Engine");var store=Field<SqliteOperationalStore>(owned,"Store");
        using var response=await (Task<HttpResponseMessage>)type.GetMethod("Create",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(owned,new object[]{"raw","display","system_audio",1})!;
        Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());var tray=Field<object>(owned,"Tray");tray.GetType().GetMethod("Approve",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(tray,new object?[]{Path.Combine(Field<string>(owned,"Root"),"Videos")});
        var rec=Assert.Single(engine._recs.Values);await Until(()=>rec.State==RecState.recording);
        if(natural)Field<FakeVideoCaptureWorker>(owned,"Video").EmitNaturalExit(0,"");else engine.Stop(rec.Id,"user_requested");
        await Until(()=>history.Read(rec.Id)?.Bundle?.Status is "ready" or "failed");Assert.Equal(RecState.completed,rec.State);
        var row=history.Read(rec.Id)!;AssertHashes(row.Bundle!);Assert.Equal("ready",row.Bundle!.Status);Assert.NotNull(row.MediaHash);Assert.NotNull(rec.LastMeta!.PublicationReceipt);
        Assert.True(new FfmpegOutputProber().Probe(rec.OutputPath).HasAudioStream);Assert.Equal(Independent(rec.OutputPath),row.MediaHash!.Value);
        await AssertApis(engine,store,history,row,false);AvMuxOwnershipTests.AssertNoOwnedMux(rec.OutputPath);
    }
    private static T Field<T>(object fixture,string name)=>(T)fixture.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(fixture)!;
    private static async Task Until(Func<bool> done)
    { var stop=DateTime.UtcNow.AddSeconds(15);while(!done() && DateTime.UtcNow<stop)await Task.Delay(20);Assert.True(done(),"Synthetic producer did not finish in its owned deadline."); }
    private static async Task Generate(string[] args)
    {
        var info=new ProcessStartInfo(Path.Combine(TestHelper.ProjectRoot,"tools/ffmpeg/bin/ffmpeg.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};foreach(var arg in args)info.ArgumentList.Add(arg);
        using var p=Process.Start(info)!;var err=p.StandardError.ReadToEndAsync();await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));Assert.True(p.ExitCode==0,await err);
    }
    private static async Task AssertApis(RecordingEngine engine,SqliteOperationalStore store,SqliteOrdinaryRecordingHistoryStore writer,OrdinaryRecordingHistorySnapshot row,bool headless)
    {
        var audit=new RetentionSharedOutputPathTests.QuietAudit();var q=new UnifiedHistoryQueryService(store,writer,headless);var files=new RetentionPreviewQueryTests.MetadataFake();
        var preview=new RetentionPreviewQueryService(q.GetOrdinaryEvidencePage,Path.GetDirectoryName(Path.GetDirectoryName(row.OutputPath))!,files,files,()=>DateTimeOffset.UtcNow.AddDays(1));
        var api=new ApiServer(engine,audit,new Tray(headless),null,null,null,null,null,null,null,null,null,0,
            unifiedHistoryGateway:new SqliteUnifiedHistoryGateway(q,()=>(Sid,Session)),artifactCheckGateway:new SqliteOrdinaryArtifactCheckGateway(new(writer)),retentionPreviewGateway:new SqliteRetentionPreviewGateway(preview,()=>(Sid,Session)));
        try
        {
        api.Start();using var client=new HttpClient(new HttpClientHandler {UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{api.BoundPort}"),Timeout=TimeSpan.FromSeconds(10)};
        using(var denied=await client.GetAsync("/api/v1/recording-history/"+row.RecordingId))Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
        foreach(var endpoint in new[]{"/api/v1/recording-history","/api/v1/recording-history/"+row.RecordingId,"/api/v1/history?source=ordinary"})
        {
            using var response=await client.GetAsync(endpoint);Assert.Equal(HttpStatusCode.OK,response.StatusCode);var text=await response.Content.ReadAsStringAsync();using var json=JsonDocument.Parse(text);var data=json.RootElement.GetProperty("data");
            var entry=data.TryGetProperty("items",out var items)?items.EnumerateArray().Single(i=>i.GetProperty("recording_id").GetString()==row.RecordingId):data.GetProperty("recording");var hash=entry.GetProperty("media_hash");
            AssertContentJson(entry.GetProperty("bundle"),row.Bundle!);
            Assert.Equal(4,hash.EnumerateObject().Count());Assert.Equal("sha256",hash.GetProperty("algorithm").GetString());Assert.Equal(row.MediaHash!.Value,hash.GetProperty("value").GetString());Assert.Equal("bundle_generation",hash.GetProperty("basis").GetString());Assert.Equal(row.OutputSizeBytes,hash.GetProperty("size_bytes").GetInt64());
            foreach(var secret in new[]{"file_id","volume_id","operation_id","receipt","anchor","integrity_verified"})Assert.DoesNotContain("\""+secret+"\":",text,StringComparison.OrdinalIgnoreCase);
        }
        using(var caps=await client.GetAsync("/api/v1/capabilities"))
        { using var json=JsonDocument.Parse(await caps.Content.ReadAsStringAsync());var data=json.RootElement.GetProperty("data");Assert.True(data.GetProperty("recording_history").GetProperty("media_hash_supported").GetBoolean());Assert.Equal("bundle_generation",data.GetProperty("unified_history").GetProperty("media_hash_basis").GetString());var artifact=data.GetProperty("ordinary_artifact_check");Assert.True(artifact.GetProperty("metadata_only").GetBoolean());Assert.False(artifact.GetProperty("content_verification").GetBoolean());Assert.False(artifact.GetProperty("deletion_supported").GetBoolean()); }
        using(var retention=await client.GetAsync("/api/v1/retention/preview?completed_before="+Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))))
        { Assert.Equal(HttpStatusCode.OK,retention.StatusCode);using var json=JsonDocument.Parse(await retention.Content.ReadAsStringAsync());var data=json.RootElement.GetProperty("data");var item=data.GetProperty("items").EnumerateArray().Single(i=>i.GetProperty("recording_id").GetString()==row.RecordingId);Assert.Equal("protected",item.GetProperty("evaluation").GetString());Assert.Equal("bundle_artifact_reference",item.GetProperty("reason_code").GetString());Assert.True(data.GetProperty("read_only").GetBoolean());Assert.False(data.GetProperty("deletion_supported").GetBoolean());Assert.Empty(files.Observations); }
        }
        finally { api.Stop(); }
    }
    private sealed class Tray(bool headless) : ITrayContext
    {
        internal Action<ConfirmationDecision>? Confirm;
        public string HostMode=>headless?"headless":"tray";public bool SupportsRegionSelectionUi=>false;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> c)=>Confirm=c;
        public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> c)=>throw new InvalidOperationException("No UI");
        public void SetRecording(RecordingUiPresentation p){} public void SetIdle(RecordingUiPresentation p){}public void SetAllIdle(){}public void ShowError(string s){}
    }
    private sealed class Metadata(Action? identityRead,bool drift=false) : IWindowsFileMetadataApi
    {
        private readonly IWindowsFileMetadataApi real=WindowsFileMetadataApi.Instance;private int basics;
        public bool Supported=>real.Supported; public uint DriveType(string root)=>real.DriveType(root);
        public SafeFileHandle Open(string p,uint a,uint s,uint c,uint f,out int e)=>throw new InvalidOperationException("No reopen allowed");
        public bool Basic(SafeFileHandle h,out FileBasicInformation b,out int e) {var ok=real.Basic(h,out b,out e);if(drift && ++basics==2)b.LastWriteTime++;return ok;}
        public bool Standard(SafeFileHandle h,out FileStandardInformation s,out int e)=>real.Standard(h,out s,out e);
        public bool Identity(SafeFileHandle h,out FileIdentityInformation i,out int e) {identityRead?.Invoke();return real.Identity(h,out i,out e);}
        public uint FinalPath(SafeFileHandle h,StringBuilder b,int c,out int e)=>real.FinalPath(h,b,c,out e);
    }
}
