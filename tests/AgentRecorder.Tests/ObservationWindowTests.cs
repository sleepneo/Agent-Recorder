using AgentRecorder.Api;
using System.Collections.Concurrent;
using System.Drawing;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using Xunit;
using static AgentRecorder.Tests.ObservationControllerTests;

namespace AgentRecorder.Tests;

// All Forms stay unshown. Affinity contracts and fake stop/backend, clipboard and
// Explorer seams exercise layout and wiring without recording or consent windows.
[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class ObservationWindowTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(".local-data/temp/task312", Guid.NewGuid().ToString("N")));
    public ObservationWindowTests() { Directory.CreateDirectory(_root); DataDirResolver.SetOverride(_root); }
    public void Dispose() { DataDirResolver.ClearOverride(); try { Directory.Delete(_root, true); } catch { } }
    private static void Sta(Action action)
    {
        Exception? error = null; var thread = new Thread(() => { try
        {
            using var dispatcher = new Control(); _ = dispatcher.Handle;
            using var context = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try { action(); } finally { SynchronizationContext.SetSynchronizationContext(null); }
        } catch (Exception e) { error = e; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static T Control<T>(Control root, string name) where T : Control => (T)root.Controls.Find(name, true).Single();
    private static T Field<T>(object root, string name) => (T)root.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root)!;
    private static void PumpUntil(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(10); }
        Assert.True(condition()); Application.DoEvents();
    }
    private static void ClickUnshown(Button button) => typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, new object[] { EventArgs.Empty });
    private static void Layout(Control root)
    {
        // Hidden TabPages retain WinForms' 200x100 placeholder bounds until shown.
        // Supply the real tab display rectangle without displaying a native window.
        if (root is TabControl tabs) foreach (TabPage page in tabs.TabPages) page.Bounds = tabs.DisplayRectangle;
        if (root is TabPage) foreach (Control child in root.Controls) if (child.Dock == DockStyle.Fill) child.Bounds = root.DisplayRectangle;
        root.PerformLayout(); foreach (Control child in root.Controls) Layout(child);
    }
    private static void AssertFlowFits(Control root)
    {
        foreach (var flow in Descendants(root).OfType<FlowLayoutPanel>())
        {
            var children = flow.Controls.Cast<Control>().Where(c => c.Width > 0 && c.Height > 0).ToArray();
            foreach (var child in children)
            {
                Assert.True(child.Right <= flow.ClientSize.Width + 1, $"{child.Name}: {child.Bounds} outside {flow.ClientSize} parent={flow.Parent?.Bounds} grand={flow.Parent?.Parent?.Bounds} tab={flow.Parent?.Parent?.Parent?.Bounds} form={root.FindForm()?.Bounds}");
                Assert.True(child.Bottom <= flow.ClientSize.Height + 1, $"{child.Name}: {child.Bounds} outside {flow.ClientSize} parent={flow.Parent?.Bounds} grand={flow.Parent?.Parent?.Bounds} tab={flow.Parent?.Parent?.Parent?.Bounds} form={root.FindForm()?.Bounds}");
            }
            for (var i = 0; i < children.Length; i++) for (var j = i + 1; j < children.Length; j++)
                Assert.False(children[i].Bounds.IntersectsWith(children[j].Bounds), $"{children[i].Name} overlaps {children[j].Name}");
        }
    }
    private static IEnumerable<Control> Descendants(Control root)
    { foreach (Control c in root.Controls) { yield return c; foreach (var child in Descendants(c)) yield return child; } }

    [Theory]
    [InlineData(UiLanguage.ZhCn)] [InlineData(UiLanguage.EnUs)]
    public void UnshownWindowUsesSelectableLongDetailsScalableLayoutAndBothLanguages(UiLanguage language)
    {
        Sta(() =>
        {
            var longId = new string('x', 128); var path = @"C:\Synthetic\" + new string('p', 100) + @"\output.mp4";
            var active = new ActiveFake { Rows = new[] { new ObservationActiveRecording(Presentation(longId) with { NestedRole = "inner", ParentRecordingId = "outer" }, true),
                new ObservationActiveRecording(Presentation("series") with { IsScreenshotSeries = true, SeriesCapturedFrameCount = 3, SeriesPlannedFrameCount = 10 }, true) } };
            var gateway = new HistoryFake((_, f, _) => new(new[] { Entry(longId, recorded: true, exists: false) with { OutputPath = path } }, null, 20, f));
            using var form = new ObservationCenterForm(active, gateway, () => { }, new UiTextProvider(language), new DesktopFake());
            Assert.False(form.TopMost); Assert.Equal(FormBorderStyle.Sizable, form.FormBorderStyle); Assert.Equal(AutoScaleMode.Dpi, form.AutoScaleMode);
            Assert.False(form.ActiveTimerEnabledForTests); Assert.Empty(gateway.Calls);
            var grid = Control<DataGridView>(form, "active_grid"); Assert.Equal(2, grid.Rows.Count);
            grid.ClearSelection(); grid.Rows.Cast<DataGridViewRow>().Single(r => ((ObservationActiveRecording)r.Tag!).Presentation.RecordingId == longId).Selected = true;
            var details = Control<TextBox>(form, "active_details"); Assert.True(details.ReadOnly); Assert.True(details.Multiline); Assert.False(details.WordWrap); Assert.Equal(ScrollBars.Both, details.ScrollBars);
            Assert.Contains(longId, details.Text); Assert.Contains("outer", details.Text);
            form.HistoryForTests.Reload(new()); PumpUntil(() => form.HistoryForTests.WhenIdle.IsCompleted); form.RenderHistoryForTests();
            Assert.Contains(path, Control<TextBox>(form, "history_details").Text);
            Assert.True(Control<Button>(form, "copy_path").Enabled); Assert.False(Control<Button>(form, "folder").Enabled);
            var tabs = form.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TabControl>().Single();
            var baseMinimum = form.MinimumSize; var previous = 1f;
            foreach (var scale in new[] { 1f, 1.5f, 2f })
            {
                if (scale != 1) form.Scale(new SizeF(scale / previous, scale / previous)); previous = scale;
                form.MinimumSize = new((int)Math.Ceiling(baseMinimum.Width * scale), (int)Math.Ceiling(baseMinimum.Height * scale)); form.Size = form.MinimumSize;
                foreach (TabPage tab in tabs.TabPages) { tabs.SelectedTab = tab; Layout(form); AssertFlowFits(tab); }
            }
            foreach (var button in Descendants(form).OfType<Button>()) Assert.DoesNotContain("Observation_", button.Text);
            Assert.DoesNotContain("Observation_", Control<TextBox>(form, "history_details").Text);
            form.UpdateLanguage(new UiTextProvider(language == UiLanguage.ZhCn ? UiLanguage.EnUs : UiLanguage.ZhCn));
            Assert.Contains(longId, Control<TextBox>(form, "history_details").Text); Assert.Single(gateway.Calls);
        });
    }

    [Fact]
    public void DefaultAffinityRemainsCapturableAcrossOwnerLifetimeWithoutChangingIndicators()
    {
        Sta(() =>
        {
            var audit = new AuditFake(); var engine = new RecordingEngine(audit);
            using var tray = new TrayContext(engine, audit, FakeGlobalStopHotkeyFactory.Create()); engine.SetTray(tray);
            Field<NotifyIcon>(tray, "_icon").Visible = false;
            tray.SetObservationHealthGateway(UnavailableObservationHealthGateway.Instance);
            var before = audit.Events.ToArray();
            using var form = tray.GetObservationCenter();
            void AssertDefault(ObservationCenterForm window)
            {
                // Read the owned hidden HWND only; never request an affinity change
                // or capture screen pixels. This is not a real screenshot acceptance.
                Assert.True(WindowDisplayAffinity.Instance.GetAffinity(window.Handle, out var affinity));
                Assert.Equal(Native.WDA_NONE, affinity); Assert.False(window.Visible);
                Assert.Empty(window.Controls.Find("affinity", true));
            }
            AssertDefault(form);
            var tabs = form.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TabControl>().Single(); _ = tabs.Handle;
            foreach (TabPage tab in tabs.TabPages)
            {
                tabs.SelectedTab = tab;
                form.UpdateLanguage(new UiTextProvider(UiLanguage.ZhCn)); AssertDefault(form);
                form.UpdateLanguage(new UiTextProvider(UiLanguage.EnUs)); AssertDefault(form);
            }
            PumpUntil(() => form.HealthOperationForTests.IsCompleted);
            ClickUnshown(Control<Button>(form, "health_refresh")); PumpUntil(() => form.HealthOperationForTests.IsCompleted); AssertDefault(form);
            typeof(Control).GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null); AssertDefault(form);
            ClickUnshown(Control<Button>(form, "health_refresh")); PumpUntil(() => form.HealthOperationForTests.IsCompleted); AssertDefault(form);
            form.Close(); Assert.True(form.IsDisposed);
            using var reopened = tray.GetObservationCenter(); Assert.NotSame(form, reopened); AssertDefault(reopened);
            Assert.Equal(before, audit.Events.ToArray());
            Assert.Empty(engine._recs); Assert.Empty(Field<Dictionary<string, RecordingUiPresentation>>(tray, "_activeRecordings"));
            Assert.DoesNotContain(typeof(ObservationCenterForm).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .SelectMany(c => c.GetParameters()), parameter => parameter.ParameterType == typeof(IWindowDisplayAffinity));
        });
    }

    [Fact]
    public void WindowActionsKeepMissingFilesAndCopiesSeparateAndUseExistingSafetyDelegate()
    {
        Sta(() =>
        {
            var safetyCalls = 0; var desktop = new DesktopFake();
            var gateway = new HistoryFake((_, f, _) => new(new[] { Entry(recorded: true, exists: true) }, null, 20, f));
            using var form = new ObservationCenterForm(new ActiveFake(), gateway, () => safetyCalls++, new UiTextProvider(UiLanguage.EnUs), desktop);
            form.HistoryForTests.Reload(new()); PumpUntil(() => form.HistoryForTests.WhenIdle.IsCompleted); form.RenderHistoryForTests();
            _ = form.Handle; Assert.False(form.Visible);
            ClickUnshown(Control<Button>(form, "copy_path")); Assert.Equal(new[] { @"C:\Synthetic\output.mp4" }, desktop.Copied);
            ClickUnshown(Control<Button>(form, "safety")); Assert.Equal(1, safetyCalls);
            ClickUnshown(Control<Button>(form, "folder")); PumpUntil(() => form.FolderOperationForTests.IsCompleted);
            form.FolderOperationForTests.GetAwaiter().GetResult(); Assert.Contains("missing", Control<Label>(form, "history_status").Text);
            Assert.Empty(desktop.Opened); Assert.Equal("completed", form.HistoryForTests.View.Items[0].Status);
            form.Dispose(); Assert.False(gateway.Disposed); Assert.False(form.ActiveTimerEnabledForTests);
        });
    }

    [Fact]
    public void WorkingAreaMinimumClampsAtHighDpiAndKeepsSmallScreenUsable()
    {
        Assert.Equal(new Size(1160, 840), ObservationCenterForm.MinimumForWorkingArea(new(1920, 1080), 192));
        Assert.Equal(new Size(800, 600), ObservationCenterForm.MinimumForWorkingArea(new(800, 600), 192));
    }

    [Fact]
    public void ActiveRefreshPreservesSelectionAndLateStopCannotOverwriteAnotherTargetOrDisposedControls()
    {
        Sta(() =>
        {
            var pending = new TaskCompletionSource<ObservationStopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = new ActiveFake { Rows = new[] { new ObservationActiveRecording(Presentation("outer"), true), new ObservationActiveRecording(Presentation("inner"), true) }, Stop = _ => pending.Task };
            using var form = new ObservationCenterForm(active, new HistoryFake((_, f, _) => new(Array.Empty<UnifiedHistoryEntry>(), null, 20, f)), () => { }, new UiTextProvider(UiLanguage.EnUs));
            _ = form.Handle; Assert.False(form.Visible);
            var grid = Control<DataGridView>(form, "active_grid"); var details = Control<TextBox>(form, "active_details");
            grid.ClearSelection(); grid.Rows[0].Selected = true;
            var position = details.Text.IndexOf("outer", StringComparison.Ordinal); details.Select(position, 5);
            form.RefreshActiveForTests(); Assert.Equal("outer", details.SelectedText);
            ClickUnshown(Control<Button>(form, "stop")); Assert.Equal(new[] { "outer" }, active.StoppedIds);
            grid.ClearSelection(); grid.Rows[1].Selected = true;
            pending.SetResult(new(ObservationStopOutcome.Failed, "OLD_TARGET_FAILURE"));
            PumpUntil(() => form.StopOperationForTests.IsCompleted); form.StopOperationForTests.GetAwaiter().GetResult();
            Assert.DoesNotContain("OLD_TARGET_FAILURE", Control<Label>(form, "active_status").Text);
            Assert.Contains("inner", details.Text);
            var next = new TaskCompletionSource<ObservationStopResult>(TaskCreationOptions.RunContinuationsAsynchronously); active.Stop = _ => next.Task;
            ClickUnshown(Control<Button>(form, "stop")); form.Dispose(); next.SetResult(new(ObservationStopOutcome.Requested));
            PumpUntil(() => form.StopOperationForTests.IsCompleted); form.StopOperationForTests.GetAwaiter().GetResult(); Assert.False(form.ActiveTimerEnabledForTests);
        });
    }

    [Fact]
    public void UnshownLifecycleStartsOnlyOwnTimerAndStopsItOnCloseWithoutDisposingSharedGateway()
    {
        Sta(() =>
        {
            var gateway = new HistoryFake((_, f, _) => new(Array.Empty<UnifiedHistoryEntry>(), null, 20, f));
            using var form = new ObservationCenterForm(new ActiveFake(), gateway, () => { }, new UiTextProvider(UiLanguage.EnUs));
            typeof(ObservationCenterForm).GetMethod("OnShown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object[] { EventArgs.Empty });
            Assert.False(form.Visible); Assert.True(form.ActiveTimerEnabledForTests);
            PumpUntil(() => form.HistoryForTests.WhenIdle.IsCompleted); Assert.Single(gateway.Calls);
            form.Close(); Assert.False(form.ActiveTimerEnabledForTests); Assert.False(gateway.Disposed);
            Assert.Equal(ObservationHistoryStatus.Closed, form.HistoryForTests.View.Status);
        });
    }

    [Fact]
    public void ExistingStopAllKeepsItsAllTargetsTriggerContractWithoutShowingIndicators()
    {
        Sta(() =>
        {
            var audit = new AuditFake(); var engine = new RecordingEngine(audit);
            using var tray = new TrayContext(engine, audit, FakeGlobalStopHotkeyFactory.Create()); engine.SetTray(tray);
            Field<NotifyIcon>(tray, "_icon").Visible = false;
            var rows = Field<Dictionary<string, RecordingUiPresentation>>(tray, "_activeRecordings");
            var recordings = new[] { new Recording { State = RecState.preparing }, new Recording { State = RecState.preparing } };
            foreach (var rec in recordings) { engine._recs[rec.Id] = rec; rows[rec.Id] = Presentation(rec.Id, RecordingUiState.Preparing); }
            var stopped = new ConcurrentQueue<string>(); engine.BeforeStopForTests = rec => { stopped.Enqueue(rec.Id); throw new ApiException(409, "FAKE_STOP", "fixture"); };
            typeof(TrayContext).GetMethod("StopAll", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tray, new object[] { "tray_menu" });
            PumpUntil(() => stopped.Count == 2);
            Assert.Equal(recordings.Select(r => r.Id).Order(), stopped.Order());
            Assert.Single(audit.Events, e => e.Contains("recording.stop_requested_local") && e.Contains("tray_menu"));
            Assert.Equal(2, audit.Events.Count(e => e.Contains("recording_stop_control.stopping") && e.Contains("tray_menu")));
        });
    }

    [Fact]
    public void TraySnapshotsAreImmutableOwnedByUiThreadAndWindowLifetimeDoesNotTouchRecordingsOrGateway()
    {
        Sta(() =>
        {
            var audit = new AuditFake(); var engine = new RecordingEngine(audit);
            using var tray = new TrayContext(engine, audit, FakeGlobalStopHotkeyFactory.Create(), uiTextProvider: new UiTextProvider(UiLanguage.ZhCn));
            Field<NotifyIcon>(tray, "_icon").Visible = false;
            var rows = Field<Dictionary<string, RecordingUiPresentation>>(tray, "_activeRecordings");
            rows["outer"] = Presentation("outer") with { NestedRole = "outer" }; rows["inner"] = Presentation("inner", RecordingUiState.Finalizing) with { NestedRole = "inner", ParentRecordingId = "outer" };
            var snapshot = tray.GetObservationSnapshot(); Assert.Equal(2, snapshot.Count); Assert.False(snapshot.Single(r => r.Presentation.RecordingId == "inner").CanStop);
            rows.Remove("inner"); Assert.Equal(2, snapshot.Count); Assert.Throws<NotSupportedException>(() => ((IList<ObservationActiveRecording>)snapshot).Clear());
            Exception? threadError = null; var worker = new Thread(() => { try { tray.GetObservationSnapshot(); } catch (Exception ex) { threadError = ex; } }); worker.Start(); Assert.True(worker.Join(TimeSpan.FromSeconds(5))); Assert.IsType<InvalidOperationException>(threadError);
            var gateway = new HistoryFake((_, f, _) => new(Array.Empty<UnifiedHistoryEntry>(), null, 20, f)); tray.SetUnifiedHistoryGateway(gateway);
            var first = tray.GetObservationCenter(); Assert.Same(first, tray.GetObservationCenter()); Assert.False(first.Visible);
            typeof(TrayContext).GetMethod("SetLanguage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tray, new object[] { UiLanguage.EnUs });
            Assert.Equal("Observation center", first.Text); first.Dispose();
            var second = tray.GetObservationCenter(); Assert.NotSame(first, second); Assert.Equal("Observation center", second.Text);
            Assert.Single(rows); Assert.Empty(gateway.Calls); Assert.False(gateway.Disposed); Assert.Empty(Field<HashSet<string>>(tray, "_stoppingIds"));
            tray.Dispose(); Assert.True(second.IsDisposed); Assert.False(gateway.Disposed);
        });
    }

    [Fact]
    public void TrayStopRechecksPhaseUsesExactEngineTargetAndPreservesOtherRecordingOnFailure()
    {
        Sta(() =>
        {
            var audit = new AuditFake(); var engine = new RecordingEngine(audit);
            using var tray = new TrayContext(engine, audit, FakeGlobalStopHotkeyFactory.Create()); engine.SetTray(tray);
            Field<NotifyIcon>(tray, "_icon").Visible = false;
            var rows = Field<Dictionary<string, RecordingUiPresentation>>(tray, "_activeRecordings");
            var a = new Recording { State = RecState.preparing, SourceType = "region" }; var b = new Recording { State = RecState.preparing, SourceType = "region" };
            engine._recs[a.Id] = a; engine._recs[b.Id] = b; rows[a.Id] = Presentation(a.Id, RecordingUiState.Preparing); rows[b.Id] = Presentation(b.Id, RecordingUiState.Preparing);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); var stopped = new ConcurrentQueue<string>();
            engine.BeforeStopForTests = rec => { stopped.Enqueue(rec.Id); entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); throw new ApiException(409, "FAKE_STOP_FAILURE", "fixture"); };
            engine.BackendFactory = _ => throw new InvalidOperationException("Observation started a backend");
            var request = tray.StopObservationRecordingAsync(a.Id); Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var duplicate = tray.StopObservationRecordingAsync(a.Id); Assert.True(duplicate.IsCompleted); Assert.Equal(ObservationStopOutcome.Busy, duplicate.GetAwaiter().GetResult().Outcome);
            release.Set(); PumpUntil(() => request.IsCompleted);
            Assert.Equal(new ObservationStopResult(ObservationStopOutcome.Failed, "FAKE_STOP_FAILURE"), request.GetAwaiter().GetResult());
            Assert.Equal(new[] { a.Id }, stopped.ToArray()); Assert.Equal(RecState.preparing, b.State);
            Assert.Contains(audit.Events, s => s.Contains("observation_center") && s.Contains(a.Id)); Assert.DoesNotContain(audit.Events, s => s.Contains("stop_requested_local") && s.Contains(b.Id));
            rows[a.Id] = Presentation(a.Id, RecordingUiState.Finalizing);
            Assert.Equal(ObservationStopOutcome.NotStoppable, tray.StopObservationRecordingAsync(a.Id).GetAwaiter().GetResult().Outcome);
            Assert.Equal(ObservationStopOutcome.NotStoppable, tray.StopObservationRecordingAsync("removed").GetAwaiter().GetResult().Outcome); Assert.Single(stopped);
        });
    }

    [Theory]
    [InlineData(UiLanguage.ZhCn)] [InlineData(UiLanguage.EnUs)]
    public void OrdinaryStatusSelectorCarriesCodePagesAndRangeResetsSourceAndSurvivesTranslation(UiLanguage language)
    {
        Sta(() =>
        {
            var gateway = new HistoryFake((_, f, cursor) => f.Status == "expired"
                ? new(Array.Empty<UnifiedHistoryEntry>(), null, 20, f)
                : new(new[] { Entry(cursor is null ? "first" : "second", f.Status ?? "preparing") }, cursor is null ? "next" : null, 20, f));
            using var form = new ObservationCenterForm(new ActiveFake(),gateway,()=>{},new UiTextProvider(language),new DesktopFake(),TimeZoneInfo.Utc,()=>UnifiedHistoryTestDatabase.At);
            _=form.Handle; Assert.False(form.Visible);var source=Control<ComboBox>(form,"source");var status=Control<ComboBox>(form,"status_filter");
            Assert.False(status.Enabled);Assert.Equal(0,status.SelectedIndex);Assert.Equal(12,status.Items.Count);
            source.SelectedIndex=1;PumpUntil(()=>form.HistoryForTests.WhenIdle.IsCompleted);Assert.True(status.Enabled);Assert.Null(gateway.Calls.Last().Filters.Status);
            var failed=OrdinaryRecordingHistoryStatusCodes.Supported.ToList().IndexOf("failed")+1;status.SelectedIndex=failed;
            PumpUntil(()=>form.HistoryForTests.WhenIdle.IsCompleted);Assert.Equal("failed",gateway.Calls.Last().Filters.Status);Assert.Null(gateway.Calls.Last().Cursor);
            ClickUnshown(Control<Button>(form,"next"));PumpUntil(()=>form.HistoryForTests.WhenIdle.IsCompleted);
            Assert.Equal("next",gateway.Calls.Last().Cursor);Assert.Equal("failed",gateway.Calls.Last().Filters.Status);
            var calls=gateway.Calls.Count;
            form.UpdateLanguage(new UiTextProvider(language==UiLanguage.ZhCn?UiLanguage.EnUs:UiLanguage.ZhCn));
            Assert.Equal(calls,gateway.Calls.Count);Assert.Equal(failed,status.SelectedIndex);Assert.Contains("second",Control<TextBox>(form,"history_details").Text);
            Assert.DoesNotContain("Observation_",status.Text);Assert.DoesNotContain("Observation_",Control<Label>(form,"status_label").Text);
            Control<ComboBox>(form,"range").SelectedIndex=1;PumpUntil(()=>form.HistoryForTests.WhenIdle.IsCompleted);
            Assert.Null(gateway.Calls.Last().Cursor);Assert.Equal("failed",gateway.Calls.Last().Filters.Status);Assert.NotNull(gateway.Calls.Last().Filters.CreatedFrom);
            status.SelectedIndex=OrdinaryRecordingHistoryStatusCodes.Supported.ToList().IndexOf("expired")+1;PumpUntil(()=>form.HistoryForTests.WhenIdle.IsCompleted);
            Assert.Equal(ObservationHistoryStatus.Empty,form.HistoryForTests.View.Status);Assert.False(Control<Button>(form,"next").Enabled);
            foreach(var index in new[]{2,3,0,1}) {source.SelectedIndex=index;PumpUntil(()=>form.HistoryForTests.WhenIdle.IsCompleted);Assert.Equal(0,status.SelectedIndex);Assert.Equal(index==1,status.Enabled);Assert.Null(gateway.Calls.Last().Filters.Status);Assert.Null(gateway.Calls.Last().Cursor);}
            var tabs=form.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TabControl>().Single();tabs.SelectedIndex=1;
            var minimum=form.MinimumSize;var previous=1f;
            foreach(var scale in new[]{1f,1.5f,2f})
            {
                if(scale!=1)form.Scale(new SizeF(scale/previous,scale/previous));previous=scale;
                form.MinimumSize=new((int)Math.Ceiling(minimum.Width*scale),(int)Math.Ceiling(minimum.Height*scale));form.Size=form.MinimumSize;Layout(form);AssertFlowFits(tabs.SelectedTab!);
                foreach(var item in status.Items.Cast<string>()) Assert.True(TextRenderer.MeasureText(item,status.Font).Width + SystemInformation.VerticalScrollBarWidth <= status.ClientSize.Width,$"Truncated status: {item}");
                Assert.True(Control<DataGridView>(form,"history_grid").Height>0);
            }
        });
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void SlowOldStatusIsSupersededAndLanguageReusesPendingQueryOnOwner(bool supersede)
    {
        Sta(() =>
        {
            var owner=Environment.CurrentManagedThreadId;using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
            var gateway=new HistoryFake((_,f,_)=>{if(f.Status=="failed"){entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(10)));}return new(new[]{Entry(f.Status??"all")},null,20,f);});
            using var form=new ObservationCenterForm(new ActiveFake(),gateway,()=>{},new UiTextProvider(UiLanguage.EnUs));_ = form.Handle;
            var writes=new List<int>();var label=Control<Label>(form,"history_status");label.TextChanged+=(_,_)=>writes.Add(Environment.CurrentManagedThreadId);
            Control<ComboBox>(form,"source").SelectedIndex=1;PumpUntil(()=>form.HistoryForTests.WhenIdle.IsCompleted);
            var status=Control<ComboBox>(form,"status_filter");status.SelectedIndex=7;Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var count=gateway.Calls.Count;form.UpdateLanguage(new UiTextProvider(UiLanguage.ZhCn));Assert.Equal(count,gateway.Calls.Count);Assert.Equal(7,status.SelectedIndex);
            if(supersede) status.SelectedIndex=6; // completed supersedes the still blocked failed request.
            release.Set();PumpUntil(()=>form.HistoryForTests.WhenIdle.IsCompleted);
            var expected=supersede?"completed":"failed";Assert.Equal(expected,form.HistoryForTests.View.Filters.Status);Assert.Equal(expected,Assert.Single(form.HistoryForTests.View.Items).EntryId);
            Assert.Equal(count+(supersede?1:0),gateway.Calls.Count);Assert.Equal(1,gateway.MaxConcurrent);Assert.All(writes,id=>Assert.Equal(owner,id));
        });
    }

    [Fact]
    public void TranslationDuringPendingNextPagePreservesSelectedExistingRowWithoutQueryingAgain()
    {
        Sta(() =>
        {
            using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
            var gateway=new HistoryFake((_,f,cursor)=>{if(cursor is not null){entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(10)));return new(Array.Empty<UnifiedHistoryEntry>(),null,20,f);}return new(new[]{Entry("first"),Entry("selected")},"next",20,f);});
            using var form=new ObservationCenterForm(new ActiveFake(),gateway,()=>{},new UiTextProvider(UiLanguage.EnUs));_=form.Handle;
            Control<ComboBox>(form,"source").SelectedIndex=1;PumpUntil(()=>form.HistoryForTests.WhenIdle.IsCompleted);
            Control<ComboBox>(form,"status_filter").SelectedIndex=7;PumpUntil(()=>form.HistoryForTests.WhenIdle.IsCompleted);
            var grid=Control<DataGridView>(form,"history_grid");grid.ClearSelection();grid.Rows[1].Selected=true;
            ClickUnshown(Control<Button>(form,"next"));Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal("selected",((UnifiedHistoryEntry)Assert.Single(grid.SelectedRows.Cast<DataGridViewRow>()).Tag!).EntryId);
            var calls=gateway.Calls.Count;form.UpdateLanguage(new UiTextProvider(UiLanguage.ZhCn));
            Assert.Equal(calls,gateway.Calls.Count);Assert.Equal(2,grid.Rows.Count);Assert.Equal(7,Control<ComboBox>(form,"status_filter").SelectedIndex);
            Assert.Equal("selected",((UnifiedHistoryEntry)Assert.Single(grid.SelectedRows.Cast<DataGridViewRow>()).Tag!).EntryId);
            Assert.False(Control<Button>(form,"folder").Enabled);Assert.False(Control<Button>(form,"copy_id").Enabled);
            release.Set();PumpUntil(()=>form.HistoryForTests.WhenIdle.IsCompleted);Assert.Equal(ObservationHistoryStatus.Empty,form.HistoryForTests.View.Status);
        });
    }

    private sealed class AuditFake : AuditLogger
    {
        internal ConcurrentQueue<string> Events = new(); public override void Log(string evt, object payload) => Events.Enqueue(evt + ":" + JsonSerializer.Serialize(payload));
    }
}
