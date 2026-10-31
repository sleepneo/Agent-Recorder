using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Xunit;
using static AgentRecorder.Tests.RuntimeOutputTestEngine;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed partial class BackendCreationFailureTests
{
    private const string Secret = @"private-injected-C:\users\secret\token.txt";
    private sealed class Audit(string root) : AuditLogger(Path.Combine(root,"audit.jsonl"))
    {
        internal readonly ConcurrentQueue<(string Name,string Json)> Events=new();
        public override void Log(string name,object data)=>Events.Enqueue((name,JsonSerializer.Serialize(data)));
    }
    private sealed class Ui : ITrayContext
    {
        internal Action<ConfirmationDecision>? Confirm;
        internal readonly TaskCompletionSource Requested=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ConcurrentQueue<string> Errors=new();
        internal int Requests,Recording,Idle;
        public string HostMode=>"tray"; public bool SupportsRegionSelectionUi=>true;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> callback){Requests++;Confirm=callback;Requested.TrySetResult();}
        public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> c)=>throw new InvalidOperationException();
        public void SetRecording(RecordingUiPresentation p)=>Interlocked.Increment(ref Recording);
        public void SetIdle(RecordingUiPresentation p)=>Interlocked.Increment(ref Idle);
        public void SetAllIdle() { }
        public void ShowError(string text)=>Errors.Enqueue(text);
    }
    private sealed class Capacity : IStorageCapacityProvider
    {
        public Task<IReadOnlyList<StorageVolumeSample>> QueryAsync(IReadOnlyList<string> paths,CancellationToken token)=>
            Task.FromResult<IReadOnlyList<StorageVolumeSample>>(paths.Select(p=>new StorageVolumeSample(p,"fake",100L*1024*1024*1024)).ToArray());
    }
    private sealed class Bundle : IRecordingBundleGenerator
    {
        internal int Calls;
        public Task<RecordingBundleGenerationResult> GenerateAsync(RecordingBundleRequest r,CancellationToken c=default){Calls++;throw new InvalidOperationException("Must not generate");}
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root=Path.Combine(Path.GetTempPath(),"backend-creation-"+Guid.NewGuid().ToString("N"));
        internal readonly Ui Ui=new();internal readonly Bundle Bundle=new();
        internal readonly Audit Audit;internal readonly RecordingPerformanceTracer Trace;
        internal readonly SqliteOperationalStore Store;internal readonly SqliteOrdinaryRecordingHistoryStore History;
        internal readonly RecordingEngine Engine;
        private readonly IStorageCapacityProvider capacity=RecordingPreflightChecker.StorageCapacityProvider;
        private readonly string? mode=Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
        private readonly string? data=DataDirResolver.HasOverride?DataDirResolver.Resolve():null;
        internal Fixture()
        {
            Directory.CreateDirectory(Root);DataDirResolver.SetOverride(Root);Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE","1");
            RecordingPreflightChecker.StorageCapacityProvider=new Capacity();
            SystemQuery.SetDisplayProvider(()=>new(){new("display_1","Synthetic",true,new(0,0,1920,1080),1.0)});
            Audit=new(Root);Trace=new(Root);Store=new(Path.Combine(Root,"history.db"));Store.Initialize();History=new(Store);
            Engine=new(Audit,Trace,bundleGenerator:Bundle){CountdownSteps=0,DisableDeadlineWatchdogForTests=true};Engine.SetTray(Ui);Engine.ConfigureOrdinaryRecordingHistory(History);
        }
        internal JsonObject Request()=>new(){["source"]=new JsonObject{["type"]="display",["display_id"]="display_1"},["duration_seconds"]=5,["countdown_seconds"]=0,["output"]=new JsonObject{["directory"]=Root,["filename"]="never-created.mp4"}};
        internal Recording Create(){Engine.CreateRecording(Request(),"task371",Ui);return Assert.Single(Engine._recs.Values);}
        public void Dispose()
        {
            Engine.Dispose();History.Dispose();Trace.Dispose();SystemQuery.SetDisplayProvider(null);RecordingPreflightChecker.StorageCapacityProvider=capacity;
            Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE",mode);if(data is null)DataDirResolver.ClearOverride();else DataDirResolver.SetOverride(data);
        }
    }
    private static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static void AssertFailed(Fixture f,Recording rec)
    {
        Assert.Equal(RecState.failed,rec.State);Assert.True(rec.IsFinalized);Assert.NotNull(rec.CompletedAtUtc);
        Assert.Equal("backend_creation_failed",rec.Error);Assert.Equal("backend_creation_failed",rec.StopReason);
        Assert.Equal("not_applicable",rec.BundleSnapshot.Status);Assert.Null(rec.Backend);Assert.False(rec.BackendStartAttempted);
        Assert.Equal(default,rec.StartedAtUtc);Assert.Equal(0,Json(f.Engine.GetStatus(rec.Id)).GetProperty("elapsed_seconds").GetDouble());
        Assert.Equal(CaptureAuthorizationProofState.Revoked,rec.AuthorizationProof!.State);
        Assert.Equal("approved",Json(f.Engine.GetConfirmation(rec.ConfirmationId!)).GetProperty("status").GetString());
        Assert.Equal(0,f.Ui.Recording);Assert.Equal(1,f.Ui.Idle);Assert.Single(f.Ui.Errors);Assert.False(File.Exists(rec.OutputPath));Assert.Equal(0,f.Bundle.Calls);
        var failed=Assert.Single(f.Audit.Events.Where(e=>e.Name=="recording.failed"));
        Assert.Equal("backend_selection",JsonDocument.Parse(failed.Json).RootElement.GetProperty("stage").GetString());
        Assert.DoesNotContain(Secret,Json(f.Engine.GetStatus(rec.Id)).GetRawText());Assert.DoesNotContain(Secret,string.Join("|",rec.Warnings.Concat(f.Ui.Errors)));
        Assert.DoesNotContain(Secret,string.Join("|",f.Audit.Events.Select(e=>e.Json)));
        var durable=f.History.Read(rec.Id)!;Assert.Equal("failed",durable.Status);Assert.Equal("backend_creation_failed",durable.ReasonCode);Assert.NotNull(durable.CompletedAtUtc);
        Assert.DoesNotContain(Secret,Json(durable).GetRawText());
        var query=new UnifiedHistoryQueryService(f.Store,f.History,true);Assert.Equal("failed",Assert.Single(query.GetPage(UnifiedHistoryTestDatabase.Sid,UnifiedHistoryTestDatabase.Session,20,new("ordinary")).Items).Status);
        Assert.False(rec.OrdinaryBackendCreation!.ObserveRetired(()=>false));
        var other=Path.Combine(f.Root,"unrelated.mp4");Claim(f.Engine,new Recording(false){IsOrdinaryLocalRequest=true,OutputPath=other},other);
        Assert.True(Read(f.Engine,rec.OutputPath)!.Paths.Single().InUse);
        Assert.Equal("worker_retirement_unknown",f.Engine.TryReserveIdleHostShutdown(()=>null));Assert.False(f.Engine.RetentionHostStopping);
        f.Trace.Flush();var trace=File.ReadAllText(Path.Combine(f.Root,"perf/recording-traces.jsonl"));Assert.DoesNotContain(Secret,trace);
        var events=trace.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(line=>JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        var startFailed=Assert.Single(events.Where(e=>e.GetProperty("event").GetString()=="capture.backend_start_failed"));
        Assert.Equal("backend_creation_failed",startFailed.GetProperty("data").GetProperty("error_code").GetString());
        Assert.Single(events.Where(e=>e.GetProperty("event").GetString()=="recording.terminal"));
        Assert.DoesNotContain(events,e=>e.GetProperty("event").GetString() is "capture.first_frame_observed" or "capture.start_requested");
    }
    [Theory]
    [InlineData("legacy_throw")][InlineData("legacy_null")][InlineData("selection_throw")][InlineData("selection_null")]
    public void BackendCreationFailureFormalApprovalConvergesWithoutEscapingOrLeaking(string kind)
    {
        using var f=new Fixture();var calls=0;
        if(kind.StartsWith("legacy"))f.Engine.BackendFactory=_=>{calls++;if(kind.EndsWith("throw"))throw new IOException(Secret);return(null!,"fake");};
        else f.Engine.BackendSelectionFactoryForTests=_=>{calls++;if(kind.EndsWith("throw"))throw new IOException(Secret);return null!;};
        var rec=f.Create();Assert.Equal(RecState.pending_confirmation,rec.State);
        Assert.Null(Record.Exception(()=>f.Ui.Confirm!(ConfirmationDecision.Approve())));
        AssertFailed(f,rec);f.Ui.Confirm!(ConfirmationDecision.Approve());f.Ui.Confirm(ConfirmationDecision.Reject());
        Assert.Equal(1,calls);Assert.Equal(1,f.Ui.Requests);Assert.Single(f.Ui.Errors);
    }
}
