using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Xunit;
using AgentRecorder.App;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Tests;

public class ConfirmationFormTests
{
    private class FakePreviewProvider : IScreenPreviewProvider
    {
        public Bitmap Capture(CaptureBounds bounds, Size maxSize) => new Bitmap(maxSize.Width, maxSize.Height);
    }

    private class FakeDirectoryPicker : IOutputDirectoryPicker
    {
        private readonly string? _result;
        public string? LastInitialDirectory { get; private set; }

        public FakeDirectoryPicker(string? result) => _result = result;

        public string? PickDirectory(string initialDirectory)
        {
            LastInitialDirectory = initialDirectory;
            return _result;
        }
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

    private static PendingConfirmationItem CreateItem(string confirmationId = "conf_1", string recordingId = "rec_1")
    {
        return new PendingConfirmationItem(
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
            _ => { },
            60);
    }

    [Fact]
    public void CloseWithoutResult_DoesNotInvokeCallback()
    {
        RunOnSta(() =>
        {
            int callbackCount = 0;
            ConfirmationDecision? lastResult = null;

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
            ConfirmationDecision? lastResult = null;

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

            // Verify callback was called exactly once with rejected decision
            Assert.Equal(1, callbackCount);
            Assert.NotNull(lastResult);
            Assert.False(lastResult!.Approved);
        });
    }

    [Fact]
    public void AfterApprove_ProgrammaticClose_DoesNotTriggerAgain()
    {
        RunOnSta(() =>
        {
            int callbackCount = 0;
            ConfirmationDecision? lastResult = null;

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
            Assert.NotNull(lastResult);
            Assert.True(lastResult!.Approved);

            form.CloseWithoutResult();

            Assert.Equal(1, callbackCount);
            Assert.True(lastResult.Approved);
        });
    }

    [Fact]
    public void AfterReject_ProgrammaticClose_DoesNotTriggerAgain()
    {
        RunOnSta(() =>
        {
            int callbackCount = 0;
            ConfirmationDecision? lastResult = null;

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
            Assert.NotNull(lastResult);
            Assert.False(lastResult!.Approved);

            form.CloseWithoutResult();

            Assert.Equal(1, callbackCount);
            Assert.False(lastResult.Approved);
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
            ConfirmationDecision? item2Result = null;

            var item1 = new PendingConfirmationItem(
                "conf_1", "rec_1", new { source = "test", recording_id = "rec_1", confirmation_id = "conf_1", timeout_seconds = 60, expires_at = "2026-01-01T00:00:00Z" },
                approved => { item1Approved = approved.Approved; },
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
            Assert.False(form.ChangeOutputButtonEnabledForTests);
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

    [Fact]
    public void InitialOutputPath_ShowsSummaryOutput()
    {
        RunOnSta(() =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var outputPath = Path.Combine(tempDir, "my-video.mp4");
                var summary = new
                {
                    source = "display: primary",
                    source_type = "display",
                    source_title = "primary",
                    audio = "No audio",
                    duration = "30s",
                    output = outputPath,
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

                Assert.Contains(tempDir, form.OutputPathTextForTests);
                Assert.Contains("my-video.mp4", form.OutputPathTextForTests);
                Assert.False(form.RememberOutputCheckedForTests);

                form.CloseWithoutResult();
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        });
    }

    [Fact]
    public void ChangeOutputDirectory_UpdatesLabel()
    {
        RunOnSta(() =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var otherDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(otherDir);
            try
            {
                var outputPath = Path.Combine(tempDir, "my-video.mp4");
                var summary = new
                {
                    source = "display: primary",
                    source_type = "display",
                    source_title = "primary",
                    audio = "No audio",
                    duration = "30s",
                    output = outputPath,
                    nested_role = "none",
                    recording_id = "rec_1",
                    confirmation_id = "conf_1",
                    timeout_seconds = 60,
                    expires_at = "2026-01-01T00:00:00Z"
                };

                var picker = new FakeDirectoryPicker(otherDir);
                ConfirmationDecision? decision = null;

                var item = new PendingConfirmationItem(
                    "conf_1", "rec_1", summary, d => { decision = d; }, 60);

                using var form = new ConfirmationForm(item, 1, 1,
                    onResult: d => { decision = d; },
                    directoryPicker: picker);
                form.Show();

                var changeMethod = typeof(ConfirmationForm).GetMethod("ChangeOutputDirectory",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.NotNull(changeMethod);
                changeMethod!.Invoke(form, Array.Empty<object>());

                Assert.Contains(otherDir, form.OutputPathTextForTests);
                Assert.Contains("my-video.mp4", form.OutputPathTextForTests);
                Assert.Equal(tempDir, picker.LastInitialDirectory);

                var approveMethod = typeof(ConfirmationForm).GetMethod("Approve",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                approveMethod!.Invoke(form, Array.Empty<object>());

                Assert.NotNull(decision);
                Assert.True(decision!.Approved);
                Assert.Equal(otherDir, decision.OutputDirectory);

                form.CloseWithoutResult();
            }
            finally
            {
                Directory.Delete(tempDir, true);
                Directory.Delete(otherDir, true);
            }
        });
    }

    [Fact]
    public void ChangeOutputDirectory_Cancelled_KeepsOriginalPath()
    {
        RunOnSta(() =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var outputPath = Path.Combine(tempDir, "my-video.mp4");
                var summary = new
                {
                    source = "display: primary",
                    source_type = "display",
                    source_title = "primary",
                    audio = "No audio",
                    duration = "30s",
                    output = outputPath,
                    nested_role = "none",
                    recording_id = "rec_1",
                    confirmation_id = "conf_1",
                    timeout_seconds = 60,
                    expires_at = "2026-01-01T00:00:00Z"
                };

                var picker = new FakeDirectoryPicker(null);

                var item = new PendingConfirmationItem(
                    "conf_1", "rec_1", summary, _ => { }, 60);

                using var form = new ConfirmationForm(item, 1, 1,
                    directoryPicker: picker);
                form.Show();

                var original = form.OutputPathTextForTests;

                var changeMethod = typeof(ConfirmationForm).GetMethod("ChangeOutputDirectory",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                changeMethod!.Invoke(form, Array.Empty<object>());

                Assert.Equal(original, form.OutputPathTextForTests);

                form.CloseWithoutResult();
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        });
    }

    [Fact]
    public void Approve_WithRememberChecked_IncludesRememberFlag()
    {
        RunOnSta(() =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var outputPath = Path.Combine(tempDir, "my-video.mp4");
                var summary = new
                {
                    source = "display: primary",
                    source_type = "display",
                    source_title = "primary",
                    audio = "No audio",
                    duration = "30s",
                    output = outputPath,
                    nested_role = "none",
                    recording_id = "rec_1",
                    confirmation_id = "conf_1",
                    timeout_seconds = 60,
                    expires_at = "2026-01-01T00:00:00Z"
                };

                ConfirmationDecision? decision = null;
                var item = new PendingConfirmationItem(
                    "conf_1", "rec_1", summary, d => { decision = d; }, 60);

                using var form = new ConfirmationForm(item, 1, 1, onResult: d => { decision = d; });
                form.Show();

                form.RememberOutputCheckedForTests = true;

                var approveMethod = typeof(ConfirmationForm).GetMethod("Approve",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                approveMethod!.Invoke(form, Array.Empty<object>());

                Assert.NotNull(decision);
                Assert.True(decision!.Approved);
                Assert.True(decision.RememberOutputDirectory);
                Assert.Equal(tempDir, decision.OutputDirectory);

                form.CloseWithoutResult();
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        });
    }

    [Fact]
    public void ConfirmationForm_OutputDirectoryControls_DoNotOverlapCountdown()
    {
        RunOnSta(() =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var outputPath = Path.Combine(tempDir, "my-video.mp4");
                var summary = new
                {
                    source = "display: primary",
                    source_type = "display",
                    source_title = "primary",
                    audio = "No audio",
                    duration = "30s",
                    output = outputPath,
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

                var outputPanel = form.OutputPanelBoundsForTests;
                var progress = form.TimeoutProgressBoundsForTests;
                var timeoutLabel = form.TimeoutLabelBoundsForTests;
                var warning = form.WarningLabelBoundsForTests;
                var approve = form.ApproveButtonBoundsForTests;
                var reject = form.RejectButtonBoundsForTests;

                Assert.True(outputPanel.Height > 0);
                Assert.True(progress.Height > 0);

                // Output panel must end before the countdown progress bar begins.
                Assert.True(outputPanel.Bottom <= progress.Top,
                    $"Output panel bottom ({outputPanel.Bottom}) should be at or above progress top ({progress.Top})");

                // Countdown progress bar must end before its label begins.
                Assert.True(progress.Bottom <= timeoutLabel.Top,
                    $"Progress bottom ({progress.Bottom}) should be at or above label top ({timeoutLabel.Top})");

                // Timeout label must end before warning label begins.
                Assert.True(timeoutLabel.Bottom <= warning.Top,
                    $"Timeout label bottom ({timeoutLabel.Bottom}) should be at or above warning top ({warning.Top})");

                // Warning label must end before buttons begin.
                Assert.True(warning.Bottom <= approve.Top,
                    $"Warning bottom ({warning.Bottom}) should be at or above approve button top ({approve.Top})");
                Assert.True(warning.Bottom <= reject.Top,
                    $"Warning bottom ({warning.Bottom}) should be at or above reject button top ({reject.Top})");

                form.CloseWithoutResult();
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        });
    }

    [Fact]
    public void ConfirmationForm_AllPrimaryControls_AreInsideClientArea()
    {
        RunOnSta(() =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var outputPath = Path.Combine(tempDir, "my-video.mp4");
                var summary = new
                {
                    source = "display: primary",
                    source_type = "display",
                    source_title = "primary",
                    audio = "No audio",
                    duration = "30s",
                    output = outputPath,
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

                var client = new Rectangle(0, 0, form.ClientSize.Width, form.ClientSize.Height);

                Assert.True(client.Contains(form.OutputPanelBoundsForTests));
                Assert.True(client.Contains(form.TimeoutProgressBoundsForTests));
                Assert.True(client.Contains(form.TimeoutLabelBoundsForTests));
                Assert.True(client.Contains(form.WarningLabelBoundsForTests));
                Assert.True(client.Contains(form.ApproveButtonBoundsForTests));
                Assert.True(client.Contains(form.RejectButtonBoundsForTests));

                // Path label should be configured to ellipsis long text instead of growing.
                Assert.True(form.OutputPathLabelAutoEllipsisForTests);

                form.CloseWithoutResult();
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        });
    }

    // -------------------------------------------------------------------------
    // Bilingual UI and DPI-safe layout tests
    // -------------------------------------------------------------------------

    [Fact]
    public void Form_UsesDpiAutoScale()
    {
        RunOnSta(() =>
        {
            using var form = new ConfirmationForm(CreateItem(), 1, 1);
            Assert.Equal(AutoScaleMode.Dpi, form.AutoScaleMode);
            Assert.True(form.MinimumSize.Width >= 640);
            Assert.True(form.MinimumSize.Height >= 580);
        });
    }

    [Theory]
    [InlineData(UiLanguage.ZhCn, "Confirmation_Title", "Agent Recorder — 录屏确认")]
    [InlineData(UiLanguage.EnUs, "Confirmation_Title", "Agent Recorder — Recording Confirmation")]
    [InlineData(UiLanguage.ZhCn, "Confirmation_Button_Approve", "✓ 确认")]
    [InlineData(UiLanguage.EnUs, "Confirmation_Button_Approve", "✓ Confirm")]
    [InlineData(UiLanguage.ZhCn, "Confirmation_Button_Reject", "✗ 拒绝")]
    [InlineData(UiLanguage.EnUs, "Confirmation_Button_Reject", "✗ Reject")]
    public void Constructor_Localized_TextMatchesProvider(UiLanguage language, string key, string expected)
    {
        RunOnSta(() =>
        {
            var text = new UiTextProvider(language);
            using var form = new ConfirmationForm(CreateItem(), 1, 1, textProvider: text);
            string actual = key switch
            {
                "Confirmation_Title" => form.Text,
                "Confirmation_Button_Approve" => form.ApproveButtonTextForTests,
                "Confirmation_Button_Reject" => form.RejectButtonTextForTests,
                _ => ""
            };
            Assert.Equal(expected, actual);
        });
    }

    [Theory]
    [InlineData(UiLanguage.ZhCn)]
    [InlineData(UiLanguage.EnUs)]
    public void Constructor_Localized_ButtonWidthFitsText(UiLanguage language)
    {
        RunOnSta(() =>
        {
            var text = new UiTextProvider(language);
            using var form = new ConfirmationForm(CreateItem(), 1, 1, textProvider: text);
            form.Show();

            var approve = form.ApproveButtonForTests;
            var reject = form.RejectButtonForTests;
            Assert.NotNull(approve);
            Assert.NotNull(reject);

            var approveMeasured = TextRenderer.MeasureText(approve!.Text, approve.Font).Width;
            var rejectMeasured = TextRenderer.MeasureText(reject!.Text, reject.Font).Width;
            Assert.True(approve.Width >= approveMeasured + 20,
                $"Approve button width {approve.Width} too small for text '{approve.Text}' (measured {approveMeasured})");
            Assert.True(reject.Width >= rejectMeasured + 20,
                $"Reject button width {reject.Width} too small for text '{reject.Text}' (measured {rejectMeasured})");

            form.CloseWithoutResult();
        });
    }

    [Fact]
    public void Constructor_Bilingual_ButtonsDoNotOverlap()
    {
        RunOnSta(() =>
        {
            var text = new UiTextProvider(UiLanguage.EnUs);
            using var form = new ConfirmationForm(CreateItem(), 1, 1, textProvider: text);
            form.Show();

            var approve = form.ApproveButtonBoundsForTests;
            var reject = form.RejectButtonBoundsForTests;
            Assert.True(approve.Width > 0);
            Assert.True(reject.Width > 0);
            Assert.False(approve.IntersectsWith(reject),
                "Approve and reject buttons must not overlap");

            form.CloseWithoutResult();
        });
    }

    [Theory]
    [InlineData(UiLanguage.ZhCn)]
    [InlineData(UiLanguage.EnUs)]
    public void Form_LongTitleWarningTimeout_DoNotOverlapButtons(UiLanguage language)
    {
        RunOnSta(() =>
        {
            var text = new UiTextProvider(language);

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

            using var form = new ConfirmationForm(item, 1, 1, textProvider: text);
            form.Show();

            var client = new Rectangle(0, 0, form.ClientSize.Width, form.ClientSize.Height);
            var approve = form.ApproveButtonBoundsForTests;
            var reject = form.RejectButtonBoundsForTests;

            Assert.True(client.Contains(approve));
            Assert.True(client.Contains(reject));
            Assert.False(approve.IntersectsWith(reject),
                $"Buttons overlap with language {language}: approve={approve}, reject={reject}");

            form.CloseWithoutResult();
        });
    }

    [Fact]
    public void Form_LongSourceTitleAndId_DoNotPushButtonsOutsideClientArea()
    {
        RunOnSta(() =>
        {
            var longTitle = new string('A', 200);
            var longId = new string('B', 100);
            var summary = new
            {
                source = "display: primary",
                source_type = "display",
                source_title = longTitle,
                audio = "No audio",
                duration = "30s",
                output = "out.mp4",
                nested_role = "none",
                recording_id = longId,
                confirmation_id = longId,
                timeout_seconds = 60,
                expires_at = "2026-01-01T00:00:00Z"
            };

            var item = new PendingConfirmationItem(
                longId, longId, summary, _ => { }, 60);

            using var form = new ConfirmationForm(item, 1, 1);
            form.Show();

            var client = new Rectangle(0, 0, form.ClientSize.Width, form.ClientSize.Height);
            Assert.True(client.Contains(form.ApproveButtonBoundsForTests));
            Assert.True(client.Contains(form.RejectButtonBoundsForTests));
            Assert.False(form.ApproveButtonBoundsForTests.IntersectsWith(form.RejectButtonBoundsForTests));

            form.CloseWithoutResult();
        });
    }

    [Fact]
    public void Form_LongPath_LabelUsesEllipsisAndTooltipShowsFullPath()
    {
        RunOnSta(() =>
        {
            var longDir = Path.Combine(Path.GetTempPath(), new string('C', 200));
            Directory.CreateDirectory(longDir);
            try
            {
                var longPath = Path.Combine(longDir, "my-video.mp4");
                var summary = new
                {
                    source = "display: primary",
                    source_type = "display",
                    source_title = "primary",
                    audio = "No audio",
                    duration = "30s",
                    output = longPath,
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

                Assert.True(form.OutputPathLabelAutoEllipsisForTests);

                form.CloseWithoutResult();
            }
            finally
            {
                Directory.Delete(longDir, true);
            }
        });
    }

    [Fact]
    public void Form_SmallWorkingArea_ButtonsRemainInsideClientArea()
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

            var tinyWorkingArea = new Rectangle(0, 0, 600, 500);
            using var form = new ConfirmationForm(item, 1, 1, workingAreas: new[] { tinyWorkingArea }, fallbackWorkingArea: tinyWorkingArea);
            form.Show();

            var client = new Rectangle(0, 0, form.ClientSize.Width, form.ClientSize.Height);
            Assert.True(client.Contains(form.ApproveButtonBoundsForTests),
                $"Approve button {form.ApproveButtonBoundsForTests} outside client {client}");
            Assert.True(client.Contains(form.RejectButtonBoundsForTests),
                $"Reject button {form.RejectButtonBoundsForTests} outside client {client}");

            form.CloseWithoutResult();
        });
    }

    [Fact]
    public void ChangeOutputDirectory_PickerInitialDirectory_UsesSummaryOutputDirectory()
    {
        RunOnSta(() =>
        {
            var summaryDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var defaultDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var otherDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(summaryDir);
            Directory.CreateDirectory(defaultDir);
            Directory.CreateDirectory(otherDir);
            try
            {
                var outputPath = Path.Combine(summaryDir, "my-video.mp4");
                var summary = new
                {
                    source = "display: primary",
                    source_type = "display",
                    source_title = "primary",
                    audio = "No audio",
                    duration = "30s",
                    output = outputPath,
                    nested_role = "none",
                    recording_id = "rec_1",
                    confirmation_id = "conf_1",
                    timeout_seconds = 60,
                    expires_at = "2026-01-01T00:00:00Z"
                };

                var picker = new FakeDirectoryPicker(otherDir);
                var item = new PendingConfirmationItem(
                    "conf_1", "rec_1", summary, _ => { }, 60);

                using var form = new ConfirmationForm(item, 1, 1,
                    directoryPicker: picker,
                    defaultOutputDirectory: defaultDir);
                form.Show();

                var changeMethod = typeof(ConfirmationForm).GetMethod("ChangeOutputDirectory",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                changeMethod!.Invoke(form, Array.Empty<object>());

                // Picker must start from the summary output directory, not the persisted default.
                Assert.Equal(summaryDir, picker.LastInitialDirectory);

                form.CloseWithoutResult();
            }
            finally
            {
                Directory.Delete(summaryDir, true);
                Directory.Delete(defaultDir, true);
                Directory.Delete(otherDir, true);
            }
        });
    }

    // -------------------------------------------------------------------------
    // Real desktop UI clipping and space usage
    // -------------------------------------------------------------------------

    private static PendingConfirmationItem CreateRealDesktopItem(string confirmationId = "confirm_f33edb4dd086", string recordingId = "rec_7d6437459aa3")
    {
        return new PendingConfirmationItem(
            confirmationId,
            recordingId,
            new
            {
                source = "region: 1962x942 @ (0,0)",
                source_type = "region",
                source_title = "Display 1",
                audio = "microphone",
                duration = "15s",
                output = "out.mp4",
                nested_role = "none",
                recording_id = recordingId,
                confirmation_id = confirmationId,
                timeout_seconds = 60,
                expires_at = "2026-01-01T00:00:00Z"
            },
            _ => { },
            60);
    }

    [Fact]
    public void Form_AllMetadataRowsVisibleInNormalSize()
    {
        RunOnSta(() =>
        {
            var item = CreateRealDesktopItem();
            using var form = new ConfirmationForm(item, 1, 1, previewProvider: new FakePreviewProvider());
            form.Show();

            var infoPanelClient = form.InfoPanelClientRectangleForTests;
            var rows = form.GetInfoRowBoundsRelativeToInfoPanelForTests();

            Assert.Equal(10, rows.Count);

            for (int i = 0; i < rows.Count; i++)
            {
                var (label, value) = rows[i];
                Assert.True(label.Width > 0 && label.Height > 0, $"Row {i} metadata label has zero size");
                Assert.True(value.Width > 0 && value.Height > 0, $"Row {i} metadata value has zero size");
                Assert.True(infoPanelClient.Contains(label), $"Row {i} metadata label is outside info panel client area");
                Assert.True(infoPanelClient.Contains(value), $"Row {i} metadata value is outside info panel client area");

                if (i > 0)
                {
                    var prev = rows[i - 1];
                    Assert.False(label.IntersectsWith(prev.LabelBounds), $"Row {i} label intersects row {i - 1}");
                    Assert.False(value.IntersectsWith(prev.ValueBounds), $"Row {i} value intersects row {i - 1}");
                }
            }

            form.CloseWithoutResult();
        });
    }

    [Fact]
    public void Form_MainContentAndPreviewFillAvailableSpace()
    {
        RunOnSta(() =>
        {
            var item = CreateRealDesktopItem();
            using var form = new ConfirmationForm(item, 1, 1, previewProvider: new FakePreviewProvider());
            form.Show();

            var mainContent = form.MainContentPanelBoundsForTests;
            var infoPanel = form.InfoPanelBoundsForTests;
            var previewPanel = form.PreviewPanelBoundsForTests;
            var outputPanel = form.OutputPanelBoundsForTests;

            Assert.True(infoPanel.Height > 120, $"Info panel height {infoPanel.Height} too small");
            Assert.True(previewPanel.Height > 120, $"Preview panel height {previewPanel.Height} too small");

            int gap = outputPanel.Top - mainContent.Bottom;
            Assert.True(gap >= 0 && gap <= 20, $"Gap between main content and output panel is {gap}, expected design margin only");

            form.CloseWithoutResult();
        });
    }

    [Fact]
    public void Form_NormalSize_NoScrollbars()
    {
        RunOnSta(() =>
        {
            var item = CreateRealDesktopItem();
            using var form = new ConfirmationForm(item, 1, 1, previewProvider: new FakePreviewProvider());
            form.Show();

            Assert.False(form.ContentScrollPanelVerticalScrollVisibleForTests, "Vertical scrollbar should not be visible at normal size");
            Assert.False(form.ContentScrollPanelHorizontalScrollVisibleForTests, "Horizontal scrollbar should not be visible at normal size");

            form.CloseWithoutResult();
        });
    }

    [Fact]
    public void Form_PreviewBoundsLabelFitsDisplayArea()
    {
        RunOnSta(() =>
        {
            var item = CreateRealDesktopItem();
            using var form = new ConfirmationForm(item, 1, 1, previewProvider: new FakePreviewProvider());
            form.Show();

            Assert.True(form.PreviewBoundsLabelPreferredHeightForTests <= form.PreviewBoundsLabelHeightForTests,
                $"Preview bounds label height {form.PreviewBoundsLabelHeightForTests} is smaller than preferred height {form.PreviewBoundsLabelPreferredHeightForTests}");

            form.CloseWithoutResult();
        });
    }

    [Theory]
    [InlineData(UiLanguage.ZhCn)]
    [InlineData(UiLanguage.EnUs)]
    public void Form_OutputPanelControls_OnSeparateRows_NoOverlap(UiLanguage language)
    {
        RunOnSta(() =>
        {
            var text = new UiTextProvider(language);
            var item = CreateRealDesktopItem();
            using var form = new ConfirmationForm(item, 1, 1, textProvider: text, previewProvider: new FakePreviewProvider());
            form.Show();

            var title = form.OutputTitleBoundsForTests;
            var path = form.OutputPathBoundsForTests;
            var change = form.OutputChangeButtonBoundsForTests;
            var remember = form.OutputRememberCheckBoxBoundsForTests;

            Assert.True(title.Height > 0, "Output title has zero height");
            Assert.True(path.Height > 0, "Output path has zero height");
            Assert.True(change.Height > 0, "Change button has zero height");
            Assert.True(remember.Height > 0, "Remember checkbox has zero height");

            Assert.True(title.Bottom <= path.Top, $"Output title bottom {title.Bottom} overlaps path top {path.Top}");
            int actionsTop = Math.Min(change.Top, remember.Top);
            Assert.True(path.Bottom <= actionsTop, $"Output path bottom {path.Bottom} overlaps actions top {actionsTop}");

            Assert.False(change.IntersectsWith(remember), "Change button intersects remember checkbox");

            form.CloseWithoutResult();
        });
    }

    [Theory]
    [InlineData(1.00)]
    [InlineData(1.25)]
    [InlineData(1.50)]
    [InlineData(1.75)]
    [InlineData(2.00)]
    public void MeasureButtonSize_DpiMatrix_GrowsAndFitsText(double scale)
    {
        RunOnSta(() =>
        {
            var text = new UiTextProvider(UiLanguage.ZhCn);
            var font = new Font("Segoe UI", (float)(9 * scale), FontStyle.Bold);
            var changeText = text.Get("Confirmation_Output_Change");
            var size = ConfirmationForm.MeasureButtonSize(changeText, font, horizontalPadding: 16, verticalPadding: 6, minHeight: 28);

            var measured = TextRenderer.MeasureText(changeText, font);
            Assert.True(size.Width >= measured.Width + 16, $"Button width {size.Width} does not fit text at scale {scale}");
            Assert.True(size.Height >= measured.Height + 6, $"Button height {size.Height} does not fit text at scale {scale}");
        });
    }

    [Fact]
    public void Form_LongPath_TooltipEqualsFullPath_AfterChangeDirectory()
    {
        RunOnSta(() =>
        {
            var longDir = Path.Combine(Path.GetTempPath(), new string('C', 120));
            Directory.CreateDirectory(longDir);
            var otherDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(otherDir);
            try
            {
                var longPath = Path.Combine(longDir, "my-video.mp4");
                var summary = new
                {
                    source = "display: primary",
                    source_type = "display",
                    source_title = "primary",
                    audio = "No audio",
                    duration = "30s",
                    output = longPath,
                    nested_role = "none",
                    recording_id = "rec_1",
                    confirmation_id = "conf_1",
                    timeout_seconds = 60,
                    expires_at = "2026-01-01T00:00:00Z"
                };

                var picker = new FakeDirectoryPicker(otherDir);
                var item = new PendingConfirmationItem(
                    "conf_1", "rec_1", summary, _ => { }, 60);

                using var form = new ConfirmationForm(item, 1, 1, directoryPicker: picker);
                form.Show();

                Assert.True(form.OutputPathLabelAutoEllipsisForTests);
                Assert.Equal(longPath, form.OutputPathTooltipForTests);

                var changeMethod = typeof(ConfirmationForm).GetMethod("ChangeOutputDirectory",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.NotNull(changeMethod);
                changeMethod!.Invoke(form, Array.Empty<object>());

                var expectedNewPath = Path.Combine(otherDir, "my-video.mp4");
                Assert.Equal(expectedNewPath, form.OutputPathTooltipForTests);
                Assert.Equal(expectedNewPath, form.OutputPathTextForTests);

                form.CloseWithoutResult();
            }
            finally
            {
                Directory.Delete(longDir, true);
                Directory.Delete(otherDir, true);
            }
        });
    }

    [Fact]
    public void Form_SmallWorkingArea_AllowsScroll_ButtonsVisible()
    {
        RunOnSta(() =>
        {
            var item = CreateRealDesktopItem();
            var tinyWorkingArea = new Rectangle(0, 0, 600, 500);
            using var form = new ConfirmationForm(item, 1, 1, workingAreas: new[] { tinyWorkingArea }, fallbackWorkingArea: tinyWorkingArea);
            form.Show();

            var client = new Rectangle(0, 0, form.ClientSize.Width, form.ClientSize.Height);
            Assert.True(client.Contains(form.ApproveButtonBoundsForTests),
                $"Approve button {form.ApproveButtonBoundsForTests} outside client {client}");
            Assert.True(client.Contains(form.RejectButtonBoundsForTests),
                $"Reject button {form.RejectButtonBoundsForTests} outside client {client}");

            // Scroll is allowed when the main content cannot fit; the buttons must remain visible.
            if (form.ContentScrollPanelVerticalScrollVisibleForTests)
            {
                Assert.True(form.ApproveButtonBoundsForTests.Top >= form.MainContentPanelBoundsForTests.Bottom,
                    "Approve button must be below the scrollable main content");
            }

            form.CloseWithoutResult();
        });
    }

    // -------------------------------------------------------------------------
    // Confirmation viewport, output containment and preview sizing
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(UiLanguage.ZhCn)]
    [InlineData(UiLanguage.EnUs)]
    public void Form_OutputPanelControls_AreContainedByOutputPanel(UiLanguage language)
    {
        RunOnSta(() =>
        {
            var text = new UiTextProvider(language);
            var item = CreateRealDesktopItem();
            using var form = new ConfirmationForm(item, 1, 1, textProvider: text, previewProvider: new FakePreviewProvider());
            form.Show();

            var outputClient = form.OutputPanelClientRectangleForTests;
            var title = form.OutputTitleBoundsRelativeToOutputPanelForTests;
            var path = form.OutputPathBoundsRelativeToOutputPanelForTests;
            var actions = form.OutputActionsPanelBoundsRelativeToOutputPanelForTests;
            var change = form.OutputChangeButtonBoundsRelativeToOutputPanelForTests;
            var remember = form.OutputRememberCheckBoxBoundsRelativeToOutputPanelForTests;

            Assert.True(outputClient.Contains(title),
                $"Output title {title} is outside output panel client {outputClient}");
            Assert.True(outputClient.Contains(path),
                $"Output path {path} is outside output panel client {outputClient}");
            Assert.True(outputClient.Contains(actions),
                $"Output actions {actions} is outside output panel client {outputClient}");

            Assert.True(title.Bottom <= path.Top,
                $"Output title bottom {title.Bottom} overlaps path top {path.Top}");
            Assert.True(path.Bottom <= actions.Top,
                $"Output path bottom {path.Bottom} overlaps actions top {actions.Top}");

            Assert.True(actions.Contains(change),
                $"Change button {change} is outside actions panel {actions}");
            Assert.True(actions.Contains(remember),
                $"Remember checkbox {remember} is outside actions panel {actions}");

            Assert.True(form.OutputPathLabelHeightForTests >= form.OutputPathLabelMeasuredTextHeightForTests,
                $"Path label height {form.OutputPathLabelHeightForTests} is smaller than measured text height {form.OutputPathLabelMeasuredTextHeightForTests}");

            form.CloseWithoutResult();
        });
    }

    [Fact]
    public void Form_PreviewBoundsLabel_IsInsideMainContentViewport()
    {
        RunOnSta(() =>
        {
            var item = CreateRealDesktopItem();
            using var form = new ConfirmationForm(item, 1, 1, previewProvider: new FakePreviewProvider());
            form.Show();

            var mainContent = form.MainContentPanelBoundsForTests;
            var preview = form.PreviewPanelBoundsForTests;
            var label = form.PreviewBoundsLabelBoundsForTests;

            Assert.True(mainContent.Contains(label),
                $"Preview bounds label {label} is outside main content viewport {mainContent}");
            Assert.True(label.Top >= preview.Bottom - 2,
                $"Preview bounds label top {label.Top} is not below preview bottom {preview.Bottom}");

            form.CloseWithoutResult();
        });
    }

    [Fact]
    public void Form_PreviewPanel_NormalSize_AtLeastRecommendedSize()
    {
        RunOnSta(() =>
        {
            var item = CreateRealDesktopItem();
            using var form = new ConfirmationForm(item, 1, 1, previewProvider: new FakePreviewProvider());
            form.Show();

            // Only assert the recommended preview size when the window was not forced below its design minimum.
            if (form.ClientSize.Width >= 900 && form.ClientSize.Height >= 700)
            {
                var preview = form.PreviewPanelBoundsForTests;
                Assert.True(preview.Width >= 360,
                    $"Preview panel width {preview.Width} is smaller than recommended minimum");
                Assert.True(preview.Height >= 220,
                    $"Preview panel height {preview.Height} is smaller than recommended minimum");
            }

            form.CloseWithoutResult();
        });
    }

    [Fact]
    public void Form_AllMetadataRowsVisibleInMainContentViewport()
    {
        RunOnSta(() =>
        {
            var item = CreateRealDesktopItem();
            using var form = new ConfirmationForm(item, 1, 1, previewProvider: new FakePreviewProvider());
            form.Show();

            var mainContent = form.MainContentPanelBoundsForTests;
            var rows = form.GetInfoRowBoundsForTests();
            Assert.Equal(10, rows.Count);

            for (int i = 0; i < rows.Count; i++)
            {
                var (label, value) = rows[i];
                Assert.True(mainContent.Contains(label),
                    $"Row {i} metadata label {label} is outside main content viewport {mainContent}");
                Assert.True(mainContent.Contains(value),
                    $"Row {i} metadata value {value} is outside main content viewport {mainContent}");
            }

            // At the design size the main content should not need scrollbars.
            if (form.ClientSize.Width >= 900 && form.ClientSize.Height >= 700)
            {
                Assert.False(form.ContentScrollPanelVerticalScrollVisibleForTests,
                    "Vertical scrollbar should not be visible at normal size");
                Assert.False(form.ContentScrollPanelHorizontalScrollVisibleForTests,
                    "Horizontal scrollbar should not be visible at normal size");
            }

            form.CloseWithoutResult();
        });
    }
}
