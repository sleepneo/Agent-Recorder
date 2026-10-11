using System.Collections.Concurrent;
using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class ObservationControllerTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    internal static UnifiedHistoryEntry Entry(string id = "ordinary:r", string status = "completed", bool recorded = false, bool? exists = null) =>
        new(id, "ordinary", "ordinary_video", At, RecordingId: id, Status: status,
            OutputPath: recorded ? @"C:\Synthetic\output.mp4" : null, OutputPathRecorded: recorded, OutputFileExists: exists);
    internal static RecordingUiPresentation Presentation(string id, RecordingUiState state = RecordingUiState.Recording) => new()
    { RecordingId = id, State = state, SourceType = "region", CaptureBounds = new(1, 2, 400, 300), StartedAtUtc = At.UtcDateTime };

    [Fact]
    public async Task EmptyFailureInvalidCursorAndMissingGatewayAreDistinctAndNeverAutoRetry()
    {
        var gateway = new HistoryFake((_, f, _) => new(Array.Empty<UnifiedHistoryEntry>(), null, 20, f));
        using var controller = new ObservationHistoryController(gateway, action => action());
        controller.Reload(new()); await controller.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ObservationHistoryStatus.Empty, controller.View.Status); Assert.Single(gateway.Calls);
        gateway.Query = (_, _, _) => throw new UnifiedHistoryUnavailableException(new Exception("private path and SID"));
        controller.Reload(new()); await controller.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ObservationHistoryStatus.Unavailable, controller.View.Status); Assert.Empty(controller.View.Items); Assert.Null(controller.View.NextCursor);
        gateway.Query = (_, _, _) => throw new UnifiedHistoryArgumentException();
        controller.Reload(new()); await controller.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ObservationHistoryStatus.InvalidCursor, controller.View.Status); Assert.Equal(3, gateway.Calls.Count);
        using var missing = new ObservationHistoryController(UnavailableObservationHistoryGateway.Instance, a => a());
        missing.Reload(new()); await missing.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ObservationHistoryStatus.Unavailable, missing.View.Status);
    }

    [Fact]
    public async Task PagesAreManualTwentyEntriesFiltersResetCursorAndLanguageKeepsFacts()
    {
        var gateway = new HistoryFake((_, f, cursor) => new(new[] { Entry(cursor is null ? "first" : "second") }, cursor is null ? "opaque-next" : null, 20, f));
        using var c = new ObservationHistoryController(gateway, a => a());
        var f = new UnifiedHistoryFilters("plan", At.AddDays(-1), At);
        c.Reload(f); await c.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(gateway.Calls); c.LanguageChanged(); Assert.Single(gateway.Calls);
        c.NextPage(); await c.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("second", Assert.Single(c.View.Items).EntryId); Assert.Null(c.View.NextCursor);
        c.NextPage(); Assert.Equal(2, gateway.Calls.Count);
        c.Reload(new("future_window")); await c.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        var calls = gateway.Calls.ToArray(); Assert.All(calls, call => Assert.Equal(20, call.Limit));
        Assert.Equal("opaque-next", calls[1].Cursor); Assert.Equal(f, calls[1].Filters); Assert.Null(calls[2].Cursor);
    }

    [Fact]
    public async Task SlowOldQueryAndMultipleNewFiltersCoalesceToLatestWithoutConcurrentQueries()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var gateway = new HistoryFake((_, f, _) => { if (f.Source == "all") { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } return new(new[] { Entry(f.Source) }, null, 20, f); });
        using var c = new ObservationHistoryController(gateway, a => a());
        c.Reload(new()); Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            c.Reload(new("ordinary")); c.Reload(new("plan")); c.Reload(new("future_window"));
            Assert.Equal(ObservationHistoryStatus.Loading, c.View.Status); Assert.Empty(c.View.Items);
            Assert.Single(gateway.Calls); Assert.Equal(1, gateway.MaxConcurrent);
        }
        finally { release.Set(); }
        await c.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("future_window", Assert.Single(c.View.Items).EntryId); Assert.Equal(2, gateway.Calls.Count); Assert.Equal(1, gateway.MaxConcurrent);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ClosingOrLanguageSwitchDiscardsSlowCompletionAndNeverPublishesOldData(bool languageSwitch)
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); var number = 0;
        var gateway = new HistoryFake((_, f, _) => { var n = Interlocked.Increment(ref number); if (n == 1) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } return new(new[] { Entry(n.ToString()) }, null, 20, f); });
        using var c = new ObservationHistoryController(gateway, a => a()); var published = new ConcurrentQueue<string>();
        c.Changed += () => { foreach (var e in c.View.Items) published.Enqueue(e.EntryId); };
        c.Reload(new()); Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        if (languageSwitch) c.LanguageChanged(); else c.Dispose();
        release.Set(); await c.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        // Language changes presentation only: reuse the one in-flight page.
        if (languageSwitch) { Assert.Equal("1", Assert.Single(c.View.Items).EntryId); Assert.Single(gateway.Calls); Assert.Contains("1", published); }
        else { Assert.DoesNotContain("1", published); Assert.Equal(ObservationHistoryStatus.Closed, c.View.Status); Assert.Empty(c.View.Items); c.Reload(new()); Assert.Single(gateway.Calls); }
    }

    [Fact]
    public async Task QueuedUiNotificationsCannotReviveDisposedOwnerAndMalformedPagesFailClosed()
    {
        var notifications = new ConcurrentQueue<Action>();
        var g = new HistoryFake((_, f, _) => new(Enumerable.Range(0, 21).Select(i => Entry(i.ToString())).ToArray(), "more", 20, f));
        using var c = new ObservationHistoryController(g, a => notifications.Enqueue(a)); var delivered = 0; c.Changed += () => delivered++;
        c.Reload(new()); await c.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ObservationHistoryStatus.Unavailable, c.View.Status); Assert.Empty(c.View.Items);
        c.Dispose(); while (notifications.TryDequeue(out var notification)) notification(); Assert.Equal(0, delivered);
    }

    [Fact]
    public async Task StopUsesFreshSnapshotExactIdRejectsDuplicatesAndEndedTargetsAndDoesNotCompleteMedia()
    {
        var active = new ActiveFake(); active.Rows = new[] { new ObservationActiveRecording(Presentation("outer"), true), new ObservationActiveRecording(Presentation("inner") with { NestedRole = "inner", ParentRecordingId = "outer" }, true) };
        var completion = new TaskCompletionSource<ObservationStopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        active.Stop = _ => completion.Task; using var stop = new ObservationStopController(active);
        var first = stop.StopAsync("inner"); Assert.True(stop.IsBusy);
        Assert.Equal(ObservationStopOutcome.Busy, (await stop.StopAsync("inner")).Outcome);
        Assert.Equal(new[] { "inner" }, active.StoppedIds);
        completion.SetResult(new(ObservationStopOutcome.Requested)); Assert.Equal(ObservationStopOutcome.Requested, (await first).Outcome);
        Assert.Equal(RecordingUiState.Recording, active.Rows[0].Presentation.State);
        active.Rows = new[] { new ObservationActiveRecording(Presentation("inner", RecordingUiState.Finalizing), false) };
        Assert.Equal(ObservationStopOutcome.NotStoppable, (await stop.StopAsync("inner")).Outcome);
        Assert.Equal(ObservationStopOutcome.NotStoppable, (await stop.StopAsync("removed")).Outcome); Assert.Single(active.StoppedIds);
    }

    [Fact]
    public async Task StopFailureAndCloseAreExplicitAndDoNotThrowUnobservedExceptions()
    {
        var active = new ActiveFake { Rows = new[] { new ObservationActiveRecording(Presentation("r"), true) }, Stop = _ => throw new ApiException(409, "REAL_STOP_FAILURE", "private detail") };
        using var stop = new ObservationStopController(active);
        Assert.Equal(new ObservationStopResult(ObservationStopOutcome.Failed, "REAL_STOP_FAILURE"), await stop.StopAsync("r"));
        var completion = new TaskCompletionSource<ObservationStopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        active.Stop = _ => completion.Task; var late = stop.StopAsync("r"); stop.Dispose(); completion.SetResult(new(ObservationStopOutcome.Requested));
        Assert.Equal(ObservationStopOutcome.Closed, (await late).Outcome);
    }

    [Theory]
    [InlineData(RecordingUiState.Preparing, true)] [InlineData(RecordingUiState.Countdown, true)]
    [InlineData(RecordingUiState.Recording, true)] [InlineData(RecordingUiState.Stopping, false)]
    [InlineData(RecordingUiState.Finalizing, false)] [InlineData(RecordingUiState.Idle, false)]
    public void ActualPhasesAndZeroCountdownNeverInventFirstFrame(RecordingUiState phase, bool canStop)
    {
        var p = Presentation("r", phase); Assert.Equal(canStop, ObservationFormatting.CanStop(p));
        foreach (var language in new[] { UiLanguage.ZhCn, UiLanguage.EnUs })
        {
            var text = new UiTextProvider(language);
            var zero = ObservationFormatting.Phase(p with { State = RecordingUiState.Countdown, CountdownRemainingSeconds = null }, text);
            Assert.Equal(text.Get("Observation_AwaitingFrame"), zero);
            Assert.Equal("new_unknown_status", ObservationFormatting.State("new_unknown_status", text));
            Assert.DoesNotContain("Observation_", ObservationFormatting.Phase(p, text));
        }
    }

    [Fact]
    public void LocalCalendarBoundsIncludeDstAndSevenDatesInsteadOfAssumingTwentyFourHours()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var spring = ObservationFormatting.Filters("plan", ObservationTimeRange.Today, new(2026, 3, 8, 16, 0, 0, TimeSpan.Zero), zone);
        Assert.Equal(TimeSpan.FromHours(23), spring.CreatedBefore - spring.CreatedFrom);
        var fall = ObservationFormatting.Filters("plan", ObservationTimeRange.Today, new(2026, 11, 1, 16, 0, 0, TimeSpan.Zero), zone);
        Assert.Equal(TimeSpan.FromHours(25), fall.CreatedBefore - fall.CreatedFrom);
        var seven = ObservationFormatting.Filters("plan", ObservationTimeRange.LastSevenDays, At, zone);
        Assert.Equal(At.Date.AddDays(-6), TimeZoneInfo.ConvertTime(seven.CreatedFrom!.Value, zone).Date);
        Assert.Equal(At.Date.AddDays(1), TimeZoneInfo.ConvertTime(seven.CreatedBefore!.Value, zone).Date);
        Assert.Equal(TimeSpan.Zero, seven.CreatedFrom.Value.Offset);
        Assert.Null(ObservationFormatting.Filters("all", ObservationTimeRange.All, At, zone).CreatedFrom);
    }

    [Fact]
    public void UnusedAuthorizationMissingMediaAndLegacyCompletionRemainDifferentInBothLanguages()
    {
        var unused = Entry("future_window:f", "active") with { Source = "future_window", EntryKind = "future_window_authorization", RecordingId = null, AuthorizationId = "f", AuthorizationStatus = "active" };
        var missing = Entry(recorded: true, exists: false); var legacy = Entry("plan:o", "completed") with { Source = "plan", EntryKind = "plan_occurrence", RunStatus = "settled" };
        foreach (var language in new[] { UiLanguage.ZhCn, UiLanguage.EnUs })
        {
            var text = new UiTextProvider(language);
            Assert.Equal(text.Get("Observation_NoRecordedOutput"), ObservationFormatting.Media(unused, text));
            Assert.Equal(text.Get("Observation_FileMissing"), ObservationFormatting.Media(missing, text));
            var details = ObservationFormatting.HistoryDetails(legacy, text, TimeZoneInfo.Utc);
            Assert.Contains("completed", details); Assert.Contains("settled", details); Assert.DoesNotContain("Observation_", details);
            Assert.False(ObservationOutputAction.HasRecordedPath(unused)); Assert.False(ObservationOutputAction.HasRecordedPath(legacy));
        }
    }

    [Fact]
    public void FolderActionRechecksRecordedFileAndNeverOpensExpectedPathsCommandsOrFallbacks()
    {
        var desktop = new DesktopFake(); var entry = Entry(recorded: true, exists: true);
        Assert.False(ObservationOutputAction.OpenFolder(entry, desktop)); Assert.Empty(desktop.Opened);
        desktop.Exists = true; Assert.True(ObservationOutputAction.OpenFolder(entry, desktop)); Assert.Equal(new[] { @"C:\Synthetic" }, desktop.Opened);
        Assert.False(ObservationOutputAction.OpenFolder(entry with { OutputPathRecorded = false }, desktop));
        Assert.False(ObservationOutputAction.OpenFolder(entry with { OutputPath = "explorer.exe /select,something" }, desktop));
        Assert.False(ObservationOutputAction.OpenFolder(entry with { OutputPath = @"C:\Synthetic\..\wrong.mp4" }, desktop));
        desktop.ThrowProbe = true; Assert.False(ObservationOutputAction.OpenFolder(entry, desktop)); Assert.Single(desktop.Opened);
    }

    internal sealed class HistoryFake(Func<int, UnifiedHistoryFilters, string?, UnifiedHistoryPage> query) : IUnifiedHistoryGateway, IDisposable
    {
        private int _concurrent; internal int MaxConcurrent; internal bool Disposed;
        internal Func<int, UnifiedHistoryFilters, string?, UnifiedHistoryPage> Query = query;
        internal ConcurrentQueue<(int Limit, UnifiedHistoryFilters Filters, string? Cursor)> Calls = new();
        public IReadOnlyList<string> SupportedSources => new[] { "ordinary", "plan", "future_window" };
        public UnifiedHistoryPage GetPage(int limit, UnifiedHistoryFilters filters, string? cursor)
        {
            Calls.Enqueue((limit, filters, cursor)); var count = Interlocked.Increment(ref _concurrent); MaxConcurrent = Math.Max(MaxConcurrent, count);
            try { return Query(limit, filters, cursor); } finally { Interlocked.Decrement(ref _concurrent); }
        }
        public void Dispose() => Disposed = true;
    }
    internal sealed class ActiveFake : IObservationActiveGateway
    {
        internal IReadOnlyList<ObservationActiveRecording> Rows = Array.Empty<ObservationActiveRecording>();
        internal List<string> StoppedIds = new();
        internal Func<string, Task<ObservationStopResult>> Stop = _ => Task.FromResult(new ObservationStopResult(ObservationStopOutcome.Requested));
        public IReadOnlyList<ObservationActiveRecording> GetObservationSnapshot() => Rows;
        public Task<ObservationStopResult> StopObservationRecordingAsync(string id) { StoppedIds.Add(id); return Stop(id); }
    }
    internal sealed class DesktopFake : IObservationDesktopActions
    {
        internal bool Exists, ThrowProbe; internal List<string> Opened = new(), Copied = new();
        public bool FileExists(string path) { if (ThrowProbe) throw new IOException("fixture"); return Exists; }
        public void OpenFolder(string directory) => Opened.Add(directory);
        public void Copy(string value) => Copied.Add(value);
    }
}
