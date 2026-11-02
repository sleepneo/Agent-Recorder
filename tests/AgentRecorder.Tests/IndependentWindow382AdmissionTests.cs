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
    [Theory]
    [InlineData("same")][InlineData("third")][InlineData("display")][InlineData("region")]
    [InlineData("series")][InlineData("system")][InlineData("mic")]
    [InlineData("nested-window")][InlineData("nested-display")][InlineData("quick")]
    [InlineData("equivalent-output")][InlineData("profile")]
    public async Task IndependentWindow382IncompatibleRequestsFailBeforeNewConfirmationOrWorker(string fault)
    {
        using var f = RuntimeFixture(); NestedWindow381Providers(f);
        SystemQuery.SetActiveWindowProvider(() => new("window_43", "Synthetic second", "owned.exe", 7, true, false, new(0, 0, 64, 64)));
        f.StartApi();
        var a = await NestedWindow381Accept(f, IndependentWindow382Request(f));
        var q = IndependentWindow382Request(f, fault == "same" ? "window_42" : "window_43");
        if (fault == "third") await NestedWindow381Accept(f, q);
        if (fault is "display" or "region") q["source"] = f.Request(fault)["source"]!.DeepClone();
        if (fault == "series") q = SeriesBundleRequest(f, Path.Combine(f.Root, "series"));
        if (fault is "system" or "mic") q["audio"] = JsonNode.Parse(fault == "system" ?
            """{"system_audio":{"enabled":true}}""" : """{"microphone":{"enabled":true}}""");
        if (fault.StartsWith("nested-"))
        {
            q["nested"] = new JsonObject { ["role"] = "outer", ["session_id"] = "legacy382" };
            if (fault == "nested-display") q["source"] = f.Request("display")["source"]!.DeepClone();
        }
        if (fault == "quick")
        {
            q.Remove("source"); q.Remove("stop_condition"); q["target"] = new JsonObject { ["type"] = "active_window" }; q["duration_seconds"] = 1;
        }
        if (fault == "equivalent-output")
        { q["output"]!["directory"] = PathCanonical381RDirectory(Path.GetDirectoryName(a.OutputPath)!, "mixed"); q["output"]!["filename"] = Path.GetFileName(a.OutputPath); }
        var count = f.Engine._recs.Count; var confirmations = f.Engine._confs.Count;
        if (fault == "profile")
        {
            Assert.Throws<ApiException>(() => f.Engine.CreateRecordingFromFixedRegionProfile(q, "profile382", f.Tray,
                new("P", 1, AgentRecorder.Core.Automation.RecurringFixedRegionProfileVersion.DigestPrefix + new string('a', 64)), () => null));
        }
        else
        {
            var result = await NestedWindow381Post(f, q, fault == "quick");
            Assert.True((int)result.Status is 400 or 409, result.Body);
            if (fault == "same") Assert.Contains("WINDOW_TARGET_IN_USE", result.Body);
            if (fault == "third") Assert.Contains("TOO_MANY_CONCURRENT_RECORDINGS", result.Body);
            if (fault == "equivalent-output") Assert.Contains("OUTPUT_PATH_IN_USE", result.Body);
        }
        Assert.Equal(count, f.Engine._recs.Count); Assert.Equal(confirmations, f.Engine._confs.Count); Assert.Equal(0, f.FactoryCalls);
    }

    [Theory]
    [InlineData("display")][InlineData("region")][InlineData("system")]
    [InlineData("nested-window")][InlineData("nested-display")][InlineData("quick")]
    public async Task IndependentWindow382CannotJoinLegacyOrQuickContracts(string kind)
    {
        using var f = RuntimeFixture(); NestedWindow381Providers(f);
        SystemQuery.SetActiveWindowProvider(() => new("window_42", "Quick synthetic", "owned.exe", 7, true, false, new(0, 0, 64, 64)));
        f.StartApi();
        var q = kind is "display" or "region" ? f.Request(kind) : f.Request("window", kind == "system");
        q["output"]!.AsObject().Remove("filename");
        if (kind.StartsWith("nested-"))
        { q["nested"] = new JsonObject { ["role"] = "outer", ["session_id"] = "legacy382" }; if (kind == "nested-display") { q["source"] = f.Request("display")["source"]!.DeepClone(); q.Remove("required_capture_semantics"); } }
        if (kind == "quick")
        {
            q.Remove("source"); q.Remove("stop_condition"); q.Remove("required_capture_semantics");
            q["target"] = new JsonObject { ["type"] = "active_window" }; q["duration_seconds"] = 1;
        }
        var first = await NestedWindow381Post(f, q, kind == "quick"); Assert.True(first.Status == HttpStatusCode.OK, first.Body);
        Assert.Null(Assert.Single(f.Engine._recs.Values).WindowIdentity);
        var next = await NestedWindow381Post(f, IndependentWindow382Request(f, "window_43"));
        Assert.Equal(HttpStatusCode.Conflict, next.Status); Assert.Single(f.Engine._recs); Assert.Single(f.Engine._confs); Assert.Equal(0, f.FactoryCalls);
    }

    [Theory]
    [InlineData(RecState.pending_confirmation)][InlineData(RecState.preparing)][InlineData(RecState.countdown)]
    [InlineData(RecState.recording)][InlineData(RecState.stopping)][InlineData(RecState.finalizing)]
    public async Task IndependentWindow382EveryOccupiedLogicalStateKeepsTheTwoSlotBound(RecState state)
    {
        using var f = RuntimeFixture(); NestedWindow381Providers(f); f.StartApi();
        var a = await NestedWindow381Accept(f, IndependentWindow382Request(f));
        var b = await NestedWindow381Accept(f, IndependentWindow382Request(f, "window_43"));
        try
        {
            // State-membership test on two actual raw admissions, without pretending
            // to construct workers for these synthetic lifecycle positions.
            a.State = b.State = state;
            var result = await NestedWindow381Post(f, IndependentWindow382Request(f, "window_44"));
            Assert.Equal(HttpStatusCode.Conflict, result.Status); Assert.Equal(2, f.Engine._recs.Count); Assert.Equal(0, f.FactoryCalls);
        }
        finally { a.State = b.State = RecState.pending_confirmation; }
    }

    [Theory][InlineData("closed")][InlineData("minimized")][InlineData("pid")][InlineData("size")]
    public async Task IndependentWindow382UnapprovedWindowIdentityChangeFailsOnlyThatRun(string fault)
    {
        using var f = RuntimeFixture(); await f.Prepare(); NestedWindow381Providers(f); f.StartApi();
        try
        {
            var a = await NestedWindow381Accept(f, IndependentWindow382Request(f));
            var b = await NestedWindow381Accept(f, IndependentWindow382Request(f, "window_43"));
            var live = await IndependentWindow382Approve(f, a);
            SystemQuery.SetWindowProvider((_, _) => {
                var list = new List<SystemQuery.WindowInfo> { new("window_42", "Synthetic one", "owned.exe", 7, false, false, new(0, 0, 64, 64)) };
                if (fault != "closed") list.Add(new("window_43", "Synthetic two", "owned.exe", fault == "pid" ? 8 : 7, false, fault == "minimized", new(0, 0, fault == "size" ? 32 : 64, 64)));
                return list;
            });
            f.Tray.Confirmations[b.Id](ConfirmationDecision.Approve()); await Until(() => b.IsFinalized);
            Assert.Equal(RecState.failed, b.State); Assert.Equal(1, f.FactoryCalls);
            Assert.Equal(RecState.recording, a.State); Assert.False(live.Process.HasExited);
        }
        finally { await NestedWindow381Drain(f); }
    }

    [Fact]
    public async Task IndependentWindow382ConcurrentBuildsCannotOversellTheLastSlot()
    {
        using var f = RuntimeFixture();
        SystemQuery.SetWindowProvider((_, _) => new() {
            new("window_42", "One", "owned.exe", 7, false, false, new(0,0,64,64)),
            new("window_43", "Two", "owned.exe", 7, false, false, new(0,0,64,64)),
            new("window_44", "Three", "owned.exe", 7, false, false, new(0,0,64,64)) });
        f.StartApi(); await NestedWindow381Accept(f, IndependentWindow382Request(f));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int arrived = 0;
        f.Engine.CapturePlanFactoryForTests = cfg => {
            if (Interlocked.Increment(ref arrived) == 2) entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("owned-last-slot-gate");
            return CaptureBackendSelector.BuildPlan(cfg, new Probe());
        };
        var one = NestedWindow381Post(f, IndependentWindow382Request(f, "window_43"));
        var two = NestedWindow381Post(f, IndependentWindow382Request(f, "window_44"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); release.Set();
            var results = await Task.WhenAll(one, two);
            Assert.Single(results, x => x.Status == HttpStatusCode.OK);
            Assert.Contains("TOO_MANY_CONCURRENT_RECORDINGS", Assert.Single(results, x => x.Status == HttpStatusCode.Conflict).Body);
            Assert.Equal(2, f.Engine._recs.Count); Assert.Equal(2, f.Engine._confs.Count); Assert.Equal(0, f.FactoryCalls);
            Assert.Equal(2, f.Engine._recs.Values.Select(r => r.WindowIdentity!.Number).Distinct().Count());
        }
        finally { release.Set(); await Task.WhenAll(one, two); }
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task IndependentWindow382CapabilitySeparatesIndependentAndLegacyWindowContracts(bool available)
    {
        using var f = RuntimeFixture(); f.StartApi();
        f.Api!.WindowSurfaceAvailabilityForTests = () => new(available, "synthetic", "owned", 0);
        using var client = f.Client(); var data = await Get(client, "/api/v1/capabilities");
        var window = data.GetProperty("recording").GetProperty("window_surface");
        var independent = window.GetProperty("independent_windows");
        Assert.True(independent.GetProperty("supported").GetBoolean()); Assert.Equal(available, independent.GetProperty("available").GetBoolean());
        Assert.Equal(2, independent.GetProperty("max_concurrent").GetInt32()); Assert.False(independent.GetProperty("parent_relationship_required").GetBoolean());
        Assert.True(independent.GetProperty("per_run_confirmation_required").GetBoolean()); Assert.False(independent.GetProperty("quick_supported").GetBoolean());
        Assert.False(independent.GetProperty("profile_supported").GetBoolean()); Assert.False(independent.GetProperty("stress_tested").GetBoolean());
        Assert.Equal("none", independent.GetProperty("audio")[0].GetString()); Assert.True(window.GetProperty("nested_supported").GetBoolean());
        Assert.False(data.GetProperty("interaction").GetProperty("profile_region").GetProperty("nested_supported").GetBoolean());
        SaveSeriesBundleEvidence("independent382-cap-" + available, data);
    }

    [Fact]
    public async Task IndependentWindow382PreconstructedRecordingIsNotAProvenRawAdmission()
    {
        using var f = RuntimeFixture(); NestedWindow381Providers(f); f.StartApi();
        var built = ConfigParser.Build(IndependentWindow382Request(f), "unclassified-builder", out _);
        Assert.Null(built.WindowIdentity); built.State = RecState.pending_confirmation;
        Assert.True(f.Engine._recs.TryAdd(built.Id, built));
        try
        {
            var result = await NestedWindow381Post(f, IndependentWindow382Request(f, "window_43"));
            Assert.Equal(HttpStatusCode.Conflict, result.Status); Assert.Single(f.Engine._recs); Assert.Equal(0, f.FactoryCalls);
        }
        finally { f.Engine._recs.TryRemove(built.Id, out _); }
    }

    [Fact]
    public async Task IndependentWindow382CreationWaitTracksOnlyItsOwnLocalDecision()
    {
        using var f = RuntimeFixture(); await f.Prepare(); NestedWindow381Providers(f); f.StartApi();
        try
        {
            var first = await NestedWindow381Accept(f, IndependentWindow382Request(f));
            using var client = f.Client();
            var waiting = client.PostAsync("/api/v1/recordings?creation_wait_ms=3000",
                new System.Net.Http.StringContent(IndependentWindow382Request(f, "window_43").ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
            await Until(() => f.Tray.Confirmations.Count == 2);
            var second = f.Engine._recs.Values.Single(r => r.Id != first.Id);
            await IndependentWindow382Approve(f, second);
            using var response = await waiting; Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["data"]!;
            Assert.Equal(second.Id, body["recording_id"]!.GetValue<string>());
            Assert.Equal(RecState.pending_confirmation, first.State); Assert.Null(first.AuthorizationProof);
            Assert.Equal(RecState.recording, second.State); Assert.Equal(1, f.FactoryCalls);
        }
        finally { await NestedWindow381Drain(f); }
    }
}
