using AgentRecorder.Core;
using AgentRecorder.Persistence;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

public sealed class RetentionRuntimeOutputTests
{
    internal sealed class Provider : IOrdinaryRuntimeOutputUseProvider
    {
        internal readonly List<string[]> Calls = new();
        internal Func<IReadOnlyList<string>, OrdinaryRuntimeOutputSnapshot?> Read =
            paths => new(paths.Select(path => new OrdinaryRuntimeOutputUse(path, true)));
        public OrdinaryRuntimeOutputSnapshot? ReadSnapshot(IReadOnlyList<string> paths)
        { Calls.Add(paths.ToArray()); return Read(paths); }
    }

    private static RetentionPreviewQueryService Service(RetentionPreviewQueryTests.Fixture f,
        IOrdinaryRuntimeOutputUseProvider? provider,
        Func<string,string,int,DateTimeOffset,string?,OrdinaryRetentionEvidencePage>? read = null)
        => new(read ?? f.Db.Query.GetOrdinaryEvidencePage, f.Db.Root, f.Files, f.Files, () => At.AddDays(2), provider);

    private static RetentionPreviewPage Page(RetentionPreviewQueryService service, RetentionPreviewQueryTests.Fixture f,
        int limit = 20, string? cursor = null) => service.GetPage(Sid, Session, limit, f.Before, cursor);

    internal static void NoProbes(RetentionPreviewQueryTests.Fixture f)
    { Assert.Empty(f.Files.Attributes); Assert.Empty(f.Files.Observations); Assert.Empty(f.Files.ProbeAttributes); Assert.Empty(f.Files.Lengths); }

    [Fact]
    public void MatchingPathsProtectBeforeAllProbesAndRetainUnknownByteAccounting()
    {
        using var f = new RetentionPreviewQueryTests.Fixture(); var row = f.Seed("old"); var before = f.Db.Snapshot();
        var provider = new Provider { Read = paths => new(paths.Select(p => new OrdinaryRuntimeOutputUse(p.ToUpperInvariant(), true))) };
        var page = Page(Service(f, provider), f); var item = Assert.Single(page.Items);
        Assert.Equal("protected", item.Evaluation); Assert.Equal("runtime_output_in_use", item.ReasonCode);
        Assert.True(item.AgeCandidate); Assert.Equal(row.OutputPath, item.RecordedPath); Assert.Equal(12, item.RecordedBytes);
        Assert.Null(item.ObservedFileBytes); Assert.Equal(1, page.AgeCandidateCount); Assert.Equal(1, page.UnknownCandidateFileCount);
        Assert.Equal(0, page.ObservedCandidateFileCount); Assert.Equal(0, page.ObservedCandidateBytes);
        Assert.Equal(new[] { row.OutputPath }, Assert.Single(provider.Calls)); NoProbes(f); Assert.Equal(before, f.Db.Snapshot());
    }

    [Fact]
    public void UnmatchedPathsKeepExistingResultsAndMixedPageCounts()
    {
        using var f = new RetentionPreviewQueryTests.Fixture(); f.Seed("a"); f.Seed("b");
        var prior = f.Page(); f.Files.Attributes.Clear(); f.Files.Observations.Clear(); f.Files.Lengths.Clear(); f.Files.ProbeAttributes.Clear();
        var none = new Provider { Read = paths => TestRuntimeOutputUse.None.ReadSnapshot(paths) };
        var unchanged = Page(Service(f, none), f); Assert.Equal(prior.Items, unchanged.Items);
        Assert.Equal(prior.ObservedCandidateBytes, unchanged.ObservedCandidateBytes); Assert.Single(none.Calls);
        var mixed = new Provider { Read = paths => new(paths.Select(p => new OrdinaryRuntimeOutputUse(p, p.EndsWith("a.mp4")))) };
        var page = Page(Service(f, mixed), f); Assert.Equal(2, page.AgeCandidateCount); Assert.Equal(1, page.UnknownCandidateFileCount);
        Assert.Equal(1, page.ObservedCandidateFileCount); Assert.Equal(12, page.ObservedCandidateBytes);
        Assert.Equal("runtime_output_in_use", page.Items.Single(i => i.RecordingId == "a").ReasonCode);
        Assert.Equal("age_rule_match", page.Items.Single(i => i.RecordingId == "b").ReasonCode);
    }

    [Theory]
    [InlineData("missing")][InlineData("null")][InlineData("throws")][InlineData("duplicate")]
    [InlineData("short")][InlineData("foreign")][InlineData("extra")][InlineData("null_row")][InlineData("null_path")]
    [InlineData("argument")]
    public void DamagedRuntimeAnswerFailsWholePageBeforeEvenFirstUnmatchedItemProbe(string fault)
    {
        using var f = new RetentionPreviewQueryTests.Fixture(); f.Seed("a"); f.Seed("b");
        var provider = new Provider { Read = paths => fault switch
        {
            "null" => null, "throws" => throw new IOException("SECRET_RUNTIME_OWNER"),
            "argument" => throw new UnifiedHistoryArgumentException(),
            "duplicate" => new([new(paths[0],false),new(paths[0].ToUpperInvariant(),true)]),
            "short" => new([new(paths[0],false)]),
            "foreign" => new([new(paths[0],false),new(@"C:\SECRET_OWNER\foreign.mp4",true)]),
            "extra" => new(paths.Select(p=>new OrdinaryRuntimeOutputUse(p,false)).Append(new(@"C:\foreign.mp4",true))),
            "null_row" => new([new(paths[0],false),null!]),
            "null_path" => new([new(paths[0],false),new(null!,true)]),
            _ => TestRuntimeOutputUse.None.ReadSnapshot(paths)
        } };
        Assert.Throws<RetentionPreviewUnavailableException>(() => Page(Service(f,fault=="missing"?null:provider),f));
        Assert.Equal(fault=="missing"?0:1,provider.Calls.Count); NoProbes(f);
    }

    [Theory]
    [InlineData("empty")][InlineData("not_completed")][InlineData("new")][InlineData("outside")]
    [InlineData("device")][InlineData("superscript")][InlineData("extension")][InlineData("depth")]
    public void NoEligibleScopedPathDoesNotRequireOrQueryRuntimeProvider(string kind)
    {
        using var f = new RetentionPreviewQueryTests.Fixture();
        if(kind!="empty") f.Seed("row",kind=="not_completed"?"preparing":"completed",
            completed:kind=="new"?f.Before:null,path:kind switch
            {
                "outside"=>Path.Combine(f.Db.Root,"outside.mp4"), "device"=>Path.Combine(f.Videos,"COM1.mp4"),
                "superscript"=>Path.Combine(f.Videos,"LPT\u00B3.mp4"), "extension"=>Path.Combine(f.Videos,"a.png"),
                "depth"=>Path.Combine(f.Videos,string.Join("\\",Enumerable.Repeat("d",33)),"a.mp4"),_=>null
            });
        var provider=new Provider{Read=_=>throw new IOException("Must not be called")};
        var page=Page(Service(f,provider),f); Assert.Empty(provider.Calls); NoProbes(f);
        Assert.Equal(page.Items,Page(Service(f,null),f).Items);
    }

    [Fact]
    public void HundredCandidatePageGetsOneDeduplicatedAnswerAndPaginationStaysLive()
    {
        using var f = new RetentionPreviewQueryTests.Fixture();
        for(var i=0;i<101;i++)f.Seed("row"+i.ToString("D3"));
        var provider=new Provider();var service=Service(f,provider);var first=Page(service,f,100);
        Assert.Equal(100,first.EvaluatedCount);Assert.Equal(100,first.AgeCandidateCount);Assert.Equal(100,first.UnknownCandidateFileCount);
        Assert.NotNull(first.NextCursor);Assert.Equal(100,Assert.Single(provider.Calls).Length);NoProbes(f);
        provider.Read=paths=>TestRuntimeOutputUse.None.ReadSnapshot(paths);
        var next=Page(service,f,100,first.NextCursor);Assert.Single(next.Items);Assert.Equal(2,provider.Calls.Count);
        Assert.Equal("age_rule_match",next.Items[0].ReasonCode);Assert.Null(next.NextCursor);
    }

    [Fact]
    public void SharedCandidateTextIsRequestedOnceWhileOldSharedReasonWins()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();var row=f.Seed("a");f.Seed("b",path:row.OutputPath!.ToUpperInvariant());
        var provider=new Provider();var page=Page(Service(f,provider),f);
        Assert.Single(Assert.Single(provider.Calls));Assert.All(page.Items,i=>Assert.Equal("shared_output_path",i.ReasonCode));NoProbes(f);
    }

    [Theory]
    [InlineData("nested_relationship")][InlineData("nested_parent_referenced")][InlineData("nested_reference_invalid")]
    [InlineData("shared_output_path")][InlineData("planned_output_path")][InlineData("setup_output_path")]
    [InlineData("planned_output_template")][InlineData("pending_output_use_unknown")][InlineData("publication_identity_missing")]
    [InlineData("bundle_evidence_unknown")][InlineData("bundle_generation_in_progress")]
    [InlineData("bundle_artifact_reference")][InlineData("bundle_artifact_state_unknown")]
    public void ExistingDatabaseProtectionReasonsWinOverPositiveRuntimeAnswer(string reason)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");var provider=new Provider();
        OrdinaryRetentionEvidencePage Read(string sid,string session,int limit,DateTimeOffset before,string? cursor)
        {
            var page=f.Db.Query.GetOrdinaryEvidencePage(sid,session,limit,before,cursor);var e=page.Items.Single();
            e=reason switch
            {
                "nested_relationship"=>e with{NestedReference=OrdinaryNestedReferenceState.Relationship},
                "nested_parent_referenced"=>e with{NestedReference=OrdinaryNestedReferenceState.ParentReferenced},
                "nested_reference_invalid"=>e with{NestedReference=OrdinaryNestedReferenceState.Invalid},
                "shared_output_path"=>e with{SharedOutputReference=SharedOutputPathReferenceState.Referenced},
                "planned_output_path"=>e with{PlannedOutputReference=PlannedOutputPathReferenceState.Referenced},
                "setup_output_path"=>e with{PendingOutputUse=PendingOutputUseState.SetupExact},
                "planned_output_template"=>e with{PendingOutputUse=PendingOutputUseState.Template},
                "pending_output_use_unknown"=>e with{PendingOutputUse=PendingOutputUseState.Unknown},
                "publication_identity_missing"=>e with{PublicationAnchor=null,PublicationIdentityReference=null},
                "bundle_evidence_unknown"=>e with{Entry=e.Entry with{Bundle=null}},
                _=>e with{Entry=e.Entry with{Bundle=new(reason=="bundle_generation_in_progress"?"generating":reason=="bundle_artifact_reference"?"ready":"failed",Path.ChangeExtension(e.Entry.OutputPath,"bundle"),reason=="bundle_artifact_state_unknown"?"bundle_generation_failed":null)}}
            };
            return page with{Items=new[]{e}};
        }
        var page=Page(Service(f,provider,Read),f);Assert.Equal(reason,Assert.Single(page.Items).ReasonCode);
        Assert.Single(provider.Calls);Assert.Equal(1,page.UnknownCandidateFileCount);NoProbes(f);
    }

    [Theory]
    [InlineData("anchor")][InlineData("bundle")][InlineData("nested")][InlineData("planned")][InlineData("pending")][InlineData("duplicate")]
    public void BadDatabasePageCannotBeMaskedByRuntimePositive(string damage)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");f.Seed("b");var provider=new Provider();
        OrdinaryRetentionEvidencePage Read(string sid,string session,int limit,DateTimeOffset before,string? cursor)
        {
            var page=f.Db.Query.GetOrdinaryEvidencePage(sid,session,limit,before,cursor);var entries=page.Items.ToArray();var e=entries[1];
            entries[1]=damage switch
            {
                "anchor"=>e with{PublicationAnchor=e.PublicationAnchor! with{Length=999}},
                "bundle"=>e with{Entry=e.Entry with{Bundle=new("ready",null,null)}},
                "nested"=>e with{NestedReference=(OrdinaryNestedReferenceState)999},
                "planned"=>e with{PlannedOutputReference=PlannedOutputPathReferenceState.Indeterminate},
                "pending"=>e with{PendingOutputUse=(PendingOutputUseState)999},_=>entries[0]
            };return page with{Items=entries};
        }
        Assert.Throws<RetentionPreviewUnavailableException>(()=>Page(Service(f,provider,Read),f));
        Assert.Empty(provider.Calls);NoProbes(f);
    }
}
