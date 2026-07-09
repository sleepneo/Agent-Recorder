using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Xunit;
using AgentRecorder.App;

namespace AgentRecorder.Tests;

public class ConfirmationFormTests
{
    private class FakePreviewProvider : IScreenPreviewProvider
    {
        public Bitmap Capture(CaptureBounds bounds, Size maxSize) => new Bitmap(maxSize.Width, maxSize.Height);
    }
    private static T RunOnSta<T>(Func<T> func)
    {
        T result = default!;
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception e)
            {
                ex = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex != null)
            throw new System.Reflection.TargetInvocationException(ex);
        return result;
    }

    private static void RunOnSta(Action action)
    {
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                ex = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex != null)
            throw new System.Reflection.TargetInvocationException(ex);
    }

    [Fact]
    public void CloseWithoutResult_DoesNotInvokeCallback()
    {
        RunOnSta(() =>
        {
            int callbackCount = 0;
            bool? lastResult = null;

            var item = new PendingConfirmationItem(
                "conf_1", "rec_1", new { source = "test", recording_id = "rec_1", confirmation_id = "conf_1", timeout_seconds = 60, expires_at = "2026-01-01T00:00:00Z" },
                _ => { },
                60);

            using var form = new ConfirmationForm(item, 1, 1,
                r => { callbackCount++; lastResult = r; });

            form.CloseWithoutResult();

            Assert.Equal(0, callbackCount);
            Assert.Null(lastResult);
        });
    }

    [Fact]
    public void UserCloseX_FormClosingTriggersReject()
    {
        RunOnSta(() =>
        {
            int callbackCount = 0;
            bool? lastResult = null;

            var item = new PendingConfirmationItem(
                "conf_1", "rec_1", new { source = "test", recording_id = "rec_1", confirmation_id = "conf_1", timeout_seconds = 60, expires_at = "2026-01-01T00:00:00Z" },
                _ => { },
                60);

            using var form = new ConfirmationForm(item, 1, 1,
                r => { callbackCount++; lastResult = r; });
            form.Show();

            // Simulate user clicking X by calling form.Close()
            // This triggers FormClosing event, which will call Reject() if result not handled
            form.Close();

            // Verify callback was called exactly once with false (rejected)
            Assert.Equal(1, callbackCount);
            Assert.False(lastResult);
        });
    }

    [Fact]
    public void AfterApprove_ProgrammaticClose_DoesNotTriggerAgain()
    {
        RunOnSta(() =>
        {
            int callbackCount = 0;
            bool? lastResult = null;

            var item = new PendingConfirmationItem(
                "conf_1", "rec_1", new { source = "test", recording_id = "rec_1", confirmation_id = "conf_1", timeout_seconds = 60, expires_at = "2026-01-01T00:00:00Z" },
                _ => { },
                60);

            using var form = new ConfirmationForm(item, 1, 1,
                r => { callbackCount++; lastResult = r; });

            form.Show();

            var method = typeof(ConfirmationForm).GetMethod("Approve",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);
            method!.Invoke(form, Array.Empty<object>());

            Assert.Equal(1, callbackCount);
            Assert.True(lastResult);

            form.CloseWithoutResult();

            Assert.Equal(1, callbackCount);
            Assert.True(lastResult);
        });
    }

    [Fact]
    public void AfterReject_ProgrammaticClose_DoesNotTriggerAgain()
    {
        RunOnSta(() =>
        {
            int callbackCount = 0;
            bool? lastResult = null;

            var item = new PendingConfirmationItem(
                "conf_1", "rec_1", new { source = "test", recording_id = "rec_1", confirmation_id = "conf_1", timeout_seconds = 60, expires_at = "2026-01-01T00:00:00Z" },
                _ => { },
                60);

            using var form = new ConfirmationForm(item, 1, 1,
                r => { callbackCount++; lastResult = r; });

            form.Show();

            var method = typeof(ConfirmationForm).GetMethod("Reject",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);
            method!.Invoke(form, Array.Empty<object>());

            Assert.Equal(1, callbackCount);
            Assert.False(lastResult);

            form.CloseWithoutResult();

            Assert.Equal(1, callbackCount);
            Assert.False(lastResult);
        });
    }



    [Fact]
    public void QueueAdvance_CloseWithoutResult_DoesNotRejectNext()
    {
        // This tests the scenario:
        // 1. Approve current item from tray menu
        // 2. HideConfirmationForm calls CloseWithoutResult
        // 3. Show next item - the next item should NOT be rejected

        RunOnSta(() =>
        {
            var queue = new ConfirmationQueue();
            var item1Approved = false;
            var item2Called = false;
            bool? item2Result = null;

            var item1 = new PendingConfirmationItem(
                "conf_1", "rec_1", new { source = "test", recording_id = "rec_1", confirmation_id = "conf_1", timeout_seconds = 60, expires_at = "2026-01-01T00:00:00Z" },
                approved => { item1Approved = approved; },
                60);

            var item2 = new PendingConfirmationItem(
                "conf_2", "rec_2", new { source = "test2", recording_id = "rec_2", confirmation_id = "conf_2", timeout_seconds = 60, expires_at = "2026-01-01T00:00:00Z" },
                approved => { item2Called = true; item2Result = approved; },
                60);

            queue.Enqueue(item1);
            queue.Enqueue(item2);

            // Show form for item1
            using var form1 = new ConfirmationForm(item1, 1, 2);
            form1.Show();

            // Approve from tray menu
            queue.ApproveCurrent();
            Assert.True(item1Approved);

            // HideConfirmationForm calls CloseWithoutResult - this is the fix
            form1.CloseWithoutResult();

            // Now show form for item2 (next in queue)
            Assert.Equal(1, queue.PendingCount);
            Assert.Equal("conf_2", queue.Current?.ConfirmationId);

            // Item2 should NOT have been called yet
            Assert.False(item2Called);
            Assert.Null(item2Result);
        });
    }

    [Fact]
    public void Form_WithCaptureBounds_ShowsPreviewImageAndBounds()
    {
        RunOnSta(() =>
        {
            var summary = new
            {
                source = "region: test",
                capture_bounds = new { x = 100, y = 200, width = 1280, height = 720 },
                coordinate_space = "virtual_screen",
                source_type = "region",
                source_title = "test",
                audio = "No audio",
                duration = "30s",
                output = "out.mp4",
                nested_role = "none",
                recording_id = "rec_1",
                confirmation_id = "conf_1",
                timeout_seconds = 60,
                expires_at = "2026-01-01T00:00:00Z"
            };

            var item = new PendingConfirmationItem(
                "conf_1", "rec_1", summary, _ => { }, 60);

            using var form = new ConfirmationForm(item, 1, 1, null, new FakePreviewProvider());
            form.Show();

            Assert.True(form.HasPreviewAreaForTests);
            Assert.True(form.HasPreviewImageForTests);
            Assert.Contains("X=100", form.PreviewBoundsTextForTests);
            Assert.Contains("Y=200", form.PreviewBoundsTextForTests);
            Assert.Contains("W=1280", form.PreviewBoundsTextForTests);
            Assert.Contains("H=720", form.PreviewBoundsTextForTests);

            form.CloseWithoutResult();
        });
    }

    [Fact]
    public void Form_WithMalformedCaptureBounds_ShowsFallbackAndKeepsButtons()
    {
        RunOnSta(() =>
        {
            var summary = new
            {
                source = "region: test",
                capture_bounds = new { x = "bad", y = 200, width = 1280, height = 720 },
                source_type = "region",
                source_title = "test",
                audio = "No audio",
                duration = "30s",
                output = "out.mp4",
                nested_role = "none",
                recording_id = "rec_1",
                confirmation_id = "conf_1",
                timeout_seconds = 60,
                expires_at = "2026-01-01T00:00:00Z"
            };

            var item = new PendingConfirmationItem(
                "conf_1", "rec_1", summary, _ => { }, 60);

            using var form = new ConfirmationForm(item, 1, 1, null, new FakePreviewProvider());
            form.Show();

            Assert.True(form.HasPreviewAreaForTests);
            Assert.False(form.HasPreviewImageForTests);
            Assert.Contains("无法生成预览", form.PreviewFallbackTextForTests);
            Assert.NotNull(form.AcceptButton);
            Assert.NotNull(form.CancelButton);

            form.CloseWithoutResult();
        });
    }

    [Fact]
    public void Form_WithoutCaptureBounds_ShowsFallback()
    {
        RunOnSta(() =>
        {
            var summary = new
            {
                source = "display: primary",
                source_type = "display",
                source_title = "primary",
                audio = "No audio",
                duration = "30s",
                output = "out.mp4",
                nested_role = "none",
                recording_id = "rec_1",
                confirmation_id = "conf_1",
                timeout_seconds = 60,
                expires_at = "2026-01-01T00:00:00Z"
            };

            var item = new PendingConfirmationItem(
                "conf_1", "rec_1", summary, _ => { }, 60);

            using var form = new ConfirmationForm(item, 1, 1);
            form.Show();

            Assert.True(form.HasPreviewAreaForTests);
            Assert.False(form.HasPreviewImageForTests);
            Assert.Contains("无法生成预览", form.PreviewFallbackTextForTests);

            form.CloseWithoutResult();
        });
    }

    [Fact]
    public void DefaultFocus_IsRejectButton()
    {
        RunOnSta(() =>
        {
            var item = new PendingConfirmationItem(
                "conf_1", "rec_1", new { source = "test", recording_id = "rec_1", confirmation_id = "conf_1", timeout_seconds = 60, expires_at = "2026-01-01T00:00:00Z" },
                _ => { },
                60);

            using var form = new ConfirmationForm(item, 1, 1);
            form.Show();

            Assert.NotNull(form.DefaultActionForTests);
            Assert.Same(form.DefaultActionForTests, form.CancelActionForTests);
            Assert.Contains("拒绝", form.DefaultActionForTests!.Text);
        });
    }

    [Fact]
    public void AcceptButton_DoesNotApproveByDefault()
    {
        RunOnSta(() =>
        {
            var item = new PendingConfirmationItem(
                "conf_1", "rec_1", new { source = "test", recording_id = "rec_1", confirmation_id = "conf_1", timeout_seconds = 60, expires_at = "2026-01-01T00:00:00Z" },
                _ => { },
                60);

            using var form = new ConfirmationForm(item, 1, 1);
            form.Show();

            // The default AcceptButton must not be the approve button.
            Assert.NotNull(form.DefaultActionForTests);
            Assert.Same(form.DefaultActionForTests, form.CancelActionForTests);
            Assert.DoesNotContain("确认", form.DefaultActionForTests!.Text);
            Assert.Contains("拒绝", form.DefaultActionForTests.Text);
        });
    }

    [Fact]
    public void CancelButton_IsRejectButton()
    {
        RunOnSta(() =>
        {
            var item = new PendingConfirmationItem(
                "conf_1", "rec_1", new { source = "test", recording_id = "rec_1", confirmation_id = "conf_1", timeout_seconds = 60, expires_at = "2026-01-01T00:00:00Z" },
                _ => { },
                60);

            using var form = new ConfirmationForm(item, 1, 1);
            form.Show();

            Assert.NotNull(form.CancelActionForTests);
            Assert.Contains("拒绝", form.CancelActionForTests!.Text);
        });
    }

    [Fact]
    public void Countdown_InitializesFromPendingItemTimeout()
    {
        RunOnSta(() =>
        {
            var item = new PendingConfirmationItem(
                "conf_1", "rec_1", new { source = "test", recording_id = "rec_1", confirmation_id = "conf_1", timeout_seconds = 60, expires_at = "2026-01-01T00:00:00Z" },
                _ => { },
                60);

            using var form = new ConfirmationForm(item, 1, 1);
            form.Show();

            Assert.Contains("剩余", form.TimeoutTextForTests);
            Assert.True(form.TimeoutProgressValueForTests > 0);
            Assert.True(form.CountdownTimerEnabledForTests);

            form.CloseWithoutResult();
        });
    }

    [Fact]
    public void Countdown_Expired_DisablesApproveAndShowsExpired()
    {
        RunOnSta(() =>
        {
            var item = new PendingConfirmationItem(
                "conf_1", "rec_1", new { source = "test", recording_id = "rec_1", confirmation_id = "conf_1", timeout_seconds = 60, expires_at = "2026-01-01T00:00:00Z" },
                _ => { },
                60);

            // Simulate that the confirmation already expired.
            var expiredNow = item.ExpiresAtUtc.AddSeconds(1);
            using var form = new ConfirmationForm(item, 1, 1, null, new FakePreviewProvider(), () => expiredNow);
            form.Show();

            Assert.Equal("确认已过期", form.TimeoutTextForTests);
            Assert.Equal(0, form.TimeoutProgressValueForTests);
            Assert.False(form.ApproveButtonEnabledForTests);
            Assert.False(form.CountdownTimerEnabledForTests);

            form.CloseWithoutResult();
        });
    }

    [Fact]
    public void CloseWithoutResult_DisposesCountdownTimer()
    {
        RunOnSta(() =>
        {
            var item = new PendingConfirmationItem(
                "conf_1", "rec_1", new { source = "test", recording_id = "rec_1", confirmation_id = "conf_1", timeout_seconds = 60, expires_at = "2026-01-01T00:00:00Z" },
                _ => { },
                60);

            using var form = new ConfirmationForm(item, 1, 1);
            form.Show();
            Assert.True(form.CountdownTimerEnabledForTests);

            form.CloseWithoutResult();

            Assert.False(form.CountdownTimerEnabledForTests);
        });
    }
}
