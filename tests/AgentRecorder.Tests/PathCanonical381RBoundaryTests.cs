using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class OrdinaryWgcBundleTests
{
    [Theory]
    [InlineData("mixed")]
    [InlineData("trailing")]
    public async Task PathCanonical381RLocalDirectoryChangeFreezesCanonicalPublication(string form)
    {
        using var f = RuntimeFixture(); await f.Prepare(); f.StartApi();
        try
        {
            var request = f.Request("window");
            request["output"]!["directory"] = PathCanonical381RDirectory(Path.Combine(f.Root, "initial"), "mixed");
            request["output"]!.AsObject().Remove("filename");
            var rec = await NestedWindow381Accept(f, request);
            var initial = rec.OutputPath; var name = Path.GetFileName(initial);
            Assert.Equal(Path.Combine(f.Root, "initial", name), initial);
            var selected = Path.Combine(f.Root, "chosen");
            f.Tray.Confirm!(ConfirmationDecision.Approve(PathCanonical381RDirectory(selected, form)));
            await Until(() => rec.State == RecState.recording || rec.IsFinalized);
            Assert.Equal(RecState.recording, rec.State);
            Assert.Equal(Path.Combine(selected, name), rec.OutputPath);
            Assert.Equal(rec.OutputPath, rec.Config.OutputPath);
            Assert.Equal(selected, rec.Config.WritePaths!.OutputDirectory);
            // The initial requested name remains the original anti-rename claim input.
            Assert.Equal(initial, rec.OrdinaryRequestedOutputPath);
            f.Generator.Release.TrySetResult(); await f.Terminal(rec, false);
            await Until(() => rec.BundleSnapshot.Status == "ready");
            await PathCanonical381RHistory(f, rec);
        }
        finally { await NestedWindow381Drain(f); }
    }

    [Fact]
    public async Task PathCanonical381RTwoWindow120And20UseIndependentSyntheticClocksAndCompletionGates()
    {
        using var f = RuntimeFixture(); NestedWindow381Providers(f); f.StartApi();
        var outerClip = Path.Combine(f.Root, "outer-color.mp4");
        var innerClip = Path.Combine(f.Root, "inner-color.mp4");
        foreach (var (path, seconds, color) in new[] { (outerClip, 120, "blue"), (innerClip, 20, "red") })
        {
            var result = await new ExternalProcessRunner().RunAsync(AvMuxOwnershipTests.Ffmpeg,
                new[] { "-y", "-f", "lavfi", "-i", $"color=c={color}:s=64x64:r=15", "-t", seconds.ToString(),
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", path }, TimeSpan.FromSeconds(30));
            Assert.True(result.ExitCode == 0, result.Stderr);
        }
        f.SyntheticProcessCreatedForTests = p => {
            p.MediaClipOverride = p.Options.WindowHandle == (nint)42 ? outerClip : innerClip;
            p.DurationMsOverride = p.Options.DurationMs;
            p.SourceTimeHnsOverride = (long)((decimal)Stopwatch.GetTimestamp() * 10_000_000 / Stopwatch.Frequency);
        };
        try
        {
            var q = NestedWindow381Request(f, "outer"); q["stop_condition"]!["seconds"] = 120;
            var outer = await NestedWindow381Accept(f, q); await NestedWindow381Approve(f, outer);
            var op = f.Process!; var ob = f.Wgc!;
            q = NestedWindow381Request(f, "inner", "window_43", outer); q["stop_condition"]!["seconds"] = 20;
            q["output"]!["directory"] = PathCanonical381RDirectory(Path.Combine(f.Root, "Videos"), "mixed");
            var inner = await NestedWindow381Accept(f, q); await NestedWindow381Approve(f, inner);
            var ip = f.Process!; var ib = f.Wgc!;
            Assert.Equal(120000, op.Options.DurationMs); Assert.Equal(20000, ip.Options.DurationMs);
            Assert.NotSame(op.Finish, ip.Finish); Assert.NotEqual(op.SourceTimeHnsOverride, ip.SourceTimeHnsOverride);
            Assert.NotSame(outer.AuthorizationProof, inner.AuthorizationProof);
            Assert.NotEqual(outer.AuthorizationProof!.ScopeDigest, inner.AuthorizationProof!.ScopeDigest);
            f.Generator.Release.TrySetResult(); ip.Finish.TrySetResult();
            await Until(() => inner.IsFinalized && inner.BundleSnapshot.Status == "ready");
            await ib.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RecState.recording, outer.State); Assert.False(op.HasExited);
            await PathCanonical381RHistory(f, inner);
            op.Finish.TrySetResult(); await Until(() => outer.IsFinalized && outer.BundleSnapshot.Status == "ready");
            await ob.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
            await PathCanonical381RHistory(f, outer);
            Assert.Equal(120, outer.LastMeta!.DurationSeconds); Assert.Equal(20, inner.LastMeta!.DurationSeconds);
            Assert.NotEqual(outer.OrdinaryTargetKey, inner.OrdinaryTargetKey);
            Assert.NotEqual(outer.BundleSnapshot.Path, inner.BundleSnapshot.Path);
            Assert.Equal(outer.Id, f.Writer.Read(inner.Id)!.ParentRecordingId);
            SaveSeriesBundleEvidence("path381r-two-windows", new { outer = outer.Id, inner = inner.Id,
                outerDuration = outer.LastMeta.DurationSeconds, innerDuration = inner.LastMeta.DurationSeconds,
                outerPath = outer.OutputPath, innerPath = inner.OutputPath, outerBundle = outer.BundleSnapshot.Status,
                innerBundle = inner.BundleSnapshot.Status, independentCompletionGates = true });
        }
        finally { await NestedWindow381Drain(f); }
    }

    [Theory]
    [InlineData("mixed")][InlineData("forward")][InlineData("trailing")]
    public async Task PathCanonical381REquivalentDirectoryCannotBypassLiveMainOutput(string form)
    {
        using var f = RuntimeFixture(); await f.Prepare(); NestedWindow381Providers(f); f.StartApi();
        try
        {
            var outer = await NestedWindow381Accept(f, NestedWindow381Request(f, "outer"));
            await NestedWindow381Approve(f, outer);
            var q = NestedWindow381Request(f, "inner", "window_43", outer);
            q["output"]!["directory"] = PathCanonical381RDirectory(Path.GetDirectoryName(outer.OutputPath)!, form);
            q["output"]!["filename"] = Path.GetFileName(outer.OutputPath);
            var count = f.Engine._confs.Count;
            var response = await NestedWindow381Post(f, q);
            Assert.Equal(HttpStatusCode.Conflict, response.Status); Assert.Contains("OUTPUT_PATH_IN_USE", response.Body);
            Assert.Single(f.Engine._recs); Assert.Equal(count, f.Engine._confs.Count); Assert.Equal(1, f.FactoryCalls);
        }
        finally { await NestedWindow381Drain(f); }
    }

    [Theory]
    [InlineData("mixed")][InlineData("trailing")]
    public async Task PathCanonical381REquivalentLocalMoveCannotBypassLiveMainOutput(string form)
    {
        using var f = RuntimeFixture(); await f.Prepare(); NestedWindow381Providers(f); f.StartApi();
        try
        {
            var outer = await NestedWindow381Accept(f, NestedWindow381Request(f, "outer"));
            await NestedWindow381Approve(f, outer);
            var q = NestedWindow381Request(f, "inner", "window_43", outer);
            q["output"]!["directory"] = Path.Combine(f.Root, "pending");
            q["output"]!["filename"] = Path.GetFileName(outer.OutputPath);
            var inner = await NestedWindow381Accept(f, q);
            f.Tray.Confirm!(ConfirmationDecision.Approve(PathCanonical381RDirectory(Path.GetDirectoryName(outer.OutputPath)!, form)));
            Assert.Equal(RecState.rejected, inner.State); Assert.Equal("OUTPUT_PATH_IN_USE", inner.StopReason);
            Assert.Null(inner.AuthorizationProof); Assert.Equal(1, f.FactoryCalls); Assert.Equal(RecState.recording, outer.State);
            Assert.False(File.Exists(inner.OutputPath));
        }
        finally { await NestedWindow381Drain(f); }
    }

    [Theory]
    [InlineData(false, false)][InlineData(true, false)]
    [InlineData(false, true)][InlineData(true, true)]
    public async Task PathCanonical381REquivalentBundleNamespaceRejectsVideoAndSeriesBeforeCreatingDirectories(bool local, bool series)
    {
        using var f = RuntimeFixture(); await f.Prepare();
        var runner = new SeriesBundlePngRunner(); ConfigureSeriesBundle(f, runner);
        Func<Task>? work = null; f.Engine.BundleGenerationDispatcherForTests = run => { work = run; return Task.CompletedTask; };
        var video = f.Create("window"); await f.Approve(video); await f.Terminal(video, false);
        var bundle = Path.ChangeExtension(video.OutputPath, ".bundle");
        var directory = PathCanonical381RDirectory(Path.Combine(bundle, "must-not-create"), "mixed") + "/";
        f.StartApi();
        try
        {
            var q = series ? SeriesBundleRequest(f, local ? Path.Combine(f.Root, "pending") : directory) : f.Request("window");
            if (!series) q["output"]!["directory"] = local ? Path.Combine(f.Root, "pending") : directory;
            if (local)
            {
                var pending = await NestedWindow381Accept(f, q);
                f.Tray.Confirm!(ConfirmationDecision.Approve(directory));
                Assert.Equal(RecState.rejected, pending.State); Assert.Equal("OUTPUT_PATH_IN_USE", pending.StopReason);
                Assert.Null(pending.AuthorizationProof);
            }
            else
            {
                var count = f.Engine._confs.Count; var result = await NestedWindow381Post(f, q);
                Assert.Equal(HttpStatusCode.Conflict, result.Status); Assert.Contains("OUTPUT_PATH_IN_USE", result.Body);
                Assert.Equal(count, f.Engine._confs.Count); Assert.Single(f.Engine._recs);
            }
            Assert.False(Directory.Exists(bundle)); Assert.Equal(1, f.FactoryCalls); Assert.Equal(0, runner.Calls);
        }
        finally { f.Generator.Release.TrySetResult(); await work!(); await NestedWindow381Drain(f); }
        Assert.Equal("ready", video.BundleSnapshot.Status);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task PathCanonical381RSeriesAdmissionAndLocalChangeUseCanonicalDirectory(bool local)
    {
        using var f = RuntimeFixture(); f.StartApi();
        var runner = new SeriesBundlePngRunner(); ConfigureSeriesBundle(f, runner);
        var initial = Path.Combine(f.Root, "initial"); var chosen = Path.Combine(f.Root, "chosen");
        var rec = await NestedWindow381Accept(f, SeriesBundleRequest(f, PathCanonical381RDirectory(initial, "mixed")));
        Assert.Equal(Path.Combine(initial, "frames"), rec.OutputPath);
        f.Tray.Confirm!(ConfirmationDecision.Approve(local ? PathCanonical381RDirectory(chosen, "trailing") : null));
        await Until(() => rec.IsFinalized);
        var expected = Path.Combine(local ? chosen : initial, "frames");
        Assert.Equal(RecState.completed, rec.State); Assert.Equal(expected, rec.OutputPath);
        Assert.Equal(expected, rec.Config.OutputPath); Assert.Equal(expected, rec.ScreenshotSeries!.OutputDirectory);
        Assert.True(Directory.Exists(expected)); Assert.Equal(1, runner.Calls); Assert.Equal(0, f.FactoryCalls);
        await Until(() => !SeriesBundleInUse(f, expected));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task PathCanonical381RDeclinedOrExpiredMixedRequestDoesNotStart(bool expired)
    {
        using var f = RuntimeFixture(); f.StartApi();
        var q = f.Request("window"); q["output"]!["directory"] = PathCanonical381RDirectory(Path.Combine(f.Root, "Videos"), "mixed");
        var rec = await NestedWindow381Accept(f, q); var callback = f.Tray.Confirm!;
        if (expired) f.Engine.TriggerConfirmationExpiryForTests(rec.ConfirmationId!); else callback(ConfirmationDecision.Reject());
        callback(ConfirmationDecision.Approve());
        Assert.Null(rec.AuthorizationProof); Assert.Equal(0, f.FactoryCalls); Assert.False(File.Exists(rec.OutputPath));
        Assert.Equal(expired ? RecState.expired : RecState.rejected, rec.State);
        Assert.Equal(rec.State.ToString(), f.Writer.Read(rec.Id)!.Status);
    }

    [Theory]
    [InlineData("relative")][InlineData("drive-relative")][InlineData("unc")][InlineData("device")]
    [InlineData("nul")][InlineData("ads")][InlineData("control")][InlineData("trailing-dot")][InlineData("trailing-space")]
    public async Task PathCanonical381RInvalidDirectoryFailsBeforeConfirmationOrWorker(string fault)
    {
        using var f = RuntimeFixture(); f.StartApi();
        var path = fault switch {
            "relative" => "relative/Videos", "drive-relative" => "D:relative", "unc" => @"\\invalid-host\share\Videos",
            "device" => @"\\?\D:\Videos", "nul" => Path.Combine(f.Root, "NUL"), "ads" => Path.Combine(f.Root, "bad:ads"),
            "control" => Path.Combine(f.Root, "bad\u0001name"), "trailing-space" => Path.Combine(f.Root, "bad "), _ => Path.Combine(f.Root, "bad.") };
        var q = f.Request("window"); q["output"]!["directory"] = path;
        var result = await NestedWindow381Post(f, q);
        Assert.Equal(HttpStatusCode.BadRequest, result.Status); Assert.Contains("OUTPUT_PATH_INVALID", result.Body);
        Assert.Empty(f.Engine._recs); Assert.Empty(f.Engine._confs); Assert.Equal(0, f.FactoryCalls); Assert.Null(f.Tray.Confirm);
        Assert.False(Directory.Exists(Path.Combine(f.Root, "Videos")));
    }
}
