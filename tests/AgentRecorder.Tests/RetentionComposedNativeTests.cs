using System.Runtime.InteropServices;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Phase = AgentRecorder.Tests.OriginalFileHoldFixture.Phase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RetentionComposedNativeTests
{
    [Fact]
    public async Task ActualEngineWalAndNativeScopeCoexistAndSameResourcesRecoverAfterRetirement()
    {
        Assert.True(OperatingSystem.IsWindows()); using var f = new RetentionComposedFixture(native: true); using var phase = new Phase();
        var identity = f.Anchor.Identity; using var service = f.Service(hostAdmissions: true); var calls = 0;
        var session = f.Execute(service, context => { context.RequireActive(); Interlocked.Increment(ref calls); phase.Block(); return RetentionComposedContinuationResult.Completed; });
        try
        {
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); f.AssertProtected();
            foreach (var policy in new[] { "fail", "rename" })
            { var error = Assert.Throws<ApiException>(() => f.Create(policy)); Assert.Equal(policy == "fail" ? "OUTPUT_PATH_INVALID" : "OUTPUT_PATH_IN_USE", error.Code); Assert.Equal(409, error.Status); }
            Assert.Empty(f.Engine._recs); Assert.Equal(0, f.Capture.Starts); Assert.Equal(0, f.Ui.Requests);
            using var writer = RetentionComposedContractTests.Writer(f);
            Assert.Equal(5, Assert.Throws<SqliteException>(() => RetentionGuardFixture.Write(writer, "UPDATE ordinary_recording_history SET source_title='same_writer';")).SqliteErrorCode);
            foreach (var access in new uint[] { 1, 2, 0x10000 })
            { using var handle = Open(f.Media, access); Assert.True(handle.IsInvalid); Assert.Equal(32, Marshal.GetLastWin32Error()); }
            Assert.ThrowsAny<IOException>(() => File.Move(f.Media, f.Media + ".moved"));
            Assert.ThrowsAny<IOException>(() => Directory.Move(f.Videos, f.Videos + ".moved"));
            Assert.False(session.Retirement.IsCompleted); phase.Release();
            Assert.Equal(RetentionComposedReason.Checked, (await RetentionComposedFixture.Finish(session)).Reason); Assert.Equal(1, calls);
            RetentionGuardFixture.Write(writer, "UPDATE ordinary_recording_history SET source_title='same_writer';");
            foreach (var access in new uint[] { 1, 2, 0x10000 }) { using var handle = Open(f.Media, access); Assert.False(handle.IsInvalid); }
            File.Move(f.Media, f.Media + ".moved"); File.Move(f.Media + ".moved", f.Media);
            Directory.Move(f.Videos, f.Videos + ".moved"); Directory.Move(f.Videos + ".moved", f.Videos);
            Assert.Equal(Enumerable.Range(0, 12).Select(i => (byte)i), File.ReadAllBytes(f.Media));
            Assert.Equal(identity, WindowsReadOnlyFileMetadataProbe.Instance.Observe(f.Media).Before!.Identity);
            var rec = f.Create("rename"); Assert.Equal(RecState.pending_confirmation, rec.State); Assert.NotEqual(f.Media, rec.OutputPath); Assert.Equal(0, f.Capture.Starts);
        }
        finally { phase.Release(); await session.Retirement.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    private static SafeFileHandle Open(string path, uint access) => CreateFileW(@"\\?\" + path, access, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);

    [Fact]
    public async Task FormalApprovalCannotRenameAroundLiveComposedReservation()
    {
        using var f = new RetentionComposedFixture(native: true); var rec = f.Create("rename", Path.Combine(f.Db.Root, "Old")); var original = rec.OutputPath;
        using var service = f.Service(hostAdmissions: true); using var phase = new Phase();
        var session = f.Execute(service, _ => { phase.Block(); return RetentionComposedContinuationResult.Completed; });
        try
        {
            await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Ui.Confirm!(ConfirmationDecision.Approve(f.Videos));
            Assert.Equal(RecState.rejected, rec.State); Assert.Equal("OUTPUT_PATH_IN_USE", rec.StopReason); Assert.Equal(original, rec.OutputPath);
            Assert.Equal(0, f.Capture.Starts); f.AssertProtected(); phase.Release();
            Assert.Equal(RetentionComposedReason.Checked, (await RetentionComposedFixture.Finish(session)).Reason);
            Assert.Equal(Enumerable.Range(0, 12).Select(i => (byte)i), File.ReadAllBytes(f.Media));
        }
        finally { phase.Release(); await session.Retirement.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
}
