using AgentRecorder.Infrastructure;
using AgentRecorder.Core;
using AgentRecorder.Persistence;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.OrdinaryArtifactHistoryTests;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class MetadataPathUnicodeTests(ITestOutputHelper output)
{
    // The test file and its literals are ASCII. C# interprets explicit codepoints.
    private static IEnumerable<string> Paths(string stem, bool includeBareLeaf)
    {
        if(includeBareLeaf)yield return @"C:\sample\"+stem;
        yield return @"C:\sample\"+stem+".mp4";
        yield return @"C:\sample\"+stem+@"\media.mp4";
        yield return @"C:\sample\"+stem+@".mp4\media.mp4";
    }

    public static IEnumerable<object[]> InvalidPaths()
    {
        var numbers="123456789\u00B9\u00B2\u00B3";
        foreach(var family in new[]{"COM","com","LPT","lpt"})
            foreach(var number in numbers)
                foreach(var path in Paths(family+number,includeBareLeaf:true))yield return [path];
        foreach(var stem in new[]{"CON","PRN","AUX","NUL","CONIN$","CONOUT$","CLOCK$"})
            foreach(var path in Paths(stem,includeBareLeaf:true))yield return [path];
    }

    public static IEnumerable<object[]> LegalPaths()
    {
        foreach(var family in new[]{"COM","com","LPT","lpt"})
            foreach(var number in "\u9E7F\u864F\u9C81")
                foreach(var path in Paths(family+number,includeBareLeaf:false))yield return [path];
        yield return ["C:\\"+"\u6D4B\u8BD5"+"\\"+"\u89C6\u9891.mp4"];
        yield return ["C:\\sample\\"+"\u5F55\u50CF.mp4"];
        foreach(var stem in new[]{"COM0","COM10","LPT0","LPT10"})
            yield return [@"C:\sample\"+stem+".mp4"];
    }

    public static IEnumerable<object[]> InvalidServices()=>Services(InvalidPaths());
    public static IEnumerable<object[]> LegalServices()=>Services(LegalPaths());
    private static IEnumerable<object[]> Services(IEnumerable<object[]> paths)
    {
        foreach(var row in paths)
            foreach(var source in new[]{"ordinary","plan","future_window"})yield return [source,row[0]];
    }

    [Theory][MemberData(nameof(InvalidPaths))]
    public void DeviceCodepointsAreRejectedLexically(string path)
    {
        Assert.Equal("path_invalid",OrdinaryArtifactMetadataSampler.PathReason(path,out var parents));
        Assert.Empty(parents);
    }

    [Theory][MemberData(nameof(LegalPaths))]
    public void ChineseAndNonDeviceNamesAreAcceptedLexically(string path)
    {
        Assert.Null(OrdinaryArtifactMetadataSampler.PathReason(path,out var parents));
        Assert.InRange(parents.Length,2,3);Assert.Equal(@"C:\",parents[0]);
    }

    private static OrdinaryRecordingHistorySnapshot Ordinary(string path)
    {
        var at=new DateTimeOffset(2026,10,13,0,0,0,TimeSpan.Zero);
        return new("unicode",at,"completed","display","display_capture","Synthetic",15,"none",
            null,null,null,null,path,null,null,null,null,null,null,at.AddSeconds(1),at.AddSeconds(2),"duration_reached",12,1);
    }

    private static async Task<OrdinaryArtifactFileCheck> Check(string source,string path,Ancestors ancestors,Leaf leaf,Action read,Func<string,bool> drive)
    {
        if(source=="ordinary")
        {
            var service=new OrdinaryArtifactCheckService(_=>{read();return new(Ordinary(path),null);},ancestors,leaf,drive);
            var result=(await service.ReadAsync("unicode"))!;Assert.Equal("complete",result.ProbeState);
            Assert.Null(result.Bundle.Contents);return result.Media;
        }
        var history=new HistoryArtifactCheckService((id,_,_)=>{read();return new(id,source,"completed",path,12,null);},ancestors,leaf,drive);
        var checkedHistory=(await history.ReadAsync(source+":unicode","sid","session"))!;
        Assert.Equal("complete",checkedHistory.ProbeState);Assert.Equal(source,checkedHistory.Source);return checkedHistory.Media;
    }

    [Theory][MemberData(nameof(InvalidServices))]
    public async Task BothEndpointsRejectDevicesBeforeDriveAncestorsAndLeaf(string source,string path)
    {
        var ancestors=new Ancestors();var leaf=new Leaf();var reads=0;var drives=0;
        var result=await Check(source,path,ancestors,leaf,()=>reads++,_=>{drives++;return true;});
        output.WriteLine($"source={source}; observation={result.Observation}; reason={result.ReasonCode}; reads={reads}; drives={drives}; ancestors={ancestors.Calls.Count}; leaves={leaf.Calls.Count}; adapters only, no native device IO.");
        Assert.Equal(1,reads);Assert.Equal("unsafe_path",result.Observation);Assert.Equal("path_invalid",result.ReasonCode);
        Assert.Equal(0,drives);Assert.Empty(ancestors.Calls);Assert.Empty(leaf.Calls);Assert.Null(result.SampledAtUtc);
        Assert.Null(result.ObservedSizeBytes);Assert.Equal("unknown",result.IdentityState);
    }

    [Theory][MemberData(nameof(LegalServices))]
    public async Task BothEndpointsSampleLegalChineseNamesThroughAdapters(string source,string path)
    {
        var ancestors=new Ancestors();var leaf=new Leaf();var reads=0;var drives=0;
        var result=await Check(source,path,ancestors,leaf,()=>reads++,_=>{drives++;return true;});
        output.WriteLine($"source={source}; observation={result.Observation}; reads={reads}; drives={drives}; ancestors={ancestors.Calls.Count}; leaves={leaf.Calls.Count}; adapters only, no native IO.");
        Assert.Equal(1,reads);Assert.Equal(1,drives);Assert.Null(OrdinaryArtifactMetadataSampler.PathReason(path,out var parents));
        Assert.Equal(parents,ancestors.Calls);Assert.Equal(path,Assert.Single(leaf.Calls));
        Assert.Equal("present_size_matches",result.Observation);Assert.Equal("unverified",result.IdentityState);
        Assert.Equal(12,result.RecordedSizeBytes);Assert.Equal(12,result.ObservedSizeBytes);Assert.NotNull(result.SampledAtUtc);
    }
}
