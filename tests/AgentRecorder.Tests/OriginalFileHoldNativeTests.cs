using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using AgentRecorder.Infrastructure;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class OriginalFileHoldNativeTests(ITestOutputHelper output)
{
    private sealed class Files : IDisposable
    {
        internal readonly string Root = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task353", "native-" + Guid.NewGuid().ToString("N")));
        internal string Videos => Path.Combine(Root, "Videos");
        internal string PathName => Path.Combine(Videos, "视频-原始对象.mp4");
        internal readonly byte[] Bytes = Enumerable.Range(0, 12).Select(x => (byte)x).ToArray();
        internal OriginalFileHoldService Service => new(Videos, admission: new OriginalFileHoldAdmission());
        internal Files() { Directory.CreateDirectory(Videos); File.WriteAllBytes(PathName, Bytes); }
        internal PublicationIdentityAnchor Anchor()
        {
            var sampled = WindowsReadOnlyFileMetadataProbe.Instance.Observe(PathName);
            Assert.Equal(FileObservationOutcome.Sampled, sampled.Outcome); Assert.Equal(sampled.Before, sampled.After);
            return new(PathName, Bytes.Length, sampled.Before!.Identity!); // test anchor from original native observation, never from guard
        }
        public void Dispose()
        {
            var allowed = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task353")) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(Root); Assert.StartsWith(allowed, target, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("native-", Path.GetFileName(target)); Directory.Delete(target, recursive: true); // only this fixture's owned directory
        }
    }
    private static SafeFileHandle OtherOpen(string path, uint access, out int error)
    {
        var handle = CreateFileW(@"\\?\" + path, access, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        error = Marshal.GetLastWin32Error(); return handle;
    }
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);

    [Fact]
    public async Task HeldExistingObjectBlocksOrdinaryReadWriteDeleteRenameThenOperationsRecover()
    {
        Assert.True(OperatingSystem.IsWindows()); using var files = new Files(); var anchor = files.Anchor(); var service = files.Service;
        var held = await service.AcquireAsync(anchor); Assert.Equal(OriginalFileHoldReason.Held, held.Reason); var owner = held.Owner!;
        try
        {
            foreach (var access in new[] { 1u, 2u, 0x10000u })
            {
                using var other = OtherOpen(files.PathName, access, out var error); Assert.True(other.IsInvalid); Assert.Equal(32, error);
                output.WriteLine($"owned leaf: competing access=0x{access:x}, share=7 fails ERROR_SHARING_VIOLATION=32");
            }
            var deletion = Assert.Throws<IOException>(() => File.Delete(files.PathName)); Assert.Equal(32, deletion.HResult & 0xffff);
            var rename = Assert.Throws<IOException>(() => File.Move(files.PathName, Path.Combine(files.Videos, "moved.mp4"))); Assert.Equal(32, rename.HResult & 0xffff);
            Assert.Equal(OriginalFileHoldReason.Observed, (await owner.ObserveAsync()).Reason);
            Assert.False(held.Retirement.IsCompleted); Assert.Equal(OriginalFileHoldReason.Busy, (await service.AcquireAsync(anchor)).Reason);
        }
        finally { await OriginalFileHoldFixture.Retire(owner); }
        foreach (var access in new[] { 1u, 2u, 0x10000u }) { using var other = OtherOpen(files.PathName, access, out var error); Assert.False(other.IsInvalid, "after release win32=" + error); }
        var moved = Path.Combine(files.Videos, "moved.mp4"); File.Move(files.PathName, moved); File.Move(moved, files.PathName);
        Assert.Equal(files.Bytes, File.ReadAllBytes(files.PathName)); Assert.Equal(anchor.Identity, files.Anchor().Identity);
        var next = await service.AcquireAsync(anchor); Assert.NotNull(next.Owner); await OriginalFileHoldFixture.Retire(next.Owner!);
        File.Delete(files.PathName); Assert.False(File.Exists(files.PathName));
        output.WriteLine("positive controls after confirmed close: read/write/delete access, owned rename/delete, unchanged contents/identity, reacquisition.");
    }
    [Fact]
    public async Task ExistingIncompatibleWriterRejectsGuardWithoutChangingObjectThenRecovers()
    {
        using var files = new Files(); var anchor = files.Anchor(); var service = files.Service;
        using (var writer = new FileStream(files.PathName, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            var result = await service.AcquireAsync(anchor); Assert.Equal(OriginalFileHoldReason.SharingViolation, result.Reason); Assert.Null(result.Owner);
            Assert.True((await result.Retirement).Retired); Assert.Equal(OriginalFileCloseOutcome.NotNeeded, (await result.Retirement).Cleanup);
        }
        var next = await service.AcquireAsync(anchor); Assert.NotNull(next.Owner); await OriginalFileHoldFixture.Retire(next.Owner!);
        Assert.Equal(files.Bytes, File.ReadAllBytes(files.PathName)); Assert.Equal(anchor.Identity, files.Anchor().Identity);
    }
    [Fact]
    public async Task AttributeOnlyOpensRemainPossibleDuringLeafHold()
    {
        using var files = new Files(); var owner = (await files.Service.AcquireAsync(files.Anchor())).Owner!;
        try
        {
            foreach (var access in new[] { 0u, 0x80u, 0x100u })
            {
                using var attributes = OtherOpen(files.PathName, access, out var error); Assert.False(attributes.IsInvalid, "attribute access win32=" + error);
                output.WriteLine($"metadata-only access=0x{access:x} admitted while sharing=0 leaf hold persists; no universal metadata lock.");
            }
        }
        finally { await OriginalFileHoldFixture.Retire(owner); }
    }
    [Fact]
    public async Task ParentMoveIsMeasuredAsPlatformBoundaryAndNeverFollowsReplacement()
    {
        using var files = new Files(); var anchor = files.Anchor(); var service = files.Service; var owner = (await service.AcquireAsync(anchor)).Owner!;
        var moved = Path.Combine(files.Root, "MovedVideos"); var didMove = false;
        try
        {
            try { Directory.Move(files.Videos, moved); didMove = true; output.WriteLine("owned parent move permitted; leaf hold is not a namespace lock."); }
            catch (IOException e) { output.WriteLine("owned parent move refused on this platform, win32=" + (e.HResult & 0xffff) + "; no universal namespace-lock conclusion."); }
            catch (UnauthorizedAccessException e) { output.WriteLine("owned parent move permission-limited, win32=" + (e.HResult & 0xffff) + "; no ACL bypass."); }
            if (didMove)
            {
                Directory.CreateDirectory(files.Videos); File.WriteAllBytes(files.PathName, new byte[12]);
                var observation = await owner.ObserveAsync(); Assert.NotEqual(OriginalFileHoldReason.Observed, observation.Reason); Assert.Null(observation.Snapshot);
                Assert.Equal(OriginalFileHoldReason.Busy, (await service.AcquireAsync(anchor)).Reason); Assert.False(owner.Retirement.IsCompleted);
                output.WriteLine("replacement directory exists, but same original handle reports unavailable scope; no reopen/follow.");
            }
        }
        finally { await OriginalFileHoldFixture.Retire(owner); }
        if (didMove) { File.Delete(files.PathName); Directory.Delete(files.Videos); Directory.Move(moved, files.Videos); }
        else { Directory.Move(files.Videos, moved); Directory.Move(moved, files.Videos); }
        var next = await service.AcquireAsync(anchor); Assert.NotNull(next.Owner); await OriginalFileHoldFixture.Retire(next.Owner!);
        Assert.Equal(files.Bytes, File.ReadAllBytes(files.PathName)); Assert.Equal(anchor.Identity, files.Anchor().Identity);
    }
    [Fact]
    public async Task PreexistingWritableMappingIsReportedWithoutClaimingImmutableContents()
    {
        using var files = new Files(); var anchor = files.Anchor(); var service = files.Service;
        using (var stream = new FileStream(files.PathName, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        using (var mapping = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true))
        {
            using var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite); stream.Dispose();
            var result = await service.AcquireAsync(anchor); output.WriteLine("closed original data stream, retained writable mapping: acquire=" + result.Reason);
            if (result.Owner is { } owner)
            {
                try { view.Write(0, (byte)99); view.Flush(); var sampled = await owner.ObserveAsync(); output.WriteLine("controlled existing mapping write while held; re-observation=" + sampled.Reason + "; metadata result does not verify contents."); }
                finally { await OriginalFileHoldFixture.Retire(owner); }
            }
            else { Assert.Contains(result.Reason, new[] { OriginalFileHoldReason.SharingViolation, OriginalFileHoldReason.Inaccessible }); Assert.True((await result.Retirement).Retired); }
        }
        var restored = await service.AcquireAsync(anchor); Assert.NotNull(restored.Owner); await OriginalFileHoldFixture.Retire(restored.Owner!);
        Assert.Equal(anchor.Identity, files.Anchor().Identity);
    }
    [Fact]
    public void ReusedMetadataAbiPreservesNativeBooleanAndCompleteIdentitySizes()
    {
        Assert.Equal(40, Marshal.SizeOf<FileBasicInformation>()); Assert.Equal(24, Marshal.SizeOf<FileStandardInformation>()); Assert.Equal(24, Marshal.SizeOf<FileIdentityInformation>());
        Assert.Equal(20, Marshal.OffsetOf<FileStandardInformation>(nameof(FileStandardInformation.DeletePending)).ToInt32());
        Assert.Equal(21, Marshal.OffsetOf<FileStandardInformation>(nameof(FileStandardInformation.Directory)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<FileIdentityInformation>(nameof(FileIdentityInformation.IdHigh)).ToInt32());
    }
}
