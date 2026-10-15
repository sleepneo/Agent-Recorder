using System.Runtime.InteropServices;
using System.Text;
using AgentRecorder.Infrastructure;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;
using Phase = AgentRecorder.Tests.OriginalFileHoldFixture.Phase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class OriginalFileScopeNativeTests(ITestOutputHelper output)
{
    private sealed class Files : IDisposable
    {
        internal readonly string Root = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task354", "native-" + Guid.NewGuid().ToString("N")));
        internal string Videos => Path.Combine(Root, "Videos");
        internal string Media => Path.Combine(Videos, "原始视频.mp4");
        internal readonly byte[] Bytes = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();
        internal Files() { Directory.CreateDirectory(Videos); File.WriteAllBytes(Media, Bytes); }
        internal PublicationIdentityAnchor Anchor()
        {
            var sample = WindowsReadOnlyFileMetadataProbe.Instance.Observe(Media);
            Assert.Equal(FileObservationOutcome.Sampled, sample.Outcome); Assert.Equal(sample.Before, sample.After);
            return new(Media, Bytes.Length, sample.Before!.Identity!);
        }
        internal OriginalFileScopeHoldService Service(IOriginalFileHoldNative? api = null) => new(Videos, api, new OriginalFileHoldAdmission());
        public void Dispose()
        {
            var allowed = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task354")) + Path.DirectorySeparatorChar;
            Assert.StartsWith(allowed, Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase); Assert.StartsWith("native-", Path.GetFileName(Root));
            Directory.Delete(Root, true); // only this fixture's owned descendants; never project ancestors
        }
    }
    private sealed class BeforeLeaf(Phase phase) : IOriginalFileHoldNative
    {
        private readonly IOriginalFileHoldNative _native = WindowsOriginalFileHoldNative.Instance;
        internal bool LeafEntered;
        internal int LeafReturned;
        public bool Supported => _native.Supported;
        public uint DriveType(string root) => _native.DriveType(root);
        public SafeFileHandle Open(string path, uint access, uint share, uint creation, uint flags, out int error)
        {
            if ((flags & 0x02000000) == 0) { LeafEntered = true; phase.Block(); }
            var result = _native.Open(path, access, share, creation, flags, out error);
            if ((flags & 0x02000000) == 0) Interlocked.Increment(ref LeafReturned); return result;
        }
        public bool Basic(SafeFileHandle h, out FileBasicInformation v, out int e) => _native.Basic(h, out v, out e);
        public bool Standard(SafeFileHandle h, out FileStandardInformation v, out int e) => _native.Standard(h, out v, out e);
        public bool Identity(SafeFileHandle h, out FileIdentityInformation v, out int e) => _native.Identity(h, out v, out e);
        public uint FinalPath(SafeFileHandle h, StringBuilder b, int c, out int e) => _native.FinalPath(h, b, c, out e);
        public OriginalFileCloseOutcome Close(SafeFileHandle h) => _native.Close(h);
    }
    private static SafeFileHandle OtherOpen(string path, uint access, bool directory, out int error)
    {
        var handle = CreateFileW(@"\\?\" + path, access, 7, IntPtr.Zero, 3, directory ? 0x02200000u : 0x00200000u, IntPtr.Zero);
        error = Marshal.GetLastWin32Error(); return handle;
    }
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    private int Refused(Action operation)
    {
        var exception = Record.Exception(operation); Assert.NotNull(exception);
        Assert.True(exception is IOException or UnauthorizedAccessException, exception.ToString()); var code = exception.HResult & 0xffff;
        Assert.Contains(code, new[] { 5, 32 }); return code; // measured refusal, paired with same-target success below
    }
    [Fact]
    public async Task ChainAloneBlocksOwnedDirectoryRenameAndDeleteAccessBeforeLeafIsOpenedThenRecovers()
    {
        Assert.True(OperatingSystem.IsWindows()); using var files = new Files(); var anchor = files.Anchor();
        using var phase = new Phase(); var api = new BeforeLeaf(phase); var service = files.Service(api);
        var pending = service.AcquireAsync(anchor); await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var moved = Path.Combine(files.Root, "MovedVideos");
        OriginalFileScopeResult? held = null;
        try
        {
            Assert.True(api.LeafEntered); Assert.Equal(0, api.LeafReturned);
            using var delete = OtherOpen(files.Videos, 0x10000, true, out var error); Assert.True(delete.IsInvalid); Assert.Equal(32, error);
            var renameCode = Refused(() => Directory.Move(files.Videos, moved));
            output.WriteLine($"CHAIN ONLY: leaf native Open not yet called; owned Videos DELETE/share7 denied=32, Directory.Move denied={renameCode}.");
        }
        finally { phase.Release(); held = await pending; if (held.Owner is not null) await OriginalFileScopeFixture.Retire(held.Owner); else await held.Retirement; }
        Assert.Equal(OriginalFileHoldReason.Held, held.Reason);
        Assert.All((await held.Retirement).Resources, r => Assert.Equal(OriginalFileCloseOutcome.Closed, r.Outcome));
        using (var delete = OtherOpen(files.Videos, 0x10000, true, out var error)) Assert.False(delete.IsInvalid, "after retirement win32=" + error);
        Directory.Move(files.Videos, moved); Directory.Move(moved, files.Videos);
        Assert.Equal(files.Bytes, File.ReadAllBytes(files.Media)); Assert.Equal(anchor.Identity, files.Anchor().Identity);
        var next = await service.AcquireAsync(anchor);
        try { Assert.NotNull(next.Owner); }
        finally { if (next.Owner is not null) await OriginalFileScopeFixture.Retire(next.Owner); }
        output.WriteLine("same owned directory DELETE admission/rename restored after actual all-resource retirement; original bytes/128-bit ID unchanged; reacquired.");
    }
    [Fact]
    public async Task EmptyOwnedVideosDeleteIsRefusedWithChainOnlyAndSucceedsAfterFailedLeafRetirement()
    {
        using var files = new Files(); var anchor = files.Anchor(); File.Delete(files.Media); // fixture deliberately removes only its placeholder before acquisition
        using var phase = new Phase(); var api = new BeforeLeaf(phase); var pending = files.Service(api).AcquireAsync(anchor);
        await phase.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        OriginalFileScopeResult? failed = null;
        try { Assert.Equal(0, api.LeafReturned); var code = Refused(() => Directory.Delete(files.Videos)); output.WriteLine("CHAIN ONLY empty owned Videos Directory.Delete refused=" + code); }
        finally { phase.Release(); failed = await pending; if (failed.Owner is not null) await OriginalFileScopeFixture.Retire(failed.Owner); else await failed.Retirement; }
        Assert.Equal(OriginalFileHoldReason.Missing, failed.Reason); Assert.Null(failed.Owner);
        Assert.True((await failed.Retirement).Retired); Directory.Delete(files.Videos); Assert.False(Directory.Exists(files.Videos));
        output.WriteLine("same empty owned Videos Directory.Delete succeeded after original leaf missing and chain actually retired.");
    }
    [Fact]
    public async Task CombinedOwnerPreservesLeafRulesAndEveryOperationRecoversWithUnchangedOriginalObject()
    {
        using var files = new Files(); var anchor = files.Anchor(); var service = files.Service(); var held = await service.AcquireAsync(anchor);
        Assert.Equal(OriginalFileHoldReason.Held, held.Reason);
        try
        {
            foreach (var access in new[] { 1u, 2u, 0x10000u })
            { using var other = OtherOpen(files.Media, access, false, out var error); Assert.True(other.IsInvalid); Assert.Equal(32, error); output.WriteLine($"combined leaf access=0x{access:x}/share7 denied=32"); }
            Assert.Equal(32, Refused(() => File.Move(files.Media, files.Media + ".moved"))); Assert.Equal(32, Refused(() => File.Delete(files.Media)));
            Assert.Equal(OriginalFileHoldReason.Observed, (await held.Owner!.ObserveAsync()).Reason);
        }
        finally { if (held.Owner is not null) await OriginalFileScopeFixture.Retire(held.Owner); }
        foreach (var access in new[] { 1u, 2u, 0x10000u })
        { using var other = OtherOpen(files.Media, access, false, out var error); Assert.False(other.IsInvalid, "positive control=" + error); }
        File.Move(files.Media, files.Media + ".moved"); File.Move(files.Media + ".moved", files.Media);
        Assert.Equal(files.Bytes, File.ReadAllBytes(files.Media)); Assert.Equal(anchor.Identity, files.Anchor().Identity);
        var next = await service.AcquireAsync(anchor); Assert.NotNull(next.Owner); await OriginalFileScopeFixture.Retire(next.Owner!);
        File.Delete(files.Media); Assert.False(File.Exists(files.Media));
        output.WriteLine("actual retirement: leaf data-read/write/DELETE, File.Move/Delete recover; contents/ID preserved before explicit fixture delete, scope reacquired.");
    }
    [Fact]
    public async Task PreexistingOwnedDirectoryDeleteHandleConflictsThenAcquisitionRecovers()
    {
        using var files = new Files(); var anchor = files.Anchor(); var service = files.Service();
        using (var prior = OtherOpen(files.Videos, 0x10000, true, out var error))
        {
            Assert.False(prior.IsInvalid, "DELETE positive precondition=" + error); var failed = await service.AcquireAsync(anchor);
            try
            {
                Assert.Equal(OriginalFileHoldReason.SharingViolation, failed.Reason); Assert.Null(failed.Owner); Assert.True((await failed.Retirement).Retired);
                Assert.Contains((await failed.Retirement).Resources, r => r.Outcome == OriginalFileCloseOutcome.NotNeeded);
            }
            finally { if (failed.Owner is not null) await OriginalFileScopeFixture.Retire(failed.Owner); }
        }
        var held = await service.AcquireAsync(anchor); Assert.Equal(OriginalFileHoldReason.Held, held.Reason); await OriginalFileScopeFixture.Retire(held.Owner!);
        Assert.Equal(files.Bytes, File.ReadAllBytes(files.Media)); Assert.Equal(anchor.Identity, files.Anchor().Identity);
        output.WriteLine("pre-open DELETE/share7 on owned Videos admitted; directory share3 acquisition conflicts; closing that precise handle restores full acquisition.");
    }
    [Fact]
    public async Task MetadataOnlyHandlesStillAdmitDuringCombinedHold()
    {
        using var files = new Files(); var held = await files.Service().AcquireAsync(files.Anchor()); Assert.NotNull(held.Owner);
        try
        {
            foreach (var access in new[] { 0u, 0x80u, 0x100u }) foreach (var directory in new[] { true, false })
            {
                using var other = OtherOpen(directory ? files.Videos : files.Media, access, directory, out var error);
                Assert.False(other.IsInvalid, "metadata-only=" + error); output.WriteLine($"metadata-only 0x{access:x} {(directory ? "directory" : "leaf")} admitted; no universal metadata protection.");
            }
        }
        finally { await OriginalFileScopeFixture.Retire(held.Owner!); }
    }
    [Fact]
    public async Task CompatibleDirectoryReadersWritersAndOwnedNewChildRemainPossible()
    {
        using var files = new Files(); var anchor = files.Anchor(); var held = await files.Service().AcquireAsync(anchor);
        try
        {
            Assert.NotNull(held.Owner);
            foreach (var access in new[] { 1u, 2u })
            { using var compatible = OtherOpen(files.Videos, access, true, out var error); Assert.False(compatible.IsInvalid, "compatible directory=" + error); }
            var child = Path.Combine(files.Videos, "owned-new-child.txt"); File.WriteAllText(child, "fixture", Encoding.UTF8);
            Assert.Equal(OriginalFileHoldReason.Observed, (await held.Owner!.ObserveAsync()).Reason); File.Delete(child);
            output.WriteLine("owned directory FILE_LIST_DIRECTORY/FILE_ADD_FILE share7 admitted; fixture-created child admitted; chain observation does not freeze tree contents.");
        }
        finally { if (held.Owner is not null) await OriginalFileScopeFixture.Retire(held.Owner); }
        Assert.Equal(files.Bytes, File.ReadAllBytes(files.Media)); Assert.Equal(anchor.Identity, files.Anchor().Identity);
    }
    [Fact]
    public async Task OwnedDirectorySymbolicReparsePreparationIsMeasuredWithoutPrivilegeOrAclChanges()
    {
        using var files = new Files(); var anchor = files.Anchor(); var old = Path.Combine(files.Root, "ActualVideos");
        Directory.Move(files.Videos, old);
        try
        {
            try { Directory.CreateSymbolicLink(files.Videos, old); }
            catch (UnauthorizedAccessException e) { output.WriteLine("directory symbolic reparse preparation unavailable win32=" + (e.HResult & 0xffff) + "; no privilege/ACL bypass; fake reparse cases remain."); return; }
            catch (IOException e) when ((e.HResult & 0xffff) is 1314 or 5 or 50)
            { output.WriteLine("directory symbolic reparse preparation unavailable win32=" + (e.HResult & 0xffff) + "; fake coverage only."); return; }
            var result = await files.Service().AcquireAsync(anchor); Assert.Equal(OriginalFileHoldReason.AncestorUnsafe, result.Reason);
            Assert.Null(result.Owner); Assert.True((await result.Retirement).Retired); output.WriteLine("self-owned directory symbolic reparse prepared and rejected on actual directory handle.");
        }
        finally
        {
            if ((File.GetAttributes(files.Root) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Unexpected fixture parent reparse");
            // The link itself is removed non-recursively, before restoring our owned directory.
            if (Directory.Exists(files.Videos)) Directory.Delete(files.Videos);
            Directory.Move(old, files.Videos);
        }
    }
}
