using AgentRecorder.Capture;
using AgentRecorder.Core;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class BundleContentsGenerationTests
{
    internal static RecordingBundleContentItem[] Items(long start = 11) => new[] {
        new RecordingBundleContentItem("metadata.json","application/json",start),
        new RecordingBundleContentItem("thumbnail.jpg","image/jpeg",start+1),
        new RecordingBundleContentItem("first_frame.png","image/png",start+2),
        new RecordingBundleContentItem("last_frame.png","image/png",start+3),
        new RecordingBundleContentItem("marks.json","application/json",start+4) };

    [Fact]
    public void ResultsAndBothSnapshotsDetachCollectionsAndReplayByValue()
    {
        var items = Items(); var result = RecordingBundleGenerationResult.Ready("owned.bundle", items);
        var snapshot = new OrdinaryRecordingBundleSnapshot("ready","owned.bundle",null,result.ContentsEvidence);
        var live = RecordingBundleSnapshot.Ready("owned.bundle",items);
        items[0] = new("metadata.json","application/json",999);
        Assert.Equal(11,result.ContentsEvidence!.Contents[0].SizeBytes);
        Assert.Equal(11,snapshot.ContentsEvidence!.Contents[0].SizeBytes); Assert.Equal(11,live.Contents[0].SizeBytes);
        Assert.NotSame(result.ContentsEvidence.Contents,snapshot.ContentsEvidence.Contents);
        Assert.Throws<NotSupportedException>(() => ((IList<RecordingBundleContentItem>)result.ContentsEvidence.Contents)[0] = items[0]);
        var equal = new OrdinaryRecordingBundleSnapshot("ready","owned.bundle",null,new(Items().Reverse()));
        Assert.Equal(snapshot,equal); Assert.Equal(snapshot.GetHashCode(),equal.GetHashCode());
        Assert.NotEqual(snapshot,equal with { ContentsEvidence = new(Items(21)) });
        Assert.Null(RecordingBundleGenerationResult.Ready("owned.bundle").ContentsEvidence);
        Assert.Null(RecordingBundleGenerationResult.Failed(RecordingBundleErrorCodes.GenerationFailed).ContentsEvidence);
    }

    [Theory]
    [InlineData("missing")] [InlineData("extra")] [InlineData("duplicate")] [InlineData("type")]
    [InlineData("zero")] [InlineData("negative")] [InlineData("traversal")] [InlineData("absolute")]
    [InlineData("separator")] [InlineData("case")] [InlineData("null")]
    public void OnlyCompleteKnownPositiveOutputsAreEvidence(string fault)
    {
        Assert.Throws<ArgumentException>(() => RecordingBundleGenerationResult.Ready("owned.bundle", BadItems(fault)));
    }

    internal static RecordingBundleContentItem[] BadItems(string fault)
    {
        var items = Items();
        return fault switch {
            "missing" => items[..4], "extra" => items.Append(new("extra.txt","text/plain",1)).ToArray(),
            "duplicate" => new[] {items[0],items[0],items[2],items[3],items[4]},
            "type" => Replace(items,new("metadata.json","text/plain",1)),
            "zero" => Replace(items,new("metadata.json","application/json",0)),
            "negative" => Replace(items,new("metadata.json","application/json",-1)),
            "traversal" => Replace(items,new("../metadata.json","application/json",1)),
            "absolute" => Replace(items,new(@"C:\metadata.json","application/json",1)),
            "separator" => Replace(items,new(@"dir\metadata.json","application/json",1)),
            "case" => Replace(items,new("METADATA.json","application/json",1)),
            "null" => Replace(items,null!), _ => throw new ArgumentException(fault) };
    }
    private static RecordingBundleContentItem[] Replace(RecordingBundleContentItem[] items,RecordingBundleContentItem value)
    { items[0]=value; return items; }

    [Fact]
    public void UnboundedProducerStopsAtSixAndLargeIndividualLengthsNeedNoTotal()
    {
        var enumerated = 0;
        IEnumerable<RecordingBundleContentItem> Endless()
        { while(true) { enumerated++; yield return new("metadata.json","application/json",1); } }
        Assert.Throws<ArgumentException>(()=>new RecordingBundleContentsEvidence(Endless())); Assert.Equal(6,enumerated);
        var items=Items();items[0]=new("metadata.json","application/json",long.MaxValue);
        Assert.Equal(long.MaxValue,new RecordingBundleContentsEvidence(items).Contents[0].SizeBytes);
    }

    [Theory]
    [InlineData("success")] [InlineData("existing")] [InlineData("publish")]
    [InlineData("metadata")] [InlineData("marks")] [InlineData("frame")] [InlineData("cancel")]
    public async Task GeneratorReturnsLengthsOnlyAfterSuccessfulFiveFilePublication(string fault)
    {
        using var f = new OrdinaryBundleContentsHistoryTests.Fixture();
        var media=Path.Combine(f.Root,"generated.mp4");await File.WriteAllBytesAsync(media,new byte[100]);
        var bundle=OrdinaryRecordingBundleOperation.DerivePath(media);
        if(fault=="existing")Directory.CreateDirectory(bundle);
        if(fault=="publish")await File.WriteAllTextAsync(bundle,"owned publication blocker");
        using var cancellation=new CancellationTokenSource(); if(fault=="cancel")cancellation.Cancel();
        var generator=new FfmpegRecordingBundleGenerator(new Runner(fault),ffmpegPathProvider:()=>"owned-fake-ffmpeg.exe");
        var request = new RecordingBundleRequest("r",null,"display","Synthetic",(0,0,64,64),"virtual_screen",
            DateTime.UtcNow.AddSeconds(-1),DateTime.UtcNow,1,1,15,"ffmpeg","duration_reached",false,"not_requested",null,null,null,null,null,null,
            media,"mp4","h264",64,64);
        if(fault=="cancel")
        { await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>generator.GenerateAsync(request,cancellation.Token));return; }
        var result=await generator.GenerateAsync(request,cancellation.Token);
        Assert.Equal(fault=="success",result.Success);
        if(!result.Success) { Assert.Null(result.ContentsEvidence);Assert.Null(result.BundlePath);return; }
        Assert.Equal(bundle,result.BundlePath); Assert.NotNull(result.ContentsEvidence);
        Assert.Equal(Items().Select(i=>i.Name),result.ContentsEvidence.Contents.Select(i=>i.Name));
        foreach(var item in result.ContentsEvidence.Contents)
            Assert.Equal(new FileInfo(Path.Combine(bundle,item.Name)).Length,item.SizeBytes);
    }

    private sealed class Runner(string fault) : IExternalProcessRunner
    {
        public Task<ExternalProcessResult> RunAsync(string filename,IReadOnlyList<string> args,TimeSpan timeout,
            bool captureStderr=true,System.Text.Encoding? stderrEncoding=null,CancellationToken cancellationToken=default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(fault=="frame")return Task.FromResult(new ExternalProcessResult(1,false,"owned injected frame failure"));
            var path=args.Last();var dir=Path.GetDirectoryName(path)!;
            if(fault is "metadata" or "marks")Directory.CreateDirectory(Path.Combine(dir,fault+".json"));
            var header=path.EndsWith(".png",StringComparison.Ordinal)?new byte[]{137,80,78,71,13,10,26,10}:new byte[]{255,216};
            File.WriteAllBytes(path,header.Concat(new byte[100]).ToArray());
            return Task.FromResult(new ExternalProcessResult(0,false,""));
        }
    }
}
