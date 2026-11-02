using System.Drawing;
using System.Reflection;
using AgentRecorder.App;
using AgentRecorder.Infrastructure;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;
public sealed partial class IndependentWindow382UiTests
{
    [Theory][InlineData(UiLanguage.ZhCn,96)][InlineData(UiLanguage.EnUs,96)]
    [InlineData(UiLanguage.ZhCn,192)][InlineData(UiLanguage.EnUs,192)]
    public void IndependentWindow383TenHiddenControlsKeepMeasuredNumbersAndExactClickTargets(UiLanguage language,int dpi)
    {
        Sta(()=>{
            var text=new UiTextProvider(language);var clicked=new List<string>();
            var manager=new RecordingIndicatorManager(new RetentionSharedOutputPathTests.QuietAudit(),_=>{},text);
            var display=new DisplayDpiInfo("synthetic",new(0,0,3840,2160),dpi,dpi,dpi/96f,false,null);
            var indicators=(Dictionary<string,RecordingIndicatorForm>)typeof(RecordingIndicatorManager).GetField("_indicators",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(manager)!;
            var stops=(Dictionary<string,RecordingStopControlForm>)typeof(RecordingIndicatorManager).GetField("_stopControls",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(manager)!;
            var occupied=new List<Rectangle>();
            try {
                for(int n=1;n<=10;n++) {
                    var rec=new RecordingUiPresentation {RecordingId="rec-"+n,State=RecordingUiState.Recording,SourceType="window",CaptureBounds=new(100,100,64,64),DurationSeconds=120,WindowIdentity=new(n,"合成长标题 Paint & Notepad")};
                    var plan=manager.ComputeControlPlan(rec,null,display.MonitorBounds,display)!;
                    var button=new Rectangle(plan.StopBounds.X,plan.StopBounds.Y,plan.StopBounds.Width,plan.StopBounds.Height);
                    foreach(var rect in occupied){Assert.False(rect.IntersectsWith(button));Assert.False(rect.IntersectsWith(plan.IndicatorPresentation.LabelBounds));}
                    Assert.False(button.IntersectsWith(plan.IndicatorPresentation.LabelBounds));occupied.Add(button);occupied.Add(plan.IndicatorPresentation.LabelBounds);
                    var stop=new RecordingStopControlForm(rec.RecordingId,plan.StopBounds,plan.StopControlSize,display,CaptureVisibilityMode.ExcludeFromCapture,null,text,new Affinity());
                    stop.SetWindowIdentity(rec.WindowIdentity);stop.StopClicked+=clicked.Add;stops.Add(rec.RecordingId,stop);
                    var indicator=new RecordingIndicatorForm(rec.RecordingId,plan.IndicatorPresentation,DateTime.UtcNow,displayAffinity:new Affinity(),textProviderFactory:()=>text);
                    indicator.SetWindowIdentity(rec.WindowIdentity);indicators.Add(rec.RecordingId,indicator);
                    Assert.Contains((language==UiLanguage.ZhCn?"窗口":"WIN")+n,stop.ButtonTextForTests);Assert.False(stop.Visible);Assert.False(indicator.Visible);
                }
                var click=typeof(RecordingStopControlForm).GetMethod("OnButtonClick",BindingFlags.Instance|BindingFlags.NonPublic)!;
                foreach(var entry in stops.Reverse()){click.Invoke(entry.Value,new object?[]{null,EventArgs.Empty});click.Invoke(entry.Value,new object?[]{null,EventArgs.Empty});}
                Assert.Equal(Enumerable.Range(1,10).Reverse().Select(n=>"rec-"+n),clicked);
            } finally {foreach(var form in indicators.Values)form.Dispose();foreach(var form in stops.Values)form.Dispose();indicators.Clear();stops.Clear();}
        });
    }
}
