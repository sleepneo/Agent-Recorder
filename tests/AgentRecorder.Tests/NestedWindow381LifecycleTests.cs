using System.Net;
using System.Text.Json.Nodes;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;
public sealed partial class OrdinaryWgcBundleTests
{
    private static void NestedWindow381Providers(Fixture f)
    {
        SystemQuery.SetWindowProvider((_, _) => new() {
            new("window_42", "Outer synthetic", "owned.exe", 7, false, false, new(0,0,64,64)),
            new("window_43", "Inner synthetic", "owned.exe", 7, false, false, new(0,0,64,64)) });
        f.Engine.OrdinaryWindowMetadataForTests = (hwnd, pid) => OrdinaryWindowTargetKeyTests.Facts(hwnd, pid);
    }
    private static async Task<Recording> NestedWindow381Accept(Fixture f, JsonObject q)
    {
        var response = await NestedWindow381Post(f, q); Assert.True(response.Status == HttpStatusCode.OK, response.Body);
        return f.Engine._recs[JsonNode.Parse(response.Body)!["data"]!["recording_id"]!.GetValue<string>()];
    }
    private static async Task NestedWindow381Approve(Fixture f, Recording rec)
    {
        f.Tray.Confirm!(ConfirmationDecision.Approve());
        await Until(() => rec.State == RecState.recording || rec.IsFinalized);
        Assert.True(rec.State == RecState.recording, $"{rec.State}: {rec.Error} / {rec.StopReason}");
    }
    private static async Task NestedWindow381Drain(Fixture f)
    {
        foreach (var rec in f.Engine._recs.Values.Where(r => !r.IsFinalized).ToArray())
            f.Engine.Stop(rec.Id, "user_requested");
        f.Generator.Release.TrySetResult();
        await Until(() => f.Engine._recs.Values.All(r => r.IsFinalized) && BundleUseCount(f) == 0);
    }
    [Theory]
    [InlineData("natural")]
    [InlineData("stop")]
    [InlineData("closed")]
    [InlineData("parent-stop")]
    [InlineData("parent-natural")]
    [InlineData("parent-closed")]
    public async Task NestedWindow381TwoRealBackendsOwnIndependentProofsKeysHistoryAndPublication(string ending)
    {
        using var f = RuntimeFixture(); await f.Prepare(); NestedWindow381Providers(f); f.StartApi();
        try
        {
        var outer = await NestedWindow381Accept(f, NestedWindow381Request(f, "outer"));
        Assert.Null(outer.AuthorizationProof); await NestedWindow381Approve(f, outer);
        var outerProcess = f.Process!; var outerBackend = f.Wgc!;
        var inner = await NestedWindow381Accept(f, NestedWindow381Request(f, "inner", "window_43", outer));
        var callback = f.Tray.Confirm!; Assert.Null(inner.AuthorizationProof); await NestedWindow381Approve(f, inner);
        var innerProcess = f.Process!; var innerBackend = f.Wgc!;
        callback(ConfirmationDecision.Approve()); Assert.Equal(2, f.FactoryCalls);
        Assert.NotSame(outerBackend, innerBackend); Assert.NotSame(outerProcess, innerProcess);
        Assert.StartsWith("wgc-c-", outerProcess.Options.RecordingId); Assert.NotEqual(outerProcess.Options.RecordingId, innerProcess.Options.RecordingId);
        Assert.Equal(WgcContinuousTargetKind.Window, outerProcess.Options.TargetKind); Assert.Equal(WgcContinuousTargetKind.Window, innerProcess.Options.TargetKind);
        Assert.NotEqual(System.IO.Path.GetDirectoryName(outerProcess.Options.OutputPath), System.IO.Path.GetDirectoryName(innerProcess.Options.OutputPath));
        Assert.NotEqual(outerProcess.Options.BeginSignalPath, innerProcess.Options.BeginSignalPath);
        Assert.NotEqual(outerProcess.Options.StopSignalPath, innerProcess.Options.StopSignalPath);
        Assert.NotEqual(outerProcess.Options.BeginToken, innerProcess.Options.BeginToken);
        Assert.NotSame(outer.Config, inner.Config); Assert.NotSame(outer.AuthorizationProof, inner.AuthorizationProof);
        Assert.Equal(outer.Id, outer.AuthorizationProof!.RecordingId); Assert.Equal(inner.Id, inner.AuthorizationProof!.RecordingId);
        Assert.NotEqual(outer.AuthorizationProof.ScopeDigest, inner.AuthorizationProof.ScopeDigest);
        Assert.Equal((nint)42, outer.ApprovedCapturePlan!.WindowHandle); Assert.Equal((nint)43, inner.ApprovedCapturePlan!.WindowHandle);
        Assert.NotNull(outer.OrdinaryTargetKey); Assert.NotNull(inner.OrdinaryTargetKey); Assert.NotEqual(outer.OrdinaryTargetKey, inner.OrdinaryTargetKey);
        Assert.NotEqual(outer.OutputPath, inner.OutputPath); Assert.Contains(outer.Id, outer.OutputPath); Assert.Contains(inner.Id, inner.OutputPath);
        f.Generator.Release.TrySetResult();
        if (ending is "parent-stop" or "parent-natural" or "parent-closed")
        {
            if (ending == "parent-closed") { outerProcess.FailMedia = true; outerProcess.FailureCode = "window_closed"; outerProcess.FailureReason = "window_closed"; }
            if (ending == "parent-stop") f.Engine.Stop(outer.Id, "user_requested"); else outerProcess.Finish.TrySetResult();
            await Until(() => outer.IsFinalized);
            Assert.Equal(RecState.recording, inner.State); Assert.False(innerProcess.HasExited);
            innerProcess.Finish.TrySetResult();
        }
        else
        {
            if (ending == "closed") { innerProcess.FailMedia = true; innerProcess.FailureCode = "window_closed"; innerProcess.FailureReason = "window_closed"; }
            if (ending == "stop") f.Engine.Stop(inner.Id, "user_requested"); else innerProcess.Finish.TrySetResult();
            await Until(() => inner.IsFinalized); await innerBackend.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RecState.recording, outer.State); Assert.False(outerProcess.HasExited);
            Assert.True(RuntimeOutputTestEngine.Read(f.Engine, outer.OutputPath)!.Paths.Single().InUse);
            outerProcess.Finish.TrySetResult();
        }
        await Until(() => outer.IsFinalized && inner.IsFinalized);
        await outerBackend.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5)); await innerBackend.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ending == "parent-closed" ? RecState.failed : RecState.completed, outer.State);
        Assert.Equal(ending != "parent-closed", System.IO.File.Exists(outer.OutputPath));
        if (ending == "parent-closed") Assert.Equal("window_closed", outer.StopReason);
        if (ending == "closed") { Assert.Equal("window_closed", inner.StopReason); Assert.Equal(RecState.failed, inner.State); Assert.False(System.IO.File.Exists(inner.OutputPath)); }
        else
        {
            Assert.Equal(RecState.completed, inner.State); await Until(() => inner.BundleSnapshot.Status == "not_applicable");
            Assert.NotEqual(outer.OutputPath,inner.OutputPath);Assert.Null(outer.BundleSnapshot.Path);Assert.Null(inner.BundleSnapshot.Path);
        }
        if (ending != "parent-closed") await Until(() => outer.BundleSnapshot.Status == "not_applicable");
        Assert.Equal(outer.OrdinaryTargetKey, f.Writer.Read(outer.Id)!.TargetKey);
        Assert.Equal(inner.OrdinaryTargetKey, f.Writer.Read(inner.Id)!.TargetKey);
        Assert.Equal("outer", f.Writer.Read(outer.Id)!.NestedRole); Assert.Equal(outer.Id, f.Writer.Read(inner.Id)!.ParentRecordingId);
        SaveSeriesBundleEvidence("window381-two-" + ending, new { ending, outer = outer.State.ToString(), inner = inner.State.ToString(), outerKey = outer.OrdinaryTargetKey,
            innerKey = inner.OrdinaryTargetKey, factories = f.FactoryCalls, outerBundle = outer.BundleSnapshot.Status, innerBundle = inner.BundleSnapshot.Status, innerReason = inner.StopReason, innerError = inner.Error });
        }
        finally { await NestedWindow381Drain(f); }
    }

    [Theory]
    [InlineData("early")][InlineData("third")][InlineData("same")][InlineData("session")][InlineData("parent")]
    [InlineData("system")][InlineData("mic")][InlineData("mixed")][InlineData("empty")][InlineData("path")][InlineData("quick")]
    public async Task NestedWindow381InvalidCombinationsDoNotCreateConfirmationOrWorker(string fault)
    {
        using var f = RuntimeFixture(); await f.Prepare(); NestedWindow381Providers(f); f.StartApi();
        try
        {
        var outer = await NestedWindow381Accept(f, NestedWindow381Request(f, "outer"));
        if (fault != "early") await NestedWindow381Approve(f, outer);
        var request = NestedWindow381Request(f, "inner", fault == "same" ? "window_42" : "window_43", outer);
        if (fault == "third")
        {
            var inner = await NestedWindow381Accept(f, request); await NestedWindow381Approve(f, inner);
            request = NestedWindow381Request(f, "inner", "window_43", outer);
        }
        if (fault == "session") request["nested"]!["session_id"] = "wrong";
        if (fault == "parent") request["nested"]!["parent_recording_id"] = "missing";
        if (fault is "system" or "mic") request["audio"] = JsonNode.Parse(fault == "system" ? """{"system_audio":{"enabled":true}}""" : """{"microphone":{"enabled":true}}""");
        if (fault == "mixed") { request["source"] = f.Request("display")["source"]!.DeepClone(); request.Remove("required_capture_semantics"); }
        if (fault == "empty") request["nested"] = new JsonObject();
        if (fault == "path") request["output"]!["filename"] = System.IO.Path.GetFileName(outer.OutputPath);
        if (fault == "quick") { request["target"] = new JsonObject { ["type"] = "active_window" }; request.Remove("source"); request.Remove("required_capture_semantics"); request["duration_seconds"] = 1; request.Remove("stop_condition"); }
        var count = f.Engine._recs.Count; var factories = f.FactoryCalls; var confirmation = f.Tray.Confirm;
        var response = await NestedWindow381Post(f, request, fault == "quick");
        Assert.False(response.Status == HttpStatusCode.OK, response.Body);
        if (fault == "same") Assert.Contains("WINDOW_TARGET_IN_USE", response.Body);
        if (fault == "path") Assert.Contains("OUTPUT_PATH_IN_USE", response.Body);
        Assert.Equal(count, f.Engine._recs.Count); Assert.Equal(factories, f.FactoryCalls); Assert.Same(confirmation, f.Tray.Confirm);
        // Finish actual synthetic workers, without retries or touching the other run's proof.
        foreach (var rec in f.Engine._recs.Values.Where(r => r.State == RecState.recording).ToArray()) f.Engine.Stop(rec.Id, "user_requested");
        f.Generator.Release.TrySetResult();
        }
        finally { await NestedWindow381Drain(f); }
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task NestedWindow381DeclinedOrExpiredInnerNeverStarts(bool expired)
    {
        using var f = RuntimeFixture(); await f.Prepare(); NestedWindow381Providers(f); f.StartApi();
        try
        {
        var outer = await NestedWindow381Accept(f, NestedWindow381Request(f, "outer")); await NestedWindow381Approve(f, outer);
        var inner = await NestedWindow381Accept(f, NestedWindow381Request(f, "inner", "window_43", outer)); var callback = f.Tray.Confirm!;
        if (expired) f.Engine.TriggerConfirmationExpiryForTests(inner.ConfirmationId!); else callback(ConfirmationDecision.Reject());
        callback(ConfirmationDecision.Approve()); Assert.Null(inner.AuthorizationProof); Assert.Equal(1, f.FactoryCalls); Assert.Equal(RecState.recording, outer.State);
        f.Engine.Stop(outer.Id, "user_requested"); f.Generator.Release.TrySetResult();
        }
        finally { await NestedWindow381Drain(f); }
    }
    [Theory]
    [InlineData("closed")][InlineData("minimized")][InlineData("pid")][InlineData("size")]
    public async Task NestedWindow381InnerTargetChangeBeforeApprovalDoesNotStartOrStopOuter(string fault)
    {
        using var f = RuntimeFixture(); await f.Prepare(); NestedWindow381Providers(f); f.StartApi();
        try
        {
            var outer = await NestedWindow381Accept(f, NestedWindow381Request(f, "outer")); await NestedWindow381Approve(f, outer);
            var process = f.Process!;
            var inner = await NestedWindow381Accept(f, NestedWindow381Request(f, "inner", "window_43", outer));
            SystemQuery.SetWindowProvider((_, _) => {
                var list = new System.Collections.Generic.List<SystemQuery.WindowInfo> {
                    new("window_42", "Outer", "owned.exe", 7, false, false, new(0,0,64,64)) };
                if (fault != "closed") list.Add(new("window_43", "Inner", "owned.exe", fault == "pid" ? 8 : 7,
                    false, fault == "minimized", new(0,0,fault == "size" ? 32 : 64,64)));
                return list;
            });
            f.Tray.Confirm!(ConfirmationDecision.Approve()); await Until(() => inner.IsFinalized);
            Assert.Equal(RecState.failed, inner.State); Assert.Equal(1, f.FactoryCalls);
            Assert.Equal(RecState.recording, outer.State); Assert.False(process.HasExited);
        }
        finally { await NestedWindow381Drain(f); }
    }
    [Fact]
    public async Task NestedWindow381ParentChangeDuringBuildIsRecheckedAtAtomicAdmission()
    {
        using var f = RuntimeFixture(); await f.Prepare(); NestedWindow381Providers(f); f.StartApi();
        using var release = new System.Threading.ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<(HttpStatusCode Status,string Body)>? request = null;
        try
        {
            var outer = await NestedWindow381Accept(f, NestedWindow381Request(f, "outer")); await NestedWindow381Approve(f, outer);
            f.Engine.CapturePlanFactoryForTests = cfg => {
                if (cfg.WindowHandle == (nint)43) { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("owned admission gate"); }
                return CaptureBackendSelector.BuildPlan(cfg, new Probe());
            };
            request = NestedWindow381Post(f, NestedWindow381Request(f, "inner", "window_43", outer));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); f.Engine.Stop(outer.Id, "user_requested");
            release.Set(); var result = await request;
            Assert.Equal(HttpStatusCode.Conflict, result.Status); Assert.Contains("PARENT_NOT_RECORDING", result.Body);
            Assert.Single(f.Engine._recs); Assert.Equal(1, f.FactoryCalls);
        }
        finally { release.Set(); if(request is not null) await request; await NestedWindow381Drain(f); }
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task NestedWindow381CapabilitiesExposeOnlyRestrictedRawContract(bool available)
    {
        using var f = RuntimeFixture(); NestedWindow381Providers(f); f.StartApi();
        f.Api!.WindowSurfaceAvailabilityForTests = () => new(available, available ? "ready" : "unavailable", "synthetic", 0);
        using var client = f.Client(); using var response = await client.GetAsync("/api/v1/capabilities");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["data"]!;
        var window = body["recording"]!["window_surface"]!;
        Assert.True(window["nested_supported"]!.GetValue<bool>());
        Assert.Equal(available, window["nested"]!["available"]!.GetValue<bool>());
        Assert.Equal("none", window["nested"]!["audio"]![0]!.GetValue<string>());
        Assert.Equal(2, window["nested"]!["max_concurrent"]!.GetValue<int>());
        Assert.False(window["nested"]!["quick_supported"]!.GetValue<bool>());
        Assert.False(body["interaction"]!["profile_region"]!["nested_supported"]!.GetValue<bool>());
        var recipe = body["interaction"]!["quick_recipes"]!.AsArray().Single(n => n?["target_type"]?.GetValue<string>() == "active_window");
        Assert.False(recipe!["nested_supported"]!.GetValue<bool>());
        // Keep a full capability snapshot for checking independent quick/Profile flags.
        SaveSeriesBundleEvidence("window381-cap-" + available, body);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task NestedWindow381ReverseMixedSourcesAreRejected(bool nonNestedWindowParent)
    {
        using var f = RuntimeFixture(); await f.Prepare(); NestedWindow381Providers(f); f.StartApi();
        try
        {
            var parentRequest = nonNestedWindowParent ? f.Request("window") : f.Request("display");
            if (!nonNestedWindowParent) parentRequest["nested"] = new JsonObject { ["role"] = "outer", ["session_id"] = "window381" };
            var parent = await NestedWindow381Accept(f, parentRequest); await NestedWindow381Approve(f, parent);
            var child = NestedWindow381Request(f, nonNestedWindowParent ? "outer" : "inner", "window_43", parent);
            if (nonNestedWindowParent) { child["source"] = f.Request("display")["source"]!.DeepClone(); child.Remove("required_capture_semantics"); }
            var result = await NestedWindow381Post(f, child);
            Assert.Equal(HttpStatusCode.BadRequest, result.Status); Assert.Equal(1, f.FactoryCalls); Assert.Single(f.Engine._recs);
            Assert.Equal(RecState.recording, parent.State);
        }
        finally { await NestedWindow381Drain(f); }
    }

}
