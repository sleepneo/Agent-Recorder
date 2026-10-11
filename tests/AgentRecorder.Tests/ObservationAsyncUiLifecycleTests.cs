using System.Collections.Concurrent;
using System.Reflection;
using System.Windows.Forms;
using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;
using static AgentRecorder.Tests.ObservationControllerTests;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class ObservationAsyncUiLifecycleTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(".local-data/temp/task314-ui", Guid.NewGuid().ToString("N")));
    public ObservationAsyncUiLifecycleTests() { Directory.CreateDirectory(_root); DataDirResolver.SetOverride(_root); }
    public void Dispose() { DataDirResolver.ClearOverride(); Directory.Delete(_root, true); }

    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var dispatcher = new Control(); _ = dispatcher.Handle;
                using var context = new WindowsFormsSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(context);
                try { action(); } finally { SynchronizationContext.SetSynchronizationContext(null); }
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Owner test thread did not finish.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static T Find<T>(Control root, string name) where T : Control => (T)root.Controls.Find(name, true).Single();
    private static void Click(Button button) => typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, new object[] { EventArgs.Empty });
    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Yield(); }
        Assert.True(done(), "The gated operation did not finish."); Application.DoEvents();
    }
    private static void Finish(Task operation) { PumpUntil(() => operation.IsCompleted); operation.GetAwaiter().GetResult(); }
    private static void Load(ObservationCenterForm form)
    {
        form.HistoryForTests.Reload(new()); Finish(form.HistoryForTests.WhenIdle); form.RenderHistoryForTests();
    }
    private static HistoryFake History() => new((_, filters, cursor) => new(
        cursor is null ? new[] { Entry("a", recorded: true, exists: true), Entry("b", recorded: true, exists: true) }
            : new[] { Entry("next", recorded: true, exists: true) }, cursor is null ? "next-page" : null, 20, filters));
    private static ObservationCenterForm Window(HistoryFake history, IObservationDesktopActions desktop, IObservationActiveGateway? active = null) =>
        new(active ?? new ActiveFake(), history, () => { }, new UiTextProvider(UiLanguage.EnUs), desktop);
    private static void HandleOnly(ObservationCenterForm form)
    {
        _ = form.Handle; Assert.False(form.Visible);
        // Deliberately defeat await's ambient UI restoration. Production must own delivery.
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
    }
    private static ConcurrentQueue<int> Writes(Control control)
    {
        var threads = new ConcurrentQueue<int>();
        control.TextChanged += (_, _) => threads.Enqueue(Environment.CurrentManagedThreadId);
        control.EnabledChanged += (_, _) => threads.Enqueue(Environment.CurrentManagedThreadId);
        control.Layout += (_, _) => threads.Enqueue(Environment.CurrentManagedThreadId);
        return threads;
    }

    [Theory]
    [InlineData("opened", false)] [InlineData("missing", false)]
    [InlineData("probe_error", false)] [InlineData("open_error", false)]
    [InlineData("opened", true)] [InlineData("missing", true)]
    public void FolderCompletionUsesOwnerAfterGatedBackgroundWork(string outcome, bool plainContext)
    {
        Sta(() =>
        {
            var owner = Environment.CurrentManagedThreadId;
            using var desktop = new GatedDesktop(outcome);
            using var form = Window(History(), desktop); _ = form.Handle;
            if (plainContext) SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            Load(form); var label = Find<Label>(form, "history_status"); var folder = Find<Button>(form, "folder");
            var writes = Writes(label); var buttons = Writes(folder);
            Click(folder); var operation = form.FolderOperationForTests; PumpUntil(() => desktop.Entered.IsCompleted);
            Assert.False(operation.IsCompleted); Assert.False(folder.Enabled); Assert.NotEqual(owner, desktop.ProbeThread);
            desktop.Release.Set(); Finish(operation);
            Assert.Equal(new UiTextProvider(UiLanguage.EnUs).Get(outcome == "opened" ? "Observation_FolderOpened" : "Observation_FolderUnavailable"), label.Text);
            Assert.NotEmpty(writes); Assert.All(writes, id => Assert.Equal(owner, id)); Assert.All(buttons, id => Assert.Equal(owner, id));
            Assert.Equal(outcome == "opened" ? 1 : 0, desktop.Opened.Count);
            Assert.True(folder.Enabled); Assert.False(form.Visible);
        });
    }

    [Fact]
    public void ClosingDuringFolderProbeNeverWritesDisposedOrReopenedWindow()
    {
        Sta(() =>
        {
            using var desktop = new GatedDesktop("opened"); var history = History();
            using var old = Window(history, desktop); HandleOnly(old); Load(old);
            var label = Find<Label>(old, "history_status"); var writes = Writes(label); var creates = 0;
            old.HandleCreated += (_, _) => creates++;
            Click(Find<Button>(old, "folder")); var operation = old.FolderOperationForTests; PumpUntil(() => desktop.Entered.IsCompleted);
            old.Dispose(); writes.Clear();
            using var reopened = Window(history, new DesktopFake()); _ = reopened.Handle; Load(reopened);
            var current = Find<Label>(reopened, "history_status").Text;
            desktop.Release.Set(); Finish(operation);
            Assert.Empty(writes); Assert.Equal(0, creates); Assert.False(old.IsHandleCreated);
            Assert.Equal(current, Find<Label>(reopened, "history_status").Text); Assert.False(history.Disposed);
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void QueuedFolderCompletionRechecksClosureAndHandleGeneration(bool recreateHandle)
    {
        Sta(() =>
        {
            using var desktop = new GatedDesktop("opened"); using var form = Window(History(), desktop);
            HandleOnly(form); Load(form); var queue = new ConcurrentQueue<Action>();
            form.CompletionPostForTests = action => queue.Enqueue(action);
            var writes = Writes(Find<Label>(form, "history_status")); var creates = 0; form.HandleCreated += (_, _) => creates++;
            Click(Find<Button>(form, "folder")); var operation = form.FolderOperationForTests;
            PumpUntil(() => desktop.Entered.IsCompleted); desktop.Release.Set(); PumpUntil(() => queue.Count == 1);
            Assert.False(operation.IsCompleted);
            if (recreateHandle)
                typeof(Control).GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
            else form.Dispose();
            Finish(operation); writes.Clear(); Assert.True(queue.TryDequeue(out var callback)); callback();
            Assert.Empty(writes); Assert.Equal(recreateHandle ? 1 : 0, creates);
            Assert.Equal(recreateHandle, form.IsHandleCreated);
        });
    }

    [Fact]
    public void RecreatedHandleCannotReceiveResultFromProbeStartedInOldLifetime()
    {
        Sta(() =>
        {
            using var desktop = new GatedDesktop("opened"); using var form = Window(History(), desktop);
            HandleOnly(form); Load(form); var folder = Find<Button>(form, "folder");
            Click(folder); var operation = form.FolderOperationForTests; PumpUntil(() => desktop.Entered.IsCompleted);
            typeof(Control).GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
            var expected = Find<Label>(form, "history_status").Text; var writes = Writes(Find<Label>(form, "history_status"));
            Click(folder); Assert.Same(operation, form.FolderOperationForTests); Assert.Equal(1, desktop.Probes);
            desktop.Release.Set(); Finish(operation);
            Assert.Empty(writes); Assert.Equal(expected, Find<Label>(form, "history_status").Text); Assert.True(folder.Enabled);
        });
    }

    [Theory]
    [InlineData("item")] [InlineData("page")] [InlineData("filter")]
    [InlineData("language")] [InlineData("tab")]
    public void SupersededFolderResultsCannotOverwriteNewStateOrCreateDuplicateWork(string change)
    {
        Sta(() =>
        {
            using var desktop = new GatedDesktop("opened"); using var form = Window(History(), desktop);
            HandleOnly(form); Load(form); var folder = Find<Button>(form, "folder");
            if (change == "tab") _ = form.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TabControl>().Single().Handle;
            Click(folder); var operation = form.FolderOperationForTests; PumpUntil(() => desktop.Entered.IsCompleted);
            var grid = Find<DataGridView>(form, "history_grid");
            switch (change)
            {
                case "item": grid.ClearSelection(); grid.Rows[1].Selected = true; break;
                case "page": Click(Find<Button>(form, "next")); Finish(form.HistoryForTests.WhenIdle); form.RenderHistoryForTests(); break;
                case "filter": Find<ComboBox>(form, "source").SelectedIndex = 1; Finish(form.HistoryForTests.WhenIdle); form.RenderHistoryForTests(); break;
                case "language": form.UpdateLanguage(new UiTextProvider(UiLanguage.ZhCn)); break;
                case "tab": var tabs = form.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TabControl>().Single(); tabs.SelectedIndex = 1; tabs.SelectedIndex = 0; break;
            }
            var expected = Find<Label>(form, "history_status").Text;
            Assert.False(folder.Enabled); Click(folder); Assert.Same(operation, form.FolderOperationForTests); Assert.Equal(1, desktop.Probes);
            desktop.Release.Set(); Finish(operation);
            Assert.Equal(expected, Find<Label>(form, "history_status").Text); Assert.True(folder.Enabled); Assert.Single(desktop.Opened);
        });
    }

    [Theory]
    [InlineData("folder")] [InlineData("stop")]
    public void WorkerCompletionWithoutWindowHandleIsDiscardedWithoutCreatingOne(string action)
    {
        Sta(() =>
        {
            using var desktop = new GatedDesktop("opened"); var pending = new TaskCompletionSource<ObservationStopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = new OwnerActive { Stop = _ => pending.Task };
            using var form = Window(History(), desktop, active); Load(form);
            Assert.False(form.IsHandleCreated); SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            var label = Find<Label>(form, action == "folder" ? "history_status" : "active_status");
            var writes = Writes(label); var creates = 0; form.HandleCreated += (_, _) => creates++;
            Click(Find<Button>(form, action)); var operation = action == "folder" ? form.FolderOperationForTests : form.StopOperationForTests;
            var expected = label.Text; writes.Clear(); Assert.False(operation.IsCompleted);
            if (action == "folder") { PumpUntil(() => desktop.Entered.IsCompleted); desktop.Release.Set(); }
            else pending.SetResult(new(ObservationStopOutcome.Requested));
            Finish(operation);
            Assert.Empty(writes); Assert.Equal(expected, label.Text); Assert.False(form.IsHandleCreated); Assert.Equal(0, creates);
        });
    }

    [Theory]
    [InlineData("requested")] [InlineData("failed")] [InlineData("throws")] [InlineData("not_stoppable")]
    public void ExactStopCompletionAndFailuresUpdateOnlyOwner(string outcome)
    {
        Sta(() =>
        {
            var owner = Environment.CurrentManagedThreadId;
            var pending = new TaskCompletionSource<ObservationStopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = new OwnerActive { Stop = _ => pending.Task };
            using var form = Window(History(), new DesktopFake(), active); HandleOnly(form);
            var label = Find<Label>(form, "active_status"); var writes = Writes(label); var button = Find<Button>(form, "stop");
            var buttonWrites = Writes(button);
            Click(button); var operation = form.StopOperationForTests; Assert.False(operation.IsCompleted); Assert.False(button.Enabled);
            Click(button); Assert.Same(operation, form.StopOperationForTests); Assert.Equal(new[] { "a" }, active.Stopped);
            writes.Clear();
            if (outcome == "throws") pending.SetException(new ApiException(409, "GATED_FAILURE", "fixture"));
            else pending.SetResult(new(outcome switch { "requested" => ObservationStopOutcome.Requested, "not_stoppable" => ObservationStopOutcome.NotStoppable, _ => ObservationStopOutcome.Failed }, "GATED_FAILURE"));
            Finish(operation);
            var text = new UiTextProvider(UiLanguage.EnUs);
            Assert.Equal(outcome switch { "requested" => text.Get("Observation_StopRequested"), "not_stoppable" => text.Get("Observation_StopNotStoppable"), _ => text.Format("Observation_StopFailed", "GATED_FAILURE") }, label.Text);
            Assert.All(writes, id => Assert.Equal(owner, id)); Assert.NotEmpty(writes); Assert.All(buttonWrites, id => Assert.Equal(owner, id));
            Assert.Equal(new[] { "a" }, active.Stopped); Assert.True(button.Enabled);
        });
    }

    [Theory]
    [InlineData("target")] [InlineData("language")] [InlineData("closed")] [InlineData("queued_close")]
    public void OldStopCompletionCannotWriteChangedTargetOrClosedWindow(string change)
    {
        Sta(() =>
        {
            var pending = new TaskCompletionSource<ObservationStopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = new OwnerActive { Stop = _ => pending.Task }; using var form = Window(History(), new DesktopFake(), active);
            HandleOnly(form); var queue = new ConcurrentQueue<Action>();
            if (change == "queued_close") form.CompletionPostForTests = action => queue.Enqueue(action);
            Click(Find<Button>(form, "stop")); var operation = form.StopOperationForTests;
            Assert.False(operation.IsCompleted); var label = Find<Label>(form, "active_status"); var writes = Writes(label);
            if (change == "target") { var grid = Find<DataGridView>(form, "active_grid"); grid.ClearSelection(); grid.Rows[1].Selected = true; }
            if (change == "language") form.UpdateLanguage(new UiTextProvider(UiLanguage.ZhCn));
            if (change == "closed") form.Dispose();
            var expected = label.Text; writes.Clear(); pending.SetResult(new(ObservationStopOutcome.Failed, "OLD_TARGET_FAILURE"));
            if (change == "queued_close") { PumpUntil(() => queue.Count == 1); form.Dispose(); writes.Clear(); }
            Finish(operation);
            if (change == "queued_close") { Assert.True(queue.TryDequeue(out var callback)); callback(); }
            Assert.Empty(writes); Assert.Equal(expected, label.Text); Assert.Equal(new[] { "a" }, active.Stopped);
            if (!form.IsDisposed) Assert.True(Find<Button>(form, "stop").Enabled);
            else Assert.False(form.IsHandleCreated);
        });
    }

    [Fact]
    public void PendingFolderMessageIsInvalidatedByOrdinaryStatusChangeOnOwner()
    {
        Sta(() =>
        {
            var owner=Environment.CurrentManagedThreadId;using var desktop=new GatedDesktop("missing");
            using var form=Window(History(),desktop);HandleOnly(form);
            Find<ComboBox>(form,"source").SelectedIndex=1;PumpUntil(()=>form.HistoryForTests.WhenIdle.IsCompleted);
            Click(Find<Button>(form,"folder"));var operation=form.FolderOperationForTests;PumpUntil(()=>desktop.Entered.IsCompleted);
            Find<ComboBox>(form,"status_filter").SelectedIndex=7;PumpUntil(()=>form.HistoryForTests.WhenIdle.IsCompleted);
            var label=Find<Label>(form,"history_status");var expected=label.Text;var writes=Writes(label);writes.Clear();desktop.Release.Set();Finish(operation);
            Assert.Equal(expected,label.Text);Assert.Empty(writes);Assert.Empty(desktop.Opened);Assert.NotEqual(owner,desktop.ProbeThread);Assert.True(Find<Button>(form,"folder").Enabled);
        });
    }

    private sealed class GatedDesktop(string outcome) : IObservationDesktopActions, IDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Entered => _entered.Task;
        internal ManualResetEventSlim Release { get; } = new();
        internal int ProbeThread, Probes;
        internal ConcurrentQueue<string> Opened = new();
        public bool FileExists(string path)
        {
            ProbeThread = Environment.CurrentManagedThreadId; Interlocked.Increment(ref Probes); _entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Probe gate not released.");
            if (outcome == "probe_error") throw new IOException("fake probe error");
            return outcome != "missing";
        }
        public void OpenFolder(string directory) { if (outcome == "open_error") throw new IOException("fake Explorer error"); Opened.Enqueue(directory); }
        public void Copy(string value) => throw new InvalidOperationException("Unexpected clipboard use");
        public void Dispose() { Release.Set(); Release.Dispose(); }
    }
    private sealed class OwnerActive : IObservationActiveGateway
    {
        private readonly int _owner = Environment.CurrentManagedThreadId;
        internal List<string> Stopped = new();
        internal Func<string, Task<ObservationStopResult>> Stop = _ => Task.FromResult(new ObservationStopResult(ObservationStopOutcome.Requested));
        public IReadOnlyList<ObservationActiveRecording> GetObservationSnapshot()
        {
            Assert.Equal(_owner, Environment.CurrentManagedThreadId);
            return new[] { new ObservationActiveRecording(Presentation("a"), true), new ObservationActiveRecording(Presentation("b"), true) };
        }
        public Task<ObservationStopResult> StopObservationRecordingAsync(string recordingId)
        { Assert.Equal(_owner, Environment.CurrentManagedThreadId); Stopped.Add(recordingId); return Stop(recordingId); }
    }
}
