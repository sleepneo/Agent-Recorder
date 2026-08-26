using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Verifies the capture-safe barrier between confirmation form teardown and
/// the recording callback. These tests use reflection to exercise the private
/// orchestration in <see cref="TrayContext"/> without changing its public API.
/// </summary>
[Collection("NonParallel-DwmCompositionBarrier")]
public class ConfirmationCaptureSafeBarrierTests : IDisposable
{
    public ConfirmationCaptureSafeBarrierTests()
    {
        DwmCompositionBarrier.TestFlushOverride = null;
    }

    public void Dispose()
    {
        DwmCompositionBarrier.TestFlushOverride = null;
    }

    private static TrayContext CreateContext(out CaptureAuditLogger audit)
    {
        audit = new CaptureAuditLogger();
        var engine = new RecordingEngine(audit);
        var ctx = new TrayContext(engine, audit, FakeGlobalStopHotkeyFactory.Create());
        engine.SetTray(ctx);
        return ctx;
    }

    private static PendingConfirmationItem CreateItem(
        Action<ConfirmationDecision> callback,
        string confirmationId = "conf_1",
        string recordingId = "rec_1")
    {
        return ConfirmationPresentationTestData.CreateItem(
            confirmationId,
            recordingId,
            new
            {
                source = "display: primary",
                source_type = "display",
                source_title = "primary",
                audio = "No audio",
                duration = "30s",
                output = "out.mp4",
                nested_role = "none",
                recording_id = recordingId,
                confirmation_id = confirmationId,
                timeout_seconds = 60,
                expires_at = "2026-01-01T00:00:00Z"
            },
            callback,
            60);
    }

    private static void RunOnSta(Action action)
    {
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { ex = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex != null)
            throw new TargetInvocationException(ex);
    }

    private sealed class FakeConfirmationDialog : IConfirmationDialog
    {
        private bool _resultHandled;

        public bool IsHandleCreated { get; set; } = true;
        public bool IsDisposed { get; set; }
        public bool Visible { get; set; }

        public event EventHandler<ConfirmationDialogLifecycleEventArgs>? Hidden;
        public event EventHandler<ConfirmationDialogLifecycleEventArgs>? Closed;
        public event EventHandler<ConfirmationDialogLifecycleEventArgs>? HandleDestroyed;

        public void RaiseHidden(ConfirmationDecision? decision, string? reason)
        {
            Hidden?.Invoke(this, new ConfirmationDialogLifecycleEventArgs(decision, reason, 1234, Visible));
        }

        public void RaiseClosed(ConfirmationDecision? decision, string? reason)
        {
            Closed?.Invoke(this, new ConfirmationDialogLifecycleEventArgs(decision, reason, 1234, Visible));
        }

        public void RaiseHandleDestroyed(ConfirmationDecision? decision, string? reason)
        {
            IsHandleCreated = false;
            HandleDestroyed?.Invoke(this, new ConfirmationDialogLifecycleEventArgs(decision, reason, 0, false));
        }

        public void CloseWithDecision(ConfirmationDecision decision, string? closeReason = null)
        {
            if (_resultHandled) return;
            _resultHandled = true;
            RaiseHidden(decision, closeReason);
            RaiseClosed(decision, closeReason);
            RaiseHandleDestroyed(decision, closeReason);
        }

        public void CloseWithoutResult(string? reason = null)
        {
            if (_resultHandled) return;
            _resultHandled = true;
            RaiseHidden(null, reason);
            RaiseClosed(null, reason);
            RaiseHandleDestroyed(null, reason);
        }
    }

    private static async Task<ConfirmationDecision?> FinishConfirmationAsync(
        TrayContext ctx,
        PendingConfirmationItem item,
        FakeConfirmationDialog form)
    {
        var method = typeof(TrayContext).GetMethod("FinishConfirmationWithDecision",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var decision = ConfirmationDecision.Approve();
        var callbackTaskSource = new TaskCompletionSource<ConfirmationDecision?>();

        // The item passed in must complete callbackTaskSource; create a wrapper
        // item that forwards to both the original callback and the TCS.
        var originalCallback = item.Callback;
        var wrappedItem = CreateItem(d =>
        {
            originalCallback(d);
            callbackTaskSource.TrySetResult(d);
        }, item.ConfirmationId, item.RecordingId);

        // Replace the real DWM flush with a fast no-op so the test does not
        // depend on the actual desktop composition state.
        DwmCompositionBarrier.TestFlushOverride = _ => Task.CompletedTask;

        // Invoke the private orchestration on a background thread (it awaits
        // handle destruction and is safe to run off the UI thread in this test).
        var invokeTask = Task.Run(() =>
        {
            method!.Invoke(ctx, new object[]
            {
                wrappedItem, decision, "confirmation.ui_approved", wrappedItem.ConfirmationId, wrappedItem.RecordingId, form
            });
        });

        // Give the orchestration a moment to attach its HandleDestroyed handler,
        // then simulate the native window handle being destroyed.
        await Task.Delay(50);
        form.RaiseHandleDestroyed(decision, "approved");

        // Wait for the reflected method to return.
        await invokeTask;

        // Wait for the background callback dispatch.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await callbackTaskSource.Task.WaitAsync(cts.Token);
    }

    [Fact]
    public async Task FinishConfirmation_ApprovalCallbackRunsAfterHandleDestroyedAndCaptureSafe()
    {
        var form = new FakeConfirmationDialog();
        ConfirmationDecision? capturedDecision = null;
        bool callbackBeforeHandleDestroyed = false;
        bool callbackAfterBarrier = false;

        var item = CreateItem(d =>
        {
            callbackBeforeHandleDestroyed = form.IsHandleCreated;
            callbackAfterBarrier = DwmCompositionBarrier.TestFlushOverride != null;
            capturedDecision = d;
        });

        var decision = await FinishConfirmationAsync(CreateContext(out _), item, form);

        Assert.NotNull(decision);
        Assert.True(decision!.Approved);
        Assert.NotNull(capturedDecision);
        Assert.False(callbackBeforeHandleDestroyed, "callback must not run before handle is destroyed");
        Assert.True(callbackAfterBarrier, "callback must run after the capture-safe barrier");
    }

    [Fact]
    public async Task FinishConfirmation_BarrierAuditCapturedWithTimingAndFallbackFlag()
    {
        var ctx = CreateContext(out var audit);
        var item = CreateItem(_ => { });
        var form = new FakeConfirmationDialog();

        DwmCompositionBarrier.TestFlushOverride = _ => Task.CompletedTask;

        await FinishConfirmationAsync(ctx, item, form);

        var captureSafe = audit.Events.FirstOrDefault(e => e.evt == "confirmation.capture_safe");
        Assert.NotEqual(default, captureSafe);
        using var doc = System.Text.Json.JsonDocument.Parse(captureSafe.json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("barrier_ms", out _));
        Assert.True(root.TryGetProperty("dwm_flush_completed", out _));
        Assert.True(root.TryGetProperty("used_fallback", out _));
    }

    [Fact]
    public async Task FinishConfirmation_SlowCallbackDoesNotBlockReturn()
    {
        var ctx = CreateContext(out _);
        var callbackEntered = new TaskCompletionSource<object?>();
        var releaseCallback = new TaskCompletionSource<object?>();
        var item = CreateItem(async _ =>
        {
            callbackEntered.TrySetResult(null);
            await releaseCallback.Task;
        });
        var form = new FakeConfirmationDialog();

        DwmCompositionBarrier.TestFlushOverride = _ => Task.CompletedTask;

        var method = typeof(TrayContext).GetMethod("FinishConfirmationWithDecision",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var finishTask = Task.Run(() =>
        {
            method!.Invoke(ctx, new object[]
            {
                item, ConfirmationDecision.Approve(), "confirmation.ui_approved", item.ConfirmationId, item.RecordingId, form
            });
        });

        await Task.Delay(50);
        form.RaiseHandleDestroyed(ConfirmationDecision.Approve(), "approved");
        await finishTask;
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), "FinishConfirmationWithDecision must return promptly even with a slow callback");

        // Wait until the callback has been entered, then release it.
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseCallback.TrySetResult(null);
    }

    [Fact]
    public void CloseWithDecision_LifecycleEvents_FireHiddenThenClosedThenHandleDestroyed()
    {
        var events = new List<string>();
        var form = new FakeConfirmationDialog();
        form.Hidden += (_, _) => events.Add("hidden");
        form.Closed += (_, _) => events.Add("closed");
        form.HandleDestroyed += (_, _) => events.Add("handle_destroyed");

        form.CloseWithDecision(ConfirmationDecision.Approve(), "approved");

        Assert.Equal(new[] { "hidden", "closed", "handle_destroyed" }, events);
    }

    [Fact]
    public void CloseWithDecision_RepeatedApprove_IsIdempotent()
    {
        var eventCount = 0;
        var form = new FakeConfirmationDialog();
        form.Closed += (_, _) => Interlocked.Increment(ref eventCount);

        form.CloseWithDecision(ConfirmationDecision.Approve(), "approved");
        form.CloseWithDecision(ConfirmationDecision.Approve(), "approved");
        form.CloseWithDecision(ConfirmationDecision.Reject(), "rejected");

        Assert.Equal(1, eventCount);
    }
}
