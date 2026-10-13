using System.Runtime.InteropServices;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class BundlePublicationIdentityTests(ITestOutputHelper output)
{
    internal sealed class Fixture
    {
        internal readonly string Root = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task337r", Guid.NewGuid().ToString("N")));
        internal string Media => Path.Combine(Root, "out.mp4");
        internal string Final => OrdinaryRecordingBundleOperation.DerivePath(Media);
        internal string Stage => Path.Combine(Root, "owned-source");
        internal readonly RecordingBundleContentsEvidence Contents = new(BundleContentsGenerationTests.Items());
        internal Fixture()
        {
            Directory.CreateDirectory(Stage);
            foreach (var item in Contents.Contents) File.WriteAllBytes(Path.Combine(Stage, item.Name), new byte[item.SizeBytes]);
        }
        internal RecordingBundleRequest Request(string? operation = null) => RequestFor(Media, "r", operation ?? Guid.NewGuid().ToString("N"));
        internal Task<RecordingBundleGenerationResult> Publish(BundlePublicationReceipt.Publisher? publisher = null, CancellationToken token = default)
            => (publisher ?? new()).PublishAsync(Request(), Stage, Final, Contents, token);
    }
    internal static RecordingBundleRequest RequestFor(string media, string id, string? operation) =>
        new(id, null, "display", "Synthetic", (0, 0, 64, 64), "virtual_screen", DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow,
            1, 1, 15, "ffmpeg", "duration_reached", false, "not_requested", null, null, null, null, null, null, media, "mp4", "h264", 64, 64)
        { PublicationOperationId = operation };

    [Theory]
    [InlineData("rename")] [InlineData("replace")] [InlineData("hardlink")] [InlineData("foreign")]
    public void StagingCleanupUsesOriginalObjectsAndPreservesForeignNames(string fault)
    {
        var f = new Fixture(); var dir = Path.Combine(f.Root, "tracked-staging");
        string path = Path.Combine(dir, "first_frame.png"), other = Path.Combine(f.Root, "other");
        using (var staging = new OwnedBundleStaging(dir))
        {
            staging.Prepare(path); File.WriteAllText(path, "original"); staging.Validate(path);
            if (fault is "rename" or "replace")
            { File.Move(path, other); if (fault == "replace") File.WriteAllText(path, "third-party"); }
            if (fault == "hardlink") Assert.True(CreateHardLinkW(other, path, IntPtr.Zero));
            if (fault == "foreign") File.WriteAllText(Path.Combine(dir, "foreign"), "third-party");
            if (fault != "foreign") Assert.Throws<IOException>(() => staging.Validate(path));
            staging.Cleanup();
            Assert.Equal(fault is "replace" or "foreign" ? "staging_retained_unproven_cleanup" : "staging_originals_removed", staging.CleanupFact);
        }
        if (fault == "replace") Assert.Equal("third-party", File.ReadAllText(path));
        if (fault == "foreign") Assert.Equal("third-party", File.ReadAllText(Path.Combine(dir, "foreign")));
        if (fault == "hardlink") Assert.Equal("original", File.ReadAllText(other));
        if (fault == "rename") Assert.False(File.Exists(other));
        if (fault is "rename" or "hardlink") Assert.False(Directory.Exists(dir));
    }
    [Fact]
    public void StagingFallbackRetiresDeletedOriginalAndCreatesNextExclusiveObject()
    {
        var f = new Fixture(); var dir = Path.Combine(f.Root, "tracked-fallback");
        using (var staging = new OwnedBundleStaging(dir))
        {
            var path = Path.Combine(dir, "last_frame.png");
            staging.Prepare(path); File.Delete(path); staging.Prepare(path);
            using (var stream = staging.OpenOriginalForWrite(path)) { stream.WriteByte(42); stream.Flush(true); }
            staging.Validate(path); Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(path));
            staging.Cleanup(); Assert.Equal("staging_originals_removed", staging.CleanupFact);
        }
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void NativeOwnedEmptyDirectoryRename()
    {
        var f = new Fixture(); using var owned = OwnedBundleDirectory.Create(Path.Combine(f.Root, "empty"));
        owned.Rename(Path.Combine(f.Root, "committed-empty")); Assert.True(Directory.Exists(Path.Combine(f.Root, "committed-empty")));
    }

    [Fact]
    public async Task RealLavfiGeneratorHeldObjectsRestartAndSameSizeReplacement()
    {
        using var f = new BundlePublicationHistoryTests.Fixture();
        var row = f.Row(); Directory.CreateDirectory(Path.GetDirectoryName(row.OutputPath)!);
        var ffmpeg = Path.Combine(TestHelper.ProjectRoot, "tools/ffmpeg/bin/ffmpeg.exe");
        var process = await new ExternalProcessRunner().RunAsync(ffmpeg, new[] { "-y", "-f", "lavfi", "-i", "color=c=blue:s=64x64:r=15", "-t", "1", "-c:v", "libx264", "-pix_fmt", "yuv420p", row.OutputPath }, TimeSpan.FromSeconds(15));
        Assert.Equal(0, process.ExitCode); Assert.False(process.TimedOut);
        row = row with { OutputSizeBytes = new FileInfo(row.OutputPath).Length };
        var op = OrdinaryRecordingBundleOperation.Create("r", row.OutputPath); f.Writer.Register(row with { BundleRegistration = new(op) });
        var publisher = new BundlePublicationReceipt.Publisher
        {
            StageForTests = (stage, stream) =>
            {
                if (stage == "before_commit") Assert.False(Directory.Exists(op.BundlePath));
                if (stage == "copy" && stream is not null)
                { stream.Flush(true); var native = WindowsFileMetadataApi.Instance; Assert.True(native.Identity(stream.SafeFileHandle, out var id, out _));
                    output.WriteLine($"original held FILE_ID_INFO volume={id.VolumeSerialNumber} file={id.IdHigh:x16}{id.IdLow:x16}"); }
            }
        };
        var generator = new FfmpegRecordingBundleGenerator(ffmpegPathProvider: () => ffmpeg) { PublisherFactory = () => publisher };
        var generated = await generator.GenerateAsync(RequestFor(row.OutputPath, "r", op.OperationId));
        Assert.True(generated.Success, generated.ErrorDetail); Assert.NotNull(generated.PublicationReceipt);
        Assert.Equal(generated.ContentsEvidence!.Contents.Sum(i => i.SizeBytes), publisher.CopiedBytes);
        Assert.Equal(new FileInfo(row.OutputPath).Length, row.OutputSizeBytes); // No MP4 recopy.
        var evidence = generated.PublicationReceipt.Evidence;
        Assert.Equal(op.OperationId, evidence.OperationId); Assert.Equal(op.BundlePath, evidence.BundlePath);
        foreach (var anchor in evidence.Anchors)
        {
            var sample = WindowsReadOnlyFileMetadataProbe.Instance.Observe(anchor.Path);
            Assert.Equal(FileObservationOutcome.Sampled, sample.Outcome); Assert.Equal(anchor.Identity, sample.Before!.Identity); Assert.Equal(anchor.Length, sample.Before.Length);
        }
        var snapshot = new OrdinaryRecordingBundleSnapshot("ready", op.BundlePath, null, generated.ContentsEvidence) { IdentityEvidence = evidence };
        Assert.True(f.Writer.TryPublishBundle(new(op, snapshot))); Assert.Equal(snapshot, f.Writer.Read("r")!.Bundle);
        f.Reopen(); Assert.Equal(snapshot, f.Writer.Read("r")!.Bundle);
        var service = new OrdinaryArtifactCheckService(f.Writer);
        var checkedResult = await service.ReadAsync("r"); Assert.Equal("complete", checkedResult!.ProbeState);
        Assert.All(checkedResult.Bundle.Contents!, i => Assert.Equal("match", i.IdentityState));
        var changed = evidence.Anchors[0]; File.Move(changed.Path, changed.Path + ".original");
        await File.WriteAllBytesAsync(changed.Path, new byte[changed.Length]);
        var replacement = await service.ReadAsync("r"); var first = replacement!.Bundle.Contents![0];
        Assert.Equal("present_size_matches", first.Observation); Assert.Equal("mismatch", first.IdentityState); Assert.Equal("publication_identity_mismatch", first.ReasonCode);
        output.WriteLine("Real FFmpeg lavfi -> formal generator -> V35 -> reopen -> five native identity matches; same-size replacement mismatches.");
    }

    [Theory]
    [InlineData("prepare")] [InlineData("create")] [InlineData("copy")] [InlineData("flush")] [InlineData("before_commit")]
    public async Task PrecommitCancellationNeverPublishesAnyFinalDirectory(string stage)
    {
        var f = new Fixture(); using var cancellation = new CancellationTokenSource();
        var publisher = new BundlePublicationReceipt.Publisher { StageForTests = (current, _) => { if (stage == current) cancellation.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Publish(publisher, cancellation.Token));
        Assert.False(Directory.Exists(f.Final)); Assert.InRange(publisher.CopiedBytes, 0, f.Contents.Contents.Sum(i => i.SizeBytes));
    }
    [Fact]
    public async Task CancellationAfterCommitPreservesCompleteTrustedResult()
    {
        var f = new Fixture(); using var cancellation = new CancellationTokenSource();
        var publisher = new BundlePublicationReceipt.Publisher { StageForTests = (stage, _) => { if (stage == "after_commit") cancellation.Cancel(); } };
        var ready = await f.Publish(publisher, cancellation.Token); Assert.True(ready.Success); Assert.NotNull(ready.PublicationReceipt);
        Assert.All(f.Contents.Contents, item => Assert.True(File.Exists(Path.Combine(f.Final, item.Name))));
    }
    [Fact]
    public async Task ConcurrentNoReplaceDirectoryCommitKeepsWinner()
    {
        var f = new Fixture(); using var barrier = new Barrier(2);
        BundlePublicationReceipt.Publisher Publisher() => new() { StageForTests = (stage, _) => { if (stage == "before_commit") Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5))); } };
        var tasks = new[] { Task.Run(() => f.Publish(Publisher())), Task.Run(() => f.Publish(Publisher())) };
        var results = await Task.WhenAll(tasks.Select(async task => { try { return await task; } catch (IOException) { return null; } }));
        var winner = Assert.Single(results, r => r is not null)!; Assert.True(winner.Success); Assert.NotNull(winner.PublicationReceipt);
        Assert.All(winner.PublicationReceipt.Evidence.Anchors, a => Assert.Equal(a.Identity, WindowsReadOnlyFileMetadataProbe.Instance.Observe(a.Path).Before!.Identity));
    }
    [Theory]
    [InlineData("rename")] [InlineData("replace")] [InlineData("hardlink")] [InlineData("foreign_directory_file")]
    public async Task OriginalMutationRejectsCommitAndCleanupPreservesThirdPartyObjects(string fault)
    {
        var f = new Fixture(); string? originalPath = null; string? foreignPath = null; var mutated = false;
        var files = new TrackingFiles();
        BundlePublicationReceipt.Publisher? publisher = null;
        publisher = new BundlePublicationReceipt.Publisher { Files = files, StageForTests = (stage, stream) =>
        {
            if (stage != "copy" || mutated || stream is null) return; mutated = true;
            originalPath = files.Paths[stream.SafeFileHandle];
            if (fault == "foreign_directory_file")
            { foreignPath = Path.Combine(publisher!.PublishingDirectoryForTests!, "third-party.txt"); File.WriteAllText(foreignPath, "preserve"); throw new IOException("owned failure"); }
            if (fault == "hardlink")
            { foreignPath = Path.Combine(f.Root, "third-party-link"); Assert.True(CreateHardLinkW(foreignPath, originalPath, IntPtr.Zero)); }
            else { File.Move(originalPath, originalPath + ".renamed"); if (fault == "replace") { foreignPath = originalPath; File.WriteAllText(foreignPath, "third-party replacement"); } }
        } };
        await Assert.ThrowsAsync<IOException>(() => f.Publish(publisher)); Assert.False(Directory.Exists(f.Final));
        if (fault == "replace") Assert.Equal("third-party replacement", File.ReadAllText(foreignPath!));
        if (fault == "foreign_directory_file") Assert.Equal("preserve", File.ReadAllText(foreignPath!));
        if (fault == "hardlink") Assert.True(File.Exists(foreignPath));
        if (fault == "foreign_directory_file") Assert.Equal(PublicationCleanupOutcome.RetainedDeleteFailed, publisher.Cleanup);
    }
    [Fact]
    public async Task OriginalDirectoryCannotBeSubstitutedWhileContextOwnsIt()
    {
        var f = new Fixture(); var files = new TrackingFiles(); var seen = false;
        BundlePublicationReceipt.Publisher? publisher = null;
        publisher = new BundlePublicationReceipt.Publisher { Files = files, StageForTests = (stage, stream) =>
        {
            if (stage != "create" || seen) return; seen = true; var directory = publisher!.PublishingDirectoryForTests!;
            Assert.Throws<IOException>(() => Directory.Move(directory, directory + ".foreign"));
        } };
        Assert.NotNull((await f.Publish(publisher)).PublicationReceipt); Assert.True(seen);
    }
    [Fact]
    public async Task FormalCopySharingPinsEveryAliasThroughDirectoryCommit()
    {
        var f = new Fixture(); BundlePublicationReceipt.Publisher? publisher = null; var attempts = 0;
        publisher = new() { StageForTests = (stage, _) =>
        {
            if (stage != "after_commit") return;
            foreach (var item in f.Contents.Contents)
            {
                var alias = Path.Combine(f.Final, item.Name);
                Assert.Throws<IOException>(() => File.Move(alias, alias + ".foreign"));
                Assert.Throws<IOException>(() => File.Delete(alias));
                Assert.Throws<IOException>(() => File.WriteAllBytes(alias, new byte[item.SizeBytes]));
                attempts++;
            }
        } };
        var result = await f.Publish(publisher); Assert.NotNull(result.PublicationReceipt); Assert.Equal(5, attempts);
        Assert.All(result.PublicationReceipt.Evidence.Anchors, a => Assert.Equal(a.Identity, WindowsReadOnlyFileMetadataProbe.Instance.Observe(a.Path).Before!.Identity));
    }
    [Fact]
    public async Task CleanupWithoutDeletePermissionRetainsObjectsAndNeverClaimsSuccess()
    {
        var f = new Fixture(); var files = new RetainingFiles();
        var publisher = new BundlePublicationReceipt.Publisher { Files = files, StageForTests = (stage, _) => { if (stage == "flush") throw new IOException("owned flush failure"); } };
        await Assert.ThrowsAsync<IOException>(() => f.Publish(publisher)); Assert.False(Directory.Exists(f.Final));
        Assert.Single(files.Paths); Assert.True(File.Exists(files.Paths[0]));
    }
    [Theory]
    [InlineData("source_short")] [InlineData("source_long")] [InlineData("flush")] [InlineData("late")] [InlineData("existing_file")] [InlineData("existing_directory")]
    public async Task FailedPublishNeverIssuesPartialEvidenceAndNeverDeletesExistingFinal(string fault)
    {
        var f = new Fixture();
        if (fault.StartsWith("source")) File.WriteAllBytes(Path.Combine(f.Stage, "metadata.json"), new byte[fault == "source_short" ? 1 : 100]);
        if (fault == "existing_file") File.WriteAllText(f.Final, "preserve");
        if (fault == "existing_directory") { Directory.CreateDirectory(f.Final); File.WriteAllText(Path.Combine(f.Final, "third-party"), "preserve"); }
        var publisher = new BundlePublicationReceipt.Publisher { StageForTests = (stage, _) => { if (stage == (fault == "late" ? "after_commit" : "flush") && fault is "flush" or "late") throw new IOException("owned failure"); } };
        await Assert.ThrowsAsync<IOException>(() => f.Publish(publisher));
        if (fault == "existing_file") Assert.Equal("preserve", File.ReadAllText(f.Final));
        else if (fault == "existing_directory") Assert.Equal("preserve", File.ReadAllText(Path.Combine(f.Final, "third-party")));
        else Assert.Equal(fault == "late", Directory.Exists(f.Final));
        if (fault == "late") Assert.All(f.Contents.Contents, item => Assert.True(File.Exists(Path.Combine(f.Final, item.Name))));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MissingOptionalNativeIdentityOrUnboundRequestRemainsUnknown(bool unbound)
    {
        var f = new Fixture(); var publisher = new BundlePublicationReceipt.Publisher { IdentityApi = unbound ? WindowsFileMetadataApi.Instance : new NoIdentityApi() };
        var request = unbound ? RequestFor(f.Media, "r", null) : f.Request();
        var result = await publisher.PublishAsync(request, f.Stage, f.Final, f.Contents, default);
        Assert.True(result.Success); Assert.NotNull(result.ContentsEvidence); Assert.Null(result.PublicationReceipt);
        Assert.Null(RecordingBundleGenerationResult.Ready(f.Final, f.Contents.Contents).PublicationReceipt);
        Assert.Null(RecordingBundleGenerationResult.Ready(f.Final).PublicationReceipt);
    }
    [Fact]
    public async Task CopyGuardAccountsOnlyActualAttachmentBytesWithoutMediaReservation()
    {
        var f = new Fixture(); var provider = new Capacity(); BundlePublicationCapacity.Reservation? reservation = null;
        var sum = f.Contents.Contents.Sum(i => i.SizeBytes);
        var publisher = new BundlePublicationReceipt.Publisher { BeginCopy = (bytes, _) => { Assert.Equal(sum, bytes); reservation = new(bytes, provider); return Task.FromResult<IPublicationCopyReservation?>(reservation); } };
        provider.Written = () => publisher.CopiedBytes;
        Assert.True((await f.Publish(publisher)).Success); Assert.Equal(sum, publisher.CopiedBytes);
        Assert.Equal(0, reservation!.Remaining); Assert.Contains(WindowStorageSafety.LowWaterBytes + sum, provider.Available);
        Assert.Contains(WindowStorageSafety.LowWaterBytes, provider.Available);
    }
    [Theory]
    [InlineData("App")] [InlineData("Headless")]
    public async Task ActualCapacityChildProtectsRealAttachmentCopyWithoutCapture(string host)
    {
        var f = new Fixture(); var executable = Path.Combine(AppContext.BaseDirectory, "AgentRecorder." + host + ".exe");
        Assert.True(File.Exists(executable));
        var provider = new ProcessStorageCapacityProvider { ExecutablePath = () => executable };
        var publisher = new BundlePublicationReceipt.Publisher { BeginCopy = (bytes, _) => Task.FromResult<IPublicationCopyReservation?>(new BundlePublicationCapacity.Reservation(bytes, provider)) };
        var ready = await f.Publish(publisher); Assert.NotNull(ready.PublicationReceipt); Assert.Equal(65, publisher.CopiedBytes);
        output.WriteLine($"host={host}, actual capacity child exited, only five attachments physically copied={publisher.CopiedBytes}; no media copy or capture.");
    }
    [Fact]
    public void PrivateAccessRestoresExactDaclAndDoesNotGrantReadRights()
    {
        var f = new Fixture(); var path = Path.Combine(f.Root, "owned-access");
        using (var original = BundlePublicationFileOperations.Instance.CreateNew(path))
        {
            var before = Security(original.SafeFileHandle);
            using (var hold = BundleOriginalAccess.Hold(original.SafeFileHandle))
            {
                Assert.Equal(before, Security(original.SafeFileHandle));
                Assert.Equal(before, Security(hold.Handle));
            }
            Assert.Equal(before, Security(original.SafeFileHandle));
        }
        File.WriteAllText(path, "normal access restored"); Assert.Equal("normal access restored", File.ReadAllText(path));
    }
    private static byte[] Security(SafeFileHandle handle)
    { GetKernelObjectSecurity(handle, 4, null, 0, out var length); var bytes = new byte[length]; Assert.True(GetKernelObjectSecurity(handle, 4, bytes, length, out _)); return bytes; }
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[]? descriptor, uint length, out uint required);
    [Theory]
    [InlineData("low")] [InlineData("unavailable")] [InlineData("cancel")]
    public async Task ApplicableCapacityGuardStopsBeforeCommit(string fault)
    {
        var f = new Fixture(); using var cancel = new CancellationTokenSource();
        var provider = new Capacity { Fault = fault, Cancel = cancel };
        var publisher = new BundlePublicationReceipt.Publisher { BeginCopy = (bytes, _) => Task.FromResult<IPublicationCopyReservation?>(new BundlePublicationCapacity.Reservation(bytes, provider)) };
        if (fault == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Publish(publisher, cancel.Token));
        else await Assert.ThrowsAnyAsync<IOException>(() => f.Publish(publisher));
        Assert.False(Directory.Exists(f.Final)); Assert.Equal(0, publisher.CopiedBytes);
    }
    internal sealed class Capacity : IStorageCapacityProvider
    {
        internal string? Fault; internal CancellationTokenSource? Cancel; internal readonly List<long> Available = new(); internal Func<long> Written = () => 0;
        public Task<IReadOnlyList<StorageVolumeSample>> QueryAsync(IReadOnlyList<string> paths, CancellationToken token)
        {
            if (Fault == "cancel") { Cancel!.Cancel(); token.ThrowIfCancellationRequested(); }
            var available = Fault == "low" ? 0 : WindowStorageSafety.LowWaterBytes + 65 - Written();
            // Five attachments total 65 bytes; each write changes free space, never subtract twice.
            Available.Add(available);
            if (Fault == "unavailable") return Task.FromResult<IReadOnlyList<StorageVolumeSample>>(Array.Empty<StorageVolumeSample>());
            return Task.FromResult<IReadOnlyList<StorageVolumeSample>>(new[] { new StorageVolumeSample(paths[0], "owned-volume", available) });
        }
    }
    internal sealed class TrackingFiles : IPublicationFileOperations
    {
        internal readonly Dictionary<SafeFileHandle, string> Paths = new();
        public FileStream CreateNew(string path) { var stream = WindowsPublicationFileOperations.NoReplace.CreateNew(path); Paths.Add(stream.SafeFileHandle, path); return stream; }
        public void Rename(SafeFileHandle original, string final) => WindowsPublicationFileOperations.NoReplace.Rename(original, final);
        public PublicationCleanupOutcome DeleteOriginal(SafeFileHandle original) => WindowsPublicationFileOperations.NoReplace.DeleteOriginal(original);
    }
    private sealed class RetainingFiles : IPublicationFileOperations
    {
        internal readonly List<string> Paths = new();
        public FileStream CreateNew(string path) { Paths.Add(path); return BundlePublicationFileOperations.Instance.CreateNew(path); }
        public void Rename(SafeFileHandle original, string final) => throw new NotSupportedException();
        public PublicationCleanupOutcome DeleteOriginal(SafeFileHandle original) => PublicationCleanupOutcome.RetainedDeleteFailed;
    }
    internal sealed class NoIdentityApi : IWindowsFileMetadataApi
    {
        private readonly IWindowsFileMetadataApi _real = WindowsFileMetadataApi.Instance;
        public bool Supported => true;
        public uint DriveType(string root) => _real.DriveType(root);
        public SafeFileHandle Open(string path, uint access, uint sharing, uint creation, uint flags, out int error) => _real.Open(path, access, sharing, creation, flags, out error);
        public bool Basic(SafeFileHandle handle, out FileBasicInformation value, out int error) => _real.Basic(handle, out value, out error);
        public bool Standard(SafeFileHandle handle, out FileStandardInformation value, out int error) => _real.Standard(handle, out value, out error);
        public bool Identity(SafeFileHandle handle, out FileIdentityInformation value, out int error) { value = default; error = 50; return false; }
        public uint FinalPath(SafeFileHandle handle, System.Text.StringBuilder buffer, int capacity, out int error) => _real.FinalPath(handle, buffer, capacity, out error);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateHardLinkW(string link, string existing, IntPtr security);
}
