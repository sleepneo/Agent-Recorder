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
            Assert.Equal(0, (setup.markHotkey?.RegisterCallCount ?? 0));
            Assert.False(context.IsChapterMarksHotkeyRegistered);
            setup.markHotkey?.SimulatePressed();
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
            Assert.Equal(0, (setup.markHotkey?.RegisterCallCount ?? 0));
            context.SetRecording(setup.engine.CreateRecordingUiPresentationForTests(bundle, RecordingUiState.Recording));
            setup.markHotkey?.SimulatePressed();
            Assert.Empty(bundle.SnapshotMarks());Assert.Empty(media.SnapshotMarks());Assert.Empty(setup.feedback.Calls);
            Assert.DoesNotContain(setup.audit.Events,e=>e.evt=="recording.mark_added");
            Assert.False(context.IsChapterMarksHotkeyRegistered);Assert.False(context.SupportsChapterMarksLocalHotkey);

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
            var snapshot = setup.engine.CreateRecordingUiPresentationForTests(rec, RecordingUiState.Recording) with { ChapterMarksEligible=true };
            Assert.True(snapshot.ChapterMarksEligible);
            context.SetRecording(snapshot);
            rec.BackendType = "wgc-unsupported";
            Assert.True(snapshot.ChapterMarksEligible); // immutable snapshot; domain owns the last check
            setup.markHotkey?.SimulatePressed();
            Assert.Empty(rec.SnapshotMarks());
            Assert.DoesNotContain(setup.audit.Events, e => e.evt == "recording.mark_added");
            Assert.DoesNotContain(setup.feedback.Calls, c => c.text.Contains("已添加"));
            context.SetRecording(setup.engine.CreateRecordingUiPresentationForTests(rec, RecordingUiState.Recording));
            Assert.False(context.IsChapterMarksHotkeyRegistered);
        });
    }
}
