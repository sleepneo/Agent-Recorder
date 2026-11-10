using System.Diagnostics;
using NAudio.Wave;
using Xunit;

namespace AgentRecorder.AudioHelper.Tests;

public partial class AudioHelperCaptureSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Task415ROwnedWavProgramWritesPcmAndStopsOnOriginalObject(bool loopback)
    {
        using var files = new OwnedWavFixture();
        var input = new FakeAudioInput { SourceKind = loopback ? AudioSourceKind.SystemLoopback : AudioSourceKind.Microphone };
        using var stdout = new StringWriter();
        using var events = TextWriter.Synchronized(stdout);
        var run = Task.Run(() => Program.RunAsync(files.Args(loopback), (_, _) => (input, null, null), new(events, TextWriter.Null)));
        var pcm = Enumerable.Repeat((byte)0x53, 3200).ToArray();
        try
        {
            Assert.True(SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(4)));
            if (loopback)
            {
                Assert.True(SpinWait.SpinUntil(() => stdout.ToString().Contains("RESULT: STARTED"), TimeSpan.FromSeconds(4)));
                input.InjectPositionedPacket(pcm, 0, Stopwatch.GetTimestamp());
            }
            else
                input.InjectData(pcm, pcm.Length);
        }
        finally { File.WriteAllText(files.Signal, "stop"); }

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(7)));
        Assert.Equal(files.Identity, OwnedWavFixture.IdentityAt(files.Output));
        Assert.Contains("RESULT: STOPPED", stdout.ToString());
        Assert.Equal(2, stdout.ToString().Split($"OwnedWavIdentity: {files.Identity}").Length - 1);
        using var reader = new WaveFileReader(files.Output);
        var written = new byte[checked((int)reader.Length)];
        Assert.Equal(written.Length, reader.Read(written, 0, written.Length));
        if (loopback)
            Assert.True(written.AsSpan().IndexOf(pcm) >= 0, "The submitted PCM must survive existing loopback padding.");
        else
            Assert.Equal(pcm, written);
        Assert.Empty(Directory.GetFiles(files.Root, "*.partial.wav"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Task415ROwnedWavProgramRetryRevalidatesBeforeTruncate(bool replace)
    {
        using var files = new OwnedWavFixture();
        var first = new FakeAudioInput
        {
            StartRecordingException = new AudioCaptureStartException("synthetic retry", new IOException(), unchecked((int)0x80070057))
        };
        var second = new FakeAudioInput();
        using var stdout = new StringWriter();
        int attempts = 0;
        var foreign = new byte[] { 7, 5, 4, 1 };
        var run = Task.Run(() => Program.RunAsync(files.Args(), (_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) return (first, null, null);
            if (replace)
            {
                File.Move(files.Output, files.Displaced);
                File.WriteAllBytes(files.Output, foreign);
            }
            return (second, null, null);
        }, new(stdout, TextWriter.Null)));

        if (replace)
        {
            Assert.NotEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(7)));
            Assert.Contains("ErrorCode: audio_output_conflict", stdout.ToString());
            Assert.DoesNotContain("OwnedWavIdentity:", stdout.ToString());
            Assert.False(second.Started);
            Assert.Equal(foreign, File.ReadAllBytes(files.Output));
            Assert.Equal(files.Identity, OwnedWavFixture.IdentityAt(files.Displaced));
        }
        else
        {
            var pcm = Enumerable.Repeat((byte)0x35, 3200).ToArray();
            try
            {
                Assert.True(SpinWait.SpinUntil(() => second.Started, TimeSpan.FromSeconds(4)));
                second.InjectData(pcm, pcm.Length);
            }
            finally { File.WriteAllText(files.Signal, "stop"); }
            Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(7)));
            Assert.Contains("RESULT: STOPPED", stdout.ToString());
            Assert.Equal(files.Identity, OwnedWavFixture.IdentityAt(files.Output));
            Assert.Equal(pcm, ReadOwnedPcm(files.Output, pcm.Length));
        }
        Assert.Equal(2, attempts);
        Assert.True(first.Disposed);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("occupied")]
    [InlineData("outside_scope")]
    public async Task Task415ROwnedWavProgramRefusesBeforeWriting(string fault)
    {
        using var files = new OwnedWavFixture();
        var input = new FakeAudioInput();
        using var stdout = new StringWriter();
        var bytes = fault == "occupied" ? new byte[] { 5, 1, 5 } : Array.Empty<byte>();
        File.WriteAllBytes(files.Output, bytes);
        var args = files.Args(identity: fault == "identity" ? new string('f', 48) : null,
            allowedRoot: fault == "outside_scope" ? Path.GetDirectoryName(files.Root) : null);
        var result = await Program.RunAsync(args, (_, _) => (input, null, null), new(stdout, TextWriter.Null));

        Assert.NotEqual(0, result);
        Assert.False(input.Started);
        Assert.Equal(bytes, File.ReadAllBytes(files.Output));
        Assert.Equal(files.Identity, OwnedWavFixture.IdentityAt(files.Output));
    }

    [Fact]
    public async Task Task415ROwnedWavWriterFailureRetainsOriginalForHost()
    {
        using var files = new OwnedWavFixture();
        var input = new FakeAudioInput();
        using var stdout = new StringWriter();
        var run = Task.Run(() => Program.RunAsync(files.Args(), (_, _) => (input, null, null), new(stdout, TextWriter.Null)));
        try
        {
            Assert.True(SpinWait.SpinUntil(() => input.Started, TimeSpan.FromSeconds(4)));
            // A malformed callback forces the real WAV writer's Write to throw.
            input.InjectData(new byte[1], 3200);
        }
        finally { File.WriteAllText(files.Signal, "stop"); }
        Assert.NotEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(7)));
        Assert.Contains("ErrorCode: audio_write_failure", stdout.ToString());
        Assert.Contains($"OwnedWavIdentity: {files.Identity}", stdout.ToString());
        Assert.Equal(files.Identity, OwnedWavFixture.IdentityAt(files.Output));
        Assert.Empty(Directory.GetFiles(files.Root, "*.partial.wav"));
    }

    private static byte[] ReadOwnedPcm(string path, int bytes)
    {
        using var reader = new WaveFileReader(path);
        var pcm = new byte[bytes];
        Assert.Equal(bytes, reader.Read(pcm, 0, pcm.Length));
        return pcm;
    }

    private sealed class OwnedWavFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), ".agent-recorder-av-" + Guid.NewGuid().ToString("N"));
        internal string Output => Path.Combine(Root, "run_audio.wav");
        internal string Displaced => Path.Combine(Root, "displaced.wav");
        internal string Signal => Path.Combine(Root, "stop.signal");
        internal string Identity { get; }

        internal OwnedWavFixture()
        {
            Directory.CreateDirectory(Root);
            using var created = new FileStream(Output, FileMode.CreateNew, FileAccess.ReadWrite);
            Identity = Assert.IsType<string>(OwnedWavTarget.ReadIdentity(created.SafeFileHandle));
        }

        internal string[] Args(bool loopback = false, string? identity = null, string? allowedRoot = null) =>
            new[] { "--endpoint-id", "synthetic", "--allowed-root", allowedRoot ?? Root, "--output", Output,
                "--recording-id", "synthetic", "--stop-signal", Signal, "--source-kind",
                loopback ? "system-loopback" : "microphone", "--owned-wav", identity ?? Identity };

        internal static string? IdentityAt(string path)
        {
            using var file = File.OpenRead(path);
            return OwnedWavTarget.ReadIdentity(file.SafeFileHandle);
        }

        public void Dispose()
        {
            File.Delete(Signal);
            File.Delete(Output);
            File.Delete(Displaced);
            Directory.Delete(Root);
        }
    }
}
