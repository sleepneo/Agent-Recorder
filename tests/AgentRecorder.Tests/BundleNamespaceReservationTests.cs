using System.Reflection;
using AgentRecorder.Core;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using static AgentRecorder.Tests.RuntimeOutputTestEngine;

namespace AgentRecorder.Tests;

public sealed partial class OrdinaryWgcBundleTests
{
    [Fact]
    public async Task BundleNamespaceRejectedRequestMustNotMaterializePublicationDirectory()
    {
        using var f = RuntimeFixture(); await f.Prepare(); Func<Task>? run = null;
        f.Engine.BundleGenerationDispatcherForTests = work => { run = work; return Task.CompletedTask; };
        var rec = f.Create("display"); await f.Approve(rec); await f.Terminal(rec, false);
        var directory = Path.ChangeExtension(rec.OutputPath, ".bundle");
        try
        {
            Assert.False(Directory.Exists(directory)); f.StartApi(); using var client = f.Client();
            var request = f.Request("display"); request["output"]!["directory"] = Path.Combine(directory, "rejected");
            using var response = await client.PostAsync("/api/v1/recordings?creation_wait_ms=0",
                new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.False(Directory.Exists(directory)); // Known blocker: parser creates it before Claim.
        }
        finally { f.Generator.Release.TrySetResult(); await run!(); }
        Assert.Equal("ready", rec.BundleSnapshot.Status);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BundleNamespaceQueuedAndGeneratingProtectDescendantsUntilActualExit(bool queued)
    {
        using var f = RuntimeFixture(); await f.Prepare(); Func<Task>? run = null;
        if (queued) f.Engine.BundleGenerationDispatcherForTests = work => { run = work; return Task.CompletedTask; };
        var rec = f.Create("display"); await f.Approve(rec); await f.Terminal(rec, false);
        var root = Path.ChangeExtension(rec.OutputPath, ".bundle");
        var child = Path.Combine(root, "sub", "new.mp4");
        try
        {
            Assert.True(Read(f.Engine, child)!.Paths.Single().InUse);
            Assert.True(Read(f.Engine, child.ToUpperInvariant())!.Paths.Single().InUse);
            Assert.False(Read(f.Engine, root + "-extra/new.mp4")!.Paths.Single().InUse);
            var many = Enumerable.Range(0, 100).Select(i => Path.Combine(root, i + ".mp4")).ToArray();
            Assert.All(Read(f.Engine, many)!.Paths, p => Assert.True(p.InUse));
            Assert.Null(Read(f.Engine, many.Append(child).ToArray()));
            var scopedRoot = Path.Combine(root, "Videos");
            var anchor = rec.LastMeta!.PublicationReceipt!.Anchor with { Path = Path.Combine(scopedRoot, "held.mp4") };
            var native = new OriginalFileScopeFixture.Native { Anchor = anchor };
            var preparation = await f.Engine.CreateRetentionPreparationService(scopedRoot, native).AcquireAsync(anchor);
            Assert.Equal(OrdinaryPreparationReason.RuntimeInUse, preparation.Reason); Assert.Empty(native.Calls);
            var error = Assert.Throws<TargetInvocationException>(() => Claim(f.Engine,
                new Recording(false) { IsOrdinaryLocalRequest = true, OutputPath = child }, child));
            Assert.Equal("OUTPUT_PATH_IN_USE", Assert.IsType<ApiException>(error.InnerException).Code);
            f.StartApi(); using var client = f.Client(); var request = f.Request("display");
            request["output"]!["directory"] = Path.Combine(root, "api-child");
            using var response = await client.PostAsync("/api/v1/recordings?creation_wait_ms=0",
                new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("OUTPUT_PATH_IN_USE", await response.Content.ReadAsStringAsync());
            Assert.Single(f.Engine._recs); Assert.Equal(1, f.FactoryCalls);
            // Same guard is reached through the original local directory approval.
            var pendingRequest = f.Request("display"); pendingRequest["output"]!["filename"] = "pending.mp4";
            f.Engine.CreateRecording(pendingRequest, "namespace-local", f.Tray);
            var pending = f.Engine._recs.Values.Single(r => r.Id != rec.Id);
            f.Tray.Confirm!(ConfirmationDecision.Approve(Path.Combine(root, "local-child")));
            Assert.Equal(RecState.rejected, pending.State); Assert.Equal("OUTPUT_PATH_IN_USE", pending.StopReason);
            Assert.Equal(1, f.FactoryCalls); Assert.False(File.Exists(Path.Combine(root, "local-child", "pending.mp4")));
        }
        finally { f.Generator.Release.TrySetResult(); }
        if (queued) await run!();
        await Until(() => BundleUseCount(f) == 0);
        Assert.False(Read(f.Engine, child)!.Paths.Single().InUse);
        Assert.Equal(RecState.completed, rec.State); Assert.True(File.Exists(rec.OutputPath));
        Assert.Equal("ready", rec.BundleSnapshot.Status);
    }

    [Fact]
    public void BundleNamespaceDirectoryCreationFailureRestoresExactPreviousClaim()
    {
        using var f = RuntimeFixture(); var old = Path.Combine(f.Root, "old.mp4");
        var rec = new Recording(false) { IsOrdinaryLocalRequest = true, OutputPath = old };
        Claim(f.Engine, rec, old);
        var file = Path.Combine(f.Root, "file-not-directory"); File.WriteAllText(file, "owned");
        var next = Path.Combine(file, "child.mp4");
        var method = typeof(RecordingEngine).GetMethod("ClaimAndCreateOrdinaryOutput", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(f.Engine, new object?[] { rec, next, null }));
        Assert.IsAssignableFrom<IOException>(error.InnerException);
        Assert.True(Read(f.Engine, old)!.Paths.Single().InUse); Assert.False(Read(f.Engine, next)!.Paths.Single().InUse);
        Assert.Equal("owned", File.ReadAllText(file));
    }

    [Fact]
    public async Task BundleNamespaceReverseWriterConflictKeepsMainCompletedAndWriterUntouched()
    {
        using var f = RuntimeFixture(); await f.Prepare(); var rec = f.Create("display"); await f.Approve(rec);
        var child = Path.Combine(Path.ChangeExtension(rec.OutputPath, ".bundle"), "existing.mp4");
        var writer = new Recording(false) { IsOrdinaryLocalRequest = true, OutputPath = child };
        Directory.CreateDirectory(Path.GetDirectoryName(child)!); File.WriteAllText(child, "owned writer must survive");
        Claim(f.Engine, writer, child); await f.Terminal(rec, false);
        try
        {
            await Until(() => rec.BundleSnapshot.Status == "failed");
            Assert.Equal(0, f.Generator.Calls); Assert.Equal(RecState.completed, rec.State);
            Assert.True(File.Exists(rec.OutputPath)); Assert.True(Read(f.Engine, child)!.Paths.Single().InUse);
            Assert.Equal("owned writer must survive", File.ReadAllText(child));
        }
        finally { f.Generator.Release.TrySetResult(); }
    }

    [Fact]
    public async Task BundleNamespaceTwoReservationsAndLateDuplicateCannotReleaseOtherUse()
    {
        using var f = RuntimeFixture(); await f.Prepare(); var queue = new List<Func<Task>>();
        f.Engine.BundleGenerationDispatcherForTests = work => { queue.Add(work); return Task.CompletedTask; };
        var first = f.Create("display"); await f.Approve(first); await f.Terminal(first, false);
        var request = f.Request("display"); request["output"]!["filename"] = "second.mp4";
        f.Engine.CreateRecording(request, "namespace-second", f.Tray);
        var second = f.Engine._recs.Values.Single(r => r.Id != first.Id);
        f.Tray.Confirm!(ConfirmationDecision.Approve()); await Until(() => second.State == RecState.recording);
        await f.Terminal(second, false); Assert.Equal(2, queue.Count);
        var a = Path.Combine(Path.ChangeExtension(first.OutputPath, ".bundle"), "child.mp4");
        var b = Path.Combine(Path.ChangeExtension(second.OutputPath, ".bundle"), "child.mp4");
        Assert.All(Read(f.Engine, a, b)!.Paths, p => Assert.True(p.InUse));
        f.Generator.Release.TrySetResult(); await queue[0](); await Until(() => BundleUseCount(f) == 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue[0]());
        Assert.False(Read(f.Engine, a)!.Paths.Single().InUse); Assert.True(Read(f.Engine, b)!.Paths.Single().InUse);
        await queue[1](); await Until(() => BundleUseCount(f) == 0);
        Assert.False(Read(f.Engine, b)!.Paths.Single().InUse);
    }
}
