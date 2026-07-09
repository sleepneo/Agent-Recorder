using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using AgentRecorder.App;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Tests;

public class ConfirmationQueueTests
{
    [Fact]
    public void Enqueue_TwoConfirmations_DoesNotAutoRejectSecond()
    {
        var queue = new ConfirmationQueue();
        var callback1Called = false;
        var callback2Called = false;

        var item1 = new PendingConfirmationItem(
            "conf_1", "rec_1", new { source = "test1" },
            _ => { callback1Called = true; },
            60);

        var item2 = new PendingConfirmationItem(
            "conf_2", "rec_2", new { source = "test2" },
            _ => { callback2Called = true; },
            60);

        queue.Enqueue(item1);
        queue.Enqueue(item2);

        Assert.Equal(2, queue.PendingCount);
        Assert.False(callback1Called);
        Assert.False(callback2Called); // Second item NOT auto-rejected
    }

    [Fact]
    public void ApproveCurrent_InvokesFirstCallbackAndAdvances()
    {
        var queue = new ConfirmationQueue();
        var callback1Called = false;
        ConfirmationDecision? callback1Result = null;
        var callback2Called = false;

        var item1 = new PendingConfirmationItem(
            "conf_1", "rec_1", new { source = "test1" },
            result => { callback1Called = true; callback1Result = result; },
            60);

        var item2 = new PendingConfirmationItem(
            "conf_2", "rec_2", new { source = "test2" },
            _ => { callback2Called = true; },
            60);

        queue.Enqueue(item1);
        queue.Enqueue(item2);

        var approved = queue.ApproveCurrent();

        Assert.True(approved);
        Assert.True(callback1Called);
        Assert.NotNull(callback1Result);
        Assert.True(callback1Result!.Approved); // Approved = true
        Assert.Null(callback1Result.OutputDirectory);
        Assert.False(callback2Called); // Second callback not called yet
        Assert.Equal(1, queue.PendingCount);
        Assert.Equal("conf_2", queue.Current?.ConfirmationId);
    }

    [Fact]
    public void ApproveCurrent_WithDecision_PassesOutputDirectory()
    {
        var queue = new ConfirmationQueue();
        ConfirmationDecision? received = null;

        var item = new PendingConfirmationItem(
            "conf_1", "rec_1", new { source = "test" },
            result => { received = result; },
            60);

        queue.Enqueue(item);

        var decision = ConfirmationDecision.Approve("D:\\Custom", true);
        var approved = queue.ApproveCurrent(decision);

        Assert.True(approved);
        Assert.NotNull(received);
        Assert.True(received!.Approved);
        Assert.Equal("D:\\Custom", received.OutputDirectory);
        Assert.True(received.RememberOutputDirectory);
    }

    [Fact]
    public void RejectCurrent_InvokesCallbackAndAdvances()
    {
        var queue = new ConfirmationQueue();
        var callbackCalled = false;
        ConfirmationDecision? callbackResult = null;

        var item1 = new PendingConfirmationItem(
            "conf_1", "rec_1", new { source = "test1" },
            result => { callbackCalled = true; callbackResult = result; },
            60);

        queue.Enqueue(item1);

        var rejected = queue.RejectCurrent();

        Assert.True(rejected);
        Assert.True(callbackCalled);
        Assert.NotNull(callbackResult);
        Assert.False(callbackResult!.Approved); // Rejected = false
        Assert.Equal(0, queue.PendingCount);
        Assert.Null(queue.Current);
    }

    [Fact]
    public void SameItem_MultipleApproveReject_CallbackOnlyCalledOnce()
    {
        var queue = new ConfirmationQueue();
        var callbackCount = 0;

        var item = new PendingConfirmationItem(
            "conf_1", "rec_1", new { source = "test" },
            _ => callbackCount++,
            60);

        queue.Enqueue(item);

        // First approve should work
        var first = queue.ApproveCurrent();
        Assert.True(first);
        Assert.Equal(1, callbackCount);

        // Second approve should NOT work (callback already called)
        var second = queue.ApproveCurrent();
        Assert.False(second); // No current item anymore
        Assert.Equal(1, callbackCount); // Still 1, not 2

        // Same for reject
        var third = queue.RejectCurrent();
        Assert.False(third);
        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public void Clear_LeavesQueueEmptyAndNoCurrent()
    {
        var queue = new ConfirmationQueue();

        var item1 = new PendingConfirmationItem(
            "conf_1", "rec_1", new { source = "test1" },
            _ => { },
            60);

        var item2 = new PendingConfirmationItem(
            "conf_2", "rec_2", new { source = "test2" },
            _ => { },
            60);

        queue.Enqueue(item1);
        queue.Enqueue(item2);

        Assert.Equal(2, queue.PendingCount);

        queue.Clear(invokeCallbacks: false);

        Assert.Equal(0, queue.PendingCount);
        Assert.Null(queue.Current);
    }

    [Fact]
    public void Clear_WithInvokeCallbacks_InvokesAllUncalledCallbacks()
    {
        var queue = new ConfirmationQueue();
        var callbackCount = 0;
        var rejectedCount = 0;

        var item1 = new PendingConfirmationItem(
            "conf_1", "rec_1", new { source = "test1" },
            d => { callbackCount++; if (!d.Approved) rejectedCount++; },
            60);

        var item2 = new PendingConfirmationItem(
            "conf_2", "rec_2", new { source = "test2" },
            d => { callbackCount++; if (!d.Approved) rejectedCount++; },
            60);

        queue.Enqueue(item1);
        queue.Enqueue(item2);

        queue.Clear(invokeCallbacks: true);

        Assert.Equal(0, queue.PendingCount);
        Assert.Null(queue.Current);
        Assert.Equal(2, callbackCount); // Both callbacks invoked
        Assert.Equal(2, rejectedCount); // Both rejected
    }

    [Fact]
    public void GetAllItems_ReturnsSnapshotCopy()
    {
        var queue = new ConfirmationQueue();

        var item1 = new PendingConfirmationItem(
            "conf_1", "rec_1", new { source = "test1" },
            _ => { },
            60);

        var item2 = new PendingConfirmationItem(
            "conf_2", "rec_2", new { source = "test2" },
            _ => { },
            60);

        queue.Enqueue(item1);
        queue.Enqueue(item2);

        var snapshot = queue.GetAllItems();

        Assert.Equal(2, snapshot.Count);
        Assert.Equal("conf_1", snapshot[0].ConfirmationId);
        Assert.Equal("conf_2", snapshot[1].ConfirmationId);

        // Modify queue after snapshot
        queue.ApproveCurrent();

        // Snapshot should still have 2 items
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(1, queue.PendingCount);
    }

    [Fact]
    public void PendingConfirmationItem_InvokeCallback_OnlyOnce()
    {
        var callbackCount = 0;
        var item = new PendingConfirmationItem(
            "conf_1", "rec_1", new { source = "test" },
            _ => callbackCount++,
            60);

        // First invoke
        var first = item.InvokeCallback(ConfirmationDecision.Approve());
        Assert.True(first);
        Assert.Equal(1, callbackCount);

        // Second invoke should not work
        var second = item.InvokeCallback(ConfirmationDecision.Reject());
        Assert.False(second);
        Assert.Equal(1, callbackCount); // Still 1

        Assert.True(item.CallbackCalled);
    }

    [Fact]
    public async Task ApproveCurrent_CallbackNotExecutedUnderLock()
    {
        var queue = new ConfirmationQueue();
        var callbackStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCompleted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool readSucceeded = false;

        var item = new PendingConfirmationItem(
            "conf_1", "rec_1", new { source = "test" },
            _ =>
            {
                callbackStarted.TrySetResult(null);
                Thread.Sleep(200);
                callbackCompleted.TrySetResult(null);
            },
            60);

        queue.Enqueue(item);

        var approveTask = Task.Run(() => queue.ApproveCurrent());

        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var readTask = Task.Run(() =>
        {
            var count = queue.PendingCount;
            readSucceeded = true;
        });

        var completedRead = await Task.WhenAny(readTask, Task.Delay(500));
        Assert.Same(readTask, completedRead);
        await readTask;
        Assert.True(readSucceeded, "Read operation succeeded");

        await callbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(await approveTask.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Clear_WithInvokeCallbacks_DoesNotHoldLockWhileInvokingCallbacks()
    {
        var queue = new ConfirmationQueue();
        var callbackStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCompleted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool readSucceeded = false;

        var item = new PendingConfirmationItem(
            "conf_1", "rec_1", new { source = "test" },
            _ =>
            {
                callbackStarted.TrySetResult(null);
                Thread.Sleep(200);
                callbackCompleted.TrySetResult(null);
            },
            60);

        queue.Enqueue(item);

        var clearTask = Task.Run(() => queue.Clear(invokeCallbacks: true));

        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var readTask = Task.Run(() =>
        {
            var count = queue.PendingCount;
            readSucceeded = true;
        });

        var completedRead = await Task.WhenAny(readTask, Task.Delay(500));
        Assert.Same(readTask, completedRead);
        await readTask;
        Assert.True(readSucceeded, "Read operation succeeded");

        await callbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await clearTask.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
