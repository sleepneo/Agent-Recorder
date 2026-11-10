using System.Drawing;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
namespace AgentRecorder.Tests;
public sealed class PolicyDeletion405UiTests {
 [Theory][InlineData(UiLanguage.ZhCn,1f)][InlineData(UiLanguage.ZhCn,1.5f)][InlineData(UiLanguage.ZhCn,2f)][InlineData(UiLanguage.EnUs,1f)][InlineData(UiLanguage.EnUs,1.5f)][InlineData(UiLanguage.EnUs,2f)]
 public void PolicyIsReadableLocalizedAndSafeWithoutShowingWindow(UiLanguage language,float scale) {
  Exception? error=null;var thread=new Thread(()=>{try {
   var clock=new LocalRetentionApprovalFixture.Clock();var policy=new RetentionDeletionPolicy(clock.GetUtcNow().AddDays(-1),20);
   var facts=new LocalRetentionFacts("owned-policy-video",new(@"D:\Owned\Videos\待删除-test.mp4",12,new(1,2,3)),UnifiedHistoryTestDatabase.At,clock.GetUtcNow()){Policy=policy};
   var intent=LocalRetentionIntent.ForConfirmedFacts(facts,clock);using var form=new LocalRetentionApprovalForm(intent,new UiTextProvider(language));
   Assert.Contains(policy.CompletedBefore.ToString("O"),form.ScopeText);Assert.Contains("20",form.ScopeText);Assert.Contains(language==UiLanguage.ZhCn?"预览不是批准":"preview is not approval",form.ScopeText);
   Assert.True(form.SafeDefaults);Assert.False(form.Visible);Assert.False(form.ApproveButton.Enabled);Assert.True(form.Inspectable);
   form.CreateControl();form.PerformLayout();form.Scale(new SizeF(scale,scale));form.PerformLayout();Assert.True(form.LayoutSeparated);
   var root=Path.Combine(Path.GetTempPath(),"task405-ui");Directory.CreateDirectory(root);using var canvas=new Panel {Size=form.ClientSize,Font=form.Font};var content=form.Controls[0];form.Controls.Remove(content);canvas.Controls.Add(content);content.Dock=DockStyle.Fill;canvas.CreateControl();content.CreateControl();canvas.PerformLayout();content.PerformLayout();using var bitmap=new Bitmap(canvas.Width,canvas.Height);canvas.DrawToBitmap(bitmap,new Rectangle(0,0,canvas.Width,canvas.Height));bitmap.Save(Path.Combine(root,language+"-"+scale.ToString(System.Globalization.CultureInfo.InvariantCulture)+".png"));
  }catch(Exception e){error=e;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(10)));if(error is not null)throw new InvalidOperationException("Policy UI verification failed",error);
 }
}
