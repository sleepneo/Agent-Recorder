using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RetentionHandleMetadataTests
{
    internal static HandleMetadataSnapshot Stable(long length = 12) => new(length, 0, 1, FileAttributes.Normal, 0, 0,
        133000000000000000, new(0x123456789abcdef0, 0xfedcba9876543210, 0x876543210abcdef0));
    internal static FileMetadataObservation Good(string path, long length = 12) => new(FileObservationOutcome.Sampled, Stable(length), Stable(length), path, path);
    internal static FileMetadataObservation Alter(string kind, string path)
    {
        var observation=Good(path);var a=observation.Before!;var b=a;
        switch(kind)
        {
            case "hard_link": a=a with{Links=2};b=a;break;
            case "link_change": b=b with{Links=2};break;
            case "delete_pending": a=a with{DeletePending=1};b=a;break;
            case "delete_change": b=b with{DeletePending=1};break;
            case "identity": b=b with{Identity=b.Identity! with{IdHigh=123}};break;
            case "identity_low": b=b with{Identity=b.Identity! with{IdLow=123}};break;
            case "volume": b=b with{Identity=b.Identity! with{VolumeSerialNumber=123}};break;
            case "attributes": b=b with{Attributes=FileAttributes.ReadOnly};break;
            case "length": b=b with{Length=13};break;
            case "allocation": b=b with{AllocationSize=4096};break;
            case "write_time": b=b with{LastWriteTime=b.LastWriteTime+1};break;
            case "reparse": a=a with{Attributes=FileAttributes.ReparsePoint};b=a;break;
            case "directory": a=a with{Attributes=FileAttributes.Directory,Directory=1};b=a;break;
            case "late_reparse": b=b with{Attributes=FileAttributes.ReparsePoint};break;
            case "late_directory": b=b with{Attributes=FileAttributes.Directory,Directory=1};break;
            case "negative_length": a=a with{Length=-1};b=a;break;
            case "zero_links": a=a with{Links=0};b=a;break;
            case "missing_identity": a=a with{Identity=null};b=a;break;
            case "zero_identity": a=a with{Identity=new(1,0,0)};b=a;break;
            case "zero_volume": a=a with{Identity=new(0,1,2)};b=a;break;
            case "invalid_attributes": a=a with{Attributes=unchecked((FileAttributes)uint.MaxValue)};b=a;break;
            case "empty_attributes": a=a with{Attributes=0};b=a;break;
            case "invalid_boolean": a=a with{DeletePending=2};b=a;break;
            case "invalid_directory_boolean": a=a with{Directory=2};b=a;break;
            case "invalid_time": a=a with{LastWriteTime=long.MaxValue};b=a;break;
            case "missing_time": a=a with{LastWriteTime=0};b=a;break;
            case "invalid_allocation": a=a with{AllocationSize=-1};b=a;break;
            case "missing_first": return observation with{Before=null};
            case "missing_second": return observation with{After=null};
            case "missing_path": return observation with{BeforePath=null};
            case "different_path": return observation with{BeforePath=path+"other"};
            case "outside_path": return observation with{BeforePath=@"C:\foreign-secret\out.mp4",AfterPath=@"C:\foreign-secret\out.mp4"};
            case "prefix_path": return observation with{BeforePath=path.Replace("Videos","Videos-old")};
            case "unc": return observation with{BeforePath=@"\\server\share\out.mp4"};
            case "device": return observation with{BeforePath=@"\Device\HarddiskVolume1\out.mp4"};
            case "guid": return observation with{BeforePath=@"\\?\Volume{secret}\out.mp4"};
            case "alias": return observation with{BeforePath=Path.GetDirectoryName(path)+@"\..\Videos\out.mp4"};
            case "changed_path": return observation with{AfterPath=path+"different"};
            case "native_failure": return new(FileObservationOutcome.Unavailable);
            case "missing_file": return new(FileObservationOutcome.Missing);
            case "inaccessible": return new(FileObservationOutcome.Inaccessible);
            case "buffer_failure": return new(FileObservationOutcome.ResolvedPathUnavailable);
            default: throw new ArgumentException(kind);
        }
        return observation with{Before=a,After=b};
    }
    public static TheoryData<string,string> Cases => new()
    {
        {"hard_link","hard_linked_output"},{"link_change","hard_linked_output"},{"delete_pending","file_identity_changed"},{"delete_change","file_identity_changed"},
        {"identity","file_identity_changed"},{"identity_low","file_identity_changed"},{"volume","file_identity_changed"},{"attributes","file_identity_changed"},{"length","file_identity_changed"},{"allocation","file_identity_changed"},{"write_time","file_identity_changed"},
        {"reparse","reparse_point"},{"directory","output_not_file"},{"late_reparse","metadata_changed"},{"late_directory","metadata_changed"},{"negative_length","metadata_changed"},
        {"zero_links","file_identity_unavailable"},{"missing_identity","file_identity_unavailable"},{"zero_identity","file_identity_unavailable"},{"zero_volume","file_identity_unavailable"},
        {"invalid_attributes","file_identity_unavailable"},{"empty_attributes","file_identity_unavailable"},{"invalid_boolean","file_identity_unavailable"},{"invalid_directory_boolean","file_identity_unavailable"},
        {"invalid_time","file_identity_unavailable"},{"missing_time","file_identity_unavailable"},{"invalid_allocation","file_identity_unavailable"},{"missing_first","file_identity_unavailable"},{"missing_second","file_identity_unavailable"},
        {"missing_path","resolved_path_mismatch"},{"different_path","resolved_path_mismatch"},{"outside_path","resolved_path_mismatch"},{"prefix_path","resolved_path_mismatch"},
        {"unc","resolved_path_mismatch"},{"device","resolved_path_mismatch"},{"guid","resolved_path_mismatch"},{"alias","resolved_path_mismatch"},{"changed_path","resolved_path_mismatch"},
        {"native_failure","file_identity_unavailable"},{"missing_file","file_missing"},{"inaccessible","file_inaccessible"},{"buffer_failure","resolved_path_mismatch"}
    };
    [Theory,MemberData(nameof(Cases))]
    public void UntrustedObservationsHaveStableReasonsAndUnknownBytes(string kind,string reason)
    {
        using var f=RetentionPlannedOutputPathTests.Fixture();f.Seed();f.Files.Observation=p=>Alter(kind,p);var before=f.Snapshot();var page=f.Page();var item=Assert.Single(page.Items);
        Assert.Equal(reason,item.ReasonCode);Assert.Equal("protected",item.Evaluation);Assert.True(item.AgeCandidate);Assert.Null(item.ObservedFileBytes);
        Assert.Equal(1,page.UnknownCandidateFileCount);Assert.Equal(0,page.ObservedCandidateBytes);Assert.Equal(before,f.Snapshot());Assert.Single(f.Files.Observations);Assert.Empty(f.Files.Lengths);
    }
    [Theory]
    [InlineData(12L,"age_rule_match")][InlineData(13L,"file_length_changed")]
    public void ReliableStableLengthUsesOneObservationAndNeverPathLength(long length,string reason)
    {
        using var f=RetentionPlannedOutputPathTests.Fixture();f.Seed();f.Files.Observation=p=>Good(p,length) with{BeforePath=p.ToUpperInvariant(),AfterPath=p.ToUpperInvariant()};
        var page=f.Page();Assert.Equal(reason,Assert.Single(page.Items).ReasonCode);Assert.Equal(length,page.ObservedCandidateBytes);Assert.Equal(1,page.ObservedCandidateFileCount);Assert.Equal(0,page.UnknownCandidateFileCount);
        Assert.Single(f.Files.Observations);Assert.Empty(f.Files.Lengths);Assert.All(f.Files.Attributes,p=>Assert.False(p.EndsWith(".mp4"))); // only ancestors use path metadata
    }
    [Theory]
    [InlineData("missing_provider")][InlineData("null_result")][InlineData("invalid_enum")][InlineData("invalid_payload")]
    public void ObservationContractDamageFailsWholePageWithoutLeakingMaterials(string fault)
    {
        using var f=RetentionPlannedOutputPathTests.Fixture();f.Seed();f.Files.Observation=_=>fault switch{"null_result"=>null,"invalid_enum"=>new((FileObservationOutcome)999),_=>new(FileObservationOutcome.Missing,Stable())};
        var service=new RetentionPreviewQueryService(f.Query.GetOrdinaryEvidencePage,f.Db.Root,f.Files,fault=="missing_provider"?null:f.Files,()=>At.AddDays(5));
        var before=f.Snapshot();Assert.Throws<RetentionPreviewUnavailableException>(()=>service.GetPage(Sid,Session,20,f.Before));Assert.Equal(before,f.Snapshot());if(fault=="missing_provider")f.NoProbes();
    }
    [Theory]
    [InlineData("status")][InlineData("completion")][InlineData("cutoff")][InlineData("lexical")][InlineData("nested")][InlineData("shared")][InlineData("planned")]
    public async Task AllPriorProtectionsPrecedeEveryLeafObservation(string kind)
    {
        using var f=RetentionPlannedOutputPathTests.Fixture();var path=kind=="planned"?await RetentionPlannedOutputPathTests.Purpose(f,"future"):f.PathFor();
        if(kind=="completion")f.Db.Ordinary.Register(f.Db.Recording("target",completed:true) with{OutputPath=path,CompletedAtUtc=null});
        else if(kind=="cutoff")f.Db.Ordinary.Register(f.Db.Recording("target",completed:true) with{OutputPath=path,CompletedAtUtc=f.Before});
        else f.Seed(path:kind=="lexical"?Path.Combine(f.Db.Root,"Outside","out.mp4"):path,status:kind=="status"?"failed":"completed",at:kind=="cutoff"?f.Before:At,role:kind=="nested"?"outer":null);
        if(kind=="shared")f.Seed("other",path,"failed",f.Before.AddDays(1));
        f.Files.Observation=_=>throw new InvalidOperationException("SECRET_UNEXPECTED_PROBE");Assert.Null(Assert.Single(f.Page(1).Items).ObservedFileBytes);f.NoProbes();
    }
    [Fact]
    public void PriorityForSimultaneousHardLinkDeleteAndPathMismatchIsFixed()
    {
        using var f=RetentionPlannedOutputPathTests.Fixture();f.Seed();f.Files.Observation=p=>Good(p) with{Before=Stable() with{Links=2,DeletePending=1},After=Stable() with{Links=2,DeletePending=1},BeforePath=@"C:\secret\out.mp4"};
        Assert.Equal("hard_linked_output",Assert.Single(f.Page().Items).ReasonCode);
    }
}
