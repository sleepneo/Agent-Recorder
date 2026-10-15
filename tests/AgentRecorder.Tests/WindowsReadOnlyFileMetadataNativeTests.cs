using System.Runtime.InteropServices;
using System.Text;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class WindowsReadOnlyFileMetadataNativeTests(ITestOutputHelper output)
{
    internal sealed class Files : IDisposable
    {
        internal static string TaskRoot=>Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task325r"));
        internal readonly string Root=Path.Combine(TaskRoot,"native-"+Guid.NewGuid().ToString("N"));
        internal string Videos=>Path.Combine(Root,"Videos");
        internal Files(){Assert.True(OperatingSystem.IsWindows());AssertOwnedRoot();Directory.CreateDirectory(Videos);}
        internal void AssertOwnedRoot()
        {Assert.Equal(TaskRoot,Path.GetDirectoryName(Path.GetFullPath(Root)),ignoreCase:true);Assert.StartsWith(TaskRoot+Path.DirectorySeparatorChar,Path.GetFullPath(Root),StringComparison.OrdinalIgnoreCase);}
        internal string Create(string name="out.mp4",long length=12)
        {
            var path=Path.GetFullPath(Path.Combine(Videos,name));Assert.StartsWith(Videos+Path.DirectorySeparatorChar,path,StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);file.SetLength(length);return path;
        }
        public void Dispose()
        {
            AssertOwnedRoot();if(!Directory.Exists(Root))return;
            foreach(var file in Directory.EnumerateFiles(Root,"*",SearchOption.AllDirectories))File.SetAttributes(file,FileAttributes.Normal);
            Directory.Delete(Root,recursive:true);
        }
    }
    internal sealed class Trace : IWindowsFileMetadataApi
    {
        internal int Opens,Basics,Standards,Identities,Finals;
        internal List<SafeFileHandle> Handles=new();
        internal Action<int>? BeforeBasic;
        private readonly IWindowsFileMetadataApi _real=WindowsFileMetadataApi.Instance;
        public bool Supported=>_real.Supported;
        public uint DriveType(string root)=>_real.DriveType(root);
        public SafeFileHandle Open(string path,uint access,uint share,uint creation,uint flags,out int error)
        {Opens++;Assert.Equal(0x80u,access);Assert.Equal(1u,share);Assert.Equal(3u,creation);Assert.Equal(0x02200000u,flags);var h=_real.Open(path,access,share,creation,flags,out error);Handles.Add(h);return h;}
        public bool Basic(SafeFileHandle h,out FileBasicInformation i,out int error){Basics++;BeforeBasic?.Invoke(Basics);return _real.Basic(h,out i,out error);}
        public bool Standard(SafeFileHandle h,out FileStandardInformation i,out int error){Standards++;return _real.Standard(h,out i,out error);}
        public bool Identity(SafeFileHandle h,out FileIdentityInformation i,out int error){Identities++;return _real.Identity(h,out i,out error);}
        public uint FinalPath(SafeFileHandle h,StringBuilder b,int size,out int error){Finals++;return _real.FinalPath(h,b,size,out error);}
    }
    internal static void HardLink(string link,string target)
    {Assert.True(CreateHardLinkW(link,target,IntPtr.Zero),"Synthetic hard-link preparation failed: "+Marshal.GetLastWin32Error());}
    internal static RetentionPreviewQueryService Service(UnifiedHistoryTestDatabase db,IReadOnlyFileMetadataProbe probe) =>
        new(db.Query.GetOrdinaryEvidencePage,db.Root,ReadOnlyPathMetadata.Instance,probe,()=>At.AddDays(5), TestRuntimeOutputUse.None);
    [Fact]
    public void RealSingleFileHasStable128BitIdentityOneHandleAndNoContentRead()
    {
        using var f=new Files();var path=f.Create();var beforeWrite=File.GetLastWriteTimeUtc(path);var api=new Trace();var o=new WindowsReadOnlyFileMetadataProbe(api).Observe(path);
        Assert.Equal(FileObservationOutcome.Sampled,o.Outcome);Assert.Equal(o.Before,o.After);Assert.Equal(12,o.Before!.Length);Assert.Equal(1u,o.Before.Links);Assert.NotNull(o.Before.Identity);Assert.Equal(path,o.BeforePath,ignoreCase:true);
        Assert.Equal(1,api.Opens);Assert.Equal(2,api.Basics);Assert.Equal(2,api.Standards);Assert.Equal(2,api.Identities);Assert.Equal(2,api.Finals);Assert.True(api.Handles.Single().IsClosed);Assert.Equal(beforeWrite,File.GetLastWriteTimeUtc(path));
        using var exclusive=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);Assert.Equal(12,exclusive.Length);
    }
    [Fact]
    public void RealHardLinksHaveSameVolumeAndFullIdAndPreviewProtectsUnknownBytes()
    {
        using var f=new Files();var path=f.Create();var link=Path.Combine(f.Videos,"alias.mp4");HardLink(link,path);
        var first=WindowsReadOnlyFileMetadataProbe.Instance.Observe(path);var second=WindowsReadOnlyFileMetadataProbe.Instance.Observe(link);
        Assert.Equal(FileObservationOutcome.Sampled,first.Outcome);Assert.Equal(FileObservationOutcome.Sampled,second.Outcome);Assert.Equal(first.Before!.Identity,second.Before!.Identity);Assert.Equal(2u,first.Before.Links);Assert.Equal(2u,second.Before.Links);
        var db=new UnifiedHistoryTestDatabase(f.Root);try
        {
            db.Ordinary.Register(PublicationAnchorTestFixture.Anchored(db.Recording("hard",completed:true) with{OutputPath=path,BundleRegistration=new(null)},native:true));var snapshot=db.Snapshot();var api=new Trace();var page=Service(db,new WindowsReadOnlyFileMetadataProbe(api)).GetPage(Sid,Session,20,At.AddHours(1));
            Assert.Equal("hard_linked_output",Assert.Single(page.Items).ReasonCode);Assert.Null(page.Items[0].ObservedFileBytes);Assert.Equal(1,page.UnknownCandidateFileCount);Assert.Equal(0,page.ObservedCandidateBytes);Assert.Equal(snapshot,db.Snapshot());Assert.True(api.Handles.Single().IsClosed);
        }finally{db.Ordinary.Dispose();SqliteConnection.ClearAllPools();}
    }
    [Fact]
    public void RealDataSharingConflictDoesNotPretendToLockPureMetadataAccess()
    {
        using var f=new Files();var path=f.Create();using var writer=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        var conflict=Assert.Throws<IOException>(()=>{using var second=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);});Assert.True((conflict.HResult&0xffff) is 32 or 33);
        var api=new Trace();var o=new WindowsReadOnlyFileMetadataProbe(api).Observe(path);
        Assert.Equal(FileObservationOutcome.Sampled,o.Outcome);Assert.Equal(o.Before,o.After);Assert.Equal(1,api.Opens);Assert.True(api.Handles.Single().IsClosed);
        output.WriteLine("Real data-handle sharing conflict executed (32/33); FILE_READ_ATTRIBUTES metadata open still succeeded. Native probe sharing-error mapping is separately fault-tested, not claimed exercised here.");
    }
    [Theory]
    [InlineData("write_time")][InlineData("attributes")]
    public void RealMetadataChangeBetweenReadsProtectsWithoutTrustingBytes(string change)
    {
        using var f=new Files();var path=f.Create();var db=new UnifiedHistoryTestDatabase(f.Root);try
        {
            db.Ordinary.Register(PublicationAnchorTestFixture.Anchored(db.Recording("changing",completed:true) with{OutputPath=path,BundleRegistration=new(null)},native:true));var api=new Trace();var changed=false;
            api.BeforeBasic=n=>{if(n!=2)return;if(change=="write_time")File.SetLastWriteTimeUtc(path,File.GetLastWriteTimeUtc(path).AddMinutes(1));else File.SetAttributes(path,FileAttributes.ReadOnly);changed=true;};
            var page=Service(db,new WindowsReadOnlyFileMetadataProbe(api)).GetPage(Sid,Session,20,At.AddHours(1));Assert.True(changed);Assert.Equal("file_identity_changed",Assert.Single(page.Items).ReasonCode);Assert.Null(page.Items[0].ObservedFileBytes);Assert.Equal(1,page.UnknownCandidateFileCount);Assert.True(api.Handles.Single().IsClosed);
        }finally{File.SetAttributes(path,FileAttributes.Normal);db.Ordinary.Dispose();SqliteConnection.ClearAllPools();}
    }
    [Fact]
    public void RealRenameDuringMetadataHandleProtectsResolvedPathAndNextRequestSeesRename()
    {
        using var f=new Files();var path=f.Create();var renamed=Path.Combine(f.Videos,"renamed.mp4");var api=new Trace();var moved=false;var db=new UnifiedHistoryTestDatabase(f.Root);try
        {
            db.Ordinary.Register(PublicationAnchorTestFixture.Anchored(db.Recording("rename",completed:true) with{OutputPath=path,BundleRegistration=new(null)},native:true));
            api.BeforeBasic=n=>{if(n==2){File.Move(path,renamed);moved=true;}};
            var page=Service(db,new WindowsReadOnlyFileMetadataProbe(api)).GetPage(Sid,Session,20,At.AddHours(1));Assert.True(moved);Assert.Equal("resolved_path_mismatch",Assert.Single(page.Items).ReasonCode);Assert.Null(page.Items[0].ObservedFileBytes);Assert.True(api.Handles.Single().IsClosed);
            Assert.Equal(FileObservationOutcome.Missing,WindowsReadOnlyFileMetadataProbe.Instance.Observe(path).Outcome);Assert.Equal(FileObservationOutcome.Sampled,WindowsReadOnlyFileMetadataProbe.Instance.Observe(renamed).Outcome);
            output.WriteLine("Real rename succeeded during pure metadata handle; second same-handle final path changed and preview protected. No rename pin or ancestor lock claimed.");
        }finally{db.Ordinary.Dispose();SqliteConnection.ClearAllPools();}
    }
    [Fact]
    public void RealLongPathUsesBoundedPrefixAndMissingPathsRemainMissing()
    {
        using var f=new Files();var name=string.Join(Path.DirectorySeparatorChar,Enumerable.Repeat(new string('x',50),5))+@"\long.mp4";var path=f.Create(name);
        Assert.True(path.Length>260);Assert.Equal(FileObservationOutcome.Sampled,WindowsReadOnlyFileMetadataProbe.Instance.Observe(path).Outcome);
        Assert.Equal(FileObservationOutcome.Missing,WindowsReadOnlyFileMetadataProbe.Instance.Observe(Path.Combine(f.Videos,"absent","missing.mp4")).Outcome);
        Assert.Equal(FileObservationOutcome.Unavailable,WindowsReadOnlyFileMetadataProbe.Instance.Observe(@"\\?\"+path).Outcome);
    }
    [Fact]
    public void RealDirectoryAndOptionalLeafSymlinkNeverSampleTargetContents()
    {
        using var f=new Files();Assert.Equal(FileObservationOutcome.Directory,WindowsReadOnlyFileMetadataProbe.Instance.Observe(f.Videos).Outcome);var path=f.Create();var link=Path.Combine(f.Videos,"symbolic.mp4");
        if(!CreateSymbolicLinkW(link,path,2))
        {
            var error=Marshal.GetLastWin32Error();output.WriteLine("Synthetic leaf symlink was not created; untested native symlink branch, Win32 preparation error="+error);
            Assert.True(error is 1314 or 5 or 50 or 87,"Unexpected synthetic symlink preparation error.");return;
        }
        try{var api=new Trace();Assert.Equal(FileObservationOutcome.ReparsePoint,new WindowsReadOnlyFileMetadataProbe(api).Observe(link).Outcome);Assert.Equal(0,api.Identities);Assert.True(api.Handles.Single().IsClosed);output.WriteLine("Real leaf symlink branch executed using OPEN_REPARSE_POINT.");}
        finally{File.Delete(link);}
    }
    [Fact]
    public void RepeatedRealSuccessAndMissingCallsReleaseAllHandles()
    {
        using var f=new Files();var path=f.Create();var api=new Trace();var probe=new WindowsReadOnlyFileMetadataProbe(api);
        for(var i=0;i<20;i++){Assert.Equal(FileObservationOutcome.Sampled,probe.Observe(path).Outcome);Assert.Equal(FileObservationOutcome.Missing,probe.Observe(path+"absent").Outcome);Assert.All(api.Handles,h=>Assert.True(h.IsClosed));}
        Assert.Equal(40,api.Opens);File.Delete(path);Assert.False(File.Exists(path));
    }
    [Fact]
    public void SameSizeReplacementBeforePreviewCannotBeProvedAgainstPublicationWithoutAnAnchor()
    {
        using var f=new Files();var path=f.Create();var original=WindowsReadOnlyFileMetadataProbe.Instance.Observe(path);var db=new UnifiedHistoryTestDatabase(f.Root);try
        {
            db.Ordinary.Register(db.Recording("replaced",completed:true) with{OutputPath=path});var snapshot=db.Snapshot();
            File.Move(path,Path.Combine(f.Videos,"held-original.mp4"));f.Create();var current=WindowsReadOnlyFileMetadataProbe.Instance.Observe(path);
            Assert.NotEqual(original.Before!.Identity,current.Before!.Identity);Assert.Equal(original.Before.Length,current.Before.Length);
            var page=new RetentionPreviewQueryService(db.Query,db.Root, TestRuntimeOutputUse.None).GetPage(Sid,Session,20,At.AddHours(1));Assert.Equal("publication_identity_missing",Assert.Single(page.Items).ReasonCode);Assert.Equal(0,page.ObservedCandidateBytes);Assert.Equal(snapshot,db.Snapshot());
            output.WriteLine("Same-size prior replacement has a different observed current identity but no trusted publication anchor; Task325 now protects this unanchored old record as publication_identity_missing.");
        }finally{db.Ordinary.Dispose();SqliteConnection.ClearAllPools();}
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link,string target,IntPtr security);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.U1)]
    private static extern bool CreateSymbolicLinkW(string link,string target,uint flags);
}
