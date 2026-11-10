using System.Net;
using System.Text.Json.Nodes;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;
internal sealed class ForbiddenBundleGenerator400(Action? observed=null) : IRecordingBundleGenerator
{
    public Task<RecordingBundleGenerationResult> GenerateAsync(RecordingBundleRequest request,CancellationToken token=default)
    { observed?.Invoke();throw new Xunit.Sdk.XunitException("New video called a retired Bundle generator"); }
}
[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class VideoOnlyContract400Tests
{
    [Theory]
    [InlineData(0,false,false)][InlineData(-1,false,false)]
    [InlineData(0,true,false)][InlineData(-1,true,false)]
    [InlineData(0,false,true)][InlineData(0,true,true)]
    public async Task DefaultAndFalsePublishOnlyVideoWithoutSchedulingAttachments(int policy,bool quick,bool manual)
    {
        using var f=new MediaOnlyFixture();
        f.Engine.BundleGenerationDispatcherForTests=_=>throw new Xunit.Sdk.XunitException("New recording scheduled an attachment worker");
        var rec=await f.Create(policy,quick);await f.Complete(rec,manual);
        Assert.False(rec.GenerateBundle);Assert.Equal(0,f.GeneratorCalls);Assert.Equal(0,rec.BundleGenerationStarted);
        Assert.Null(rec.OrdinaryBundleOperation);Assert.Equal("not_applicable",rec.BundleSnapshot.Status);
        Assert.Equal("not_applicable",f.Writer.Read(rec.Id)!.Bundle!.Status);
        Assert.NotNull(rec.LastMeta!.PublicationReceipt);Assert.True(File.Exists(rec.OutputPath));
        using(var response=await f.Client.GetAsync("/api/v1/recordings/"+rec.Id+"/output"))
        {
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            using var json=System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data=json.RootElement.GetProperty("data");Assert.Equal("completed",data.GetProperty("status").GetString());
            Assert.Equal(rec.OutputPath,data.GetProperty("output").GetProperty("path").GetString());
        }
        Assert.False(Directory.Exists(Path.ChangeExtension(rec.OutputPath,".bundle")));
        Assert.False(f.Engine.CanAddChapterMarks(rec));
        var error=Assert.Throws<ApiException>(()=>f.Engine.AddMark(rec.Id,"owned"));Assert.Equal("UNSUPPORTED_FEATURE",error.Code);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task DefaultVideoAlsoCompletesOnOwnedCVolume(bool quick)
    {
        var parent=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Temp","AgentRecorder-task400-video-"+Guid.NewGuid().ToString("N"));
        using var f=new MediaOnlyFixture(parent);var rec=await f.Create(0,quick);await f.Complete(rec);
        Assert.NotNull(rec.LastMeta!.PublicationReceipt);Assert.Equal(0,f.GeneratorCalls);
        Assert.Null(f.Writer.Read(rec.Id)!.MediaHash);Assert.Equal("not_applicable",f.Writer.Read(rec.Id)!.Bundle!.Status);
        Assert.False(Directory.Exists(Path.ChangeExtension(rec.OutputPath,".bundle")));Assert.True(File.Exists(rec.OutputPath));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task TrueRejectsBeforeAnyRecordingOrBackendOrFileAction(bool quick)
    {
        using var f=new MediaOnlyFixture();var request=f.Request(1);
        if(quick){request.Remove("source");request["target"]=new JsonObject{["type"]="primary_display"};}
        var response=await f.Post(quick?"/api/v1/recordings/quick":"/api/v1/recordings",request.ToJsonString());
        Assert.Equal(HttpStatusCode.BadRequest,response.Status);
        Assert.Contains("UNSUPPORTED_FEATURE",response.Json.ToString());Assert.Contains("artifacts.bundle",response.Json.ToString());
        Assert.Empty(f.Engine._recs);Assert.Null(f.Tray.Presentation);Assert.Equal(0,f.BackendCalls);Assert.Equal(0,f.GeneratorCalls);
        Assert.Equal(0,f.Devices.Calls);Assert.Equal(0,f.Tray.Regions);
    }
    [Theory][InlineData("{}")][InlineData("{\"artifacts\":{}}")][InlineData("{\"artifacts\":{\"bundle\":false}}")]
    public void CompatibleDefaultsAreFalse(string json)=>Assert.False(ConfigParser.ValidateArtifactRequestJson(json,true));
    [Fact]
    public void AllAuthorizedAndDirectConstructorsDisableNewGeneration()
    {
        Assert.False(new Recording().GenerateBundle);Assert.False(new Recording(true).GenerateBundle);
        Assert.False(new Recording("authorized-plan").GenerateBundle);Assert.False(new Recording("authorized-plan",true).GenerateBundle);
        Assert.Equal("not_applicable",new Recording().BundleSnapshot.Status);
        Assert.Throws<ApiException>(()=>ConfigParser.ValidateArtifactRequestJson("{\"artifacts\":{\"bundle\":false}}",false));
    }
}
