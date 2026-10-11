using System.Runtime.InteropServices;
using System.Text;
using AgentRecorder.Infrastructure;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class WindowsReadOnlyFileMetadataProbeTests
{
    internal class Api : IWindowsFileMetadataApi
    {
        internal string Path = @"C:\Task324\Videos\out.mp4";
        internal string? Final;
        internal int OpenCount, BasicCount, StandardCount, IdentityCount, FinalCount;
        internal List<SafeFileHandle> Handles = new();
        internal List<SafeFileHandle> Queried = new();
        internal string? Fail, Throw;
        internal int Error = 50;
        internal uint? ReturnedLength;
        internal uint Type = 3;
        internal bool Enabled = true;
        internal uint Attributes = (uint)FileAttributes.Normal;
        internal Action<int>? BeforeBasic;
        public bool Supported=>Enabled;
        public uint DriveType(string root){Assert.Equal(Path[..3],root);return Type;}
        public SafeFileHandle Open(string path,uint access,uint share,uint creation,uint flags,out int error)
        {
            OpenCount++;Assert.Equal(@"\\?\"+Path,path);Assert.Equal(0x80u,access);Assert.Equal(1u,share);Assert.Equal(3u,creation);Assert.Equal(0x02200000u,flags);
            if(Throw=="open")throw new IOException("SECRET_NATIVE");error=Error;
            var handle=new SafeFileHandle(Fail=="open"?new IntPtr(-1):new IntPtr(123),ownsHandle:false);Handles.Add(handle);return handle;
        }
        private bool Query(SafeFileHandle handle,string step,out int error)
        {Queried.Add(handle);Assert.Same(Handles.Single(),handle);Assert.False(handle.IsClosed);error=Error;if(Throw==step)throw new IOException("SECRET_NATIVE");return Fail!=step;}
        public bool Basic(SafeFileHandle h,out FileBasicInformation info,out int error)
        {BasicCount++;BeforeBasic?.Invoke(BasicCount);info=new(){Attributes=Attributes,LastWriteTime=133000000000000000};return Query(h,"basic",out error);}
        public bool Standard(SafeFileHandle h,out FileStandardInformation info,out int error)
        {StandardCount++;info=new(){EndOfFile=12,AllocationSize=4096,NumberOfLinks=1,DeletePending=0,Directory=0};return Query(h,"standard",out error);}
        public bool Identity(SafeFileHandle h,out FileIdentityInformation info,out int error)
        {IdentityCount++;info=new(){VolumeSerialNumber=1234,IdLow=5678,IdHigh=ulong.MaxValue};return Query(h,"identity",out error);}
        public uint FinalPath(SafeFileHandle h,StringBuilder buffer,int capacity,out int error)
        {FinalCount++;Assert.Equal(2048,capacity);Assert.Equal(capacity,buffer.Capacity);if(!Query(h,"final",out error))return 0;buffer.Append(Final??@"\\?\"+Path);return ReturnedLength??(uint)buffer.Length;}
    }
    [Fact]
    public void NativeLayoutsSizesOffsetsBooleanAndFull128BitsAreExact()
    {
        Assert.Equal(24,Marshal.SizeOf<FileStandardInformation>());Assert.Equal(40,Marshal.SizeOf<FileBasicInformation>());Assert.Equal(24,Marshal.SizeOf<FileIdentityInformation>());
        Assert.Equal(new IntPtr(20),Marshal.OffsetOf<FileStandardInformation>(nameof(FileStandardInformation.DeletePending)));
        Assert.Equal(new IntPtr(21),Marshal.OffsetOf<FileStandardInformation>(nameof(FileStandardInformation.Directory)));
        Assert.Equal(new IntPtr(8),Marshal.OffsetOf<FileIdentityInformation>(nameof(FileIdentityInformation.IdLow)));
        Assert.Equal(new IntPtr(16),Marshal.OffsetOf<FileIdentityInformation>(nameof(FileIdentityInformation.IdHigh)));
        var memory=Marshal.AllocHGlobal(24);try
        {for(var i=0;i<24;i++)Marshal.WriteByte(memory,i,0);Marshal.WriteByte(memory,20,1);Marshal.WriteByte(memory,21,1);var s=Marshal.PtrToStructure<FileStandardInformation>(memory);Assert.Equal(1,s.DeletePending);Assert.Equal(1,s.Directory);}
        finally{Marshal.FreeHGlobal(memory);}
        var methods=typeof(WindowsFileMetadataApi).GetMethods(System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).Where(m=>m.GetCustomAttributes(typeof(DllImportAttribute),false).Cast<DllImportAttribute>().Any(a=>a.EntryPoint=="GetFileInformationByHandleEx")).ToArray();
        Assert.Equal(3,methods.Length);Assert.All(methods,m=>Assert.Equal(UnmanagedType.Bool,m.ReturnParameter.GetCustomAttributes(typeof(MarshalAsAttribute),false).Cast<MarshalAsAttribute>().Single().Value));
    }
    [Fact]
    public void SuccessUsesOneHandleTwoCompleteFramesFixedBuffersAndDisposes()
    {
        var api=new Api();var observation=new WindowsReadOnlyFileMetadataProbe(api).Observe(api.Path);
        Assert.Equal(FileObservationOutcome.Sampled,observation.Outcome);Assert.Equal(api.Path,observation.BeforePath);Assert.Equal(observation.Before,observation.After);
        Assert.Equal(ulong.MaxValue,observation.Before!.Identity!.IdHigh);Assert.Equal(1,api.OpenCount);Assert.Equal(2,api.BasicCount);Assert.Equal(2,api.StandardCount);Assert.Equal(2,api.IdentityCount);Assert.Equal(2,api.FinalCount);Assert.True(api.Handles.Single().IsClosed);
    }
    [Theory]
    [InlineData("open",2,"Missing")] [InlineData("open",3,"Missing")]
    [InlineData("open",5,"Inaccessible")] [InlineData("open",32,"Inaccessible")] [InlineData("open",33,"Inaccessible")]
    [InlineData("open",50,"Unavailable")] [InlineData("basic",5,"Inaccessible")]
    [InlineData("basic",50,"Unavailable")] [InlineData("standard",50,"Unavailable")] [InlineData("identity",50,"Unavailable")]
    [InlineData("final",5,"ResolvedPathUnavailable")]
    public void NativeFalseAndErrorsNeverFallbackAndAlwaysRelease(string stage,int error,string expected)
    {var api=new Api{Fail=stage,Error=error};Assert.Equal(Enum.Parse<FileObservationOutcome>(expected),new WindowsReadOnlyFileMetadataProbe(api).Observe(api.Path).Outcome);Assert.Equal(1,api.OpenCount);Assert.True(api.Handles.Single().IsClosed);}
    [Theory]
    [InlineData("open")][InlineData("basic")][InlineData("standard")][InlineData("identity")][InlineData("final")]
    public void ExceptionsAndRepeatedRequestsHaveNoLeakedHandles(string stage)
    {var api=new Api{Throw=stage};for(var i=0;i<3;i++){Assert.Equal(FileObservationOutcome.Unavailable,new WindowsReadOnlyFileMetadataProbe(api).Observe(api.Path).Outcome);Assert.All(api.Handles,h=>Assert.True(h.IsClosed));api.Handles.Clear();}Assert.Equal(3,api.OpenCount);}
    [Theory]
    [InlineData(0u)][InlineData(2048u)][InlineData(2049u)][InlineData(uint.MaxValue)][InlineData(1u)]
    public void ZeroInsufficientTruncatedAndInconsistentBufferResultsAreNeverSuccess(uint returned)
    {var api=new Api{ReturnedLength=returned};Assert.Equal(FileObservationOutcome.ResolvedPathUnavailable,new WindowsReadOnlyFileMetadataProbe(api).Observe(api.Path).Outcome);Assert.Equal(1,api.FinalCount);Assert.True(api.Handles.Single().IsClosed);}
    [Theory]
    [InlineData(@"\\?\UNC\server\share\out.mp4")][InlineData(@"\\?\Volume{a}\out.mp4")][InlineData(@"\\.\C:\out.mp4")]
    [InlineData(@"\Device\HarddiskVolume1\out.mp4")][InlineData(@"\\?\C:\x\..\out.mp4")][InlineData(@"\\??\C:\out.mp4")]
    public void FinalPathConversionOnlyAcceptsCanonicalLocalDriveText(string raw)
    {var api=new Api{Final=raw};Assert.Equal(FileObservationOutcome.ResolvedPathUnavailable,new WindowsReadOnlyFileMetadataProbe(api).Observe(api.Path).Outcome);Assert.True(api.Handles.Single().IsClosed);}
    [Theory]
    [InlineData(FileAttributes.ReparsePoint,"ReparsePoint")] [InlineData(FileAttributes.Directory,"Directory")]
    public void LeafTypeIsReadFromOpenReparseHandleBeforeUnsupportedIdentity(FileAttributes attributes,string expected)
    {var api=new Api{Attributes=(uint)attributes,Fail="identity"};Assert.Equal(Enum.Parse<FileObservationOutcome>(expected),new WindowsReadOnlyFileMetadataProbe(api).Observe(api.Path).Outcome);Assert.Equal(0,api.IdentityCount);Assert.Equal(0,api.FinalCount);Assert.True(api.Handles.Single().IsClosed);}
    [Fact]
    public void LateLeafTypeChangeIsMetadataChangedAndHandleReleased()
    {var api=new Api();api.BeforeBasic=i=>{if(i==2)api.Attributes=(uint)FileAttributes.ReparsePoint;};Assert.Equal(FileObservationOutcome.MetadataChanged,new WindowsReadOnlyFileMetadataProbe(api).Observe(api.Path).Outcome);Assert.True(api.Handles.Single().IsClosed);}
    [Theory]
    [InlineData("basic",false)][InlineData("standard",false)][InlineData("identity",false)][InlineData("final",false)]
    [InlineData("basic",true)][InlineData("standard",true)][InlineData("identity",true)][InlineData("final",true)]
    public void SecondFrameFailureOrExceptionStillReleasesTheOnlyHandle(string stage,bool throws)
    {
        var api=new Api();api.BeforeBasic=i=>{if(i==2){if(throws)api.Throw=stage;else api.Fail=stage;}};
        Assert.Equal(!throws&&stage=="final"?FileObservationOutcome.ResolvedPathUnavailable:FileObservationOutcome.Unavailable,new WindowsReadOnlyFileMetadataProbe(api).Observe(api.Path).Outcome);
        Assert.Equal(1,api.OpenCount);Assert.True(api.Handles.Single().IsClosed);
    }
    [Theory]
    [InlineData(0u)][InlineData(4u)][InlineData(5u)]
    public void UnsupportedAndNetworkDrivesDoNotOpen(uint drive)
    {var api=new Api{Type=drive};Assert.Equal(FileObservationOutcome.Unavailable,new WindowsReadOnlyFileMetadataProbe(api).Observe(api.Path).Outcome);Assert.Equal(0,api.OpenCount);}
    [Fact]
    public void UnsupportedPlatformDoesNotCallNativeOrOpen()
    {var api=new Api{Enabled=false};Assert.Equal(FileObservationOutcome.Unavailable,new WindowsReadOnlyFileMetadataProbe(api).Observe(api.Path).Outcome);Assert.Equal(0,api.OpenCount);}
}
