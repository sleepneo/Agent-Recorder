using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using AgentRecorder.App;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class StandingApprovalLifecycleTests
{
    internal static StandingLeaseApprovalDetails Details => new("Synthetic display", new(0, 0, 800, 600), new(10, 20, 64, 48),
        DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddMinutes(2), DateTimeOffset.UtcNow.AddMinutes(3),
        TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddMinutes(4), @"C:\Synthetic", "never-created.mp4");
    internal sealed class Events
    {
        internal readonly ConcurrentQueue<(string Name, JsonElement Data)> Items = new();
        internal void Log(string name, object data) => Items.Enqueue((name, JsonSerializer.SerializeToElement(data)));
        internal bool Has(string stage) => Items.Any(i => i.Name.EndsWith("." + stage, StringComparison.Ordinal));
    }
    internal sealed class Preview(bool fail = false, ManualResetEventSlim? release = null) : IScreenPreviewProvider
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Bitmap? Bitmap;
        internal int Calls;
        public Bitmap Capture(ConfirmationCaptureBounds bounds, Size maxSize)
        {
            Interlocked.Increment(ref Calls); Entered.TrySetResult(); release?.Wait();
            if (fail) throw new InvalidOperationException("secret exception text must not be audited");
            return Bitmap = new(32, 16);
        }
    }
    private static StandingLeaseApprovalOperation Start(Events events, StandingLeaseApprovalOptions options, CancellationToken token = default) =>
        new TrayStandingPlanSetupUi(() => new UiTextProvider(UiLanguage.EnUs), options).BeginApproval("synthetic-setup", Details, events.Log, token);

    [Fact]
    public async Task CancelBeforeHandleNeverConstructsOrCaptures()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var events = new Events(); var preview = new Preview();
        var operation = Start(events, new() { PreviewProvider = preview }, cancel.Token);
        Assert.Equal(StandingLeaseApprovalOutcome.Cancelled, (await operation.Decision).Outcome);
        await operation.Retired.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(events.Has("handle_created")); Assert.Equal(0, preview.Calls);
    }

    [Fact]
    public async Task InitializationExceptionIsUnavailableWithoutExceptionBodyOrUserRejection()
    {
        var events = new Events();
        var operation = Start(events, new() { BeforeConstructForTests = () => throw new InvalidOperationException("sensitive-path-token") });
        Assert.Equal(new(StandingLeaseApprovalOutcome.Unavailable, "lease_approval_initialization_failed"), await operation.Decision);
        await operation.Retired.WaitAsync(TimeSpan.FromSeconds(5));
        var text = string.Join("\n", events.Items.Select(i => i.Data.GetRawText()));
        Assert.DoesNotContain("sensitive-path-token", text); Assert.DoesNotContain("lease_rejected_by_user", text);
    }

    [Fact]
    public async Task HandleCreationCancellationCannotShowOrApproveLater()
    {
        using var cancel = new CancellationTokenSource(); var events = new Events(); var preview = new Preview();
        var operation = Start(events, new() { PreviewProvider = preview, BeforeShowForTests = form => { _ = form.Handle; cancel.Cancel(); } }, cancel.Token);
        Assert.Equal(StandingLeaseApprovalOutcome.Cancelled, (await operation.Decision).Outcome);
        await operation.Retired.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(events.Has("handle_created")); Assert.False(events.Has("native_visible")); Assert.Equal(0, preview.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitSyntheticDecisionHasReadableButtonsAndOneOutcome(bool approve)
    {
        var events = new Events(); var readable = false; StandingLeaseApprovalForm? captured = null;
        var operation = Start(events, new() { PreviewProvider = new Preview(), AfterVisibleForTests = form =>
        {
            captured = form; readable = form.ButtonsReadableForTests && form.AcceptButtonIsRejectForTests && form.CancelButtonIsRejectForTests;
            form.DecideForTests(approve);
        } });
        Assert.Equal(approve ? StandingLeaseApprovalOutcome.Approved : StandingLeaseApprovalOutcome.Rejected,
            (await operation.Decision.WaitAsync(TimeSpan.FromSeconds(6))).Outcome);
        await operation.Retired.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(readable); Assert.True(captured!.IsDisposed); Assert.True(captured.PreviewBitmapDisposedForTests);
        Assert.Single(events.Items, item => item.Name.EndsWith(".decision", StringComparison.Ordinal));
        Assert.True(events.Has("retired"));
    }

    [Fact]
    public async Task PermanentlyBlockedPreviewFallsBackButRetainsOwnerUntilRealExitAndDiscardsLateBitmap()
    {
        using var release = new ManualResetEventSlim(); using var cancel = new CancellationTokenSource();
        var events = new Events(); var preview = new Preview(release: release);
        var operation = Start(events, new() { PreviewProvider = preview, PreviewBudget = TimeSpan.FromMilliseconds(80) }, cancel.Token);
        try
        {
            await preview.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntil(() => events.Has("preview_fallback"));
            Assert.True(events.Has("native_visible"));
            cancel.Cancel();
            Assert.Equal(StandingLeaseApprovalOutcome.Cancelled, (await operation.Decision.WaitAsync(TimeSpan.FromSeconds(3))).Outcome);
            Assert.False(operation.Retired.IsCompleted); Assert.False(events.Has("retired"));
        }
        finally { release.Set(); cancel.Cancel(); }
        await operation.Retired.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, preview.Calls); Assert.True(events.Has("retired"));
        Assert.NotNull(preview.Bitmap); Assert.ThrowsAny<Exception>(() => preview.Bitmap.GetPixel(0, 0));
        Assert.DoesNotContain(events.Items, item => item.Data.TryGetProperty("reason_code", out var reason) && reason.GetString() == "user_approved");
    }

    [Fact]
    public async Task FastPreviewTransfersOnlyOnUiThreadAndIsDisposedAfterClose()
    {
        var events = new Events(); var preview = new Preview(); var sawImage = false;
        var operation = Start(events, new() { PreviewProvider = preview, AfterVisibleForTests = form =>
        {
            var timer = new System.Windows.Forms.Timer { Interval = 80 };
            timer.Tick += (_, _) => { if (!form.HasPreviewImageForTests) return; sawImage = true; timer.Stop(); timer.Dispose(); form.DecideForTests(false); };
            timer.Start();
        }, ApprovalBudget = TimeSpan.FromSeconds(3) });
        Assert.Equal(StandingLeaseApprovalOutcome.Rejected, (await operation.Decision.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        await operation.Retired.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(sawImage); Assert.NotNull(preview.Bitmap); Assert.ThrowsAny<Exception>(() => preview.Bitmap.GetPixel(0, 0));
    }

    [Theory]
    [InlineData(System.Windows.Forms.Keys.Enter)]
    [InlineData(System.Windows.Forms.Keys.Escape)]
    public async Task EnterAndEscapeRejectEvenWhenApproveHasFocus(System.Windows.Forms.Keys key)
    {
        var events = new Events();
        var operation = Start(events, new() { PreviewProvider = new Preview(), AfterVisibleForTests = form => form.KeyForTests(key) });
        Assert.Equal(StandingLeaseApprovalOutcome.Rejected, (await operation.Decision.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        await operation.Retired.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeDisplayFailureIsUnavailableAndNeverAUserRejection(bool throws)
    {
        var events = new Events();
        var operation = Start(events, new() { PreviewProvider = new Preview(), WindowPlatform = new Platform(unavailable: true, throws: throws) });
        var result = await operation.Decision.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StandingLeaseApprovalOutcome.Unavailable, result.Outcome);
        Assert.Equal(throws ? "lease_approval_display_failed" : "lease_approval_native_not_visible", result.Reason);
        await operation.Retired.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(events.Items, item => item.Data.TryGetProperty("reason_code", out var reason) && reason.GetString() == "lease_rejected_by_user");
    }

    [Fact]
    public async Task ForegroundExceptionAfterVisibleCannotBeMisreportedAsUserRejection()
    {
        var events = new Events();
        var operation = Start(events, new() { PreviewProvider = new Preview(), WindowPlatform = new Platform(activationThrows: true) });
        Assert.Equal(new(StandingLeaseApprovalOutcome.Unavailable, "lease_approval_display_failed"),
            await operation.Decision.WaitAsync(TimeSpan.FromSeconds(5)));
        await operation.Retired.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(events.Items, item => item.Data.TryGetProperty("reason_code", out var reason) && reason.GetString() == "lease_rejected_by_user");
    }

    [Fact]
    public async Task CancellationDuringNativeVisibilityCannotStartLatePreviewOrApprovalCallback()
    {
        using var cancel = new CancellationTokenSource(); var preview = new Preview(); var callbacks = 0;
        var operation = Start(new Events(), new() { PreviewProvider = preview, WindowPlatform = new Platform(onActivate: cancel.Cancel),
            AfterVisibleForTests = _ => Interlocked.Increment(ref callbacks) }, cancel.Token);
        Assert.Equal(StandingLeaseApprovalOutcome.Cancelled, (await operation.Decision.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        await operation.Retired.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, preview.Calls); Assert.Equal(0, callbacks);
    }

    [Theory]
    [InlineData(0, 0, 1024, 768)]
    [InlineData(-1200, 0, 600, 600)]
    [InlineData(100, 100, 480, 440)]
    [InlineData(0, 0, 320, 480)]
    public async Task SimulatedWorkingAreasKeepFrozenBodyAndActionsReadable(int x, int y, int width, int height)
    {
        var work = new Rectangle(x, y, width, height); var fits = false; var readable = false; var copy = "";
        var operation = Start(new Events(), new() { PreviewProvider = new Preview(), WindowPlatform = new Platform(), WorkingAreaForTests = work,
            AfterVisibleForTests = form => { fits = work.Contains(form.Bounds); readable = form.ButtonsReadableForTests; copy = form.DetailsTextForTests; form.DecideForTests(false); } });
        Assert.Equal(StandingLeaseApprovalOutcome.Rejected, (await operation.Decision.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        await operation.Retired.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fits); Assert.True(readable); Assert.Contains("never-created.mp4", copy); Assert.Contains("Synthetic display", copy);
    }
    private sealed class Platform(bool unavailable = false, bool throws = false, bool activationThrows = false, Action? onActivate = null) : IFutureWindowApprovalWindowPlatform
    {
        public void ShowNormal(nint handle) { }
        public FutureWindowApprovalNativeState Inspect(nint handle) => throws ? throw new InvalidOperationException("not logged")
            : new(!unavailable, false, true, new(0, 0, 100, 100), new(0, 0, 1000, 1000), Environment.ProcessId, 0, 0);
        public FutureWindowApprovalForegroundState Activate(nint handle)
        { onActivate?.Invoke(); return activationThrows ? throw new InvalidOperationException("not logged") : new(false, false, false); }
    }

    [Theory]
    [InlineData("standing-visible")]
    [InlineData("standing-invisible")]
    [InlineData("standing-blocked-preview")]
    [InlineData("standing-error-preview")]
    public async Task HiddenShellChildUsesProductionAdapterAndActualNativeVisibility(string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "native-approval-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var host = Path.Combine(AppContext.BaseDirectory, "AgentRecorder.FutureWindowUiTestHost.exe");
        using var child = Process.Start(new ProcessStartInfo(host) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
            ArgumentList = { root, mode } })!;
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
            Assert.Equal(0, child.ExitCode);
            var events = File.ReadLines(Path.Combine(root, "audit.jsonl")).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
            Assert.Contains(events, item => item.GetProperty("stage").GetString()!.EndsWith("handle_created", StringComparison.Ordinal));
            Assert.Contains(events, item => item.GetProperty("stage").GetString()!.EndsWith("retired", StringComparison.Ordinal));
            if (mode == "standing-invisible")
            {
                var handle = Assert.Single(events, item => item.GetProperty("stage").GetString()!.EndsWith("handle_created", StringComparison.Ordinal));
                Assert.False(handle.GetProperty("data").GetProperty("native_visible").GetBoolean());
                Assert.DoesNotContain(events, item => item.GetProperty("stage").GetString()!.EndsWith("native_visible", StringComparison.Ordinal));
            }
            else
            {
                var shown = Assert.Single(events, item => item.GetProperty("stage").GetString()!.EndsWith("native_visible", StringComparison.Ordinal));
                Assert.True(shown.GetProperty("data").GetProperty("native_visible").GetBoolean());
                Assert.True(shown.GetProperty("data").GetProperty("within_working_area").GetBoolean());
                Assert.True(Assert.Single(events, item => item.GetProperty("stage").GetString() == "fixture_readable").GetProperty("data").GetProperty("readable").GetBoolean());
                Assert.Equal(1, Assert.Single(events, item => item.GetProperty("stage").GetString() == "fixture_finished").GetProperty("data").GetProperty("preview_calls").GetInt32());
                if (mode is "standing-blocked-preview" or "standing-error-preview")
                    Assert.Contains(events, item => item.GetProperty("stage").GetString()!.EndsWith("preview_fallback", StringComparison.Ordinal) &&
                        item.GetProperty("data").GetProperty("reason_code").GetString() ==
                            (mode == "standing-blocked-preview" ? "lease_preview_timeout" : "lease_preview_unavailable"));
                if (mode == "standing-blocked-preview")
                    Assert.False(Assert.Single(events, item => item.GetProperty("stage").GetString() == "fixture_decision").GetProperty("data").GetProperty("retired").GetBoolean());
            }
        }
        finally { if (!child.HasExited) await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); TestDirectoryCleanup.DeleteOwnedDirectory(root); }
    }

    internal static async Task WaitUntil(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!condition()) await Task.Delay(20, timeout.Token); }
}
