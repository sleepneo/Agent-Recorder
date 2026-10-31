using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Infrastructure;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class FailureNotificationVisibilityTests
{
    private sealed class Affinity : IWindowDisplayAffinity
    {
        internal readonly List<IntPtr> Handles=new();internal bool Result=true;
        public bool SetExcludeFromCapture(IntPtr handle){Handles.Add(handle);return Result;}
        public bool GetAffinity(IntPtr handle,out uint value){value=0;return Result;}
    }
    private static void Sta(Action action)
    {
        Exception? error=null;var thread=new Thread(()=>{try{action();}catch(Exception e){error=e;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(30)));if(error is not null)ExceptionDispatchInfo.Capture(error).Throw();
    }
    [Theory][InlineData(UiLanguage.ZhCn)][InlineData(UiLanguage.EnUs)]
    public void ActualHiddenFormIsLargerAndMoreLegible(UiLanguage language)=>Sta(()=>
    {
        using var form=new RecordingFailureNotificationForm(new("visibility","storage_space_low"),new UiTextProvider(language),new Affinity(),_=>{});
        Assert.False(form.Visible);Assert.InRange(form.ClientSize.Width,450,470);
        Assert.InRange(form.TitleLabelForTests.Font.SizeInPoints,11.9f,12.1f);Assert.InRange(form.BodyLabelForTests.Font.SizeInPoints,10.9f,11.1f);
        Assert.True(form.CloseButtonForTests.Width>=96);Assert.True(form.CloseButtonForTests.Height>=34);
    });

    private static readonly string[] Reasons=["storage_space_low","storage_capacity_unavailable","display_unavailable","window_closed","window_minimized","size_changed","capture_semantics_changed","audio_capture_discontinuous","backend_creation_failed","unexpected_exit"];
    public static IEnumerable<object[]> Viewports()=>from language in new[]{UiLanguage.ZhCn,UiLanguage.EnUs} from dpi in new[]{96,144,192,240} from small in new[]{false,true} select new object[]{language,dpi,small};
    private static Rectangle Area(bool small)=>small?new(-780,-580,640,480):new(0,0,3840,2160);
    private static Bitmap Render(RecordingFailureNotificationForm form)
    {
        _=form.Handle;form.PerformLayout();Assert.False(form.Visible);
        var bitmap=new Bitmap(form.Width,form.Height,PixelFormat.Format32bppArgb);form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));
        // Hidden Form.DrawToBitmap omits children whose effective Visible is
        // false. Print the actual controls separately at their actual bounds;
        // this is labelled synthetic composition, never a desktop screenshot.
        var screen=form.PointToScreen(Point.Empty);var origin=new Point(screen.X-form.Left,screen.Y-form.Top);
        foreach(var control in new Control[]{form.TitleLabelForTests,form.BodyLabelForTests,form.CloseButtonForTests})
            control.DrawToBitmap(bitmap,new Rectangle(origin.X+control.Left,origin.Y+control.Top,control.Width,control.Height));
        var data=Pixels(bitmap);
        foreach(var label in new[]{form.TitleLabelForTests,form.BodyLabelForTests})
        {
            int ink=0;for(int y=label.Top;y<label.Bottom;y++)for(int x=label.Left;x<label.Right;x++)
                if(data.Pixels[(origin.Y+y)*data.Width+origin.X+x]==label.ForeColor.ToArgb())ink++;
            Assert.True(ink>10,"Hidden-control rendering must contain actual text pixels");
        }
        Assert.False(form.Visible);return bitmap;
    }
    private static (int[] Pixels,int Width,int Height) Pixels(Bitmap bitmap)
    {
        var data=bitmap.LockBits(new Rectangle(Point.Empty,bitmap.Size),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try{Assert.Equal(bitmap.Width*4,data.Stride);var pixels=new int[bitmap.Width*bitmap.Height];Marshal.Copy(data.Scan0,pixels,0,pixels.Length);return(pixels,bitmap.Width,bitmap.Height);}
        finally{bitmap.UnlockBits(data);}
    }
    private static Rectangle FindAmber(Bitmap bitmap)
    {
        var data=Pixels(bitmap);var target=Color.FromArgb(0xD6,0xA0,0x00).ToArgb();int left=data.Width,top=data.Height,right=-1,bottom=-1;
        for(int y=0;y<data.Height;y++)for(int x=0;x<data.Width;x++)if(data.Pixels[y*data.Width+x]==target){left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);}
        Assert.True(right>=left&&bottom>=top,"No actual amber frame pixels found");return Rectangle.FromLTRB(left,top,right+1,bottom+1);
    }
    private static void FramePixels(Bitmap bitmap,Rectangle frame,Color color,int thickness)
    {
        var p=Pixels(bitmap);var rgb=color.ToArgb();
        for(int y=frame.Top;y<frame.Bottom;y++)for(int x=frame.Left;x<frame.Right;x++)
            if(x<frame.Left+thickness||x>=frame.Right-thickness||y<frame.Top+thickness||y>=frame.Bottom-thickness)
                Assert.Equal(rgb,p.Pixels[y*p.Width+x]);
        Assert.NotEqual(rgb,p.Pixels[(frame.Top+thickness)*p.Width+frame.Left+frame.Width/2]);
    }
    private static void Save(Bitmap bitmap,RecordingFailureNotificationForm form,string name,int dpi,bool small,string reason)
    {
        var root=Environment.GetEnvironmentVariable("TASK373_RENDER_ROOT");if(string.IsNullOrEmpty(root))return;
        Directory.CreateDirectory(root);var path=Path.Combine(root,name+".png");Assert.False(File.Exists(path),"Do not overwrite visual evidence");bitmap.Save(path,ImageFormat.Png);
        File.WriteAllText(Path.Combine(root,name+".json"),JsonSerializer.Serialize(new{synthetic_dpi=true,hidden_control_composition=true,shown=false,dpi,small_work_area=small,reason,form.Size,form.ClientSize,form.Bounds,content_scale=form.ContentScaleForTests,border_pixels=form.BorderPixelsForTests,border_color=form.BorderColorForTests.ToArgb(),title=form.TitleLabelForTests.Bounds,body=form.BodyLabelForTests.Bounds,button=form.CloseButtonForTests.Bounds},new JsonSerializerOptions{WriteIndented=true}));
    }
    [Theory,MemberData(nameof(Viewports))]
    public void ActualControlsFitEveryReasonAndFrameSurvivesPainting(UiLanguage language,int dpi,bool small)=>Sta(()=>
    {
        var text=new UiTextProvider(language);var area=Area(small);
        foreach(var reason in Reasons)
        {
            Assert.True(RecordingFailureNotificationManager.IsSupportedReason(reason));using var form=new RecordingFailureNotificationForm(new("layout",reason),text,new Affinity(),_=>{});
            form.ApplyViewportForTests(dpi,area,false);_=form.Handle;form.PerformLayout();Assert.False(form.Visible);
            Assert.True(area.Contains(form.Bounds),$"Window outside working area: {form.Bounds} / {area}");Assert.Equal(Size.Empty,form.MinimumSize);
            var title=form.TitleLabelForTests;var body=form.BodyLabelForTests;var button=form.CloseButtonForTests;var edge=form.BorderPixelsForTests;
            foreach(var control in new Control[]{title,body,button})
            {
                Assert.True(form.ClientRectangle.Contains(control.Bounds),$"Clipped control {control.Bounds} / {form.ClientRectangle}");
                Assert.True(control.Left>edge&&control.Top>edge&&control.Right<form.ClientSize.Width-edge&&control.Bottom<form.ClientSize.Height-edge);
            }
            Assert.True(title.Bottom<body.Top);Assert.True(body.Bottom<button.Top);
            foreach(var label in new[]{title,body})
            {
                var preferred=label.GetPreferredSize(new Size(label.Width,0));Assert.True(preferred.Height<=label.Height,$"Actual label wants {preferred.Height}, has {label.Height}: {reason}/{language}/{dpi}/{small}");
                var measured=TextRenderer.MeasureText(label.Text,label.Font,new Size(label.Width,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl|TextFormatFlags.NoPrefix);
                Assert.True(measured.Height<=label.Height);Assert.False(label.AutoEllipsis);
            }
            var closeText=TextRenderer.MeasureText(button.Text,button.Font);Assert.True(closeText.Width<button.Width&&closeText.Height<button.Height);
            Assert.True(button.Width>=96*dpi/96&&button.Height>=34*dpi/96);
            Assert.Equal(text.Get(RecordingFailureNotificationLayout.TitleKey(reason)),title.Text);Assert.Equal(text.Get(RecordingFailureNotificationLayout.BodyKey(reason)),body.Text);
            Assert.Equal(GraphicsUnit.Pixel,title.Font.Unit);Assert.InRange(form.ContentScaleForTests,.5f,dpi/96f+.001f);
            if(!small){Assert.Equal(460*dpi/96,form.ClientSize.Width);Assert.Equal(dpi/96f,form.ContentScaleForTests);}
            if(reason=="storage_space_low"||(!small&&dpi==144&&language==UiLanguage.EnUs&&reason=="backend_creation_failed")||(small&&dpi==240&&language==UiLanguage.ZhCn&&reason=="audio_capture_discontinuous"))
            {
                using var bitmap=Render(form);var frame=FindAmber(bitmap);Assert.Equal(form.ClientSize,frame.Size);FramePixels(bitmap,frame,Color.FromArgb(0xD6,0xA0,0x00),edge);
                if(!small&&dpi==96&&language==UiLanguage.ZhCn&&reason=="storage_space_low")Save(bitmap,form,"zh-storage-96",dpi,small,reason);
                if(!small&&dpi==144&&language==UiLanguage.EnUs&&reason=="backend_creation_failed")Save(bitmap,form,"en-not-started-144",dpi,small,reason);
                if(small&&dpi==240&&language==UiLanguage.ZhCn&&reason=="audio_capture_discontinuous")Save(bitmap,form,"zh-audio-small-240",dpi,small,reason);
            }
        }
    });

    [Theory][InlineData(96)][InlineData(192)]
    public void HighContrastSelectsSystemColorWithoutChangingGlobalTheme(int dpi)=>Sta(()=>
    {
        using var form=new RecordingFailureNotificationForm(new("contrast","backend_creation_failed"),new UiTextProvider(UiLanguage.EnUs),new Affinity(),_=>{});
        form.ApplyViewportForTests(dpi,Area(false),false);using var normal=Render(form);var frame=FindAmber(normal);
        form.ApplyViewportForTests(dpi,Area(false),true);Assert.Equal(SystemColors.WindowText,form.BorderColorForTests);using var contrast=Render(form);
        FramePixels(contrast,frame,SystemColors.WindowText,form.BorderPixelsForTests);if(dpi==192)Save(contrast,form,"synthetic-high-contrast-192",dpi,false,"backend_creation_failed");
    });

    [Theory][InlineData("timeout")][InlineData("escape")][InlineData("button")][InlineData("enter")]
    public void HiddenLifecycleRetainsAffinityNoActivateEightSecondsAndCloseOwnership(string close)=>Sta(()=>
    {
        var affinity=new Affinity();var closed=new List<RecordingFailureNotificationCloseReason>();using var form=new RecordingFailureNotificationForm(new("lifecycle","window_closed"),new UiTextProvider(UiLanguage.EnUs),affinity,closed.Add);
        _=form.Handle;Assert.Contains(form.Handle,affinity.Handles);Assert.True(form.DisplayAffinityAppliedForTests);Assert.True(form.TopMost);Assert.False(form.ShowInTaskbar);Assert.True(form.ShowWithoutActivationForTests);
        Assert.Equal(FormBorderStyle.FixedDialog,form.FormBorderStyle);Assert.NotEqual(0,form.ExtendedStyleForTests&0x8000000);Assert.NotEqual(0,form.ExtendedStyleForTests&0x80);
        var timer=(System.Windows.Forms.Timer)typeof(RecordingFailureNotificationForm).GetField("_dismissTimer",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(form)!;Assert.Equal(8000,timer.Interval);
        typeof(RecordingFailureNotificationForm).GetMethod("OnShown",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(form,[EventArgs.Empty]);Assert.False(form.Visible);Assert.True(form.TimerEnabledForTests);
        Assert.Same(form.CloseButtonForTests,form.AcceptButton);Assert.Same(form.CloseButtonForTests,form.CancelButton);
        if(close=="timeout")typeof(System.Windows.Forms.Timer).GetMethod("OnTick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(timer,[EventArgs.Empty]);
        else if(close=="escape")typeof(Control).GetMethod("OnKeyDown",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(form,[new KeyEventArgs(Keys.Escape)]);
        else typeof(Button).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(form.CloseButtonForTests,[EventArgs.Empty]);
        form.CloseFor(RecordingFailureNotificationCloseReason.Timeout);Assert.True(form.TimerDisposedForTests);Assert.True(form.IsDisposed);Assert.Single(closed);
        Assert.Equal(close=="timeout"?RecordingFailureNotificationCloseReason.Timeout:RecordingFailureNotificationCloseReason.UserDismissed,closed[0]);
    });
}
