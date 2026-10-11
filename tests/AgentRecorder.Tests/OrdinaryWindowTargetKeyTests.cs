using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Windows;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class OrdinaryWindowTargetKeyTests
{
    internal static CapturePlan Plan(nint hwnd = 42, int pid = 7, int width = 320, int height = 240,
        string semantics = "window_surface", int x = 0, int y = 0, string title = "public-unused",
        AudioCaptureSourceKind audio = AudioCaptureSourceKind.None) => new("required_window_surface", "wgc-window",
            new("required_window_surface", "wgc-window", "approved_test", "test", null, false),
            semantics, "window", title, hwnd, new(x,y,width,height), audioSourceKind:audio,
            targetWindowProcessId:pid, targetWindowSurfaceBounds:new(x,y,width,height));
    internal static WindowProcessMetadata Facts(nint hwnd = 42, int pid = 7, long creation = 133000000000000000,
        int session = 1) => new(hwnd,pid,creation,session);
    internal static string Key => OrdinaryWindowTargetKey.Create(Plan(),Facts())!;

    [Fact]
    public void StableFrozenFactsExcludeMutablePresentationAndRecordingOptions()
    {
        var key=Key;
        Assert.True(OrdinaryWindowTargetKey.IsValid(key));Assert.False(OrdinaryScreenTargetKey.IsValid(key));
        Assert.Equal(key,OrdinaryWindowTargetKey.Create(Plan(x:900,y:-400,title:"changed title and window_id",audio:AudioCaptureSourceKind.SystemLoopback),Facts()));
        var rec=new Recording("r") {SourceType="window",Config=new(){RequireWindowSurface=true},OutputPath="one.mp4",DurationSeconds=10};
        rec.FreezeOrdinaryTarget(Plan(),Facts());rec.OutputPath="two.mp4";rec.DurationSeconds=60;rec.SourceTitle="different";
        Assert.Equal(key,rec.OrdinaryTargetKey);Assert.Throws<InvalidOperationException>(()=>rec.FreezeOrdinaryTarget(Plan(hwnd:99),Facts(hwnd:99)));
        // Same process + reused HWND + same size can repeat facts. This is deliberately NOT a permanent generation ID.
        Assert.Equal(key,OrdinaryWindowTargetKey.Create(Plan(),Facts()));
        Assert.Equal("target:v1:window:943a6988af14588ed47e5cf33b2e95bc3fe2876c4bed644f2a0b578712439e78",key);
    }

    [Theory]
    [InlineData("pid")] [InlineData("creation")] [InlineData("session")] [InlineData("hwnd")] [InlineData("width")] [InlineData("height")]
    public void EveryIdentityOrSurfaceFieldChangesTheKey(string field)
    {
        var plan=Plan();var facts=Facts();
        if(field=="pid"){plan=Plan(pid:8);facts=Facts(pid:8);}
        if(field=="hwnd"){plan=Plan(hwnd:43);facts=Facts(hwnd:43);}
        if(field=="creation")facts=Facts(creation:133000000000000001);
        if(field=="session")facts=Facts(session:2);
        if(field=="width")plan=Plan(width:321);
        if(field=="height")plan=Plan(height:241);
        Assert.NotEqual(Key,OrdinaryWindowTargetKey.Create(plan,facts));
    }

    [Theory]
    [InlineData("tr-TR")] [InlineData("ar-SA")] [InlineData("zh-CN")]
    public void EncodingIsInvariantAndScreenV1BytesStayUnchanged(string culture)
    {
        var prior=CultureInfo.CurrentCulture;
        try {CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(culture);Assert.Equal(Key,OrdinaryWindowTargetKey.Create(Plan(),Facts()));
            Assert.NotEqual(OrdinaryWindowTargetKey.Create(Plan(pid:12),Facts(pid:12,session:3)),OrdinaryWindowTargetKey.Create(Plan(pid:1),Facts(pid:1,session:23)));
            Assert.False(OrdinaryScreenTargetKey.IsValid(Key));}
        finally {CultureInfo.CurrentCulture=prior;}
    }

    [Theory]
    [InlineData("zero_hwnd")] [InlineData("wrong_hwnd")] [InlineData("pid")] [InlineData("wrong_pid")]
    [InlineData("creation")] [InlineData("overflow")] [InlineData("session")] [InlineData("width")] [InlineData("height")]
    [InlineData("semantics")] [InlineData("unknown")]
    public void IllegalOrIncompleteFactsAreUnknown(string fault)
    {
        var plan=Plan();WindowProcessMetadata? facts=Facts();
        switch(fault) {
            case "zero_hwnd":plan=Plan(hwnd:0);break;case "wrong_hwnd":facts=Facts(hwnd:43);break;
            case "pid":plan=Plan(pid:0);break;case "wrong_pid":facts=Facts(pid:8);break;
            case "creation":facts=Facts(creation:0);break;case "overflow":facts=Facts(creation:long.MaxValue);break;
            case "session":facts=Facts(session:-1);break;case "width":plan=Plan(width:0);break;
            case "height":plan=Plan(height:-1);break;case "semantics":plan=Plan(semantics:"screen_rectangle");break;case "unknown":facts=null;break;
        }
        Assert.Null(OrdinaryWindowTargetKey.Create(plan,facts));Assert.Null(OrdinaryWindowTargetKey.Create(null,facts));
    }

    [Fact]
    public void PublicKeyValidationIsCanonicalAndNestedWindowStaysUnknown()
    {
        foreach(var bad in new[]{Key.ToUpperInvariant(),Key+"0",Key[..^1],Key.Replace("window","Window"),Key[..^1]+"g"," "+Key})
            Assert.False(OrdinaryWindowTargetKey.IsValid(bad));
        var rec=new Recording("r"){SourceType="window",NestedRole="outer",Config=new(){RequireWindowSurface=true}};
        rec.FreezeOrdinaryTarget(Plan(),Facts());Assert.Null(rec.OrdinaryTargetKey);
    }
}

public sealed class OrdinaryWindowMetadataNativeTests
{
    internal sealed class HiddenWindow : IDisposable
    {
        internal nint Handle {get;private set;}=CreateWindowEx(0,"STATIC","Task336 owned hidden metadata",0,0,0,320,240,(nint)(-3),0,0,0);
        internal HiddenWindow() {Assert.NotEqual(0,Handle);}
        public void Dispose(){if(Handle!=0){Assert.True(DestroyWindow(Handle));Handle=0;}}
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="CreateWindowExW",SetLastError=true)]
    private static extern nint CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint module,nint data);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [Fact]
    public void OwnHiddenHwndUsesActualLifetimeSessionAndAlwaysClosesQueryHandle()
    {
        using var w=new HiddenWindow();SafeProcessHandle? opened=null;
        var facts=WindowProcessMetadataReader.ObserveForTests(w.Handle,Environment.ProcessId,h=>opened=h);
        Assert.NotNull(facts);Assert.NotNull(opened);Assert.True(opened.IsClosed);
        using var self=Process.GetCurrentProcess();
        Assert.Equal(self.StartTime.ToUniversalTime().ToFileTimeUtc(),facts.CreationFileTimeUtc);Assert.Equal(self.SessionId,facts.SessionId);
        Assert.Equal(Environment.ProcessId,facts.ProcessId);Assert.Equal(w.Handle,facts.WindowHandle);
        Assert.Null(WindowProcessMetadataReader.Observe(w.Handle,Environment.ProcessId+1));
        Assert.Null(WindowProcessMetadataReader.Observe(0,Environment.ProcessId));
        var handle=w.Handle;w.Dispose();Assert.Null(WindowProcessMetadataReader.Observe(handle,Environment.ProcessId));
    }

    [Fact]
    public void WindowDestroyedDuringReadIsUnknownAndNativeHandleIsReleased()
    {
        using var w=new HiddenWindow();SafeProcessHandle? opened=null;
        Assert.Null(WindowProcessMetadataReader.ObserveForTests(w.Handle,Environment.ProcessId,h=>{opened=h;w.Dispose();}));
        Assert.NotNull(opened);Assert.True(opened.IsClosed);
    }

    [Fact]
    public async Task OwnedHiddenChildProcessExitingDuringReadIsUnknownAndHandleCloses()
    {
        // Child creates only its own message-only HWND; no desktop pixels or user windows are involved.
        var code="using System;using System.Runtime.InteropServices; public static class T336 { [DllImport(\"user32.dll\",CharSet=CharSet.Unicode,EntryPoint=\"CreateWindowExW\")] public static extern IntPtr C(uint e,string c,string t,uint s,int x,int y,int w,int h,IntPtr p,IntPtr m,IntPtr i,IntPtr d); }";
        var script="Add-Type -TypeDefinition '"+code+"'; $w=[T336]::C(0,'STATIC','Task336',0,0,0,320,240,[IntPtr](-3),[IntPtr]::Zero,[IntPtr]::Zero,[IntPtr]::Zero); [Console]::WriteLine($w.ToInt64()); [Console]::Out.Flush(); [Console]::ReadLine() | Out-Null";
        var psi=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        psi.ArgumentList.Add("-NoProfile");psi.ArgumentList.Add("-NonInteractive");psi.ArgumentList.Add("-Command");psi.ArgumentList.Add(script);
        using var child=Process.Start(psi)!;
        try {
            var line=await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(long.TryParse(line,out var raw),line);Assert.NotEqual(0,raw);
            SafeProcessHandle? opened=null;
            var facts=WindowProcessMetadataReader.ObserveForTests((nint)raw,child.Id,h=>{opened=h;child.Kill();Assert.True(child.WaitForExit(5000));});
            Assert.Null(facts);Assert.NotNull(opened);Assert.True(opened.IsClosed);
            Assert.Null(WindowProcessMetadataReader.Observe((nint)raw,child.Id));
        } finally {if(!child.HasExited){child.Kill();child.WaitForExit(5000);}}
    }
}
