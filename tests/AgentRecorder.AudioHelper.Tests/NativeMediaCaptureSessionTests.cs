using System.IO;
using System.Text;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.AudioHelper.Tests;

public sealed class NativeMediaCaptureSessionTests
{
    [Fact]
    public async Task RunAsync_WhenStopped_HoldsExactEndpointAndFinalizesOnce()
    {
        using var fixture = new NativeSessionFixture();
        var fake = new ScriptedNativeRecorder();
        int factoryCount = 0;
        using var session = fixture.CreateSession(() =>
        {
            factoryCount++;
            return fake;
        });

        var runTask = session.RunAsync();
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.Cancellation.Cancel();
        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, exitCode);
        Assert.Equal(1, factoryCount);
        Assert.Equal(1, fake.InitializeCount);
        Assert.Equal(1, fake.StartCount);
        Assert.Equal(1, fake.StopCount);
        Assert.Equal(1, fake.FinalizeCount);
        Assert.Equal(1, fake.DisposeCount);
        Assert.Equal(fixture.Options.EndpointId, fake.Request?.EndpointId);
        Assert.True(File.Exists(fixture.OutputPath));
        Assert.False(File.Exists(fixture.OutputCheck.PartialPath));

        var summary = ParseSummary(fixture.Stdout.ToString());
        Assert.Equal(AudioHelperSessionState.Stopped, summary.State);
        Assert.Equal("windows-mediacapture", summary.CaptureEngine);
        Assert.Equal("WINDOWS_MEDIACAPTURE", summary.CaptureMethod);
        Assert.Equal(16000, summary.SampleRate);
        Assert.Equal(1, summary.Channels);
        Assert.Equal(16, summary.BitsPerSample);
        Assert.True(summary.BytesWritten > 44);
        Assert.True(summary.DurationMs is >= 90 and <= 150);

        Assert.Contains("RESULT: STARTED", fixture.Stdout.ToString());
        Assert.Contains("RESULT: STOPPED", fixture.Stdout.ToString());
    }

    [Theory]
    [InlineData("initialize", "audio_native_initialize_failed")]
    [InlineData("start", "audio_native_start_failed")]
    [InlineData("recording", "audio_native_recording_failed")]
    [InlineData("stop", "audio_native_stop_failed")]
    [InlineData("finalize", "audio_native_finalize_failed")]
    public async Task RunAsync_WhenNativeStageFails_EmitsSingleFailWithEngineAndHresult(string stage, string expectedCode)
    {
        using var fixture = new NativeSessionFixture();
        var fake = new ScriptedNativeRecorder { FailureStage = stage };
        using var session = fixture.CreateSession(() => fake);

        var runTask = session.RunAsync();
        if (stage is "stop" or "finalize")
        {
            await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Cancellation.Cancel();
        }

        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        var stdout = fixture.Stdout.ToString();
        var summary = ParseSummary(stdout);

        Assert.Equal(6, exitCode);
        Assert.Equal(AudioHelperSessionState.Failed, summary.State);
        Assert.Equal(expectedCode, summary.ErrorCode);
        Assert.Equal("windows-mediacapture", summary.CaptureEngine);
        Assert.Equal("WINDOWS_MEDIACAPTURE", summary.CaptureMethod);
        Assert.Contains("HRESULT: 0x80004005", stdout);
        Assert.Contains($"FailureStage: {stage}", stdout);
        Assert.Contains("EndpointId: " + fixture.Options.EndpointId, stdout);
        Assert.Contains("PartialOutputPath: " + fixture.OutputCheck.PartialPath, stdout);
        Assert.Equal(stage, summary.FailureStage);
        Assert.Equal(fixture.Options.EndpointId, summary.EndpointId);
        Assert.Equal(fixture.OutputCheck.PartialPath, summary.PartialOutputPath);
        Assert.Equal(1, CountTerminalBlocks(stdout));
        Assert.Equal(1, fake.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_WhenStopHangs_EmitsBoundedStopFailureAndExits()
    {
        using var fixture = new NativeSessionFixture();
        var fake = new ScriptedNativeRecorder { HangStop = true };
        using var session = fixture.CreateSession(() => fake, TimeSpan.FromMilliseconds(50));

        var runTask = session.RunAsync();
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Cancellation.Cancel();

        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        var stdout = fixture.Stdout.ToString();

        Assert.Equal(6, exitCode);
        Assert.Contains("ErrorCode: audio_native_stop_failed", stdout);
        Assert.Contains("HRESULT: 0x800705B4", stdout);
        Assert.Equal(1, fake.StopCount);
        Assert.Equal(0, fake.FinalizeCount);
        Assert.Equal(1, CountTerminalBlocks(stdout));
    }

    [Fact]
    public async Task RunAsync_WhenFinalizeHangs_EmitsBoundedFinalizeFailureAndExits()
    {
        using var fixture = new NativeSessionFixture();
        var fake = new ScriptedNativeRecorder { HangFinalize = true };
        using var session = fixture.CreateSession(() => fake, TimeSpan.FromMilliseconds(50));

        var runTask = session.RunAsync();
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Cancellation.Cancel();

        var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        var stdout = fixture.Stdout.ToString();

        Assert.Equal(6, exitCode);
        Assert.Contains("ErrorCode: audio_native_finalize_failed", stdout);
        Assert.Contains("HRESULT: 0x800705B4", stdout);
        Assert.Equal(1, fake.StopCount);
        Assert.Equal(1, fake.FinalizeCount);
        Assert.Equal(1, CountTerminalBlocks(stdout));
    }

    [Fact]
    public async Task RunAsync_PrimaryRecordingFailurePreservesSecondaryCleanupFailures()
    {
        using var fixture = new NativeSessionFixture();
        var fake = new ScriptedNativeRecorder
        {
            FailureStage = "recording",
            SecondaryStopFails = true,
            DisposeFails = true
        };
        using var session = fixture.CreateSession(() => fake, TimeSpan.FromMilliseconds(50));

        var exitCode = await session.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var stdout = fixture.Stdout.ToString();
        var summary = ParseSummary(stdout);

        Assert.Equal(6, exitCode);
        Assert.Equal("audio_native_recording_failed", summary.ErrorCode);
        Assert.Equal("recording", summary.FailureStage);
        Assert.Contains("SecondaryFailure: stop:NativeAudioRecorderException:0x80004005", stdout);
        Assert.Contains("dispose:InvalidOperationException", stdout);
        Assert.Contains("secondaryFailure=", summary.Reason);
        Assert.Equal(1, fake.StopCount);
        Assert.Equal(0, fake.FinalizeCount);
        Assert.Equal(1, fake.DisposeCount);
        Assert.Equal(1, CountTerminalBlocks(stdout));
    }

    [Fact]
    public async Task DisposeDuringRecording_CancelsWithoutDuplicateTerminalOrRecorderDispose()
    {
        for (int i = 0; i < 50; i++)
        {
            using var fixture = new NativeSessionFixture();
            var fake = new ScriptedNativeRecorder { DelayMs = 2 };
            using var session = fixture.CreateSession(() => fake);

            var runTask = session.RunAsync();
            await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            session.Dispose();

            await runTask.WaitAsync(TimeSpan.FromSeconds(5));

            var stdout = fixture.Stdout.ToString();
            Assert.Equal(1, CountTerminalBlocks(stdout));
            Assert.DoesNotContain("RESULT: PROGRESS", TextAfterTerminal(stdout));
            Assert.Equal(1, fake.DisposeCount);
            Assert.True(fake.StopCount <= 1);
            Assert.True(fake.FinalizeCount <= 1);
        }
    }

    [Fact]
    public async Task RunAsync_FinalOutputIsPcmWavWithReportedDuration()
    {
        using var fixture = new NativeSessionFixture();
        var fake = new ScriptedNativeRecorder();
        using var session = fixture.CreateSession(() => fake);

        var runTask = session.RunAsync();
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Cancellation.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        using var reader = new BinaryReader(File.OpenRead(fixture.OutputPath));
        Assert.Equal("RIFF", ReadAscii(reader, 4));
        reader.ReadInt32();
        Assert.Equal("WAVE", ReadAscii(reader, 4));
        Assert.Contains("DurationMs: 100", fixture.Stdout.ToString());
    }

    private static AudioHelperSessionSummary ParseSummary(string stdout)
        => AudioHelperEventStreamParser.ValidateAndSummarize(AudioHelperEventStreamParser.ParseEvents(stdout));

    private static int CountTerminalBlocks(string stdout)
    {
        return Count(stdout, "RESULT: OK") + Count(stdout, "RESULT: STOPPED") + Count(stdout, "RESULT: FAIL");
    }

    private static int Count(string value, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string TextAfterTerminal(string stdout)
    {
        var ok = stdout.IndexOf("RESULT: OK", StringComparison.Ordinal);
        var stopped = stdout.IndexOf("RESULT: STOPPED", StringComparison.Ordinal);
        var fail = stdout.IndexOf("RESULT: FAIL", StringComparison.Ordinal);
        var index = new[] { ok, stopped, fail }.Where(v => v >= 0).DefaultIfEmpty(stdout.Length).Min();
        return index >= stdout.Length ? "" : stdout[index..];
    }

    private static string ReadAscii(BinaryReader reader, int count)
        => Encoding.ASCII.GetString(reader.ReadBytes(count));

    private sealed class NativeSessionFixture : IDisposable
    {
        public NativeSessionFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "agent-recorder-native-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            OutputPath = Path.Combine(Root, "recording.wav");
            StopPath = Path.Combine(Root, "stop.signal");
            Options = new AudioHelperOptions
            {
                Mode = AudioHelperMode.Capture,
                CaptureEngine = AudioCaptureEngine.WindowsMediaCapture,
                EndpointId = "{0.0.1.00000000}.{native-test-endpoint}",
                OutputPath = OutputPath,
                AllowedRoot = Root,
                StopSignalPath = StopPath,
                RecordingId = "rec_native_test"
            };

            OutputCheck = new PathPolicy(Root).ValidateOutputPath(OutputPath);
            Assert.True(OutputCheck.Ok, OutputCheck.Error);
            EventWriter = new EventWriter(Stdout, null);
            Watcher = new StopWatcher(StopPath, () => Cancellation.Cancel());
        }

        public string Root { get; }
        public string OutputPath { get; }
        public string StopPath { get; }
        public AudioHelperOptions Options { get; }
        public PathCheckResult OutputCheck { get; }
        public StringWriter Stdout { get; } = new();
        public EventWriter EventWriter { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public StopWatcher Watcher { get; }

        public NativeMediaCaptureSession CreateSession(NativeAudioRecorderFactory factory, TimeSpan? cleanupTimeout = null)
        {
            return new NativeMediaCaptureSession(Options, OutputCheck, EventWriter, Watcher, Cancellation, factory, cleanupTimeout);
        }

        public void Dispose()
        {
            try { Watcher.Dispose(); } catch { }
            try { Cancellation.Dispose(); } catch { }
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    private sealed class ScriptedNativeRecorder : INativeAudioRecorder
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? FailureStage { get; set; }
        public int DelayMs { get; set; }
        public bool HangStop { get; set; }
        public bool HangFinalize { get; set; }
        public bool SecondaryStopFails { get; set; }
        public bool SecondaryFinalizeFails { get; set; }
        public bool DisposeFails { get; set; }
        public NativeAudioRecorderRequest? Request { get; private set; }
        public int InitializeCount { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int FinalizeCount { get; private set; }
        public int DisposeCount { get; private set; }

        public async Task InitializeAsync(NativeAudioRecorderRequest request, CancellationToken cancellationToken)
        {
            InitializeCount++;
            Request = request;
            await Delay(cancellationToken);
            ThrowIfStage("initialize");
        }

        public async Task<NativeAudioRecorderFormat> StartAsync(string partialPath, CancellationToken cancellationToken)
        {
            StartCount++;
            await Delay(cancellationToken);
            ThrowIfStage("start");
            Started.TrySetResult();
            return new NativeAudioRecorderFormat(16000, 1, 16);
        }

        public async Task WaitForRecordingFailureAsync(CancellationToken cancellationToken)
        {
            if (FailureStage == "recording")
                ThrowIfStage("recording");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            if (HangStop)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            await Delay(cancellationToken);
            if (SecondaryStopFails)
                throw new NativeAudioRecorderException("audio_native_stop_failed", "Injected secondary stop failure.", unchecked((int)0x80004005));
            ThrowIfStage("stop");
        }

        public async Task<NativeAudioRecorderFinalized> FinalizeAsync(string partialPath, CancellationToken cancellationToken)
        {
            FinalizeCount++;
            if (HangFinalize)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            await Delay(cancellationToken);
            if (SecondaryFinalizeFails)
                throw new NativeAudioRecorderException("audio_native_finalize_failed", "Injected secondary finalize failure.", unchecked((int)0x80004005));
            ThrowIfStage("finalize");
            WritePcmWav(partialPath, sampleRate: 16000, channels: 1, bitsPerSample: 16, durationMs: 100);
            return new NativeAudioRecorderFinalized(16000, 1, 16, new FileInfo(partialPath).Length, 100);
        }

        public void Dispose()
        {
            DisposeCount++;
            if (DisposeFails)
                throw new InvalidOperationException("Injected dispose failure.");
        }

        private async Task Delay(CancellationToken cancellationToken)
        {
            if (DelayMs > 0)
                await Task.Delay(DelayMs, cancellationToken);
        }

        private void ThrowIfStage(string stage)
        {
            if (FailureStage == stage)
                throw new NativeAudioRecorderException($"audio_native_{stage}_failed", $"Injected {stage} failure.", unchecked((int)0x80004005));
        }
    }

    [Fact]
    public async Task ProductionBridge_MediaCaptureFailed_CompletesFailureWaitImmediately()
    {
        var source = new FakeMediaCaptureNativeSource();
        using var recorder = new MediaCaptureNativeAudioRecorder(() => source, new ConstantMediaCaptureDeviceMapper("device-info-id"));

        await recorder.InitializeAsync(new NativeAudioRecorderRequest("endpoint-1", "rec", "partial.wav"), CancellationToken.None);
        await recorder.StartAsync("partial.wav", CancellationToken.None);
        var waitTask = recorder.WaitForRecordingFailureAsync(CancellationToken.None);

        source.RaiseFailed("media capture failed", unchecked((int)0x88990001));

        var ex = await Assert.ThrowsAsync<NativeAudioRecorderException>(() => waitTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("audio_native_recording_failed", ex.ErrorCode);
        Assert.Equal(unchecked((int)0x88990001), ex.HResultValue);
        Assert.Equal("MediaCapture.Failed", ex.SourceEvent);
        Assert.Contains("media capture failed", ex.Message);
    }

    [Fact]
    public async Task ProductionBridge_RecordLimitationExceeded_MapsToRecordingFailureWithoutInventedHresult()
    {
        var source = new FakeMediaCaptureNativeSource();
        using var recorder = new MediaCaptureNativeAudioRecorder(() => source, new ConstantMediaCaptureDeviceMapper("device-info-id"));

        await recorder.InitializeAsync(new NativeAudioRecorderRequest("endpoint-1", "rec", "partial.wav"), CancellationToken.None);
        await recorder.StartAsync("partial.wav", CancellationToken.None);
        var waitTask = recorder.WaitForRecordingFailureAsync(CancellationToken.None);

        source.RaiseRecordLimitationExceeded();

        var ex = await Assert.ThrowsAsync<NativeAudioRecorderException>(() => waitTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("audio_native_recording_failed", ex.ErrorCode);
        Assert.Null(ex.HResultValue);
        Assert.Equal("RecordLimitationExceeded", ex.SourceEvent);
    }

    [Fact]
    public async Task ProductionBridge_NormalCancellation_DoesNotProduceNativeFailure()
    {
        var source = new FakeMediaCaptureNativeSource();
        using var recorder = new MediaCaptureNativeAudioRecorder(() => source, new ConstantMediaCaptureDeviceMapper("device-info-id"));
        using var cts = new CancellationTokenSource();

        await recorder.InitializeAsync(new NativeAudioRecorderRequest("endpoint-1", "rec", "partial.wav"), CancellationToken.None);
        await recorder.StartAsync("partial.wav", CancellationToken.None);
        var waitTask = recorder.WaitForRecordingFailureAsync(cts.Token);

        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() => waitTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ProductionBridge_LateCallbackAfterDispose_IsIgnored()
    {
        var source = new FakeMediaCaptureNativeSource();
        var recorder = new MediaCaptureNativeAudioRecorder(() => source, new ConstantMediaCaptureDeviceMapper("device-info-id"));
        using var cts = new CancellationTokenSource();

        await recorder.InitializeAsync(new NativeAudioRecorderRequest("endpoint-1", "rec", "partial.wav"), CancellationToken.None);
        await recorder.StartAsync("partial.wav", CancellationToken.None);
        var waitTask = recorder.WaitForRecordingFailureAsync(cts.Token);
        recorder.Dispose();

        source.RaiseFailed("late failure", unchecked((int)0x80004005));
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() => waitTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ProductionBridge_NormalStopSuppressesLateFailureCallback()
    {
        var source = new FakeMediaCaptureNativeSource();
        using var recorder = new MediaCaptureNativeAudioRecorder(() => source, new ConstantMediaCaptureDeviceMapper("device-info-id"));
        using var cts = new CancellationTokenSource();

        await recorder.InitializeAsync(new NativeAudioRecorderRequest("endpoint-1", "rec", "partial.wav"), CancellationToken.None);
        await recorder.StartAsync("partial.wav", CancellationToken.None);
        var waitTask = recorder.WaitForRecordingFailureAsync(cts.Token);
        await recorder.StopAsync(CancellationToken.None);

        source.RaiseFailed("normal stop late failure", unchecked((int)0x80004005));
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() => waitTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Mapper_ExactApprovedEndpoint_ReturnsFullDeviceInformationId()
    {
        const string endpoint = "{0.0.1.00000000}.{10dfdf9e-e7c5-4d3a-b2a5-3cb4169578ac}";
        const string deviceId = @"\\?\SWD#MMDEVAPI#{0.0.1.00000000}.{10dfdf9e-e7c5-4d3a-b2a5-3cb4169578ac}#{2eef81be-33fa-4800-9670-1cd474972c3f}";
        var mapper = CreateMapper(new MediaCaptureDeviceInfo(deviceId, true, "Headset (AirPods Pro)"));

        var mapped = await mapper.MapCoreAudioEndpointToMediaCaptureDeviceIdAsync(endpoint, CancellationToken.None);

        Assert.Equal(deviceId, mapped);
    }

    [Fact]
    public async Task Mapper_CaseInsensitiveEndpoint_ReturnsUniqueMatch()
    {
        const string endpoint = "{0.0.1.00000000}.{10DFDF9E-E7C5-4D3A-B2A5-3CB4169578AC}";
        const string deviceId = @"\\?\SWD#MMDEVAPI#{0.0.1.00000000}.{10dfdf9e-e7c5-4d3a-b2a5-3cb4169578ac}#{2eef81be-33fa-4800-9670-1cd474972c3f}";
        var mapper = CreateMapper(new MediaCaptureDeviceInfo(deviceId, true, "Headset"));

        var mapped = await mapper.MapCoreAudioEndpointToMediaCaptureDeviceIdAsync(endpoint, CancellationToken.None);

        Assert.Equal(deviceId, mapped);
    }

    [Fact]
    public async Task Mapper_SameFriendlyNameDifferentEndpoint_DoesNotMatchWrongDevice()
    {
        const string approvedEndpoint = "{0.0.1.00000000}.{10dfdf9e-e7c5-4d3a-b2a5-3cb4169578ac}";
        const string wrongDeviceId = @"\\?\SWD#MMDEVAPI#{0.0.1.00000000}.{aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa}#{2eef81be-33fa-4800-9670-1cd474972c3f}";
        var mapper = CreateMapper(new MediaCaptureDeviceInfo(wrongDeviceId, true, "Headset (AirPods Pro)"));

        var ex = await Assert.ThrowsAsync<NativeAudioRecorderException>(
            () => mapper.MapCoreAudioEndpointToMediaCaptureDeviceIdAsync(approvedEndpoint, CancellationToken.None));

        Assert.Equal("audio_native_device_mapping_not_found", ex.ErrorCode);
    }

    [Fact]
    public async Task Mapper_SameTailGuidDifferentDataFlow_DoesNotMatch()
    {
        const string approvedEndpoint = "{0.0.1.00000000}.{10dfdf9e-e7c5-4d3a-b2a5-3cb4169578ac}";
        const string renderEndpointDeviceId = @"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{10dfdf9e-e7c5-4d3a-b2a5-3cb4169578ac}#{2eef81be-33fa-4800-9670-1cd474972c3f}";
        var mapper = CreateMapper(new MediaCaptureDeviceInfo(renderEndpointDeviceId, true, "Headset"));

        var ex = await Assert.ThrowsAsync<NativeAudioRecorderException>(
            () => mapper.MapCoreAudioEndpointToMediaCaptureDeviceIdAsync(approvedEndpoint, CancellationToken.None));

        Assert.Equal("audio_native_device_mapping_not_found", ex.ErrorCode);
    }

    [Fact]
    public async Task Mapper_NoMatches_FailsClosed()
    {
        var mapper = CreateMapper();

        var ex = await Assert.ThrowsAsync<NativeAudioRecorderException>(
            () => mapper.MapCoreAudioEndpointToMediaCaptureDeviceIdAsync("{0.0.1.00000000}.{endpoint}", CancellationToken.None));

        Assert.Equal("audio_native_device_mapping_not_found", ex.ErrorCode);
    }

    [Fact]
    public async Task Mapper_MultipleMatches_FailsClosedAsAmbiguous()
    {
        const string endpoint = "{0.0.1.00000000}.{endpoint}";
        var mapper = CreateMapper(
            new MediaCaptureDeviceInfo(@"\\?\SWD#MMDEVAPI#{0.0.1.00000000}.{endpoint}#a", true, "Mic 1"),
            new MediaCaptureDeviceInfo(@"\\?\SWD#MMDEVAPI#{0.0.1.00000000}.{endpoint}#b", true, "Mic 2"));

        var ex = await Assert.ThrowsAsync<NativeAudioRecorderException>(
            () => mapper.MapCoreAudioEndpointToMediaCaptureDeviceIdAsync(endpoint, CancellationToken.None));

        Assert.Equal("audio_native_device_mapping_ambiguous", ex.ErrorCode);
    }

    [Fact]
    public async Task Mapper_UniqueDisabledMatch_FailsClosedAsDisabled()
    {
        const string endpoint = "{0.0.1.00000000}.{endpoint}";
        var mapper = CreateMapper(new MediaCaptureDeviceInfo(@"\\?\SWD#MMDEVAPI#{0.0.1.00000000}.{endpoint}#a", false, "Mic"));

        var ex = await Assert.ThrowsAsync<NativeAudioRecorderException>(
            () => mapper.MapCoreAudioEndpointToMediaCaptureDeviceIdAsync(endpoint, CancellationToken.None));

        Assert.Equal("audio_native_device_mapping_disabled", ex.ErrorCode);
    }

    [Fact]
    public async Task Mapper_EnumerationException_PreservesHresult()
    {
        var failure = new InvalidOperationException("enumeration failed");
        failure.HResult = unchecked((int)0x80070490);
        var mapper = new WinRtMediaCaptureDeviceMapper(_ => { }, new FakeMediaCaptureDeviceEnumerator(failure));

        var ex = await Assert.ThrowsAsync<NativeAudioRecorderException>(
            () => mapper.MapCoreAudioEndpointToMediaCaptureDeviceIdAsync("{0.0.1.00000000}.{endpoint}", CancellationToken.None));

        Assert.Equal("audio_native_device_enumeration_failed", ex.ErrorCode);
        Assert.Equal(unchecked((int)0x80070490), ex.HResultValue);
        Assert.Equal("DeviceInformation.FindAllAsync", ex.SourceEvent);
    }

    [Fact]
    public async Task Recorder_Initialize_MappingFailureDoesNotCreateOrInitializeMediaCaptureSource()
    {
        int factoryCount = 0;
        using var recorder = new MediaCaptureNativeAudioRecorder(
            () =>
            {
                factoryCount++;
                return new FakeMediaCaptureNativeSource();
            },
            new ThrowingMediaCaptureDeviceMapper(new NativeAudioRecorderException("audio_native_device_mapping_not_found", "No match.")));

        var ex = await Assert.ThrowsAsync<NativeAudioRecorderException>(
            () => recorder.InitializeAsync(new NativeAudioRecorderRequest("{0.0.1.00000000}.{endpoint}", "rec", "partial.wav"), CancellationToken.None));

        Assert.Equal("audio_native_device_mapping_not_found", ex.ErrorCode);
        Assert.Equal(0, factoryCount);
    }

    [Fact]
    public async Task Recorder_Initialize_MappingSuccessPassesFullDeviceInformationIdToMediaCapture()
    {
        const string endpoint = "{0.0.1.00000000}.{endpoint}";
        const string deviceInformationId = @"\\?\SWD#MMDEVAPI#{0.0.1.00000000}.{endpoint}#{2eef81be-33fa-4800-9670-1cd474972c3f}";
        var source = new FakeMediaCaptureNativeSource();
        using var recorder = new MediaCaptureNativeAudioRecorder(
            () => source,
            CreateMapper(new MediaCaptureDeviceInfo(deviceInformationId, true, "Mic")));

        await recorder.InitializeAsync(new NativeAudioRecorderRequest(endpoint, "rec", "partial.wav"), CancellationToken.None);

        Assert.Equal(1, source.InitializeCount);
        Assert.Equal(deviceInformationId, source.EndpointId);
    }

    [Fact]
    public async Task Session_WhenMappingFails_FailPreservesApprovedCoreAudioEndpoint()
    {
        using var fixture = new NativeSessionFixture();
        var recorder = new MediaCaptureNativeAudioRecorder(
            () => new FakeMediaCaptureNativeSource(),
            new ThrowingMediaCaptureDeviceMapper(new NativeAudioRecorderException("audio_native_device_mapping_not_found", "No match.")));
        using var session = fixture.CreateSession(() => recorder);

        var exitCode = await session.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var summary = ParseSummary(fixture.Stdout.ToString());

        Assert.Equal(6, exitCode);
        Assert.Equal("audio_native_device_mapping_not_found", summary.ErrorCode);
        Assert.Equal("initialize", summary.FailureStage);
        Assert.Equal(fixture.Options.EndpointId, summary.EndpointId);
        Assert.Equal(fixture.OutputCheck.PartialPath, summary.PartialOutputPath);
        Assert.Equal(1, CountTerminalBlocks(fixture.Stdout.ToString()));
    }

    [Fact]
    public async Task Recorder_InitializeFailure_UnsubscribesEventsAndDisposesSourceOnce()
    {
        var source = new FakeMediaCaptureNativeSource
        {
            InitializeException = new InvalidOperationException("initialize failed")
        };
        using var recorder = new MediaCaptureNativeAudioRecorder(
            () => source,
            new ConstantMediaCaptureDeviceMapper("device-info-id"));

        var ex = await Assert.ThrowsAsync<NativeAudioRecorderException>(
            () => recorder.InitializeAsync(new NativeAudioRecorderRequest("{0.0.1.00000000}.{endpoint}", "rec", "partial.wav"), CancellationToken.None));

        Assert.Equal("audio_native_initialize_failed", ex.ErrorCode);
        Assert.Equal(1, source.FailedSubscribeCount);
        Assert.Equal(1, source.FailedUnsubscribeCount);
        Assert.Equal(1, source.RecordLimitationSubscribeCount);
        Assert.Equal(1, source.RecordLimitationUnsubscribeCount);
        Assert.Equal(1, source.DisposeCount);

        recorder.Dispose();
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task Recorder_Initialize_PreservesExistingNativeAudioRecorderException()
    {
        var source = new FakeMediaCaptureNativeSource
        {
            InitializeException = new NativeAudioRecorderException(
                "audio_native_device_mapping_disabled",
                "Injected native failure.",
                unchecked((int)0x80070005),
                sourceEvent: "DeviceInformation.FindAllAsync")
        };
        using var recorder = new MediaCaptureNativeAudioRecorder(
            () => source,
            new ConstantMediaCaptureDeviceMapper("device-info-id"));

        var ex = await Assert.ThrowsAsync<NativeAudioRecorderException>(
            () => recorder.InitializeAsync(new NativeAudioRecorderRequest("{0.0.1.00000000}.{endpoint}", "rec", "partial.wav"), CancellationToken.None));

        Assert.Equal("audio_native_device_mapping_disabled", ex.ErrorCode);
        Assert.Equal(unchecked((int)0x80070005), ex.HResultValue);
        Assert.Equal("DeviceInformation.FindAllAsync", ex.SourceEvent);
        Assert.Equal(1, source.DisposeCount);
    }

    private static WinRtMediaCaptureDeviceMapper CreateMapper(params MediaCaptureDeviceInfo[] devices)
        => new(_ => { }, new FakeMediaCaptureDeviceEnumerator(devices));

    private sealed class FakeMediaCaptureNativeSource : IMediaCaptureNativeSource
    {
        private EventHandler<NativeMediaCaptureFailureEventArgs>? _failed;
        private EventHandler? _recordLimitationExceeded;

        public event EventHandler<NativeMediaCaptureFailureEventArgs>? Failed
        {
            add
            {
                FailedSubscribeCount++;
                _failed += value;
            }
            remove
            {
                FailedUnsubscribeCount++;
                _failed -= value;
            }
        }

        public event EventHandler? RecordLimitationExceeded
        {
            add
            {
                RecordLimitationSubscribeCount++;
                _recordLimitationExceeded += value;
            }
            remove
            {
                RecordLimitationUnsubscribeCount++;
                _recordLimitationExceeded -= value;
            }
        }

        public int InitializeCount { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int FailedSubscribeCount { get; private set; }
        public int FailedUnsubscribeCount { get; private set; }
        public int RecordLimitationSubscribeCount { get; private set; }
        public int RecordLimitationUnsubscribeCount { get; private set; }
        public string? EndpointId { get; private set; }
        public Exception? InitializeException { get; set; }

        public Task InitializeAsync(string endpointId, CancellationToken cancellationToken)
        {
            InitializeCount++;
            EndpointId = endpointId;
            if (InitializeException != null)
                throw InitializeException;
            return Task.CompletedTask;
        }

        public Task StartRecordToStorageFileAsync(string partialPath, CancellationToken cancellationToken)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopRecordAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public void RaiseFailed(string message, int hresult)
        {
            _failed?.Invoke(this, new NativeMediaCaptureFailureEventArgs("MediaCapture.Failed", message, hresult));
        }

        public void RaiseRecordLimitationExceeded()
        {
            _recordLimitationExceeded?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private sealed class FakeMediaCaptureDeviceEnumerator : IMediaCaptureDeviceEnumerator
    {
        private readonly IReadOnlyList<MediaCaptureDeviceInfo> _devices;
        private readonly Exception? _exception;

        public FakeMediaCaptureDeviceEnumerator(params MediaCaptureDeviceInfo[] devices)
        {
            _devices = devices;
        }

        public FakeMediaCaptureDeviceEnumerator(Exception exception)
        {
            _devices = Array.Empty<MediaCaptureDeviceInfo>();
            _exception = exception;
        }

        public Task<IReadOnlyList<MediaCaptureDeviceInfo>> FindAudioCaptureDevicesAsync(CancellationToken cancellationToken)
        {
            if (_exception != null)
                throw _exception;

            return Task.FromResult(_devices);
        }
    }

    private sealed class ConstantMediaCaptureDeviceMapper : IMediaCaptureDeviceMapper
    {
        private readonly string _deviceInformationId;

        public ConstantMediaCaptureDeviceMapper(string deviceInformationId)
        {
            _deviceInformationId = deviceInformationId;
        }

        public Task<string> MapCoreAudioEndpointToMediaCaptureDeviceIdAsync(string coreAudioEndpointId, CancellationToken cancellationToken)
            => Task.FromResult(_deviceInformationId);
    }

    private sealed class ThrowingMediaCaptureDeviceMapper : IMediaCaptureDeviceMapper
    {
        private readonly Exception _exception;

        public ThrowingMediaCaptureDeviceMapper(Exception exception)
        {
            _exception = exception;
        }

        public Task<string> MapCoreAudioEndpointToMediaCaptureDeviceIdAsync(string coreAudioEndpointId, CancellationToken cancellationToken)
            => throw _exception;
    }

    private static void WritePcmWav(string path, int sampleRate, short channels, short bitsPerSample, int durationMs)
    {
        int bytesPerSample = bitsPerSample / 8;
        int sampleCount = sampleRate * durationMs / 1000;
        int dataLength = sampleCount * channels * bytesPerSample;
        short blockAlign = (short)(channels * bytesPerSample);
        int byteRate = sampleRate * blockAlign;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
    }
}
