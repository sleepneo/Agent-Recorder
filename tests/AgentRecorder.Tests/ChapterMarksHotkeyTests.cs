using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Focused local Chapter Marks tests. All Win32 registration is substituted and all
/// tray tests run on an STA without creating a real global registration.
/// </summary>
public sealed class ChapterMarksHotkeyTests
{
    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null)
            throw new TargetInvocationException(error);
    }

    private static T GetPrivateField<T>(object value, string name)
    {
        var field = value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (T)field!.GetValue(value)!;
    }

    private sealed class FakeRegistrar : IHotkeyRegistrar
    {
        public List<(int id, uint modifiers, uint key)> Registrations { get; } = new();
        public List<int> Unregistrations { get; } = new();
        public bool ShouldRegister { get; set; } = true;
        public bool ThrowOnRegister { get; set; }

        public bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key)
        {
            if (ThrowOnRegister)
                throw new InvalidOperationException("registration failed");
            Registrations.Add((id, modifiers, key));
            return ShouldRegister;
        }

        public bool UnregisterHotKey(IntPtr hWnd, int id)
        {
            Unregistrations.Add(id);
            return true;
        }
    }

    private sealed class NoOpIndicatorPresenter : IIndicatorPresenter
    {
        public void ShowFor(Recording recording, Recording? parent, string? parentFallbackReason = null) { }
    }

    private sealed class FeedbackSpy : IChapterMarkFeedbackPresenter
    {
        public List<(string text, TimeSpan duration, string? recordingId)> Calls { get; } = new();
        public void Show(string text, TimeSpan duration, string? preferredRecordingId = null) =>
            Calls.Add((text, duration, preferredRecordingId));
    }

    private sealed class BalloonSpy : ITrayBalloonTip
    {
        public int Calls { get; private set; }
        public void ShowBalloonTip(int timeout, string title, string body, ToolTipIcon icon) => Calls++;
    }

    private static Recording MakeRecording(RecState state, long anchor, string? role = null)
    {
        return new Recording
        {
            State = state,
            StartedAtUtc = new DateTime(2026, 8, 20, 8, 0, 0, DateTimeKind.Utc),
            MarkTimelineAnchorTicks = anchor,
            NestedRole = role,
            SourceType = "region",
            OutputPath = Path.Combine(Path.GetTempPath(), $"chapter-hotkey-{Guid.NewGuid():N}.mp4"),
            Config = new CaptureConfig
            {
                SourceKind = "region",
                Bounds = (100, 100, 800, 600),
                OutputPath = Path.Combine(Path.GetTempPath(), $"chapter-hotkey-{Guid.NewGuid():N}.mp4")
            }
        };
    }

    private static (TrayContext context, RecordingEngine engine, CaptureAuditLogger audit, FakeGlobalStopHotkey markHotkey, FeedbackSpy feedback) CreateContext(
        UiLanguage language = UiLanguage.ZhCn,
        bool markRegistrationSucceeds = true,
        Func<Action, IGlobalStopHotkey>? markHotkeyFactory = null,
        ITrayBalloonTip? balloonTip = null)
    {
        var audit = new CaptureAuditLogger();
        var engine = new RecordingEngine(audit)
        {
            MonotonicFrequencyForTests = 1000,
            MonotonicTimestampProviderForTests = () => 1000
        };
        var feedback = new FeedbackSpy();
        var stopFactory = FakeGlobalStopHotkeyFactory.Create(true);
        var markFactory = markHotkeyFactory ?? FakeGlobalStopHotkeyFactory.Create(markRegistrationSucceeds);
        var context = new TrayContext(
            engine,
            audit,
            stopFactory,
            uiTextProvider: new UiTextProvider(language),
            indicatorPresenter: new NoOpIndicatorPresenter(),
            balloonTip: balloonTip,
            chapterMarksHotkeyFactory: markFactory,
            chapterMarkFeedbackPresenter: feedback);
        engine.SetTray(context);
        var markHotkey = (FakeGlobalStopHotkey)GetPrivateField<IGlobalStopHotkey>(context, "_chapterMarksHotkey");
        return (context, engine, audit, markHotkey, feedback);
    }

    private static bool ReadChapterMarksHotkeyCapability(
        RecordingEngine engine,
        CaptureAuditLogger audit,
        TrayContext context)
    {
        var server = new AgentRecorder.Api.ApiServer(engine, audit, context);
        var capabilities = typeof(AgentRecorder.Api.ApiServer)
            .GetMethod("Capabilities", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(server, null)!;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(capabilities));
        return document.RootElement
            .GetProperty("chapter_marks")
            .GetProperty("local_hotkey")
            .GetProperty("registered")
            .GetBoolean();
    }

    private static void Add(RecordingEngine engine, params Recording[] recordings)
    {
        foreach (var recording in recordings)
            engine._recs[recording.Id] = recording;
    }

    [Fact]
    public void F11_UsesCtrlShiftAndDistinctId_AndCanUnregisterAndReregister()
    {
        RunOnSta(() =>
        {
            var registrar = new FakeRegistrar();
            using var stop = new GlobalStopHotkey(() => { }, registrar);
            using var mark = new GlobalChapterMarkHotkey(() => { }, registrar);

            Assert.NotEqual(stop.HotkeyId, mark.HotkeyId);
            Assert.True(stop.Register());
            Assert.True(mark.Register());
            Assert.Equal(Native.MOD_CONTROL | Native.MOD_SHIFT, registrar.Registrations[1].modifiers);
            Assert.Equal(Native.VK_F11, registrar.Registrations[1].key);

            Assert.True(mark.Unregister());
            Assert.True(mark.Unregister());
            Assert.False(mark.Registered);
            Assert.True(mark.Register());
            Assert.Equal(2, registrar.Registrations.Count(registration => registration.id == mark.HotkeyId));
            Assert.Equal(1, registrar.Unregistrations.Count(id => id == mark.HotkeyId));
        });
    }

    [Fact]
    public void Dispose_SuppressesLaterCallback_AndRegistrationExceptionIsNonFatal()
    {
        RunOnSta(() =>
        {
            var registrar = new FakeRegistrar { ThrowOnRegister = true };
            int callbacks = 0;
            var mark = new GlobalChapterMarkHotkey(() => callbacks++, registrar);
            Assert.False(mark.Register());
            mark.Dispose();
            mark.Dispose();
            mark.OnHotkeyReceived();
            Assert.Equal(0, callbacks);
            Assert.Empty(registrar.Unregistrations);
        });
    }

    [Fact]
    public void RegistrationLifecycle_UsesOnlyExactRecordingState()
    {
        RunOnSta(() =>
        {
            var setup = CreateContext();
            using var context = setup.context;
            var preparing = MakeRecording(RecState.preparing, 0);
            var recording = MakeRecording(RecState.recording, 0);
            Add(setup.engine, recording);

            context.SetPreparing(preparing);
            Assert.Equal(0, setup.markHotkey.RegisterCallCount);

            context.SetRecording(recording);
            Assert.Equal(1, setup.markHotkey.RegisterCallCount);
            Assert.True(context.IsChapterMarksHotkeyRegistered);

            recording.State = RecState.finalizing;
            context.SetFinalizing(recording);
            Assert.Equal(1, setup.markHotkey.UnregisterCallCount);
            Assert.False(context.IsChapterMarksHotkeyRegistered);
        });
    }

    [Fact]
    public void TwoRecordings_RegisterOnce_AndStayRegisteredUntilBothLeaveRecording()
    {
        RunOnSta(() =>
        {
            var setup = CreateContext();
            using var context = setup.context;
            var outer = MakeRecording(RecState.recording, 100, "outer");
            var inner = MakeRecording(RecState.recording, 200, "inner");
            Add(setup.engine, outer, inner);

            context.SetRecording(outer);
            context.SetRecording(inner);
            Assert.Equal(1, setup.markHotkey.RegisterCallCount);

            outer.State = RecState.finalizing;
            context.SetFinalizing(outer);
            Assert.Equal(0, setup.markHotkey.UnregisterCallCount);

            inner.State = RecState.finalizing;
            context.SetFinalizing(inner);
            Assert.Equal(1, setup.markHotkey.UnregisterCallCount);
        });
    }

    [Fact]
    public void RegistrationFailure_IsAuditedOncePerWindow_AndRetriesAfterZeroToOne()
    {
        RunOnSta(() =>
        {
            var setup = CreateContext(markRegistrationSucceeds: false);
            using var context = setup.context;
            var first = MakeRecording(RecState.recording, 0);
            Add(setup.engine, first);

            context.SetRecording(first);
            context.SetRecording(first);
            Assert.Equal(1, setup.markHotkey.RegisterCallCount);
            Assert.Single(setup.audit.Events, e => e.evt == "tray.chapter_mark_hotkey_state");

            first.State = RecState.finalizing;
            context.SetFinalizing(first);
            var second = MakeRecording(RecState.recording, 0);
            Add(setup.engine, second);
            context.SetRecording(second);

            Assert.Equal(2, setup.markHotkey.RegisterCallCount);
            Assert.Equal(2, setup.audit.Events.Count(e => e.evt == "tray.chapter_mark_hotkey_state"));
        });
    }

    [Fact]
    public void UnregisterFailure_RetiresOnceWithoutIdleSpam_AndRecreatesOnNextRecording()
    {
        RunOnSta(() =>
        {
            var created = new List<FakeGlobalStopHotkey>();
            Func<Action, IGlobalStopHotkey> factory = onPressed =>
            {
                var hotkey = new FakeGlobalStopHotkey(onPressed);
                if (created.Count == 0)
                {
                    hotkey.UnregisterSucceeds = false;
                    hotkey.LastErrorCode = 4321;
                }
                created.Add(hotkey);
                return hotkey;
            };
            var setup = CreateContext(markHotkeyFactory: factory);
            using var context = setup.context;
            var first = MakeRecording(RecState.recording, 0);
            Add(setup.engine, first);

            context.SetRecording(first);
            first.State = RecState.finalizing;
            context.SetFinalizing(first);

            Assert.Single(created);
            Assert.Equal(1, created[0].UnregisterCallCount);
            Assert.Equal(1, created[0].DisposeCallCount);
            Assert.False(context.IsChapterMarksHotkeyRegistered);
            Assert.Null(GetPrivateField<object?>(context, "_chapterMarksHotkey"));
            var unregisterEvents = setup.audit.Events
                .Where(e => e.evt == "tray.chapter_mark_hotkey_state" && e.json.Contains("\"action\":\"unregister\""))
                .ToList();
            Assert.Single(unregisterEvents);
            Assert.Contains("\"registered\":false", unregisterEvents[0].json);
            Assert.Contains("\"result_code\":\"unregister_failed\"", unregisterEvents[0].json);
            Assert.Contains("\"error_code\":4321", unregisterEvents[0].json);
            Assert.False(ReadChapterMarksHotkeyCapability(setup.engine, setup.audit, context));

            // Repeated finalizing/idle callbacks do not retry or emit another failure.
            context.SetFinalizing(first);
            context.SetIdle(first);
            context.SetAllIdle();
            Assert.Equal(1, created[0].UnregisterCallCount);
            Assert.Equal(1, created[0].DisposeCallCount);
            Assert.Single(setup.audit.Events,
                e => e.evt == "tray.chapter_mark_hotkey_state" && e.json.Contains("\"action\":\"unregister\""));

            var second = MakeRecording(RecState.recording, 0);
            Add(setup.engine, second);
            context.SetRecording(second);

            Assert.Equal(2, created.Count);
            Assert.Equal(1, created[1].RegisterCallCount);
            Assert.True(context.IsChapterMarksHotkeyRegistered);
            Assert.True(ReadChapterMarksHotkeyCapability(setup.engine, setup.audit, context));
        });
    }

    [Fact]
    public void HotkeyMarksAllExactRecordings_WithIndependentTimelineAndPrivateAudit()
    {
        RunOnSta(() =>
        {
            var setup = CreateContext();
            using var context = setup.context;
            var outer = MakeRecording(RecState.recording, 100, "outer");
            var inner = MakeRecording(RecState.recording, 200, "inner");
            var preparing = MakeRecording(RecState.preparing, 0);
            Add(setup.engine, outer, inner, preparing);
            setup.engine.MonotonicTimestampProviderForTests = () => 1100;

            context.SetRecording(outer);
            context.SetRecording(inner);
            context.SetPreparing(preparing);
            setup.markHotkey.SimulatePressed();

            Assert.Equal(1000, Assert.Single(outer.SnapshotMarks()).TMs);
            Assert.Equal(900, Assert.Single(inner.SnapshotMarks()).TMs);
            Assert.Empty(preparing.SnapshotMarks());
            Assert.Equal(2, setup.audit.Events.Count(e => e.evt == "recording.mark_added"));
            Assert.All(setup.audit.Events.Where(e => e.evt == "recording.mark_added"), e =>
            {
                Assert.Contains("\"source\":\"hotkey\"", e.json);
                Assert.DoesNotContain("快捷标记", e.json);
            });
            Assert.Single(setup.feedback.Calls);
            Assert.Contains("2", setup.feedback.Calls[0].text);
            Assert.Equal(outer.Id, setup.feedback.Calls[0].recordingId);
        });
    }

    [Fact]
    public void OneRecordingFailureDoesNotBlockTheRemainingSnapshot()
    {
        RunOnSta(() =>
        {
            var setup = CreateContext();
            using var context = setup.context;
            var good = MakeRecording(RecState.recording, 0);
            var missingFromDomain = MakeRecording(RecState.recording, 0);
            Add(setup.engine, good);

            context.SetRecording(good);
            context.SetRecording(missingFromDomain);
            setup.markHotkey.SimulatePressed();

            Assert.Single(good.SnapshotMarks());
            Assert.Empty(missingFromDomain.SnapshotMarks());
            Assert.Single(setup.feedback.Calls);
            Assert.Contains("1/2", setup.feedback.Calls[0].text);
            Assert.Single(setup.audit.Events, e => e.evt == "recording.mark_added");
        });
    }

    [Fact]
    public void LanguageSwitchChangesFutureLabelAndFeedbackOnly()
    {
        RunOnSta(() =>
        {
            var setup = CreateContext(UiLanguage.ZhCn);
            using var context = setup.context;
            var recording = MakeRecording(RecState.recording, 0);
            Add(setup.engine, recording);
            context.SetRecording(recording);
            setup.markHotkey.SimulatePressed();

            var setLanguage = typeof(TrayContext).GetMethod("SetLanguage", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(setLanguage);
            setLanguage!.Invoke(context, new object[] { UiLanguage.EnUs });
            setup.markHotkey.SimulatePressed();

            Assert.Equal(new[] { "快捷标记", "Quick mark" }, recording.SnapshotMarks().Select(m => m.Label));
            Assert.Contains("Mark", setup.feedback.Calls[^1].text);
            Assert.Equal(recording.Id, setup.feedback.Calls[^1].recordingId);
        });
    }

    [Fact]
    public void FeedbackShownOnVisibleForm_IsOpaqueInClientArea_AndExpiresAfterRefresh()
    {
        RunOnSta(() =>
        {
            using var form = new RecordingIndicatorForm(
                "rec_feedback_visible",
                new RecordingIndicatorBounds(100, 100, 800, 600),
                DateTime.UtcNow);
            form.Show();
            Application.DoEvents();

            form.ShowTransientFeedback("✓ 标记已添加", TimeSpan.FromMilliseconds(1900));
            Application.DoEvents();
            Assert.True(form.Visible);
            Assert.True(form.FeedbackVisibleForTests);
            Assert.True(form.FeedbackControlVisibleForTests);
            Assert.True(form.FeedbackControlHandleCreatedForTests);
            Assert.True(form.FeedbackBoundsNonEmptyForTests);
            Assert.True(form.FeedbackBoundsInsideClientForTests);
            Assert.False(form.FeedbackBoundsForTests.IntersectsWith(form.LabelBoundsForTests));
            Assert.True(form.FeedbackIsFrontmostChildForTests);
            Assert.True(form.FeedbackBackgroundOpaqueForTests);
            Assert.NotEqual("✓", form.FeedbackTextForTests);
            Assert.Equal(1900, form.FeedbackTimerIntervalForTests);

            // A second trigger refreshes the same child and restarts the same timer.
            Thread.Sleep(150);
            Application.DoEvents();
            form.ShowTransientFeedback("✓ Mark added", TimeSpan.FromMilliseconds(1900));
            Application.DoEvents();
            Assert.True(form.FeedbackVisibleForTests);
            Assert.Equal("✓ Mark added", form.FeedbackTextForTests);
            Assert.True(form.FeedbackTimerEnabledForTests);

            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3;
            while (form.FeedbackVisibleForTests && Stopwatch.GetTimestamp() < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }

            Assert.False(form.FeedbackVisibleForTests);
            Assert.False(form.FeedbackTimerEnabledForTests);
        });
    }

    [Theory]
    [InlineData(96, 96, 64)]
    [InlineData(144, 144, 96)]
    [InlineData(192, 192, 128)]
    public void FeedbackSmallRegion_DpiEquivalentGeometry_StaysDescriptive(int dpi, int width, int height)
    {
        RunOnSta(() =>
        {
            using var form = new RecordingIndicatorForm(
                $"rec_feedback_{dpi}",
                new RecordingIndicatorBounds(120, 120, width, height),
                DateTime.UtcNow);
            form.Show();
            Application.DoEvents();
            form.ShowTransientFeedback("✓ 已标记 2 个录制", TimeSpan.FromMilliseconds(1900));
            Application.DoEvents();

            Assert.True(form.FeedbackVisibleForTests);
            Assert.True(form.FeedbackBoundsInsideClientForTests);
            Assert.False(form.FeedbackBoundsForTests.IntersectsWith(form.LabelBoundsForTests));
            Assert.NotEqual("✓", form.FeedbackTextForTests);
            Assert.Contains("2", form.FeedbackTextForTests);
        });
    }

    [Fact]
    public void Manager_SelectsPreferredOuter_AndAuditsVisibleSubmissionWithoutBalloon()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var manager = new RecordingIndicatorManager(audit);
            var outer = MakeRecording(RecState.recording, 0, "outer");
            var inner = MakeRecording(RecState.recording, 0, "inner");
            inner.ParentRecordingId = outer.Id;
            inner.NestedSessionId = outer.NestedSessionId;
            manager.ShowFor(outer);
            manager.ShowFor(inner, outer);
            Application.DoEvents();

            manager.ShowChapterMarkFeedback("✓ 已标记 2 个录制", TimeSpan.FromMilliseconds(1900), outer.Id);
            Application.DoEvents();

            var selected = manager.IndicatorsForTests[outer.Id];
            Assert.True(selected.FeedbackVisibleForTests);
            Assert.Contains(audit.Events, e => e.evt == "tray.chapter_mark_feedback_presenter_called");
            Assert.Contains(audit.Events, e => e.evt == "tray.chapter_mark_feedback_indicator_selected" && e.json.Contains($"\"recording_id\":\"{outer.Id}\""));
            Assert.Contains(audit.Events, e => e.evt == "tray.chapter_mark_feedback_submitted" && e.json.Contains("\"bounds_inside_client\":true"));
            Assert.DoesNotContain(audit.Events, e => e.evt == "tray.chapter_mark_feedback_error");

            manager.CloseAll("test");
        });
    }

    [Fact]
    public void ChapterMarkFeedback_DoesNotCallTrayBalloonPresenter()
    {
        RunOnSta(() =>
        {
            var balloon = new BalloonSpy();
            var setup = CreateContext(balloonTip: balloon);
            using var context = setup.context;
            var recording = MakeRecording(RecState.recording, 0);
            Add(setup.engine, recording);
            context.SetRecording(recording);
            setup.markHotkey.SimulatePressed();

            Assert.Equal(0, balloon.Calls);
            Assert.Single(setup.feedback.Calls);
        });
    }

    [Fact]
    public void FeedbackRefreshesInPlaceAndClearsWithoutShellBalloon()
    {
        RunOnSta(() =>
        {
            var form = new RecordingIndicatorForm(
                "rec_feedback",
                new RecordingIndicatorBounds(100, 100, 800, 600),
                DateTime.UtcNow);
            try
            {
                var originalBounds = form.BoundsForTests;
                form.ShowTransientFeedback("✓ 已标记 2 个录制", TimeSpan.FromSeconds(1));
                var firstBounds = form.FeedbackBoundsForTests;
                form.ShowTransientFeedback("✓ Marked 2 recordings", TimeSpan.FromSeconds(1));

                Assert.True(form.FeedbackVisibleForTests);
                Assert.True(form.FeedbackTimerEnabledForTests);
                Assert.Equal("✓ Marked 2 recordings", form.FeedbackTextForTests);
                Assert.Equal(originalBounds, form.BoundsForTests);
                Assert.True(firstBounds.Width > 0);
                Assert.True(form.FeedbackBoundsForTests.Right <= form.BoundsForTests.Width);
                Assert.True(form.FeedbackBoundsForTests.Bottom <= form.BoundsForTests.Height);

                form.ClearTransientFeedback();
                Assert.False(form.FeedbackVisibleForTests);
                Assert.False(form.FeedbackTimerEnabledForTests);
            }
            finally
            {
                form.Dispose();
            }
        });
    }
}
