using System.Linq;
using System.Text.Json;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class ChapterMarksHotkeyTests
{
    [Fact]
    public void MediaOnlyDoesNotRegisterOrShowSuccessfulFeedback()
    {
        RunOnSta(() =>
        {
            var setup = CreateContext();
            using var context = setup.context;
            var rec = MakeRecording(RecState.recording, 0, bundle: false);
            Add(setup.engine, rec);
            context.SetRecording(setup.engine.CreateRecordingUiPresentationForTests(rec, RecordingUiState.Recording));
            Assert.Equal(0, setup.markHotkey.RegisterCallCount);
            Assert.False(context.IsChapterMarksHotkeyRegistered);
            setup.markHotkey.SimulatePressed();
            Assert.Empty(rec.SnapshotMarks());
            Assert.Empty(setup.feedback.Calls);
            Assert.DoesNotContain(setup.audit.Events, e => e.evt is "recording.mark_added" or "tray.chapter_mark_hotkey_result");
        });
    }

    [Fact]
    public void MixedDeliveryCountsOnlyEligibleAndReregistersWhileMediaOnlyContinues()
    {
        RunOnSta(() =>
        {
            var setup = CreateContext();
            using var context = setup.context;
            var bundle = MakeRecording(RecState.recording, 0);
            var media = MakeRecording(RecState.recording, 0, bundle: false);
            Add(setup.engine, bundle, media);
            context.SetRecording(setup.engine.CreateRecordingUiPresentationForTests(media, RecordingUiState.Recording));
            Assert.Equal(0, setup.markHotkey.RegisterCallCount);
            context.SetRecording(setup.engine.CreateRecordingUiPresentationForTests(bundle, RecordingUiState.Recording));
            setup.markHotkey.SimulatePressed();
            Assert.Single(bundle.SnapshotMarks());
            Assert.Empty(media.SnapshotMarks());
            using var result = JsonDocument.Parse(Assert.Single(setup.audit.Events, e => e.evt == "tray.chapter_mark_hotkey_result").json);
            Assert.Equal(1, result.RootElement.GetProperty("attempted_count").GetInt32());
            Assert.Equal(1, result.RootElement.GetProperty("successful_count").GetInt32());
            Assert.Equal(0, result.RootElement.GetProperty("failed_count").GetInt32());
            Assert.Equal("✓ 标记已添加", Assert.Single(setup.feedback.Calls).text);
            bundle.State = RecState.finalizing;
            context.SetFinalizing(setup.engine.CreateRecordingUiPresentationForTests(bundle, RecordingUiState.Finalizing));
            Assert.Equal(1, setup.markHotkey.UnregisterCallCount);
            Assert.False(context.IsChapterMarksHotkeyRegistered);
            Assert.Equal(RecState.recording, media.State);
            context.SetRecording(setup.engine.CreateRecordingUiPresentationForTests(media, RecordingUiState.Recording));
            Assert.Equal(1, setup.markHotkey.RegisterCallCount);
            var next = MakeRecording(RecState.recording, 0);
            Add(setup.engine, next);
            context.SetRecording(setup.engine.CreateRecordingUiPresentationForTests(next, RecordingUiState.Recording));
            Assert.Equal(2, setup.markHotkey.RegisterCallCount);
            Assert.True(context.IsChapterMarksHotkeyRegistered);
        });
    }

    [Fact]
    public void StaleEligibleSnapshotStillRechecksBackendInEngine()
    {
        RunOnSta(() =>
        {
            var setup = CreateContext();
            using var context = setup.context;
            var rec = MakeRecording(RecState.recording, 0);
            Add(setup.engine, rec);
            var snapshot = setup.engine.CreateRecordingUiPresentationForTests(rec, RecordingUiState.Recording);
            Assert.True(snapshot.ChapterMarksEligible);
            context.SetRecording(snapshot);
            rec.BackendType = "wgc-unsupported";
            Assert.True(snapshot.ChapterMarksEligible); // immutable snapshot; domain owns the last check
            setup.markHotkey.SimulatePressed();
            Assert.Empty(rec.SnapshotMarks());
            Assert.DoesNotContain(setup.audit.Events, e => e.evt == "recording.mark_added");
            Assert.DoesNotContain(setup.feedback.Calls, c => c.text.Contains("已添加"));
            context.SetRecording(setup.engine.CreateRecordingUiPresentationForTests(rec, RecordingUiState.Recording));
            Assert.False(context.IsChapterMarksHotkeyRegistered);
        });
    }
}
