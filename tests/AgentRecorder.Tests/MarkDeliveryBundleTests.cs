using System.Net;
using System.Text;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public partial class RecordingBundleTests
{
    private sealed class MarkDeliveryBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend
    {
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        internal readonly OutputMeta Meta = new() { Container = "mp4", Codec = "h264", DurationSeconds = 1, SizeBytes = 1024, Width = 100, Height = 100, Fps = 30 };
        public void Start(CaptureConfig config, CaptureAuthorizationProof proof) =>
            FirstFrameObserved?.Invoke(new FirstFrameObservation { FrameNumber = 1, TotalSizeBytes = 1 });
        public OutputMeta Stop() => Meta;
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public int ExitCode => 0;
        public void Dispose() { }
    }

    private sealed class MarkDeliveryGenerator(IRecordingBundleGenerator inner, bool fail) : IRecordingBundleGenerator
    {
        internal int Calls;
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<RecordingBundleRequest> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<RecordingBundleGenerationResult> GenerateAsync(RecordingBundleRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult(request);
            await Release.Task.WaitAsync(cancellationToken);
            return fail ? RecordingBundleGenerationResult.Failed(RecordingBundleErrorCodes.GenerationFailed) : await inner.GenerateAsync(request, cancellationToken);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AcceptedApiMarksRemainPendingUntilTerminalFiveFileDeliveryOrFailure(bool generationFails, bool raceStop)
    {
        var real = new FfmpegRecordingBundleGenerator(new FakeExternalProcessRunner().WithSuccess(), ffmpegPathProvider: FakeFfmpegPathProvider);
        var generator = new MarkDeliveryGenerator(real, generationFails);
        var audit = new CaptureAuditLogger();
        using var engine = new RecordingEngine(audit, bundleGenerator: generator)
        {
            MonotonicFrequencyForTests = 1000, MonotonicTimestampProviderForTests = () => 0
        };
        var tray = new NoOpTray();
        engine.SetTray(tray);
        var backend = new MarkDeliveryBackend();
        engine.BackendFactory = _ => (backend, "ffmpeg-region");
        var media = MediaPath("accepted-mark");
        // Synthetic media and frame outputs exercise real five-file generation/publication,
        // without claiming a playable MP4 or starting a real capture/process.
        File.WriteAllText(media, "synthetic recording bytes");
        var rec = new Recording
        {
            SourceType = "region", OutputPath = media, CountdownSeconds = 0,
            Config = new CaptureConfig { SourceKind = "region", Bounds = (0, 0, 100, 100), Fps = 30, CountdownSeconds = 0, OutputPath = media }
        };
        ApiKeyAuth.InitializeForTesting(_tmpDir);
        var server = new ApiServer(engine, audit, tray, null, null, null, null, null, null, null, null, null, 0);
        try
        {
            engine.StartCaptureForTests(rec, tray);
            Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(3)));
            engine.MonotonicTimestampProviderForTests = () => 1234;
            server.Start();
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            using var response = await client.PostAsync($"http://127.0.0.1:{server.BoundPort}/api/v1/recordings/{rec.Id}/marks",
                new StringContent("{\"label\":\"  正式接受 😀  \",\"t_ms\":999999}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var accepted = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = accepted.RootElement.GetProperty("data");
            Assert.Equal(rec.Id, data.GetProperty("recording_id").GetString());
            Assert.Equal(1234, data.GetProperty("mark").GetProperty("t_ms").GetInt64());
            Assert.Equal("正式接受 😀", data.GetProperty("mark").GetProperty("label").GetString());
            Assert.Equal("agent", data.GetProperty("mark").GetProperty("source").GetString());
            Assert.Equal("bundle/marks.json", data.GetProperty("delivery").GetProperty("artifact").GetString());
            Assert.Equal("pending", data.GetProperty("delivery").GetProperty("status").GetString());
            Assert.False(Directory.Exists(Path.ChangeExtension(media, ".bundle")));
            Assert.Equal(0, rec.BundleGenerationStarted);
            if (raceStop)
            {
                using var gate = new ManualResetEventSlim();
                var inserts = Enumerable.Range(0, 30).Select(i => Task.Run(() =>
                {
                    gate.Wait();
                    try { engine.AddMark(rec.Id, "race-" + i); }
                    catch (ApiException ex) { Assert.Equal("RECORDING_NOT_ACTIVE", ex.Code); }
                })).ToArray();
                var stop = Task.Run(() => { gate.Wait(); engine.Stop(rec.Id, "user_requested"); });
                gate.Set();
                await Task.WhenAll(inserts.Append(stop));
            }
            else engine.Stop(rec.Id, "user_requested");
            var request = await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(RecState.completed, rec.State);
            Assert.Equal("generating", rec.BundleSnapshot.Status);
            var snapshot = rec.SnapshotMarks();
            Assert.Equal(snapshot.ToArray(), request.Marks.ToArray());
            Assert.Equal(snapshot.Count, audit.Events.Count(e => e.evt == "recording.mark_added"));
            Assert.All(audit.Events.Where(e => e.evt == "recording.mark_added"), e =>
            {
                Assert.DoesNotContain("label", e.json);
                Assert.DoesNotContain(media, e.json);
            });
            Assert.False(Directory.Exists(Path.ChangeExtension(media, ".bundle")));
            generator.Release.TrySetResult();
            Assert.True(SpinWait.SpinUntil(() => rec.BundleSnapshot.Status is "ready" or "failed", TimeSpan.FromSeconds(5)));
            Assert.Equal(generationFails ? "failed" : "ready", rec.BundleSnapshot.Status);
            Assert.Equal(RecState.completed, rec.State);
            Assert.Equal("synthetic recording bytes", File.ReadAllText(media));
            if (generationFails)
            {
                Assert.False(Directory.Exists(Path.ChangeExtension(media, ".bundle")));
                Assert.Equal(RecordingBundleErrorCodes.GenerationFailed, rec.BundleSnapshot.ErrorCode);
            }
            else
            {
                var path = rec.BundleSnapshot.Path!;
                Assert.Equal(new[] { "first_frame.png", "last_frame.png", "marks.json", "metadata.json", "thumbnail.jpg" },
                    Directory.GetFiles(path).Select(Path.GetFileName).Order().ToArray());
                using var delivered = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "marks.json")));
                var marks = delivered.RootElement.GetProperty("marks").EnumerateArray().ToArray();
                Assert.Equal(snapshot.Select(m => m.Label), marks.Select(m => m.GetProperty("label").GetString()));
                Assert.All(marks, m => Assert.Equal(1234, m.GetProperty("t_ms").GetInt64()));
            }
            var late = Assert.Throws<ApiException>(() => engine.AddMark(rec.Id, "late"));
            Assert.Equal("RECORDING_NOT_ACTIVE", late.Code);
            Assert.Equal(snapshot.Count, rec.SnapshotMarks().Count);
            Assert.Equal(1, generator.Calls);
            using var status = await client.GetAsync($"http://127.0.0.1:{server.BoundPort}/api/v1/recordings/{rec.Id}");
            Assert.True(status.IsSuccessStatusCode);
            using var finalStatus = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            Assert.Equal(generationFails ? "failed" : "ready", finalStatus.RootElement.GetProperty("data").GetProperty("bundle").GetProperty("status").GetString());
        }
        finally
        {
            generator.Release.TrySetResult();
            server.Stop();
            ApiKeyAuth.ResetForTesting(null);
        }
    }
}
