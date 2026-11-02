using System.Net;
using AgentRecorder.Core;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class OrdinaryAvOutputOwnershipTests
{
    [Fact]
    public async Task SilentPendingDirectoryReplacementKeepsNewClaimAndReleasesOldPath()
    {
        using var p = new Pair(); var f = p.F;
        var parent = await p.Accept(p.Request(f.Root, "same.mp4", "outer", av: false)); var old = parent.OutputPath;
        var moved = Path.Combine(f.Root, "moved"); f.Tray.Approve(moved); p.Workers[0].Backend.StartVideo();
        await Until(() => parent.State == RecState.recording);
        Assert.Equal(Path.Combine(moved, "same.mp4"), parent.OutputPath);
        Assert.False(RuntimeOutputTestEngine.Read(f.Engine, old)!.Paths.Single().InUse);
        Assert.True(RuntimeOutputTestEngine.Read(f.Engine, parent.OutputPath)!.Paths.Single().InUse);
        var child = await p.Accept(p.Request(f.Root, "same.mp4", "inner", parent, av: false));
        Assert.Equal(old, child.OutputPath); Assert.Equal(RecState.pending_confirmation, child.State);
        Assert.Equal(1, p.Starts); Assert.Single(p.Workers); Assert.False(p.Workers[0].Video.StopCalled);
    }
    [Theory]
    [InlineData("exact")] [InlineData("case")] [InlineData("segments")]
    public async Task SilentPendingSamePathRejectsWithoutConfirmationOrWorker(string spelling)
    {
        using var p = new Pair(); var f = p.F;
        // An ordinary pending request plus a new outer is legal under the
        // existing two-slot policy; two outers would fail before path admission.
        var firstRequest = p.Request(f.Root, "same.mp4", "outer", av: false); firstRequest.Remove("nested");
        var first = await p.Accept(firstRequest);
        var directory = spelling switch { "case" => f.Root.ToUpperInvariant(), "segments" => Path.Combine(f.Root, "..", Path.GetFileName(f.Root)), _ => f.Root };
        using var response = await f.Post("/api/v1/recordings?creation_wait_ms=0", p.Request(directory, "same.mp4", "outer", av: false));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Contains("OUTPUT_PATH_IN_USE", await response.Content.ReadAsStringAsync());
        Assert.Single(f.Engine._recs); Assert.Single(f.Tray.Confirmations); Assert.Empty(p.Workers); Assert.Equal(0, p.Starts);
        Assert.Equal(RecState.pending_confirmation, first.State); Assert.Null(first.AuthorizationProof); Assert.False(File.Exists(first.OutputPath));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SilentApprovalCollisionKeepsOldClaimAndCannotRename(bool existingFile)
    {
        using var p = new Pair(); var f = p.F;
        var parent = await p.Accept(p.Request(f.Root, "same.mp4", "outer", av: false));
        f.Tray.Approve(); p.Workers[0].Backend.StartVideo(); await Until(() => parent.State == RecState.recording);
        var bytes = new byte[] { 3, 7, 8 }; if (existingFile) File.WriteAllBytes(parent.OutputPath, bytes);
        var child = await p.Accept(p.Request(Path.Combine(f.Root, "other"), "same.mp4", "inner", parent, av: false));
        var old = child.OutputPath; f.Tray.Approve(f.Root);
        Assert.Equal(RecState.rejected, child.State); Assert.Equal("OUTPUT_PATH_IN_USE", child.StopReason);
        Assert.Equal(old, child.OutputPath); Assert.Null(child.AuthorizationProof); Assert.False(child.BackendStartAttempted);
        Assert.Single(p.Workers); Assert.Equal(1, p.Starts); Assert.Equal(RecState.recording, parent.State);
        Assert.True(RuntimeOutputTestEngine.Read(f.Engine, old)!.Paths.Single().InUse);
        Assert.Equal("OUTPUT_PATH_IN_USE", f.History.Read(child.Id)!.ReasonCode);
        Assert.False(File.Exists(Path.Combine(f.Root, "same-1.mp4"))); Assert.False(p.Workers[0].Video.StopCalled);
        if (existingFile) Assert.Equal(bytes, File.ReadAllBytes(parent.OutputPath));
    }

    [Fact]
    public async Task SilentRegistrationCannotRenameAroundLiveOriginalRequestPath()
    {
        using var p = new Pair(); var f = p.F;
        var parent = await p.Accept(p.Request(f.Root, "same.mp4", "outer", av: false));
        f.Tray.Approve(); p.Workers[0].Backend.StartVideo(); await Until(() => parent.State == RecState.recording);
        var bytes = new byte[] { 3, 7, 8 }; File.WriteAllBytes(parent.OutputPath, bytes);
        var request = p.Request(f.Root, "same.mp4", "inner", parent, av: false); request["output"]!["conflict_policy"] = "rename";
        using var response = await f.Post("/api/v1/recordings?creation_wait_ms=0", request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Contains("OUTPUT_PATH_IN_USE", await response.Content.ReadAsStringAsync());
        Assert.Single(f.Engine._recs); Assert.Empty(f.Tray.Confirmations); Assert.Equal(1, p.Starts);
        Assert.Equal(bytes, File.ReadAllBytes(parent.OutputPath)); Assert.False(File.Exists(Path.Combine(f.Root, "same-1.mp4")));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SilentDifferentPathsStillAllowNestedWorkers(bool defaultNames)
    {
        using var p = new Pair(); var f = p.F;
        var a = p.Request(Path.Combine(f.Root, "outer"), "parent.mp4", "outer", av: false);
        if (defaultNames) a["output"]!.AsObject().Remove("filename");
        var parent = await p.Accept(a); f.Tray.Approve(); p.Workers[0].Backend.StartVideo(); await Until(() => parent.State == RecState.recording);
        var b = p.Request(Path.Combine(f.Root, "inner"), "child.mp4", "inner", parent, av: false);
        if (defaultNames) b["output"]!.AsObject().Remove("filename");
        var child = await p.Accept(b); f.Tray.Approve(); p.Workers[1].Backend.StartVideo(); await Until(() => child.State == RecState.recording);
        Assert.NotEqual(parent.OutputPath, child.OutputPath); Assert.Equal(2, p.Starts); Assert.Equal(2, p.Workers.Count);
        Assert.Equal(RecState.recording, parent.State); Assert.False(p.Workers[0].Video.StopCalled); Assert.False(p.Workers[1].Video.StopCalled);
    }
}
