using AgentRecorder.Core;
using Xunit;

namespace AgentRecorder.Tests;
public sealed partial class OrdinaryWgcBundleTests
{
    [Theory]
    [InlineData("display",false,"raw")][InlineData("region",false,"raw")]
    [InlineData("window",false,"raw")][InlineData("window",true,"raw")]
    [InlineData("region",false,"profile")][InlineData("display",false,"nested")]
    public async Task DefaultVideo400UsesPublishersAndNeverDispatchesAttachments(string source,bool audio,string origin)
    {
        using var f=new Fixture(mediaOnlyEvidence:true);await f.Prepare(audio);
        f.Engine.BundleGenerationDispatcherForTests=_=>throw new Xunit.Sdk.XunitException("Unexpected attachment worker");
        var rec=f.Create(source,audio,origin);await f.Approve(rec);await f.Terminal(rec,false);
        Assert.False(rec.GenerateBundle);Assert.Equal(0,f.Generator.Calls);Assert.Equal(0,rec.BundleGenerationStarted);
        Assert.Equal("not_applicable",f.Writer.Read(rec.Id)!.Bundle!.Status);Assert.Null(rec.OrdinaryBundleOperation);
        Assert.NotNull(rec.LastMeta!.PublicationReceipt);
        await Until(()=>((IOrdinaryRuntimeOutputUseProvider)f.Engine).ReadSnapshot(new[]{rec.OutputPath})?.Paths.Single().InUse==false);
    }
}
