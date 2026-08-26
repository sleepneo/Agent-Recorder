using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class RecordingTerminalPublicationTests
{
    [Theory]
    [InlineData(RecState.created)]
    [InlineData(RecState.pending_confirmation)]
    [InlineData(RecState.preparing)]
    [InlineData(RecState.countdown)]
    [InlineData(RecState.recording)]
    [InlineData(RecState.stopping)]
    [InlineData(RecState.finalizing)]
    [InlineData(RecState.paused)]
    public void PublishFinalized_NonTerminalState_IsRejected(RecState state)
    {
        var rec = new Recording { State = state };

        Assert.False(rec.PublishFinalized());
        Assert.False(rec.IsFinalized);
    }

    [Fact]
    public async Task PublishFinalized_TerminalSnapshot_IsVisibleToLockFreeReader()
    {
        var rec = new Recording { State = RecState.preparing };
        var terminalMeta = new OutputMeta { AudioStatus = "system_loopback_recorded" };
        var completedAt = DateTime.UtcNow;
        var readerReady = new ManualResetEventSlim();
        RecState observedState = RecState.created;
        DateTime? observedCompletedAt = null;
        string? observedStopReason = null;
        string? observedError = null;
        OutputMeta? observedMeta = null;
        Task reader = Task.Run(() =>
        {
            readerReady.Set();
            while (!rec.IsFinalized)
                Thread.SpinWait(1);

            observedState = rec.State;
            observedCompletedAt = rec.CompletedAtUtc;
            observedStopReason = rec.StopReason;
            observedError = rec.Error;
            observedMeta = rec.LastMeta;
        });

        Assert.True(readerReady.Wait(TimeSpan.FromSeconds(2)));
        lock (rec)
        {
            rec.CompletedAtUtc = completedAt;
            rec.StopReason = "audio_capture_discontinuous";
            rec.Error = "audio_capture_discontinuous";
            rec.LastMeta = terminalMeta;
            rec.BundleSnapshot = RecordingBundleSnapshot.NotApplicable();
            rec.State = RecState.failed;

            Assert.True(rec.PublishFinalized());
            Assert.False(rec.PublishFinalized());
        }

        await reader.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(RecState.failed, observedState);
        Assert.Equal(completedAt, observedCompletedAt);
        Assert.Equal("audio_capture_discontinuous", observedStopReason);
        Assert.Equal("audio_capture_discontinuous", observedError);
        Assert.Same(terminalMeta, observedMeta);
    }

    [Fact]
    public void RecordingEngine_UsesOnlyControlledTerminalPublicationEntryPoint()
    {
        var sourcePath = FindRepositoryFile("src", "AgentRecorder.Core", "RecordingEngine.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain("rec.IsFinalized = true", source, StringComparison.Ordinal);
        Assert.Equal(6, source.Split("PublishFinalized()", StringSplitOptions.None).Length - 1);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Repository source file was not found.", Path.Combine(parts));
    }
}
