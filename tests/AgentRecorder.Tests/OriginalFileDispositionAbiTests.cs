using System.Reflection;
using System.Runtime.InteropServices;
using AgentRecorder.Infrastructure;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class OriginalFileDispositionAbiTests
{
    [Fact]
    public void ClassicDispositionAbiUsesByteBooleanAndBoolReturnAndLastError()
    {
        var t=typeof(WindowsOriginalFileHoldNative);var s=t.GetNestedType("DispositionInformation",BindingFlags.NonPublic)!;
        Assert.Equal(1,Marshal.SizeOf(s));Assert.Equal(typeof(byte),Assert.Single(s.GetFields(BindingFlags.Instance|BindingFlags.NonPublic)).FieldType);
        var m=t.GetMethod("SetFileInformationByHandle",BindingFlags.Static|BindingFlags.NonPublic)!;var dll=m.GetCustomAttribute<DllImportAttribute>()!;
        Assert.True(dll.SetLastError);Assert.True(dll.ExactSpelling);Assert.Equal("kernel32.dll",dll.Value);Assert.Equal(UnmanagedType.Bool,m.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>()!.Value);
        Assert.Equal(typeof(SafeFileHandle),m.GetParameters()[0].ParameterType);Assert.Equal(typeof(uint),m.GetParameters()[3].ParameterType);
    }
    [Fact]
    public void InvalidHandleIsRejectedByRealAdapterWithExactNativeError()
    {
        if(!OperatingSystem.IsWindows())return;
        using var invalid=new SafeFileHandle(new IntPtr(-1),false);var r=WindowsOriginalFileHoldNative.Instance.RequestDisposition(invalid);
        Assert.Equal(OriginalFileDispositionOutcome.Rejected,r.Outcome);Assert.Equal(6,r.Win32Error);
    }
}
