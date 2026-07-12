using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

public class ConfirmationFormForegroundAndBoundsTests
{
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

    private static PendingConfirmationItem CreateItem(string confirmationId = "conf_1", string recordingId = "rec_1")
    {
        return new PendingConfirmationItem(
            confirmationId,
            recordingId,
            new
            {
                source = "region: test",
                capture_bounds = new { x = 100, y = 200, width = 1280, height = 720 },
                source_type = "region",
                source_title = "test",
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
    public void OnShown_AttemptsTopMostAndForeground()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var fake = new FakeWindowActivator();
            var item = CreateItem();

            using var form = new ConfirmationForm(item, 1, 1,
                windowActivator: fake,
                auditLogger: audit.Log)
            {
                EnableDelayedForegroundVerification = false
            };

            form.Show();
            Application.DoEvents();

            Assert.Single(fake.TopMostCalls);
            Assert.Single(fake.ForegroundCalls);
            Assert.Empty(fake.BringToTopCalls);

            Assert.Contains(audit.Events, e => e.evt == "confirmation.form_created");
            Assert.Contains(audit.Events, e => e.evt == "confirmation.form_shown");
            Assert.Contains(audit.Events, e => e.evt == "confirmation.foreground_attempt");
            Assert.Contains(audit.Events, e => e.evt == "confirmation.foreground_result");

            form.CloseWithoutResult();
        });
    }

    [Fact]
    public void ForegroundDenied_FallsBackToBringToTop()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var fake = new FakeWindowActivator
            {
                SetForegroundResult = false,
                BringToTopResult = true
            };
            var item = CreateItem();

            using var form = new ConfirmationForm(item, 1, 1,
                windowActivator: fake,
                auditLogger: audit.Log)
            {
                EnableDelayedForegroundVerification = false
            };

            form.Show();
            Application.DoEvents();

            Assert.Single(fake.TopMostCalls);
            Assert.Single(fake.ForegroundCalls);
            Assert.Single(fake.BringToTopCalls);

            var result = audit.Events.First(e => e.evt == "confirmation.foreground_result");
            using var doc = JsonDocument.Parse(result.json);
            Assert.False(doc.RootElement.GetProperty("set_foreground_window_success").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("bring_window_to_top_success").GetBoolean());

            var approved = audit.Events.Where(e => e.evt == "confirmation.ui_approved").ToList();
            Assert.Empty(approved);

            form.CloseWithoutResult();
        });
    }

    [Fact]
    public void DelayedVerification_RunsAtMostOnce()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var fake = new FakeWindowActivator
            {
                SetForegroundResult = false,
                BringToTopResult = false,
                ForegroundWindow = (IntPtr)0x1234
            };
            var item = CreateItem();

            using var form = new ConfirmationForm(item, 1, 1,
                windowActivator: fake,
                auditLogger: audit.Log)
            {
                EnableDelayedForegroundVerification = false
            };

            form.Show();
            Application.DoEvents();
            Assert.Equal(1, form.ForegroundAttemptsForTest);

            form.RunForegroundVerificationForTest();
            Assert.Equal(2, form.ForegroundAttemptsForTest);

            form.RunForegroundVerificationForTest();
            Assert.Equal(2, form.ForegroundAttemptsForTest);

            Assert.Equal(2, audit.Events.Count(e => e.evt == "confirmation.foreground_result"));

            form.CloseWithoutResult();
            Application.DoEvents();

            Assert.False(form.ForegroundVerificationTimerEnabledForTests);
        });
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("queue_advanced")]
    public void ClosePath_StopsForegroundTimerAndLogsReason(string closePath)
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var fake = new FakeWindowActivator();
            var item = CreateItem("conf_close", "rec_close");
            var callbackCount = 0;

            using var form = new ConfirmationForm(item, 1, 1,
                onResult: _ => callbackCount++,
                windowActivator: fake,
                auditLogger: audit.Log)
            {
                EnableDelayedForegroundVerification = false
            };

            form.Show();
            Application.DoEvents();
            Assert.True(form.ForegroundAttemptsForTest > 0);

            switch (closePath)
            {
                case "approved":
                    var approveMethod = typeof(ConfirmationForm).GetMethod("Approve",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    approveMethod!.Invoke(form, Array.Empty<object>());
                    break;
                case "rejected":
                    var rejectMethod = typeof(ConfirmationForm).GetMethod("Reject",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    rejectMethod!.Invoke(form, Array.Empty<object>());
                    break;
                case "queue_advanced":
                    form.CloseWithoutResult();
                    break;
            }

            Application.DoEvents();

            Assert.False(form.ForegroundVerificationTimerEnabledForTests);

            var closedEvent = audit.Events.LastOrDefault(e => e.evt == "confirmation.form_closed");
            Assert.NotEqual(default, closedEvent);
            using var doc = JsonDocument.Parse(closedEvent.json);
            Assert.Equal(closePath, doc.RootElement.GetProperty("close_reason").GetString());
            Assert.Equal("conf_close", doc.RootElement.GetProperty("confirmation_id").GetString());
            Assert.Equal("rec_close", doc.RootElement.GetProperty("recording_id").GetString());
            Assert.True(doc.RootElement.GetProperty("form_handle").GetInt64() > 0);
        });
    }

    [Fact]
    public void ComputeConfirmationBounds_SelectedRegionOnSecondaryScreen_CentersAndClamps()
    {
        var primary = new Rectangle(0, 0, 1920, 1080);
        var secondary = new Rectangle(1920, 0, 1920, 1080);
        var workingAreas = new[] { primary, secondary };
        var formSize = new Size(620, 580);
        var capture = new Rectangle(2500, 300, 800, 600);

        var computed = ConfirmationForm.ComputeConfirmationBounds(capture, formSize, workingAreas, primary);

        Assert.Equal(1, computed.ScreenIndex);
        Assert.Equal(secondary, computed.WorkingArea);
        Assert.Equal(secondary.X + (secondary.Width - formSize.Width) / 2, computed.Bounds.X);
        Assert.Equal(secondary.Y + (secondary.Height - formSize.Height) / 2, computed.Bounds.Y);
        Assert.True(computed.Bounds.Left >= secondary.Left);
        Assert.True(computed.Bounds.Right <= secondary.Right);
        Assert.True(computed.Bounds.Top >= secondary.Top);
        Assert.True(computed.Bounds.Bottom <= secondary.Bottom);
    }

    [Fact]
    public void ComputeConfirmationBounds_NegativeCoordinateScreen_StaysInsideWorkingArea()
    {
        var negativeScreen = new Rectangle(-1920, 0, 1920, 1080);
        var primary = new Rectangle(0, 0, 1920, 1080);
        var workingAreas = new[] { negativeScreen, primary };
        var formSize = new Size(620, 580);
        var capture = new Rectangle(-1500, 200, 800, 600);

        var computed = ConfirmationForm.ComputeConfirmationBounds(capture, formSize, workingAreas, primary);

        Assert.Equal(0, computed.ScreenIndex);
        Assert.Equal(negativeScreen, computed.WorkingArea);
        Assert.True(computed.Bounds.Left >= negativeScreen.Left);
        Assert.True(computed.Bounds.Right <= negativeScreen.Right);
        Assert.True(computed.Bounds.Top >= negativeScreen.Top);
        Assert.True(computed.Bounds.Bottom <= negativeScreen.Bottom);
    }

    [Theory]
    [InlineData(1.00, 0, 0, 1366, 728)]
    [InlineData(1.00, 0, 0, 1920, 1040)]
    [InlineData(1.00, -1920, 0, 1920, 1040)]
    [InlineData(1.25, 0, 0, 1366, 728)]
    [InlineData(1.25, 0, 0, 1920, 1040)]
    [InlineData(1.25, -1920, 0, 1920, 1040)]
    [InlineData(1.50, 0, 0, 1366, 728)]
    [InlineData(1.50, 0, 0, 1920, 1040)]
    [InlineData(1.50, -1920, 0, 1920, 1040)]
    [InlineData(1.75, 0, 0, 1366, 728)]
    [InlineData(1.75, 0, 0, 1920, 1040)]
    [InlineData(1.75, -1920, 0, 1920, 1040)]
    [InlineData(2.00, 0, 0, 1366, 728)]
    [InlineData(2.00, 0, 0, 1920, 1040)]
    [InlineData(2.00, -1920, 0, 1920, 1040)]
    public void ComputeConfirmationBounds_DpiWorkspaceMatrix_FitsInsideWorkingArea(double scale, int x, int y, int w, int h)
    {
        // Design-time Form.Size (including non-client area) at 96 DPI.
        var designFormSize = new Size(776, 719);
        var formSize = new Size(
            (int)(designFormSize.Width * scale),
            (int)(designFormSize.Height * scale));

        var workingArea = new Rectangle(x, y, w, h);
        var workingAreas = new[] { workingArea };

        var computed = ConfirmationForm.ComputeConfirmationBounds(
            null,
            formSize,
            workingAreas,
            workingArea);

        Assert.True(computed.Bounds.Left >= workingArea.Left,
            $"scale={scale}, workingArea={workingArea}: left overflow {computed.Bounds.Left}");
        Assert.True(computed.Bounds.Top >= workingArea.Top,
            $"scale={scale}, workingArea={workingArea}: top overflow {computed.Bounds.Top}");
        Assert.True(computed.Bounds.Right <= workingArea.Right,
            $"scale={scale}, workingArea={workingArea}: right overflow {computed.Bounds.Right}");
        Assert.True(computed.Bounds.Bottom <= workingArea.Bottom,
            $"scale={scale}, workingArea={workingArea}: bottom overflow {computed.Bounds.Bottom}");

        if (formSize.Width > workingArea.Width || formSize.Height > workingArea.Height)
        {
            Assert.True(computed.Bounds.Width < formSize.Width || computed.Bounds.Height < formSize.Height,
                $"scale={scale}, workingArea={workingArea}: expected shrink but got {computed.Bounds.Size}");
        }
    }

    [Fact]
    public void ComputeConfirmationBounds_OversizedOrCrossScreenTarget_UsesDeterministicFallback()
    {
        var primary = new Rectangle(0, 0, 1920, 1080);
        var secondary = new Rectangle(1920, 0, 1920, 1080);
        var workingAreas = new[] { primary, secondary };
        var formSize = new Size(620, 580);

        // No capture bounds -> fallback working area must be used.
        var computed = ConfirmationForm.ComputeConfirmationBounds(null, formSize, workingAreas, secondary);
        Assert.Equal(-1, computed.ScreenIndex);
        Assert.Equal(secondary, computed.WorkingArea);
        Assert.Equal(secondary.X + (secondary.Width - formSize.Width) / 2, computed.Bounds.X);

        // Cross-screen rectangle whose center is in the secondary display.
        var crossScreen = new Rectangle(1500, 100, 1000, 800);
        var cross = ConfirmationForm.ComputeConfirmationBounds(crossScreen, formSize, workingAreas, primary);
        Assert.Equal(1, cross.ScreenIndex);
        Assert.True(cross.Bounds.Left >= secondary.Left);
        Assert.True(cross.Bounds.Right <= secondary.Right);
    }

    [Fact]
    public void AuditEvents_IncludeIdHandleBoundsAndCloseReason()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var fake = new FakeWindowActivator();
            var item = CreateItem("conf_audit", "rec_audit");

            using var form = new ConfirmationForm(item, 1, 1,
                windowActivator: fake,
                auditLogger: audit.Log)
            {
                EnableDelayedForegroundVerification = false
            };

            form.Show();
            Application.DoEvents();
            form.CloseWithoutResult("app_exit");
            Application.DoEvents();

            var created = audit.Events.First(e => e.evt == "confirmation.form_created");
            var shown = audit.Events.First(e => e.evt == "confirmation.form_shown");
            var foreground = audit.Events.First(e => e.evt == "confirmation.foreground_result");
            var closed = audit.Events.Last(e => e.evt == "confirmation.form_closed");

            using (var doc = JsonDocument.Parse(created.json))
            {
                Assert.Equal("conf_audit", doc.RootElement.GetProperty("confirmation_id").GetString());
                Assert.Equal("rec_audit", doc.RootElement.GetProperty("recording_id").GetString());
            }

            using (var doc = JsonDocument.Parse(shown.json))
            {
                Assert.True(doc.RootElement.GetProperty("form_handle").GetInt64() > 0);
                Assert.True(doc.RootElement.GetProperty("visible").GetBoolean());
                Assert.True(doc.RootElement.GetProperty("topmost").GetBoolean());
                Assert.True(doc.RootElement.TryGetProperty("bounds", out _));
            }

            using (var doc = JsonDocument.Parse(foreground.json))
            {
                Assert.True(doc.RootElement.TryGetProperty("bounds", out _));
                Assert.True(doc.RootElement.TryGetProperty("target_screen_index", out _));
                Assert.True(doc.RootElement.TryGetProperty("target_working_area", out _));
                Assert.True(doc.RootElement.TryGetProperty("foreground_before", out _));
                Assert.True(doc.RootElement.TryGetProperty("foreground_after", out _));
                Assert.True(doc.RootElement.TryGetProperty("became_foreground", out _));
            }

            using (var doc = JsonDocument.Parse(closed.json))
            {
                Assert.Equal("app_exit", doc.RootElement.GetProperty("close_reason").GetString());
                Assert.Equal("conf_audit", doc.RootElement.GetProperty("confirmation_id").GetString());
                Assert.Equal("rec_audit", doc.RootElement.GetProperty("recording_id").GetString());
                Assert.True(doc.RootElement.GetProperty("form_handle").GetInt64() > 0);
            }
        });
    }

    private sealed class FakeWindowActivator : IWindowActivator
    {
        public List<IntPtr> TopMostCalls { get; } = new();
        public List<IntPtr> ForegroundCalls { get; } = new();
        public List<IntPtr> BringToTopCalls { get; } = new();

        public bool SetTopMostResult { get; set; } = true;
        public bool SetForegroundResult { get; set; } = true;
        public bool BringToTopResult { get; set; } = true;
        public IntPtr ForegroundWindow { get; set; } = IntPtr.Zero;

        public bool SetTopMost(IntPtr hWnd)
        {
            TopMostCalls.Add(hWnd);
            return SetTopMostResult;
        }

        public bool SetForeground(IntPtr hWnd)
        {
            ForegroundCalls.Add(hWnd);
            return SetForegroundResult;
        }

        public bool BringToTop(IntPtr hWnd)
        {
            BringToTopCalls.Add(hWnd);
            return BringToTopResult;
        }

        public IntPtr GetForegroundWindow() => ForegroundWindow;
    }
}
