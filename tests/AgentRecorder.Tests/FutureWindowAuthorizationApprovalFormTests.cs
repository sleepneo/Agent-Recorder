using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class FutureWindowAuthorizationApprovalFormTests
{
    private static T RunOnSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "STA UI test did not complete.");
        if (failure is not null)
            throw new InvalidOperationException("STA UI test failed.", failure);
        return result;
    }

    private static FutureWindowAuthorizationApprovalDetails CreateDetails(bool systemAudio = true) => new(
        ExecutablePath: @"C:\Program Files\Video Player\" + new string('P', 180) + @"\player.exe",
        FileIdentity: "A1B2C3D4:000000000000002A",
        Sha256: "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
        PublisherSubject: "CN=Example Publisher, O=Example Ltd",
        PublisherCertificateSha256: "FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210",
        SystemAudioEndpointName: systemAudio ? "Speakers (USB Audio)" : null,
        SystemAudioEndpointId: systemAudio ? "{0.0.0.00000000}.{RenderEndpoint-ID-CaseSensitive}" : null,
        MaximumDurationSeconds: 1800,
        ValiditySeconds: 3600,
        OutputPath: @"D:\Recordings\" + new string('O', 180) + ".mp4",
        UserSid: "S-1-5-21-111-222-333-1001",
        SessionBinding: @"sid=S-1-5-21-111-222-333-1001;session=2;desktop=WinSta0\Default");

    [Theory]
    [InlineData(UiLanguage.ZhCn, "隐私提示", "授权有效期", "最晚允许启动", "批准一次运行", "拒绝")]
    [InlineData(UiLanguage.EnUs, "Privacy warning", "Authorization validity", "Latest permissible start", "Approve one run", "Reject")]
    public void ApprovalForm_LocalizesCompleteConsentAndPreservesIdentityValues(
        UiLanguage language,
        string privacyText,
        string validityText,
        string latestStartText,
        string approveText,
        string rejectText)
    {
        RunOnSta(() =>
        {
            var details = CreateDetails();
            var text = new UiTextProvider(language);
            using var form = new FutureWindowAuthorizationApprovalForm(details, text);

            Assert.Contains(privacyText, form.PrivacyWarningTextForTests, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(validityText, form.ScopeTextForTests, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(latestStartText, form.ScopeTextForTests, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(text.Format("FutureWindowApproval_MaximumDuration", 1800), form.ScopeTextForTests);
            Assert.Contains(text.Format("FutureWindowApproval_Validity", 3600), form.ScopeTextForTests);
            Assert.Contains(text.Get("FutureWindowApproval_TargetDescription"), form.ScopeTextForTests);
            Assert.Contains(text.Get("FutureWindowApproval_NoShortening"), form.ScopeTextForTests);
            Assert.Contains(text.Get("FutureWindowApproval_OutputPath"), form.ScopeTextForTests);
            Assert.Contains(approveText, form.ApproveButtonForTests.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(rejectText, form.RejectButtonForTests.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(text.Get("FutureWindowApproval_NoPreview"), form.ScopeTextForTests);
            Assert.Contains(text.Get("FutureWindowApproval_OneLaterRun"), form.ScopeTextForTests);
            Assert.Contains(text.Get("FutureWindowApproval_LocalRevocation"), form.ScopeTextForTests);
            Assert.Contains(details.ExecutablePath, form.ScopeTextForTests);
            Assert.Contains(details.FileIdentity, form.ScopeTextForTests);
            Assert.Contains(details.Sha256, form.ScopeTextForTests);
            Assert.Contains(details.PublisherSubject!, form.ScopeTextForTests);
            Assert.Contains(details.PublisherCertificateSha256!, form.ScopeTextForTests);
            Assert.Contains(details.SystemAudioEndpointName!, form.ScopeTextForTests);
            Assert.Contains(details.SystemAudioEndpointId!, form.ScopeTextForTests);
            Assert.Contains(details.OutputPath, form.ScopeTextForTests);
            Assert.Contains(details.UserSid, form.ScopeTextForTests);
            Assert.Contains(details.SessionBinding, form.ScopeTextForTests);
            Assert.True(form.AudioWarningVisibleForTests);
            Assert.Contains(language == UiLanguage.ZhCn ? "全部声音" : "all sound", form.AudioWarningTextForTests,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("window-only audio", form.ScopeTextForTests, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(text.Get("FutureWindowApproval_ScopeAccessibleName"), form.AccessibleNameForTests);
            Assert.Equal(text.Get("FutureWindowApproval_AccessibleName"), form.WindowAccessibleNameForTests);
            Assert.True(form.ScopeCanBeInspectedForTests);
            Assert.True(form.SafeDefaultsForTests);
            Assert.Equal(approveText, form.ApproveButtonForTests.Text);
            Assert.Equal(rejectText, form.RejectButtonForTests.Text);
            return true;
        });
    }

    [Fact]
    public void ApprovalForm_AllLocalizedKeysExistForBothLanguages()
    {
        var keys = new[]
        {
            "Title", "AccessibleName", "ScopeAccessibleName", "Heading", "PrivacyWarning", "AudioWarning",
            "ExecutablePath", "FileIdentity", "Sha256", "PublisherEvidence", "PublisherSubject",
            "PublisherCertificateSha256", "Unavailable", "TargetScope", "TargetDescription", "AudioScope",
            "AudioNone", "AudioLoopback", "MaximumDuration", "NoCountdown", "Validity", "LatestStart",
            "NoShortening", "OutputPath", "UserSid", "SessionBinding", "NoPreview", "OneLaterRun",
            "LocalRevocation", "Approve", "Reject", "Keyboard",
        };
        var chinese = new UiTextProvider(UiLanguage.ZhCn);
        var english = new UiTextProvider(UiLanguage.EnUs);

        foreach (var suffix in keys)
        {
            var key = "FutureWindowApproval_" + suffix;
            Assert.NotEqual(key, chinese.Get(key));
            Assert.NotEqual(key, english.Get(key));
            Assert.NotEqual(chinese.Get(key), english.Get(key));
        }
    }

    [Theory]
    [InlineData(UiLanguage.ZhCn, "不可用", "无音频")]
    [InlineData(UiLanguage.EnUs, "not available", "No audio")]
    public void ApprovalForm_LocalizesUnavailableAndNoAudioValues(
        UiLanguage language,
        string unavailable,
        string noAudio)
    {
        RunOnSta(() =>
        {
            var details = CreateDetails(systemAudio: false) with
            {
                PublisherSubject = null,
                PublisherCertificateSha256 = null,
            };
            using var form = new FutureWindowAuthorizationApprovalForm(details, new UiTextProvider(language));

            Assert.Contains(unavailable, form.ScopeTextForTests, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(noAudio, form.ScopeTextForTests, StringComparison.OrdinalIgnoreCase);
            Assert.False(form.AudioWarningVisibleForTests);
            return true;
        });
    }

    [Theory]
    [InlineData(Keys.Enter)]
    [InlineData(Keys.Escape)]
    public void ApprovalForm_EnterAndEscapeRejectEvenWhenApproveHasFocus(Keys key)
    {
        RunOnSta(() =>
        {
            using var form = new FutureWindowAuthorizationApprovalForm(
                CreateDetails(), new UiTextProvider(UiLanguage.EnUs));

            Assert.True(form.SafeDefaultsForTests);
            Assert.True(form.ProcessKeyForTests(key));
            Assert.Equal(DialogResult.No, form.DialogResult);
            return true;
        });
    }

    [Fact]
    public void ApprovalForm_ResizableDpiAwareLayoutKeepsWarningsAndActionsSeparate()
    {
        RunOnSta(() =>
        {
            using var form = new FutureWindowAuthorizationApprovalForm(
                CreateDetails(), new UiTextProvider(UiLanguage.ZhCn));

            Assert.Equal(FormBorderStyle.Sizable, form.FormBorderStyle);
            Assert.Equal(AutoScaleMode.Dpi, form.AutoScaleModeForTests);
            Assert.True(form.ScopeCanBeInspectedForTests);

            // Exercise compact, 150%-sized and 200%-sized client viewports. Actual
            // per-monitor DPI transitions still require supervised desktop validation.
            foreach (var size in new[] { new Size(760, 640), new Size(1140, 960), new Size(1520, 1280) })
            {
                form.ClientSize = size;
                form.PerformLayout();
                Application.DoEvents();
                Assert.True(form.LayoutKeepsWarningsAndActionsSeparateForTests);
                Assert.True(form.ButtonsFitFooterForTests);
            }
            return true;
        });
    }

    [Fact]
    public void ApprovalForm_CancellationBeforeHandleCreationReturnsCancelledWithoutCreatingWindow()
    {
        RunOnSta(() =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var stages = new List<string>();
            var outcome = FutureWindowAuthorizationApprovalForm.ShowModal(
                "fwa_before_handle", CreateDetails(), new UiTextProvider(UiLanguage.EnUs),
                (stage, _) => stages.Add(stage), cancellation.Token);

            Assert.Equal(FutureWindowApprovalOutcome.Cancelled, outcome);
            Assert.Contains("cancelled", stages);
            Assert.DoesNotContain("handle_created", stages);
            return true;
        });
    }

    [Fact]
    public void ApprovalForm_NativeDisplayFailureReturnsUnavailableAndAuditsStableReason()
    {
        RunOnSta(() =>
        {
            var stages = new List<(string Stage, object Payload)>();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var outcome = FutureWindowAuthorizationApprovalForm.ShowModal(
                "fwa_display_failure", CreateDetails(), new UiTextProvider(UiLanguage.EnUs),
                (stage, payload) => stages.Add((stage, payload)), cancellation.Token,
                new HiddenWindowPlatform(), disableApprovalForTest: true);

            Assert.Equal(FutureWindowApprovalOutcome.Unavailable, outcome);
            Assert.Contains(stages, item => item.Stage == "display_failed");
            Assert.Contains(stages, item => item.Stage == "closed");
            return true;
        });
    }

    private sealed class HiddenWindowPlatform : IFutureWindowApprovalWindowPlatform
    {
        public void ShowNormal(nint handle) { }

        public FutureWindowApprovalNativeState Inspect(nint handle) => new(
            IsVisible: false,
            IsMinimized: false,
            IsWithinWorkingArea: true,
            Bounds: new Rectangle(100, 100, 600, 500),
            WorkingArea: new Rectangle(0, 0, 1920, 1080),
            ProcessId: Environment.ProcessId,
            ThreadId: Environment.CurrentManagedThreadId,
            ForegroundWindow: nint.Zero);

        public FutureWindowApprovalForegroundState Activate(nint handle) => new(false, false, false);
    }

}
