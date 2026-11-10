using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed partial class ManagedVideoReconciliation421Tests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentOrEmptyIsPositiveEvidenceWithoutCreatingAnything(bool present)
    {
        using var f = new Fixture();
        if (present) Directory.CreateDirectory(f.Videos);
        var before = f.Db.Snapshot();
        var start = DateTimeOffset.UtcNow;
        var sample = await f.Service().ReadAsync();
        Assert.Null(sample.UnavailableReason);
        Assert.Equal(present ? "present" : "absent", sample.RootState);
        Assert.Equal((0, 0, 0, 0), Counts(sample));
        Assert.Empty(sample.ReasonCodes);
        Assert.Equal(TimeSpan.Zero, sample.SampledAtUtc.Offset);
        Assert.InRange(sample.SampledAtUtc, start, DateTimeOffset.UtcNow);
        Assert.Equal(present, Directory.Exists(f.Videos));
        Assert.Equal(before, f.Db.Snapshot());
    }

    [Fact]
    public async Task TrustedExactMatchUnregisteredAndMissingUseDistinctCountersWithoutContentIo()
    {
        using var f = new Fixture();
        var main = f.File("private-title-主片.mp4", 31);
        var unregistered = f.File("private-unregistered.mp4", 0);
        var missing = f.File("missing.mp4", 42);
        f.Register("main", main);
        f.Register("duplicate", main);
        f.Register("gone", missing);
        File.Move(missing, Path.Combine(f.Db.Root, "outside-missing.mp4"));
        var external = f.File("external.mp4", 12, f.Db.Root);
        f.Register("external", external);
        f.File("ignored.mp4", 12, Path.Combine(f.Videos, "nested"));
        f.File("notes.txt", 12);
        var writeTime = File.GetLastWriteTimeUtc(main);
        var data = f.Db.Snapshot();
        var sample = await f.Service().ReadAsync();
        Assert.Null(sample.UnavailableReason);
        Assert.Equal("present", sample.RootState);
        Assert.Equal((1, 1, 1, 0), Counts(sample));
        Assert.Empty(sample.ReasonCodes);
        Assert.Equal(data, f.Db.Snapshot());
        Assert.Equal(new byte[31], File.ReadAllBytes(main));
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(main));
        Assert.Equal(0, new FileInfo(unregistered).Length);
        Assert.Equal(12, new FileInfo(external).Length);
    }

    [Fact]
    public async Task ConfirmedAbsentRootCanProveRegisteredLocationMissing()
    {
        using var f = new Fixture();
        var old = f.File("old.mp4", 44);
        f.Register("old", old);
        Directory.Move(f.Videos, Path.Combine(f.Db.Root, "fixture-relocated"));
        var sample = await f.Service().ReadAsync();
        Assert.Null(sample.UnavailableReason);
        Assert.Equal("absent", sample.RootState);
        Assert.Equal((0, 0, 1, 0), Counts(sample));
        Assert.Empty(sample.ReasonCodes);
        Assert.False(Directory.Exists(f.Videos));
    }

    [Theory]
    [InlineData("replaced")]
    [InlineData("different_length")]
    [InlineData("renamed")]
    [InlineData("unanchored")]
    [InlineData("conflicting_anchors")]
    public async Task ChangesOrMissingEvidenceAreUncertainRatherThanMissingOrUnregistered(string scenario)
    {
        using var f = new Fixture();
        var media = f.File("original.mp4", 12);
        var identity = f.Register("original", media, anchor: scenario != "unanchored");
        switch (scenario)
        {
            case "replaced":
                File.Move(media, Path.Combine(f.Db.Root, "retained-original.mp4"));
                f.File("original.mp4", 12);
                break;
            case "different_length":
                File.WriteAllBytes(media, new byte[13]);
                break;
            case "renamed":
                File.Move(media, Path.Combine(f.Videos, "renamed.mp4"));
                break;
            case "conflicting_anchors":
                f.Register("competing", media, identity: identity with { IdHigh = identity.IdHigh ^ 3 });
                break;
        }
        var sample = await f.Service().ReadAsync();
        Assert.Null(sample.UnavailableReason);
        Assert.Equal((0, 0, 0, scenario == "renamed" ? 2 : 1), Counts(sample));
        Assert.Equal(new[] { scenario switch
        {
            "unanchored" => "publication_evidence_missing",
            "conflicting_anchors" => "publication_identity_conflict",
            _ => "path_or_identity_conflict"
        } }, sample.ReasonCodes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeniedReadAndActiveSharedWriterDoNotProduceCountsForThatFile(bool deny)
    {
        using var f = new Fixture();
        var path = f.File("blocked.mp4", 12);
        f.Register("registered", path);
        f.File("observed.mp4", 11);
        var info = new FileInfo(path);
        FileSecurity? blocked = null;
        FileSystemAccessRule? rule = null;
        FileStream? writer = null;
        if (deny)
        {
            blocked = info.GetAccessControl(AccessControlSections.Access);
            using var user = WindowsIdentity.GetCurrent();
            rule = new FileSystemAccessRule(user.User!, FileSystemRights.ReadData, AccessControlType.Deny);
            blocked.AddAccessRule(rule);
            info.SetAccessControl(blocked);
        }
        else writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            var sample = await f.Service().ReadAsync();
            Assert.Null(sample.UnavailableReason);
            Assert.Equal((0, 1, 0, 1), Counts(sample));
            Assert.Equal(["entry_unverified"], sample.ReasonCodes);
        }
        finally
        {
            writer?.Dispose();
            if (blocked is not null && rule is not null)
            {
                blocked.RemoveAccessRuleSpecific(rule);
                info.SetAccessControl(blocked);
            }
        }
        Assert.Equal(new byte[12], File.ReadAllBytes(path));
    }

    [Fact]
    public async Task AnExternalHardLinkKeepsAnExactAnchorUncertain()
    {
        using var f = new Fixture();
        var media = f.File("linked.mp4", 15);
        f.Register("owned", media);
        var link = Path.Combine(f.Db.Root, "external-hardlink.mp4");
        Assert.True(CreateHardLinkW(link, media, IntPtr.Zero), Marshal.GetLastWin32Error().ToString());
        var sample = await f.Service().ReadAsync();
        Assert.Null(sample.UnavailableReason);
        Assert.Equal((0, 0, 0, 1), Counts(sample));
        Assert.Equal(["multiple_links"], sample.ReasonCodes);
        Assert.Equal(new byte[15], File.ReadAllBytes(link));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShortLeafAndKnownAncestorAliasesNeverProveAMatchOrMissing(bool ancestor)
    {
        const string root = @"D:\managed directory\Videos";
        const string longName = "capture-long-name.mp4";
        const string shortName = "CAPTUR~1.MP4";
        var path = ancestor ? @"D:\MANAGE~1\Videos\" + longName : Path.Combine(root, shortName);
        Assert.Equal(ancestor, ManagedVideoReconciliation.IsAlternatePath(root, ["MANAGE~1", null], path));
        Assert.False(ManagedVideoReconciliation.IsAlternatePath(root, ["MANAGE~1", null], @"D:\MANAGE~2\Videos\capture.mp4"));
        var identity = new ObservedFileIdentity(1, 2, 3);
        var row = new ManagedVideoUsage.Registration("owned", path, DateTimeOffset.UtcNow, new(path, 17, identity), ancestor);
        var directory = new ManagedVideoDirectoryObservation(false, [new(longName, shortName, identity, true, 17, null)]);
        var sample = ManagedVideoReconciliation.Compare([row], directory, DateTimeOffset.UtcNow, () => { });
        Assert.Equal((0, 0, 0, ancestor ? 1 : 2), Counts(sample));
        Assert.Equal(["path_or_identity_conflict"], sample.ReasonCodes);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("ancestor")]
    [InlineData("leaf")]
    public async Task ReparsePointsNeverLeadToExternalData(string where)
    {
        using var f = new Fixture();
        var target = Path.Combine(f.Db.Root, "external-target");
        var external = f.File("external.mp4", 47, where == "ancestor" ? Path.Combine(target, "Videos") : target);
        var alias = where switch
        {
            "ancestor" => Path.Combine(f.Db.Root, "alias"),
            "root" => f.Videos,
            _ => Path.Combine(f.Videos, "alias.mp4")
        };
        Directory.CreateDirectory(Path.GetDirectoryName(alias)!);
        CreateJunction(alias, target);
        try
        {
            var sample = await (where == "ancestor" ? new ManagedVideoReconciliation(f.Db.Store, alias) : f.Service()).ReadAsync();
            if (where == "leaf")
            {
                Assert.Null(sample.UnavailableReason);
                Assert.Equal((0, 0, 0, 1), Counts(sample));
                Assert.Equal(["entry_reparse"], sample.ReasonCodes);
            }
            else AssertUnavailable(sample, "root_unavailable");
            Assert.Equal(new byte[47], File.ReadAllBytes(external));
        }
        finally { Directory.Delete(alias, recursive: false); }
    }

    [Fact]
    public async Task DirectoryPinPreventsRootRenameAndNamespaceChangeInvalidatesWholeSample()
    {
        using var f = new Fixture();
        var old = f.File("original.mp4", 13);
        f.Register("old", old);
        var displaced = Path.Combine(f.Db.Root, "displaced");
        bool held = false;
        var service = new ManagedVideoReconciliation(f.Db.Store, f.Db.Root)
        {
            AfterListingForTests = () =>
            {
                held = Record.Exception(() => Directory.Move(f.Videos, displaced)) is IOException or UnauthorizedAccessException;
                f.File("added.mp4", 22);
            }
        };
        var sample = await service.ReadAsync();
        Assert.True(held);
        AssertUnavailable(sample, "root_changed");
        Assert.Equal(new byte[13], File.ReadAllBytes(old));
        Assert.Equal(22, new FileInfo(Path.Combine(f.Videos, "added.mp4")).Length);
    }

    [Theory]
    [InlineData("history_limit")]
    [InlineData("scan_limit")]
    [InlineData("data_invalid")]
    public async Task BudgetOrSchemaFailureHasNoSample(string reason)
    {
        using var f = new Fixture();
        if (reason == "history_limit")
            f.Db.Execute($"""
                WITH RECURSIVE rows(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM rows WHERE i<{ManagedVideoUsage.HistoryLimit + 1})
                INSERT INTO ordinary_recording_history(recording_id,history_sequence,created_at_utc,status_code,source_type,capture_semantics,
                    source_title,audio_mode,output_path,completed_at_utc,updated_at_utc)
                SELECT 'bulk-'||i,i,0,'completed','display','display_capture','synthetic','none','C:\outside\external.mp4',10,10 FROM rows;
                """);
        else if (reason == "scan_limit")
        {
            for (var i = 0; i <= WindowsManagedVideoInventory.EntryLimit; i++) f.File($"noncandidate-{i}.txt", 0);
        }
        else f.Db.Execute("DROP INDEX idx_ordinary_recording_history_status_created;");
        var before = f.Db.Snapshot();
        AssertUnavailable(await f.Service().ReadAsync(), reason);
        Assert.Equal(before, f.Db.Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutOrCancellationKeepsSharedWorkerAndDrainUntilActualReturn(bool cancel)
    {
        using var f = new Fixture();
        f.File("observed.mp4", 7);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ManagedVideoReconciliation(f.Db.Store, f.Db.Root)
        {
            AfterListingForTests = () => { entered.TrySetResult(); release.Wait(Bound); },
            DeadlineForTests = token => deadline.Task.WaitAsync(token)
        };
        var request = service.ReadAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(Bound);
            if (cancel) cancellation.Cancel(); else deadline.TrySetResult();
            AssertUnavailable(await request.WaitAsync(Bound), cancel ? "cancelled" : "timeout");
            Assert.False(service.WaitForIdleAsync().IsCompleted);
            Assert.Equal("artifact_metadata_worker_pending", HostReadOnlyWorkerState.BusyReason);
            AssertUnavailable(await service.ReadAsync(), "busy");
            Assert.Equal("busy", (await new ManagedVideoUsage(f.Db.Store, f.Db.Root).ReadAsync()).UnavailableReason);
        }
        finally
        {
            release.Set();
            await service.WaitForIdleAsync().WaitAsync(Bound);
        }
        Assert.Null(HostReadOnlyWorkerState.BusyReason);
        Assert.Equal((0, 1, 0, 0), Counts(await f.Service().ReadAsync()));
    }

    [Fact]
    public async Task CooperativeFileBudgetRejectsInsteadOfReturningAnEmptySample()
    {
        using var f = new Fixture();
        f.File("observed.mp4", 7);
        var service = new ManagedVideoReconciliation(f.Db.Store, f.Db.Root)
        {
            AfterListingForTests = () => Thread.Sleep(WindowsManagedVideoInventory.ScanMilliseconds + 30)
        };
        AssertUnavailable(await service.ReadAsync(), "scan_timeout");
        Assert.True(service.WaitForIdleAsync().IsCompletedSuccessfully);
    }

    private static (int, int, int, int) Counts(ManagedVideoReconciliationSample s) =>
        (s.MatchedFiles, s.FilesWithoutMatchingRegistration, s.MissingRegistrations, s.UncertainCandidates);

    private static void AssertUnavailable(ManagedVideoReconciliationSample sample, string reason)
    {
        Assert.Equal(reason, sample.UnavailableReason);
        Assert.Null(sample.RootState);
        Assert.Equal(default, sample.SampledAtUtc);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly UnifiedHistoryTestDatabase Db = new(Path.Combine(Path.GetTempPath(), "task421-" + Guid.NewGuid().ToString("N")));
        internal string Videos => Path.Combine(Db.Root, "Videos");
        internal ManagedVideoReconciliation Service() => new(Db.Store, Db.Root);
        internal string File(string name, int length, string? directory = null)
        {
            directory ??= Videos;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name);
            System.IO.File.WriteAllBytes(path, new byte[length]);
            return path;
        }
        internal ObservedFileIdentity Register(string id, string path, bool anchor = true, ObservedFileIdentity? identity = null)
        {
            var meta = WindowsReadOnlyFileMetadataProbe.Instance.Observe(path).Before!;
            identity ??= meta.Identity!;
            Db.Ordinary.Register(Db.Recording(id, completed: true) with
            {
                OutputPath = path, OutputSizeBytes = meta.Length,
                PublicationReceipt = anchor ? PublicationReceipt.IssueForTests(path, meta.Length, identity) : null
            });
            return identity;
        }
        public void Dispose() => Db.Dispose();
    }

    private static void CreateJunction(string path, string target)
    {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"New-Item -ErrorAction Stop -ItemType Junction -Path '{path.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' | Out-Null");
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(5000));
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string target, IntPtr security);
}
