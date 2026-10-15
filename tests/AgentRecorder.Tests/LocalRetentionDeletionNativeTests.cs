using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class LocalRetentionDeletionNativeTests
{
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task NativeAcceptanceSurvivesCancellationOrDeadlineBeforeCallerPostcheck(bool timeout)
    {
        using var f=new LocalRetentionDeletionFixture(real:true);using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
        f.Native.AfterRequest=_=>{if(timeout)f.Clock.Advance(3.1);else s.Dispose();};
        var op=a.RequestDeletion(p,f.Row.RecordingId);var r=await LocalRetentionDeletionFixture.Finish(op,s);
        Assert.Equal(LocalDeletionProgress.DispositionAccepted,r.Progress);Assert.True(r.FileActionPerformed);Assert.True(r.ApprovalConsumed);Assert.Equal(1,f.Native.Calls);f.AssertNotAddressable();
        Assert.Equal(LocalDeletionProgress.NotAttempted,(await a.RequestDeletion(p,f.Row.RecordingId).Response).Progress);
    }

    [Fact]
    public async Task ReplacementAfterApprovalBeforeFirstSubmissionCannotUseDurableOriginalAnchor()
    {
        using var f=new LocalRetentionDeletionFixture(real:true);using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
        var saved=Path.Combine(f.Videos,"保留的原对象.mp4");File.Move(f.Media,saved);f.PreservedOriginalPath=saved;
        using(var output=new FileStream(f.Media,FileMode.CreateNew,FileAccess.Write))output.Write(f.Bytes);f.Replacement=true;
        Assert.Equal(f.Anchor.Identity,WindowsReadOnlyFileMetadataProbe.Instance.Observe(saved).Before!.Identity);
        Assert.NotEqual(f.Anchor.Identity,WindowsReadOnlyFileMetadataProbe.Instance.Observe(f.Media).Before!.Identity);
        var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);
        Assert.Equal(LocalDeletionProgress.NotAttempted,r.Progress);Assert.Equal(0,f.Native.Calls);Assert.Equal(f.Bytes,File.ReadAllBytes(saved));
    }

    [Fact]
    public async Task OwnedOriginalDispositionHasDeletePendingWithWalAndEngineHeldThenControlledOpenFails()
    {
        using var f=new LocalRetentionDeletionFixture(real:true);using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
        using var writer=f.Writer();
        f.Native.BeforeRequest=h=>
        {
            f.AssertProtected();Assert.Equal(5,Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=>RetentionGuardFixture.Write(writer,"UPDATE ordinary_recording_history SET source_title='same_writer';")).SqliteErrorCode);
            Assert.Throws<IOException>(()=>File.ReadAllBytes(f.Media));Assert.True(s.Current.ApprovalConsumed);
        };
        var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);
        Assert.Equal(LocalDeletionProgress.DispositionAccepted,r.Progress);Assert.True(r.FileActionPerformed);Assert.True(f.Native.DeletePending);Assert.Equal(1,f.Native.Calls);
        Assert.True(r.Retirement!.Database!.Retired);Assert.All(r.Retirement.Preparation!.Scope!.Resources,c=>Assert.Equal(OriginalFileCloseOutcome.Closed,c.Outcome));
        f.AssertNotAddressable();RetentionGuardFixture.Write(writer,"UPDATE ordinary_recording_history SET source_title='same_writer';");
        using var command=writer.CreateCommand();command.CommandText="SELECT count(*) FROM ordinary_recording_history h JOIN ordinary_publication_anchors a USING(recording_id) WHERE h.recording_id='candidate' AND h.status_code='completed';";
        Assert.Equal(1L,command.ExecuteScalar());
    }

    [Fact]
    public async Task CompatibleMetadataHandleSeparatesOurRetirementFromFinalObjectRemoval()
    {
        using var f=new LocalRetentionDeletionFixture(real:true);using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
        var api=WindowsOriginalFileHoldNative.Instance;
        var extra=api.Open(@"\\?\"+f.Media,0,7,3,0x00200000,out var error);Assert.False(extra.IsInvalid,$"metadata-only preparation error={error}");
        try
        {
            var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);Assert.True(r.OurResourcesRetired);Assert.Equal(LocalDeletionProgress.DispositionAccepted,r.Progress);
            Assert.True(api.Standard(extra,out var standard,out _));Assert.Equal((byte)1,standard.DeletePending);
            Assert.True(api.Identity(extra,out var identity,out _));Assert.Equal(f.Anchor.Identity,new ObservedFileIdentity(identity.VolumeSerialNumber,identity.IdLow,identity.IdHigh));
            using(var path=api.Open(@"\\?\"+f.Media,0x80,7,3,0x00200000,out var pendingError))
            {Assert.True(path.IsInvalid);Assert.Contains(pendingError,new[]{5,303});f.Preparation=$"metadata-only established; extra still live after our retirement; path open error={pendingError}";}
        }
        finally{Assert.Equal(OriginalFileCloseOutcome.Closed,api.Close(extra));}
        f.AssertNotAddressable();
    }

    [Fact]
    public async Task ReadOnlyObjectIsNotMadeWritableAndRetainsOriginalIdentityAndBytes()
    {
        using var f=new LocalRetentionDeletionFixture(real:true);File.SetAttributes(f.Media,FileAttributes.ReadOnly);
        try
        {
            using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);
            Assert.Equal(LocalDeletionProgress.DispositionRejected,r.Progress);Assert.Equal(5,r.Win32Error);Assert.Equal(1,f.Native.Calls);Assert.False(r.FileActionPerformed);
            Assert.True(File.GetAttributes(f.Media).HasFlag(FileAttributes.ReadOnly));
        }
        finally{File.SetAttributes(f.Media,FileAttributes.Normal);} // restore only fixture attribute we changed, never ACL
    }

    [Fact]
    public async Task IncompatibleReaderPreventsScopeAndHasPositiveControlAfterItsOwnClose()
    {
        using var f=new LocalRetentionDeletionFixture(real:true);using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
        using(var reader=new FileStream(f.Media,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);Assert.Equal(LocalDeletionProgress.NotAttempted,r.Progress);Assert.Equal(0,f.Native.Calls);
        }
        var api=WindowsOriginalFileHoldNative.Instance;var h=api.Open(@"\\?\"+f.Media,0x10080,0,3,0x00200000,out var error);Assert.False(h.IsInvalid,$"control={error}");Assert.Equal(OriginalFileCloseOutcome.Closed,api.Close(h));
    }

    [Fact]
    public async Task OldApprovalCannotDeleteSameNameReplacementWithDifferent128BitId()
    {
        using var f=new LocalRetentionDeletionFixture(real:true);using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
        var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);Assert.Equal(LocalDeletionProgress.DispositionAccepted,r.Progress);f.AssertNotAddressable();
        using(var output=new FileStream(f.Media,FileMode.CreateNew,FileAccess.Write))output.Write(f.Bytes);f.Replacement=true;
        Assert.NotEqual(f.Anchor.Identity,WindowsReadOnlyFileMetadataProbe.Instance.Observe(f.Media).Before!.Identity);
        Assert.Equal(LocalDeletionProgress.NotAttempted,(await a.RequestDeletion(p,f.Row.RecordingId).Response).Progress);Assert.Equal(1,f.Native.Calls);
        // New approval cannot substitute a newly observed ID for the durable one.
        var newUi=new LocalRetentionApprovalFixture.Ui();using var fresh=f.Service(ui:newUi);
        var next=fresh.Request(f.Row.RecordingId);await newUi.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));newUi.Decide(LocalRetentionUiChoice.Approve);newUi.Retire.TrySetResult(true);
        var newPermit=(await next.Response.WaitAsync(TimeSpan.FromSeconds(5))).Permit!;
        var rejected=await LocalRetentionDeletionFixture.Finish(fresh.RequestDeletion(newPermit,f.Row.RecordingId),next);
        Assert.Equal(LocalDeletionProgress.NotAttempted,rejected.Progress);Assert.Equal(1,f.Native.Calls);Assert.Equal(f.Bytes,File.ReadAllBytes(f.Media));
    }

    [Fact]
    public async Task ExistingMappingNeverTriggersPosixOrPermissionFallback()
    {
        using var f=new LocalRetentionDeletionFixture(real:true);using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);
        using(var stream=new FileStream(f.Media,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
        using(var mapping=MemoryMappedFile.CreateFromFile(stream,null,0,MemoryMappedFileAccess.Read,HandleInheritability.None,true))
        using(var view=mapping.CreateViewAccessor(0,0,MemoryMappedFileAccess.Read))
        {
            stream.Dispose();
            var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);
            f.Preparation=$"read mapping established; progress={r.Progress}; error={r.Win32Error}; requests={f.Native.Calls}";
            Assert.True(r.Progress is LocalDeletionProgress.NotAttempted or LocalDeletionProgress.DispositionRejected);
            Assert.InRange(f.Native.Calls,0,1);Assert.Equal(f.Bytes[0],view.ReadByte(0));Assert.False(r.FileActionPerformed);
        }
        Assert.Equal(f.Bytes,File.ReadAllBytes(f.Media));
    }

    [Fact]
    public async Task MultiLinkObjectFailsClosedWithoutModifyingEitherName()
    {
        using var f=new LocalRetentionDeletionFixture(real:true);var second=Path.Combine(f.Videos,"same-object.mp4");
        Assert.True(CreateHardLinkW(second,f.Media,IntPtr.Zero),$"hardlink preparation={Marshal.GetLastWin32Error()}");
        using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);
        Assert.Equal(LocalDeletionProgress.NotAttempted,r.Progress);Assert.Equal(0,f.Native.Calls);Assert.Equal(f.Bytes,File.ReadAllBytes(second));
        f.Preparation="hardlink established; both names retained, link count 2 rejected";
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newName,string existingName,IntPtr reserved);
}
