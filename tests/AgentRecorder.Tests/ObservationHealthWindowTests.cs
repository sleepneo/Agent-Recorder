using AgentRecorder.Api;
using System.Collections.Concurrent;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Infrastructure;
using Xunit;
using static AgentRecorder.Tests.ObservationControllerTests;
using static AgentRecorder.Tests.ObservationHealthGatewayTests;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class ObservationHealthWindowTests : IDisposable
{
    private readonly string _root=Path.GetFullPath(Path.Combine(".local-data/temp/task316-ui",Guid.NewGuid().ToString("N")));
    public ObservationHealthWindowTests(){Directory.CreateDirectory(_root);DataDirResolver.SetOverride(_root);}
    public void Dispose(){DataDirResolver.ClearOverride();Directory.Delete(_root,true);}
    internal static void Sta(Action action)
    {
        Exception? error=null;var thread=new Thread(()=>{try {
            using var dispatcher=new Control();_=dispatcher.Handle;using var context=new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);try{action();}finally{SynchronizationContext.SetSynchronizationContext(null);}
        }catch(Exception ex){error=ex;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if(error is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static T Find<T>(Control root,string name) where T:Control=>(T)root.Controls.Find(name,true).Single();
    private static TabControl Tabs(ObservationCenterForm form)=>form.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TabControl>().Single();
    private static void Click(Button button)=>typeof(Button).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(button,new object[]{EventArgs.Empty});
    private static void Pump(Func<bool> done)
    {var end=DateTime.UtcNow.AddSeconds(10);while(!done()&&DateTime.UtcNow<end){Application.DoEvents();Thread.Yield();}Assert.True(done());Application.DoEvents();}
    private static void Finish(Task task){Pump(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
    private static HistoryFake History()=>new((_,filters,_)=>new(Array.Empty<AgentRecorder.Core.UnifiedHistoryEntry>(),null,20,filters));
    private static ObservationCenterForm Window(IObservationHealthGateway health,IObservationDesktopActions? desktop=null,HistoryFake? history=null)=>
        new(new ActiveFake { Rows=new[]{new ObservationActiveRecording(Presentation("a"),true),new ObservationActiveRecording(Presentation("b"),true)} },history??History(),()=>{},new UiTextProvider(UiLanguage.EnUs),desktop??new CopyFake(),health:health);
    private static void Enter(ObservationCenterForm form){_=form.Handle;_=Tabs(form).Handle;Assert.False(form.Visible);Tabs(form).SelectedIndex=2;}
    private static TaskCompletionSource NewGate()=>new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FirstVisitReusesSnapshotManualRefreshLanguageAndHistoryFiltersAreIndependent(bool languageWhilePending)
    {
        Sta(()=>
        {
            var owner=Environment.CurrentManagedThreadId;var gateway=new GatedGateway();var history=History();using var form=Window(gateway,history:history);_=form.Handle;
            Assert.Equal(0,gateway.Calls);Find<ComboBox>(form,"source").SelectedIndex=1;Finish(form.HistoryForTests.WhenIdle);
            Find<ComboBox>(form,"status_filter").SelectedIndex=7;Finish(form.HistoryForTests.WhenIdle);var filters=form.HistoryForTests.View.Filters;var historyCalls=history.Calls.Count;
            var activeGrid=Find<DataGridView>(form,"active_grid");activeGrid.ClearSelection();activeGrid.Rows[1].Selected=true;
            var label=Find<Label>(form,"health_status");var writes=new List<int>();label.TextChanged+=(_,_)=>writes.Add(Environment.CurrentManagedThreadId);
            Enter(form);var operation=form.HealthOperationForTests;Assert.Equal(1,gateway.Calls);Assert.Equal(new[]{owner},gateway.OwnerThreads);
            Click(Find<Button>(form,"health_refresh"));Assert.Same(operation,form.HealthOperationForTests);Assert.Equal(1,gateway.Calls);
            if(languageWhilePending)form.UpdateLanguage(new UiTextProvider(UiLanguage.ZhCn));
            gateway.Pending.SetResult(ObservationHealthSnapshot.Partial(Owner(),HealthProbeState.Complete));Finish(operation);
            Assert.NotEmpty(writes);Assert.All(writes,id=>Assert.Equal(owner,id));
            Tabs(form).SelectedIndex=1;Tabs(form).SelectedIndex=2;Assert.Equal(1,gateway.Calls);
            if(!languageWhilePending)form.UpdateLanguage(new UiTextProvider(UiLanguage.ZhCn));
            Assert.Equal(historyCalls,history.Calls.Count);Assert.Equal(filters,form.HistoryForTests.View.Filters);Assert.Equal(7,Find<ComboBox>(form,"status_filter").SelectedIndex);
            Assert.DoesNotContain("Health_",Find<TextBox>(form,"health_preview").Text);Assert.Contains("采样完成",label.Text);
            gateway.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);Click(Find<Button>(form,"health_refresh"));Assert.Equal(2,gateway.Calls);
            gateway.Pending.SetResult(ObservationHealthSnapshot.Partial(Owner(),HealthProbeState.Complete));Finish(form.HealthOperationForTests);
            Assert.Equal("b",((ObservationActiveRecording)Assert.Single(activeGrid.SelectedRows.Cast<DataGridViewRow>()).Tag!).Presentation.RecordingId);
            Assert.Empty(((CopyFake)GetDesktop(form)).Copied);Assert.False(form.ActiveTimerEnabledForTests);
        });
    }
    private static IObservationDesktopActions GetDesktop(ObservationCenterForm form)=>(IObservationDesktopActions)typeof(ObservationCenterForm).GetField("_desktop",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(form)!;

    [Theory]
    [InlineData("plain")] [InlineData("null")] [InlineData("windows")]
    public void CompletionUsesOwnerEvenWithDifferentAmbientContext(string ambient)
    {
        Sta(()=>
        {
            var gateway=new GatedGateway();using var form=Window(gateway);_=form.Handle;
            if(ambient=="plain")SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());if(ambient=="null")SynchronizationContext.SetSynchronizationContext(null);
            var owner=Environment.CurrentManagedThreadId;var writes=new List<int>();var preview=Find<TextBox>(form,"health_preview");preview.TextChanged+=(_,_)=>writes.Add(Environment.CurrentManagedThreadId);
            Enter(form);Task.Run(()=>gateway.Pending.SetResult(ObservationHealthSnapshot.Partial(Owner(),HealthProbeState.Complete))).GetAwaiter().GetResult();Finish(form.HealthOperationForTests);
            Assert.NotEmpty(writes);Assert.All(writes,id=>Assert.Equal(owner,id));Assert.False(form.Visible);
        });
    }

    [Theory]
    [InlineData("close")] [InlineData("queued_close")] [InlineData("recreate")] [InlineData("no_handle")]
    public void StaleHealthCompletionCannotTouchClosedOrRecreatedOrHandlelessWindow(string change)
    {
        Sta(()=>
        {
            var gateway=new GatedGateway();using var form=Window(gateway);var queue=new ConcurrentQueue<Action>();
            if(change!="no_handle"){_=form.Handle;_=Tabs(form).Handle;}
            if(change=="queued_close")form.CompletionPostForTests=callback=>queue.Enqueue(callback);
            Tabs(form).SelectedIndex=2;
            if(change=="no_handle")typeof(TabControl).GetMethod("OnSelectedIndexChanged",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Tabs(form),new object[]{EventArgs.Empty});
            Assert.Equal(1,gateway.Calls);var operation=form.HealthOperationForTests;var preview=Find<TextBox>(form,"health_preview");var writes=new List<int>();preview.TextChanged+=(_,_)=>writes.Add(Environment.CurrentManagedThreadId);
            if(change=="close")form.Dispose();
            if(change=="recreate")typeof(Control).GetMethod("RecreateHandle",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(form,null);
            var creates=0;form.HandleCreated+=(_,_)=>creates++;
            gateway.Pending.SetResult(ObservationHealthSnapshot.Partial(Owner(),HealthProbeState.Complete));
            if(change=="queued_close"){Pump(()=>queue.Count==1);form.Dispose();}
            Finish(operation);while(queue.TryDequeue(out var callback))callback();
            Assert.Empty(writes);Assert.Empty(preview.Text);Assert.Equal(0,creates);
            if(change is "close" or "queued_close" or "no_handle")Assert.False(form.IsHandleCreated);
            if(change=="recreate")
            {
                gateway.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);Click(Find<Button>(form,"health_refresh"));
                gateway.Pending.SetResult(ObservationHealthSnapshot.Partial(Owner(),HealthProbeState.Complete));Finish(form.HealthOperationForTests);Assert.NotEmpty(preview.Text);
            }
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TimeoutAndCloseReopenCannotStackBackgroundProbe(bool closeFirst)
    {
        Sta(()=>
        {
            var owner=Environment.CurrentManagedThreadId;using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();var completed=NewGate();var calls=0;var probeThread=0;
            var probes=new ObservationHealthProbes {Ffmpeg=()=>{probeThread=Environment.CurrentManagedThreadId;Interlocked.Increment(ref calls);entered.Set();try{Assert.True(release.Wait(TimeSpan.FromSeconds(10)));return null;}finally{completed.TrySetResult();}}};
            var gateway=new ObservationHealthGateway(Owner,probes);using var form=Window(gateway);var deadline=NewGate();form.HealthDeadlineForTests=ct=>deadline.Task.WaitAsync(ct);
            Enter(form);var first=form.HealthOperationForTests;Pump(()=>entered.IsSet);Assert.NotEqual(owner,probeThread);
            Assert.Equal(1,calls);Click(Find<Button>(form,"health_refresh"));Assert.Same(first,form.HealthOperationForTests);
            if(closeFirst)form.Dispose();else deadline.SetResult();Finish(first);
            if(!closeFirst){Assert.Contains("timed out",Find<Label>(form,"health_status").Text);Assert.Contains("PID",Find<TextBox>(form,"health_preview").Text);}
            using var reopened=Window(gateway);Enter(reopened);Finish(reopened.HealthOperationForTests);Assert.Contains("previous host probe",Find<Label>(reopened,"health_status").Text);
            for(var i=0;i<5;i++){Click(Find<Button>(reopened,"health_refresh"));Finish(reopened.HealthOperationForTests);}Assert.Equal(1,calls);
            release.Set();Finish(completed.Task);reopened.Dispose();Assert.False(reopened.Visible);
        });
    }

    [Theory]
    [InlineData("sample")] [InlineData("owner")]
    public void ErrorsAreShortAndDoNotExposeRawExceptions(string failure)
    {
        Sta(()=>
        {
            var gateway=new GatedGateway{OwnerFails=failure=="owner"};using var form=Window(gateway);Enter(form);
            if(failure=="sample")gateway.Pending.TrySetException(new IOException("API_KEY_SECRET stack trace"));else gateway.Pending.TrySetResult(ObservationHealthSnapshot.Partial(Owner(),HealthProbeState.Complete));
            Finish(form.HealthOperationForTests);
            Assert.Contains("failed",Find<Label>(form,"health_status").Text);Assert.DoesNotContain("SECRET",Find<TextBox>(form,"health_preview").Text);
        });
    }

    [Theory]
    [InlineData(UiLanguage.EnUs,1f)] [InlineData(UiLanguage.EnUs,1.5f)] [InlineData(UiLanguage.EnUs,2f)]
    [InlineData(UiLanguage.ZhCn,1f)] [InlineData(UiLanguage.ZhCn,1.5f)] [InlineData(UiLanguage.ZhCn,2f)]
    public void HiddenFormMinimumScaledLayoutLongPathsSelectableDetailsAndExplicitCopy(UiLanguage language,float scale)
    {
        Sta(()=>
        {
            var path=@"C:\Synthetic\"+new string('p',180)+@"\ffmpeg.exe";var gateway=new GatedGateway();var desktop=new CopyFake();using var form=Window(gateway,desktop);
            form.UpdateLanguage(new UiTextProvider(language));var baseMinimum=form.MinimumSize;form.Scale(new SizeF(scale,scale));
            form.MinimumSize=new((int)Math.Ceiling(baseMinimum.Width*scale),(int)Math.Ceiling(baseMinimum.Height*scale));form.Size=form.MinimumSize;Enter(form);
            var sample=ObservationHealthSnapshot.Partial(Owner() with{DataDirectory=Path.GetDirectoryName(path)},HealthProbeState.Complete) with {Ffmpeg=new(HealthState.Available,path,"project_tools","path_only")};
            gateway.Pending.SetResult(sample);Finish(form.HealthOperationForTests);Layout(form);
            var grid=Find<DataGridView>(form,"health_grid");Assert.True(grid.ReadOnly);Assert.Equal(15,grid.Rows.Count);
            grid.ClearSelection();grid.Rows.Cast<DataGridViewRow>().Single(row=>((ObservationHealthRow)row.Tag!).Id=="ffmpeg").Selected=true;
            var details=Find<TextBox>(form,"health_details");Assert.Contains(path,details.Text);Assert.True(details.ReadOnly);Assert.False(details.WordWrap);Assert.Equal(ScrollBars.Both,details.ScrollBars);Assert.True(details.Height>20);
            var preview=Find<TextBox>(form,"health_preview");Assert.Contains(path,preview.Text);Assert.True(preview.Height>20);Assert.Equal(ScrollBars.Both,preview.ScrollBars);
            foreach(var flow in Descendants(Tabs(form).TabPages[2]).OfType<FlowLayoutPanel>())foreach(Control child in flow.Controls)
            {Assert.True(child.Right<=flow.ClientSize.Width+1);Assert.True(child.Bottom<=flow.ClientSize.Height+1);}
            var expected=preview.Text;Assert.Empty(desktop.Copied);Click(Find<Button>(form,"health_copy"));Assert.Equal(expected,Assert.Single(desktop.Copied));
            desktop.Throw=true;Click(Find<Button>(form,"health_copy"));Assert.Equal(new UiTextProvider(language).Get("Observation_CopyFailed"),Find<Label>(form,"health_status").Text);
            var opposite=language==UiLanguage.EnUs?UiLanguage.ZhCn:UiLanguage.EnUs;form.UpdateLanguage(new UiTextProvider(opposite));Assert.Contains(path,details.Text);Assert.Equal("ffmpeg",((ObservationHealthRow)Assert.Single(grid.SelectedRows.Cast<DataGridViewRow>()).Tag!).Id);Assert.Equal(1,gateway.Calls);
            Assert.DoesNotContain("Health_",preview.Text);Assert.False(form.Visible);
        });
    }
    private static void Layout(Control root)
    {if(root is TabControl tabs)foreach(TabPage page in tabs.TabPages)page.Bounds=tabs.DisplayRectangle;if(root is TabPage)foreach(Control child in root.Controls)if(child.Dock==DockStyle.Fill)child.Bounds=root.DisplayRectangle;root.PerformLayout();foreach(Control child in root.Controls)Layout(child);}
    private static IEnumerable<Control> Descendants(Control root){foreach(Control child in root.Controls){yield return child;foreach(var nested in Descendants(child))yield return nested;}}
    private sealed class GatedGateway:IObservationHealthGateway
    {
        internal int Calls;internal bool OwnerFails;internal List<int> OwnerThreads=new();
        internal TaskCompletionSource<ObservationHealthSnapshot> Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ObservationHealthOwner CaptureOwner(){OwnerThreads.Add(Environment.CurrentManagedThreadId);if(OwnerFails)throw new IOException("SID_SECRET");return Owner();}
        public Task<ObservationHealthSnapshot> SampleAsync(ObservationHealthOwner owner,CancellationToken token){Calls++;return Pending.Task;}
    }
    private sealed class CopyFake:IObservationDesktopActions
    {internal List<string> Copied=new();internal bool Throw;public bool FileExists(string path)=>throw new InvalidOperationException();public void OpenFolder(string path)=>throw new InvalidOperationException();public void Copy(string text){if(Throw)throw new IOException("secret");Copied.Add(text);}}
}
