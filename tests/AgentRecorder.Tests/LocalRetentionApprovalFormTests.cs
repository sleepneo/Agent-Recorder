using System.Drawing;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class LocalRetentionApprovalFormTests
{
    [Theory]
    [InlineData(UiLanguage.ZhCn,1f)][InlineData(UiLanguage.ZhCn,1.5f)][InlineData(UiLanguage.ZhCn,2f)]
    [InlineData(UiLanguage.EnUs,1f)][InlineData(UiLanguage.EnUs,1.5f)][InlineData(UiLanguage.EnUs,2f)]
    public void CompleteLocalizedScopeAndScalableSafeLayoutWithoutShowingWindow(UiLanguage language,float scale)
    {
        Sta(()=>
        {
            var clock=new LocalRetentionApprovalFixture.Clock();var path=@"D:\Videos\"+new string('中',180)+".mp4";
            var facts=new LocalRetentionFacts("candidate",new(path,12,new(123,456,789)),UnifiedHistoryTestDatabase.At,clock.GetUtcNow());
            var intent=LocalRetentionIntent.ForConfirmedFacts(facts,clock);var text=new UiTextProvider(language);using var form=new LocalRetentionApprovalForm(intent,text);
            Assert.True(form.SafeDefaults);Assert.True(form.Inspectable);Assert.Equal(AutoScaleMode.Dpi,form.AutoScaleMode);Assert.False(form.ApproveButton.Enabled);
            Assert.Equal(text.Get("RetentionApproval_Approve"),form.ApproveButton.Text);Assert.Contains(path,form.ScopeText);Assert.Contains("12",form.ScopeText);
            Assert.Contains(facts.CompletedAtUtc.ToString("O"),form.ScopeText);Assert.Contains(language==UiLanguage.ZhCn?"不进入回收站":"Recycle Bin",form.WarningText);
            Assert.Contains(language==UiLanguage.ZhCn?"历史记录保留":"History is retained",form.WarningText);
            Assert.Contains(language==UiLanguage.ZhCn?"不授权录屏":"does not authorize recording",form.WarningText);
            form.CreateControl();form.PerformLayout();form.Scale(new SizeF(scale,scale));form.PerformLayout();Assert.True(form.LayoutSeparated);
            Assert.False(form.Visible);Assert.Equal(LocalRetentionUiChoice.Unavailable,form.Decision.Choice);
            clock.Advance(60);form.RefreshValidity();Assert.False(form.ApproveButton.Enabled);
        });
    }
    [Theory]
    [InlineData(Keys.Enter)][InlineData(Keys.Escape)]
    public void KeyboardAlwaysRejectsAndNeverInvokesProductionShow(Keys key)
    {
        Sta(()=>
        {
            var clock=new LocalRetentionApprovalFixture.Clock();var intent=LocalRetentionIntent.ForConfirmedFacts(new("id",new(@"D:\Videos\中.mp4",12,new(1,2,3)),UnifiedHistoryTestDatabase.At,clock.GetUtcNow()),clock);
            using var form=new LocalRetentionApprovalForm(intent,new UiTextProvider(UiLanguage.ZhCn));form.ActiveControl=form.ApproveButton;form.ProcessSafetyKey(key);
            Assert.Equal(LocalRetentionUiChoice.Escape,form.Decision.Choice);Assert.False(form.Visible);
        });
    }
    [Fact]
    public void SharedVisibilityPrimitiveRequiresVisibleUnminimizedInBoundsWindow()
    {
        var valid=new FutureWindowApprovalNativeState(true,false,true,new(0,0,500,400),new(0,0,1920,1080),1,1,0);
        Assert.True(LocalApprovalWindowPresentation.Usable(valid));Assert.False(LocalApprovalWindowPresentation.Usable(valid with{IsVisible=false}));
        Assert.False(LocalApprovalWindowPresentation.Usable(valid with{IsMinimized=true}));Assert.False(LocalApprovalWindowPresentation.Usable(valid with{IsWithinWorkingArea=false}));
    }
    private static void Sta(Action action)
    {Exception? error=null;var t=new Thread(()=>{try{action();}catch(Exception e){error=e;}});t.SetApartmentState(ApartmentState.STA);t.Start();Assert.True(t.Join(TimeSpan.FromSeconds(10)));if(error is not null)throw new InvalidOperationException("Control test failed",error);}

    [Theory]
    [InlineData("hidden")][InlineData("minimized")][InlineData("bounds")][InlineData("throw")]
    public void InvisibleOrUninspectablePresentationIsUnavailableWithoutNativeShow(string fault)
    {
        Sta(()=>
        {
            var clock=new LocalRetentionApprovalFixture.Clock();var intent=LocalRetentionIntent.ForConfirmedFacts(new("id",new(@"D:\Videos\中.mp4",12,new(1,2,3)),UnifiedHistoryTestDatabase.At,clock.GetUtcNow()),clock);
            var platform=new Platform(fault);using var form=new LocalRetentionApprovalForm(intent,new UiTextProvider(UiLanguage.ZhCn),platform);
            form.PresentCurrentWindow();Assert.Equal(LocalRetentionUiChoice.Unavailable,form.Decision.Choice);Assert.False(form.ApproveButton.Enabled);Assert.False(form.Visible);Assert.Equal(1,platform.Shows);
        });
    }

    [Fact]
    public async Task ProductionAdapterPreCancelledStaActuallyExitsWithoutShowingOrApproving()
    {
        var clock=new LocalRetentionApprovalFixture.Clock();var intent=LocalRetentionIntent.ForConfirmedFacts(new("id",new(@"D:\Videos\中.mp4",12,new(1,2,3)),UnifiedHistoryTestDatabase.At,clock.GetUtcNow()),clock);
        using var cancel=new CancellationTokenSource();cancel.Cancel();var ui=new LocalRetentionApprovalUi(new UiTextProvider(UiLanguage.EnUs));var session=ui.Begin(intent,cancel.Token);
        Assert.NotEqual(LocalRetentionUiChoice.Approve,(await session.Response.WaitAsync(TimeSpan.FromSeconds(5))).Choice);Assert.True(await session.Retirement.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    private sealed class Platform(string fault):IFutureWindowApprovalWindowPlatform
    {
        internal int Shows;
        public void ShowNormal(nint handle){Shows++;}
        public FutureWindowApprovalNativeState Inspect(nint handle)=>fault=="throw"?throw new IOException("PRIVATE_VISIBILITY"):new(fault!="hidden",fault=="minimized",fault!="bounds",new(0,0,500,400),new(0,0,1920,1080),1,1,0);
        public FutureWindowApprovalForegroundState Activate(nint handle)=>new(false,false,false);
    }
}
