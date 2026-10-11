using System.Runtime.InteropServices;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class HandleBoundPublicationTests(ITestOutputHelper output)
{
    private sealed class Operations : IPublicationFileOperations
    {
        internal SafeFileHandle? Original;
        internal Action? BeforeRename;
        internal int Renames, Deletes, RenameError;
        public FileStream CreateNew(string path)
        {
            var stream = WindowsPublicationFileOperations.Instance.CreateNew(path);
            Original = stream.SafeFileHandle;
            return stream;
        }
        public void Rename(SafeFileHandle original, string final)
        {
            Assert.Same(Original, original);
            Renames++;
            BeforeRename?.Invoke();
            try { WindowsPublicationFileOperations.Instance.Rename(original, final); }
            catch (IOException ex) { RenameError = ex.HResult & 0xffff; throw; }
        }
        public PublicationCleanupOutcome DeleteOriginal(SafeFileHandle original)
        {
            Assert.Same(Original, original);
            Deletes++;
            return WindowsPublicationFileOperations.Instance.DeleteOriginal(original);
        }
    }
    private sealed class InjectedGate(Action before) : IFileCommitGate
    {
        private readonly FileCommitGate _gate = new();
        public bool IsClosed => _gate.IsClosed;
        public bool TryCommit(Action move) => _gate.TryCommit(() => { before(); move(); });
        public void Close() => _gate.Close();
    }
    private static ObservedFileIdentity Identity(string path) => WindowsReadOnlyFileMetadataProbe.Instance.Observe(path).Before!.Identity!;
    private static ObservedFileIdentity Identity(SafeFileHandle handle)
    {
        Assert.True(WindowsFileMetadataApi.Instance.Identity(handle, out var id, out var error), "Identity error=" + error);
        return new(id.VolumeSerialNumber, id.IdLow, id.IdHigh);
    }
    private static string ReplaceTemporary(WindowsReadOnlyFileMetadataNativeTests.Files files, long length, out string held)
    {
        var temp = Directory.GetFiles(files.Videos, "*.publish-tmp-*.mp4").Single();
        var writeTime = File.GetLastWriteTimeUtc(temp);
        held = Path.Combine(files.Videos, "held-original.mp4");
        File.Move(temp, held);
        files.Create(Path.GetFileName(temp), length);
        File.SetLastWriteTimeUtc(temp, writeTime);
        return temp;
    }
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task SourceNameReplacementCommitsOriginalObjectNeverForeignFile(bool gated, bool sameLength)
    {
        using var f = new WindowsReadOnlyFileMetadataNativeTests.Files();
        var source = f.Create("source.mp4"); var final = f.Create("final.mp4", 5); var old = Identity(final);
        var ops = new Operations(); var publisher = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops);
        string? foreign = null, held = null; ObservedFileIdentity? original = null, foreignId = null;
        publisher.OriginalCopyCreatedForTests = stream => original = Identity(stream.SafeFileHandle);
        Action attack = () => { foreign = ReplaceTemporary(f, sameLength ? 12 : 7, out held); foreignId = Identity(foreign); };
        IFileCommitGate? gate = gated ? new InjectedGate(attack) : null;
        if (!gated) publisher.BeforeOriginalCommitForTests = attack;
        var result = await publisher.PublishAsync(source, final, commitGate: gate);
        Assert.True(result.Success); Assert.Equal(12, result.FinalSizeBytes); Assert.Equal(12, new FileInfo(final).Length);
        Assert.Equal(original, Identity(final)); Assert.NotEqual(old, Identity(final)); Assert.NotEqual(foreignId, Identity(final));
        Assert.NotNull(result.Receipt); Assert.Equal(original, result.Receipt!.Anchor.Identity);
        Assert.Equal(foreignId, Identity(foreign!)); Assert.Equal(sameLength ? 12 : 7, new FileInfo(foreign!).Length); Assert.False(File.Exists(held));
        if (sameLength) Assert.Equal(File.GetLastWriteTimeUtc(foreign!), File.GetLastWriteTimeUtc(final));
        Assert.Equal(1, ops.Renames); Assert.Equal(0, ops.Deletes); Assert.True(ops.Original!.IsClosed);
        using var exclusive = new FileStream(final, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    [Theory]
    [InlineData("copy")] [InlineData("flush")] [InlineData("cancel")]
    [InlineData("closed_gate")] [InlineData("rename_failure")]
    public async Task FailureDeletesRenamedOriginalOnlyAndPreservesReplacementAndOldFinal(string fault)
    {
        using var f = new WindowsReadOnlyFileMetadataNativeTests.Files();
        var source = f.Create("source.mp4"); var final = f.Create("final.mp4", 5); var old = Identity(final);
        var ops = new Operations(); var publisher = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops);
        var gate = new FileCommitGate(); using var cancellation = new CancellationTokenSource();
        string? foreign = null, held = null; ObservedFileIdentity? foreignId = null; FileStream? lockFinal = null;
        void Attack() { foreign = ReplaceTemporary(f, 7, out held); foreignId = Identity(foreign); }
        if (fault == "copy") publisher.OriginalCopyCreatedForTests = _ => { Attack(); throw new IOException("Injected copy failure"); };
        else if (fault == "rename_failure") publisher.BeforeOriginalCommitForTests = () => { Attack(); lockFinal = new FileStream(final, FileMode.Open, FileAccess.Read, FileShare.Read); };
        else publisher.BeforeOriginalFlushForTests = stream =>
        {
            stream.Flush(flushToDisk: true); Attack();
            if (fault == "flush") throw new IOException("Injected flush failure");
            if (fault == "cancel") cancellation.Cancel();
            if (fault == "closed_gate") gate.Close();
        };
        PublishResult result;
        try { result = await publisher.PublishAsync(source, final, cancellation.Token, gate); }
        finally { lockFinal?.Dispose(); }
        Assert.False(result.Success); Assert.Null(result.Receipt); Assert.Equal(PublicationCleanupOutcome.MarkedForDeletion, result.CleanupOutcome);
        Assert.Equal(old, Identity(final)); Assert.Equal(5, new FileInfo(final).Length);
        Assert.Equal(foreignId, Identity(foreign!)); Assert.Equal(7, new FileInfo(foreign!).Length); Assert.False(File.Exists(held));
        Assert.Equal(1, ops.Deletes); Assert.True(ops.Original!.IsClosed);
        if (fault == "rename_failure") { Assert.Contains(ops.RenameError, new[] { 5, 32, 33 }); output.WriteLine("Real replacement denial: " + ops.RenameError); }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ClosedStreamOrInvalidatedOriginalHandleLeavesBothObjectsWithoutPathDelete(bool closeStream)
    {
        using var f = new WindowsReadOnlyFileMetadataNativeTests.Files();
        var source = f.Create("source.mp4"); var final = f.Create("final.mp4", 5); var old = Identity(final);
        var ops = new Operations(); var publisher = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops);
        string? foreign = null, held = null; ObservedFileIdentity? foreignId = null, originalId = null;
        publisher.BeforeOriginalFlushForTests = stream =>
        {
            stream.Flush(flushToDisk: true); originalId = Identity(stream.SafeFileHandle);
            foreign = ReplaceTemporary(f, 7, out held); foreignId = Identity(foreign);
            if (closeStream) stream.Dispose(); else stream.SafeFileHandle.Dispose();
            throw new IOException("Original handle unavailable");
        };
        var result = await publisher.PublishAsync(source, final);
        Assert.False(result.Success); Assert.Null(result.Receipt); Assert.Equal(PublicationCleanupOutcome.RetainedHandleUnavailable, result.CleanupOutcome);
        Assert.Equal(old, Identity(final)); Assert.Equal(5, new FileInfo(final).Length);
        Assert.Equal(foreignId, Identity(foreign!)); Assert.Equal(7, new FileInfo(foreign!).Length);
        Assert.Equal(originalId, Identity(held!)); Assert.Equal(12, new FileInfo(held!).Length); Assert.True(ops.Original!.IsClosed);
    }
    [Fact]
    public async Task ReadOnlyDispositionFailureRetainsOriginalAndForeignNameWithoutFallback()
    {
        using var f = new WindowsReadOnlyFileMetadataNativeTests.Files();
        var source = f.Create("source.mp4"); var final = f.Create("final.mp4", 5); var old = Identity(final);
        var ops = new Operations(); var publisher = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops);
        string? foreign = null, held = null;
        publisher.BeforeOriginalFlushForTests = stream =>
        {
            stream.Flush(flushToDisk: true); foreign = ReplaceTemporary(f, 7, out held);
            File.SetAttributes(held, FileAttributes.ReadOnly); throw new IOException("Injected flush failure");
        };
        var result = await publisher.PublishAsync(source, final);
        Assert.False(result.Success); Assert.Equal(PublicationCleanupOutcome.RetainedDeleteFailed, result.CleanupOutcome);
        Assert.True(File.Exists(held)); Assert.Equal(12, new FileInfo(held!).Length); Assert.Equal(7, new FileInfo(foreign!).Length);
        Assert.Equal(old, Identity(final)); Assert.True(ops.Original!.IsClosed);
    }
    [Theory]
    [InlineData(false, "unsupported")] [InlineData(true, "unsupported")]
    [InlineData(false, "basic")] [InlineData(true, "basic")]
    [InlineData(false, "throw")] [InlineData(true, "throw")]
    public async Task OptionalIdentityFailureStillCommitsVerifiedOriginalObject(bool gated, string fault)
    {
        using var f = new WindowsReadOnlyFileMetadataNativeTests.Files(); var source = f.Create("source.mp4"); var final = f.Create("final.mp4", 5);
        var ops = new Operations(); var api = new PublicationIdentityAnchorTests.Native { Fault = fault };
        var publisher = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), identityApi: api, publicationFiles: ops);
        ObservedFileIdentity? original = null; publisher.OriginalCopyCreatedForTests = stream => original = Identity(stream.SafeFileHandle);
        var result = await publisher.PublishAsync(source, final, commitGate: gated ? new FileCommitGate() : null);
        Assert.True(result.Success); Assert.Null(result.Receipt); Assert.Equal(12, result.FinalSizeBytes); Assert.Equal(12, new FileInfo(final).Length);
        Assert.Equal(original, Identity(final)); Assert.Equal(1, ops.Renames); Assert.Equal(0, ops.Deletes); Assert.True(ops.Original!.IsClosed);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NativeCommitFailureNeverFallsBackEvenWhenIdentityIsUnsupported(bool unsupported)
    {
        using var f = new WindowsReadOnlyFileMetadataNativeTests.Files(); var source = f.Create("source.mp4"); var final = f.Create("final.mp4", 5); var old = Identity(final);
        var ops = new Operations(); var api = new PublicationIdentityAnchorTests.Native { Fault = unsupported ? "unsupported" : null };
        var publisher = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), identityApi: api, publicationFiles: ops);
        using var locked = new FileStream(final, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = await publisher.PublishAsync(source, final);
        Assert.False(result.Success); Assert.Null(result.Receipt); Assert.Equal(5, new FileInfo(final).Length); Assert.Equal(old, Identity(final));
        Assert.Equal(PublicationCleanupOutcome.MarkedForDeletion, result.CleanupOutcome); Assert.Empty(Directory.GetFiles(f.Videos, "*.publish-tmp-*.mp4"));
        Assert.Equal(1, ops.Renames); Assert.Equal(1, ops.Deletes); Assert.True(ops.Original!.IsClosed); Assert.Contains(ops.RenameError, new[] { 5, 32, 33 });
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UnicodeAndLongFinalPathsUseRealNativeRenameLayoutAndOriginalIdentity(bool longPath)
    {
        using var f = new WindowsReadOnlyFileMetadataNativeTests.Files(); var source = f.Create("source.mp4");
        var directory = longPath ? Path.Combine(f.Videos, new string('a', 70), new string('b', 70), new string('c', 70)) : f.Videos;
        Directory.CreateDirectory(directory); var final = Path.Combine(directory, "录制-🔒.mp4");
        Assert.Equal(IntPtr.Size == 8 ? 20 : 12, WindowsPublicationFileOperations.NameOffset);
        Assert.Equal(IntPtr.Size == 8 ? 16 : 8, WindowsPublicationFileOperations.NameLengthOffset);
        Assert.Equal(IntPtr.Size == 8 ? 24 : 16, Marshal.SizeOf<WindowsPublicationFileOperations.RenameHeader>());
        var ops = new Operations(); var publisher = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops);
        ObservedFileIdentity? original = null; publisher.OriginalCopyCreatedForTests = stream => original = Identity(stream.SafeFileHandle);
        var result = await publisher.PublishAsync(source, final);
        Assert.True(result.Success, result.FailureCategory); Assert.NotNull(result.Receipt); Assert.Equal(original, Identity(final));
        Assert.Equal(original, result.Receipt!.Anchor.Identity); Assert.Equal(final, result.Receipt.Anchor.Path); Assert.Equal(12, new FileInfo(final).Length);
        Assert.True(ops.Original!.IsClosed); using var exclusive = new FileStream(final, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (longPath) Assert.True(final.Length > 260);
    }
    [Fact]
    public async Task DeletedOriginalCannotBeReplacedByForeignNameAtCommit()
    {
        using var f = new WindowsReadOnlyFileMetadataNativeTests.Files(); var source = f.Create("source.mp4"); var final = f.Create("final.mp4", 5); var old = Identity(final);
        var ops = new Operations(); var publisher = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops);
        string? foreign = null; ObservedFileIdentity? foreignId = null;
        publisher.BeforeOriginalCommitForTests = () =>
        {
            foreign = Directory.GetFiles(f.Videos, "*.publish-tmp-*.mp4").Single(); File.Delete(foreign);
            f.Create(Path.GetFileName(foreign), 7); foreignId = Identity(foreign);
        };
        var result = await publisher.PublishAsync(source, final, commitGate: new FileCommitGate());
        Assert.False(result.Success); Assert.Null(result.Receipt); Assert.Equal(old, Identity(final)); Assert.Equal(5, new FileInfo(final).Length);
        Assert.Equal(foreignId, Identity(foreign!)); Assert.Equal(7, new FileInfo(foreign!).Length); Assert.True(ops.Original!.IsClosed);
        Assert.Equal(1, ops.Renames); Assert.Contains(ops.RenameError, new[] { 5, 32, 33, 303 });
        output.WriteLine("Delete-pending original rename denial: " + ops.RenameError);
    }
    [Fact]
    public void MissingDeleteAccessProducesRealNativeDenialAndCannotMoveOrDelete()
    {
        using var f = new WindowsReadOnlyFileMetadataNativeTests.Files(); var temp = f.Create("temp.mp4"); var final = f.Create("final.mp4", 5);
        using var handle = CreateFileW(temp, 0x40000000 | 0x80, 5, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        Assert.False(handle.IsInvalid); var ex = Assert.Throws<IOException>(() => WindowsPublicationFileOperations.Instance.Rename(handle, final));
        Assert.Equal(5, ex.HResult & 0xffff); Assert.Equal(PublicationCleanupOutcome.RetainedDeleteFailed, WindowsPublicationFileOperations.Instance.DeleteOriginal(handle));
        Assert.Equal(12, new FileInfo(temp).Length); Assert.Equal(5, new FileInfo(final).Length);
    }
    [Fact]
    public async Task GateCloseWaitsForInFlightObjectRenameAndRejectsEveryLaterCommit()
    {
        using var f = new WindowsReadOnlyFileMetadataNativeTests.Files(); var source = f.Create("source.mp4"); var final = Path.Combine(f.Videos, "final.mp4");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var closing = new ManualResetEventSlim();
        var ops = new Operations { BeforeRename = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); } };
        var publisher = new StagingToFinalPublisher(new PhysicalFileSystemOperations(), publicationFiles: ops); var gate = new FileCommitGate();
        var pending = Task.Run(() => publisher.PublishAsync(source, final, commitGate: gate));
        Task? close = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            close = Task.Run(() => { closing.Set(); gate.Close(); }); Assert.True(closing.Wait(TimeSpan.FromSeconds(5)));
            await Task.Delay(50); Assert.False(close.IsCompleted); Assert.False(File.Exists(final));
        }
        finally { release.Set(); }
        var result = await pending; await close!; Assert.True(result.Success); Assert.NotNull(result.Receipt); Assert.True(gate.IsClosed);
        ops.BeforeRename = null;
        var later = await publisher.PublishAsync(source, Path.Combine(f.Videos, "later.mp4"), commitGate: gate);
        Assert.False(later.Success); Assert.Equal("commit_closed", later.FailureCategory); Assert.Equal(1, ops.Renames);
        Assert.False(File.Exists(Path.Combine(f.Videos, "later.mp4"))); Assert.Equal(PublicationCleanupOutcome.MarkedForDeletion, later.CleanupOutcome);
        Assert.True(ops.Original!.IsClosed);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
}
