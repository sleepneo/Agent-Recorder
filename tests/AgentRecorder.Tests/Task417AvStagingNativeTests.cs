using System.Diagnostics;
using System.Runtime.InteropServices;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class Task417AvStagingNativeTests
{
    private static string Candidate(string root) => Path.Combine(root, ".agent-recorder-av-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("missing_ancestor")]
    [InlineData("missing_temp")]
    [InlineData("empty_temp")]
    public void EmptyIsProvenWithoutCreatingAnything(string kind)
    {
        using var fixture = new TempDirectory();
        var data = kind == "missing_ancestor" ? Path.Combine(fixture.Path, "missing", "also-missing") : fixture.Path;
        var root = Path.Combine(data, "temp");
        if (kind == "empty_temp") Directory.CreateDirectory(root);
        var sample = Sample(root);
        Assert.Equal(0, sample.LogicalBytes);
        Assert.Equal(0, sample.ObservedDirectories);
        Assert.Equal(0, sample.VerifiedFiles);
        Assert.Empty(sample.ReasonCodes);
        Assert.Equal(kind != "missing_ancestor", Directory.Exists(data));
        Assert.Equal(kind == "empty_temp", Directory.Exists(root));
    }

    [Fact]
    public void OnlyDirectWriterLeavesContributeLogicalBytesIncludingOpenAndEmptyFiles()
    {
        using var fixture = new TempDirectory();
        var root = Path.Combine(fixture.Path, "temp");
        var directory = Directory.CreateDirectory(Candidate(root)).FullName;
        var audio = Path.Combine(directory, "rec_private_audio.wav");
        using var writer = new FileStream(audio, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
        writer.SetLength(18729);
        var video = Put(directory, "rec_private_video.mp4", 11);
        Put(directory, "empty_video.mp4", 0);
        Put(directory, "diagnostics.json", 42);
        Put(directory, "_video.mp4", 52);
        Put(Path.Combine(directory, "nested"), "wrong_audio.wav", 62);
        Put(Path.Combine(root, ".agent-recorder-av-" + new string('z', 32)), "wrong_video.mp4", 72);
        Put(root, "loose_video.mp4", 82);
        Put(Candidate(Path.Combine(fixture.Path, "Videos")), "wrong_audio.wav", 92);
        var before = File.GetLastWriteTimeUtc(video);
        var sample = Sample(root);
        Assert.Equal(1, sample.ObservedDirectories);
        Assert.Equal(3, sample.VerifiedFiles);
        Assert.Equal(18740, sample.LogicalBytes);
        Assert.Equal(0, sample.SkippedEntries);
        Assert.Empty(sample.ReasonCodes);
        Assert.Equal(before, File.GetLastWriteTimeUtc(video));
        Assert.Equal(new byte[11], File.ReadAllBytes(video));
        Assert.Equal(18729, writer.Length);
    }

    [Theory]
    [InlineData("ancestor")]
    [InlineData("root")]
    [InlineData("candidate")]
    [InlineData("leaf")]
    public void WindowsReparseEntriesAreNeverFollowed(string position)
    {
        using var fixture = new TempDirectory();
        var target = Path.Combine(fixture.Path, "target");
        var targetRoot = position == "ancestor" ? Path.Combine(target, "temp") : target;
        var targetVideo = Put(position == "candidate" ? target : Candidate(targetRoot), "outside_video.mp4", 2048);
        var data = position == "ancestor" ? Path.Combine(fixture.Path, "alias") : fixture.Path;
        var root = Path.Combine(data, "temp");
        var alias = position switch
        {
            "ancestor" => data,
            "root" => root,
            "candidate" => Candidate(root),
            _ => Path.Combine(Candidate(root), "link_video.mp4")
        };
        Directory.CreateDirectory(Path.GetDirectoryName(alias)!);
        // A junction can occupy the leaf name even without the symlink privilege.
        CreateJunction(alias, target);
        try
        {
            var result = WindowsAvStagingInventory.Observe(root);
            if (position is "ancestor" or "root")
            {
                Assert.Null(result.Sample);
                Assert.Equal("root_unavailable", result.FailureReason);
            }
            else
            {
                var sample = Assert.IsType<AvStagingInventorySample>(result.Sample);
                Assert.Equal(0, sample.VerifiedFiles);
                Assert.Equal(0, sample.LogicalBytes);
                Assert.Equal(1, sample.SkippedEntries);
                Assert.Equal(new[] { "entry_reparse" }, sample.ReasonCodes);
            }
            Assert.Equal(2048, new FileInfo(targetVideo).Length);
        }
        finally
        {
            Directory.Delete(alias, recursive: false);
        }
    }

    [Fact]
    public void MultipleHardLinksAndAnUnopenableCandidateLeavePartialEvidence()
    {
        using var fixture = new TempDirectory();
        var root = Path.Combine(fixture.Path, "temp");
        var accepted = Candidate(root);
        var linked = Put(accepted, "linked_video.mp4", 40);
        Assert.True(CreateHardLinkW(Path.Combine(fixture.Path, "outside.mp4"), linked, IntPtr.Zero));
        Put(accepted, "good_audio.wav", 71);
        var blocked = Candidate(root);
        Put(blocked, "hidden_video.mp4", 23);
        using var exclusive = WindowsFileMetadataApi.Instance.Open(@"\\?\" + blocked, 1, 0, 3,
            WindowsReadOnlyFileMetadataProbe.OpenFlags, out var openError);
        Assert.False(exclusive.IsInvalid, $"open error {openError}");
        var sample = Sample(root);
        Assert.Equal(1, sample.ObservedDirectories);
        Assert.Equal(1, sample.VerifiedFiles);
        Assert.Equal(71, sample.LogicalBytes);
        Assert.Equal(2, sample.SkippedEntries);
        Assert.Equal(new[] { "entry_unavailable", "multiple_links" }, sample.ReasonCodes);
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("leaf")]
    [InlineData("candidate_after_open")]
    [InlineData("root_after_open")]
    public void ReplacementCannotBeCountedAsAnEnumeratedOriginal(string when)
    {
        using var fixture = new TempDirectory();
        var root = Path.Combine(fixture.Path, "temp");
        var candidate = Candidate(root);
        var original = Put(candidate, "rec_video.mp4", 41);
        var trigger = when is "candidate" or "root_after_open" ? candidate : original;
        var replaced = when.StartsWith("candidate", StringComparison.Ordinal) ? candidate : when == "root_after_open" ? root : original;
        var archived = replaced + ".displaced";
        var changes = 0;
        var result = WindowsAvStagingInventory.Observe(root, path =>
        {
            if (path != trigger || changes++ != 0) return;
            if (when == "leaf")
            {
                File.Move(replaced, archived);
                Put(candidate, "rec_video.mp4", 122);
            }
            else
            {
                Directory.Move(replaced, archived);
                Put(candidate, "rec_video.mp4", 122);
            }
        });
        Assert.True(changes > 0);
        if (when == "root_after_open")
        {
            Assert.Null(result.Sample);
            Assert.Equal("root_changed", result.FailureReason);
        }
        else
        {
            var sample = Assert.IsType<AvStagingInventorySample>(result.Sample);
            Assert.Equal(0, sample.LogicalBytes);
            Assert.Equal(0, sample.VerifiedFiles);
            Assert.Contains("entry_changed", sample.ReasonCodes);
            Assert.InRange(sample.SkippedEntries, 1, 2);
        }
        Assert.Equal(122, new FileInfo(original).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EntryBudgetIncludesUnmatchedNamesAtBothLevels(bool candidate)
    {
        using var fixture = new TempDirectory();
        var root = Path.Combine(fixture.Path, "temp");
        var dir = candidate ? Candidate(root) : root;
        Directory.CreateDirectory(dir);
        for (var i = 0; i < WindowsAvStagingInventory.EntryLimit + 5; i++) Put(dir, $"outside-{i:D4}.txt", 0);
        var sample = Sample(root);
        Assert.Equal(WindowsAvStagingInventory.EntryLimit, sample.VisitedEntries);
        Assert.Equal(candidate ? 1 : 0, sample.ObservedDirectories);
        Assert.Equal(0, sample.VerifiedFiles);
        Assert.Equal(0, sample.SkippedEntries);
        Assert.Equal(new[] { "entry_limit" }, sample.ReasonCodes);
    }

    [Fact]
    public void CooperativeDeadlineReturnsExplicitPartialTotals()
    {
        using var fixture = new TempDirectory();
        var root = Path.Combine(fixture.Path, "temp");
        var path = Put(Candidate(root), "rec_video.mp4", 81);
        var result = WindowsAvStagingInventory.Observe(root, current =>
        {
            if (current == path) Thread.Sleep(WindowsAvStagingInventory.ScanMilliseconds + 20);
        });
        var sample = Assert.IsType<AvStagingInventorySample>(result.Sample);
        Assert.Equal(81, sample.LogicalBytes);
        Assert.Equal(new[] { "scan_deadline" }, sample.ReasonCodes);
    }

    private static AvStagingInventorySample Sample(string root) => Assert.IsType<AvStagingInventorySample>(WindowsAvStagingInventory.Observe(root).Sample);

    private static string Put(string directory, string name, int length)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        using var file = File.Create(path);
        file.SetLength(length);
        return path;
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
