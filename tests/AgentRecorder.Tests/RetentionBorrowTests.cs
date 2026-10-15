using System.Reflection;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class RetentionBorrowTests
{
    [Fact]
    public async Task SameHeldHandlesExclusiveSynchronousBorrowAndExpiredContext()
    {
        var f = new OriginalFileScopeFixture(); var held = await f.Service().AcquireAsync(f.Anchor); var owner = held.Owner!;
        var nodes = f.Api.Opened.ToArray(); OriginalFileBorrowContext? saved = null; var calls = 0;
        Task<OriginalFileHoldObservation>? busy = null;
        try
        {
            var borrowed = owner.Borrow(f.Anchor, () => { }, context =>
            {
                context.RequireActive(); saved = context; calls++;
                busy = owner.ObserveAsync(); Assert.True(busy.IsCompletedSuccessfully);
                Assert.Equal(OriginalFileBorrowReason.Busy, owner.Borrow(f.Anchor, () => { }, _ => throw new InvalidOperationException()).Reason);
                Exception? error = null; var t = new Thread(() => { try { context.RequireActive(); } catch (Exception e) { error = e; } });
                t.Start(); Assert.True(t.Join(TimeSpan.FromSeconds(1))); Assert.IsType<InvalidOperationException>(error);
                return OriginalFileBorrowContinuationResult.Completed;
            });
            Assert.Equal(OriginalFileBorrowReason.Borrowed, borrowed.Reason); Assert.Equal(1, calls);
            Assert.Equal(OriginalFileHoldReason.Busy, (await busy!).Reason);
            Assert.Throws<InvalidOperationException>(() => saved!.RequireActive());
            Assert.Equal(nodes, f.Api.Opened.ToArray()); Assert.DoesNotContain(f.Api.Calls, c => c.Stage == "close");
            Assert.All(f.Api.Calls.Where(c => c.Handle is not null), c => Assert.Same(nodes[c.Index].Handle, c.Handle));
            Assert.All(typeof(OriginalFileBorrowContext).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public), c => Assert.True(c.IsPrivate));
        }
        finally { await OriginalFileScopeFixture.Retire(owner); }
        OriginalFileScopeFixture.AssertClosedInReverse(f.Api);
    }
    [Theory]
    [InlineData("dispose")][InlineData("observe")]
    public async Task BorrowAndObserveDisposeDoNotSampleOrCloseConcurrently(string action)
    {
        var f = new OriginalFileScopeFixture(); var held = await f.Service().AcquireAsync(f.Anchor); var owner = held.Owner!;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var work = Task.Run(() => owner.Borrow(f.Anchor, () => { }, context =>
        { context.RequireActive(); entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(8))); return OriginalFileBorrowContinuationResult.Completed; }));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            var before = f.Api.Calls.Count;
            if (action == "dispose") owner.Dispose(); else Assert.Equal(OriginalFileHoldReason.Busy, (await owner.ObserveAsync()).Reason);
            Assert.DoesNotContain(f.Api.Calls, c => c.Stage == "close"); Assert.Equal(before, f.Api.Calls.Count);
            Assert.False(owner.Retirement.IsCompleted); release.Set();
            var result = await work.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(action == "dispose" ? OriginalFileBorrowReason.Cancelled : OriginalFileBorrowReason.Borrowed, result.Reason);
        }
        finally { release.Set(); await work; await OriginalFileScopeFixture.Retire(owner); }
    }
    [Theory]
    [InlineData("identity")][InlineData("length")][InlineData("path")][InlineData("directory")][InlineData("metadata")][InlineData("disposed")][InlineData("anchor")]
    public async Task RecheckOrAdmissionFailureNeverInvokesCallback(string kind)
    {
        var f = new OriginalFileScopeFixture(); var held = await f.Service().AcquireAsync(f.Anchor); var owner = held.Owner!; var calls = 0;
        if (kind == "identity") f.Api.IdentityValue = (n, _, v) => { if (n.Leaf) v.IdHigh++; return v; };
        if (kind == "length") f.Api.StandardValue = (n, _, v) => { if (n.Leaf) v.EndOfFile++; return v; };
        if (kind == "path") f.Api.PathValue = (n, _, p) => n.Leaf ? p + ".other" : p;
        if (kind == "directory") f.Api.IdentityValue = (n, _, v) => { if (!n.Leaf) v.IdHigh++; return v; };
        if (kind == "metadata") f.Api.Fail = c => c.Stage == "basic";
        if (kind == "disposed") owner.Dispose();
        var result = owner.Borrow(kind == "anchor" ? f.Anchor with { Length = 13 } : f.Anchor, () => { }, _ => { calls++; return OriginalFileBorrowContinuationResult.Completed; });
        Assert.NotEqual(OriginalFileBorrowReason.Borrowed, result.Reason); Assert.Equal(0, calls);
        await OriginalFileScopeFixture.Retire(owner);
    }
    [Fact]
    public async Task CallbackFailureNoRetryAndOldObserveStillWorks()
    {
        var f = new OriginalFileScopeFixture(); var held = await f.Service().AcquireAsync(f.Anchor); var calls = 0;
        var result = held.Owner!.Borrow(f.Anchor, () => { }, _ => { calls++; throw new IOException("PRIVATE_BORROW"); });
        Assert.Equal(OriginalFileBorrowReason.CallbackFailed, result.Reason); Assert.Equal(1, calls);
        Assert.Equal(OriginalFileHoldReason.Observed, (await held.Owner.ObserveAsync()).Reason); await OriginalFileScopeFixture.Retire(held.Owner);
    }
}
