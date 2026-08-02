using System.Diagnostics;
using System.Runtime.InteropServices;
using AgentRecorder.Capture;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Xunit;

namespace AgentRecorder.AudioHelper.Tests;

public sealed class HfpDuplexAudioHelperTests
{
    private static int Occurrences(string text, string value)
        => text.Split(value, StringSplitOptions.None).Length - 1;

    [Fact]
    public void Parser_StoresHfpMetadata_AndOldEventsRemainCompatible()
    {
        var stdout = $"""
RESULT: STARTED
Stage: AudioCapturing
RecordingId: rec
SampleRate: 16000
Channels: 1
BitsPerSample: 16
FirstSampleAnchorTicks: 10
TimestampFrequency: {Stopwatch.Frequency}
BytesWritten: 320
CaptureMethod: WASAPI_SHARED_CAPTURE
CaptureEngine: wasapi-direct
CaptureStrategy: hfp-duplex-prime-classic
PairEvidence: same_container_id
AutoHfpPairStatus: paired
AutoHfpPairResultCode: same_container_id
AutoHfpPairTransportClassification: hfp_candidate
RenderPrimeReadyMs: 42

RESULT: OK
Stage: Complete
DurationMs: 10
BytesWritten: 320
CaptureMethod: WASAPI_SHARED_CAPTURE
CaptureEngine: wasapi-direct
CaptureStrategy: hfp-duplex-prime-classic
PairEvidence: same_container_id
AutoHfpPairStatus: paired
AutoHfpPairResultCode: same_container_id
AutoHfpPairTransportClassification: hfp_candidate
RenderPrimeReadyMs: 42
ContinuityStatus: continuous
RecoveryCount: 0
RecoveryAttempts: 0
GapFilledBytes: 0
GapFilledMs: 0
DiscontinuityCount: 0
MaxEstimatedGapMs: 0
""";

        var summary = AudioHelperEventStreamParser.ParseAndValidate(stdout);

        Assert.Equal(AudioHelperSessionState.Success, summary.State);
        Assert.Equal("hfp-duplex-prime-classic", summary.CaptureStrategy);
        Assert.Equal("same_container_id", summary.PairEvidence);
        Assert.Equal("paired", summary.AutoHfpPairStatus);
        Assert.Equal("same_container_id", summary.AutoHfpPairResultCode);
        Assert.Equal("hfp_candidate", summary.AutoHfpPairTransportClassification);
        Assert.Equal(42, summary.RenderPrimeReadyMs);

        var old = AudioHelperEventStreamParser.ParseAndValidate(
            $"RESULT: STARTED\nStage: AudioCapturing\nRecordingId: old\nSampleRate: 16000\nChannels: 1\nBitsPerSample: 16\nFirstSampleAnchorTicks: 10\nTimestampFrequency: {Stopwatch.Frequency}\nBytesWritten: 0\n\nRESULT: STOPPED\nStopReason: user_requested\nDurationMs: 0\nBytesWritten: 0\n");
        Assert.Equal(AudioHelperSessionState.Stopped, old.State);
        Assert.Null(old.CaptureStrategy);
    }

    [Fact]
    public void Parser_RejectsDuplicateAndBoundsHostFields()
    {
        var duplicate = AudioHelperEventStreamParser.ParseAndValidate(
            $"RESULT: STARTED\nRecordingId: x\nRecordingId: y\nSampleRate: 16000\nChannels: 1\nBitsPerSample: 16\nFirstSampleAnchorTicks: 1\nTimestampFrequency: {Stopwatch.Frequency}\nBytesWritten: 0\n\nRESULT: FAIL\nErrorCode: audio_hfp_pair_invalid\n");
        Assert.Contains(duplicate.ValidationErrors, e => e.Contains("duplicate", StringComparison.OrdinalIgnoreCase));

        var longValue = new string('x', 5000);
        var events = AudioHelperEventStreamParser.ParseEvents($"RESULT: STARTED\nCaptureStrategy: {longValue}\n");
        Assert.Single(events);
        Assert.Null(events[0].CaptureStrategy);

        var negativeReady = AudioHelperEventStreamParser.ParseAndValidate(
            "RESULT: STARTED\nCaptureStrategy: hfp-duplex-prime-classic\nPairEvidence: unverified\nRenderPrimeReadyMs: -1\n");
        Assert.Contains(negativeReady.ValidationErrors, e => e.Contains("negative", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ArgumentParser_HfpIsExplicitAndCannotMixWithProbeOrMediaCapture()
    {
        var hfp = AudioHelperArgumentParser.Parse(new[] { "--hfp-render-endpoint-id", "render" });
        Assert.True(hfp.Ok);
        Assert.Equal("render", hfp.Options.HfpRenderEndpointId);

        var probe = AudioHelperArgumentParser.Parse(new[] { "--probe", "--hfp-render-endpoint-id", "render" });
        Assert.False(probe.Ok);
        Assert.Contains("cannot be mixed", probe.Error);

        var control = AudioHelperArgumentParser.Parse(new[] { "--hfp-render-endpoint-id", "render\nendpoint" });
        Assert.False(control.Ok);
        Assert.Contains("control characters", control.Error);
    }

    [Fact]
    public void ArgumentParser_HfpRejectsMissingEmptyLongAndDuplicateValues()
    {
        Assert.False(AudioHelperArgumentParser.Parse(new[] { "--hfp-render-endpoint-id" }).Ok);
        Assert.False(AudioHelperArgumentParser.Parse(new[] { "--hfp-render-endpoint-id", "" }).Ok);
        Assert.False(AudioHelperArgumentParser.Parse(new[] { "--hfp-render-endpoint-id", new string('x', 4097) }).Ok);
        Assert.False(AudioHelperArgumentParser.Parse(new[] { "--hfp-render-endpoint-id", "render", "--hfp-render-endpoint-id", "render2" }).Ok);
        Assert.False(AudioHelperArgumentParser.Parse(new[] { "--version", "--probe" }).Ok);
        Assert.False(AudioHelperArgumentParser.Parse(new[] { "--probe", "--version" }).Ok);
        Assert.False(AudioHelperArgumentParser.Parse(new[] { "--capture-engine", "windows-mediacapture", "--hfp-render-endpoint-id", "render" }).Ok);
    }

    [Fact]
    public void ArgumentParser_AutoPairIsHiddenCaptureOnlySwitchWithDuplicateProtection()
    {
        var auto = AudioHelperArgumentParser.Parse(new[] { "--auto-hfp-pair" });
        Assert.True(auto.Ok);
        Assert.True(auto.Options.AutoHfpPairDiscovery);

        Assert.False(AudioHelperArgumentParser.Parse(new[] { "--auto-hfp-pair", "--auto-hfp-pair" }).Ok);
        Assert.False(AudioHelperArgumentParser.Parse(new[] { "--probe", "--auto-hfp-pair" }).Ok);
        Assert.False(AudioHelperArgumentParser.Parse(new[]
        {
            "--auto-hfp-pair", "--capture-engine", "windows-mediacapture"
        }).Ok);
    }

    [Fact]
    public void HfpPairResolver_SelectsTheUniqueExactStructuralCandidate()
    {
        var container = Guid.NewGuid();
        var capture = new FakeEndpoint(DataFlow.Capture, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "capture");
        var render = new FakeEndpoint(DataFlow.Render, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "render");
        var enumerator = new PairEndpointEnumerator(capture, render);

        var resolver = new HfpPairResolver(() => enumerator);

        var result = resolver.Resolve("capture");
        Assert.Equal(HfpPairDiscoveryStatus.Paired, result.Status);
        Assert.Equal("render", result.RenderEndpointId);
        Assert.Equal("same_container_id", result.PairEvidence);
        Assert.Equal(1, capture.DisposeCount);
        Assert.Equal(1, render.DisposeCount);
        Assert.Equal(1, enumerator.DisposeCount);
        Assert.Equal(HfpTransportClassification.HfpCandidate, result.TransportClassification);
        Assert.Equal(1, capture.TransportQueryCount);
        Assert.Equal(1, capture.MixFormatAccessCount);
    }

    [Fact]
    public void HfpPairResolver_DoesNotPromoteOrdinaryMonoUsbCapture()
    {
        var capture = new FakeEndpoint(DataFlow.Capture, DeviceState.Active, Guid.NewGuid(),
            new WaveFormat(16000, 16, 1), "usb-capture")
        {
            TransportClassification = HfpTransportClassification.NotHfp
        };
        var enumerator = new PairEndpointEnumerator(capture);

        var result = new HfpPairResolver(() => enumerator).Resolve("usb-capture");

        Assert.Equal(HfpPairDiscoveryStatus.NotApplicable, result.Status);
        Assert.Equal(HfpTransportClassification.NotHfp, result.TransportClassification);
        Assert.Equal(1, capture.TransportQueryCount);
        Assert.Equal(0, capture.MixFormatAccessCount);
        Assert.Equal(0, enumerator.EnumerateRenderCount);
        Assert.DoesNotContain("transport", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HfpPairResolver_UnknownMonoCaptureRemainsDirectEligible()
    {
        var capture = new FakeEndpoint(DataFlow.Capture, DeviceState.Active, Guid.NewGuid(),
            new WaveFormat(16000, 16, 1), "unknown-capture")
        {
            TransportClassification = HfpTransportClassification.Unknown
        };
        var enumerator = new PairEndpointEnumerator(capture);

        var result = new HfpPairResolver(() => enumerator).Resolve("unknown-capture");

        Assert.Equal(HfpPairDiscoveryStatus.NotApplicable, result.Status);
        Assert.Equal(HfpTransportClassification.Unknown, result.TransportClassification);
        Assert.Equal(0, capture.MixFormatAccessCount);
        Assert.Equal(0, enumerator.EnumerateRenderCount);
    }

    [Fact]
    public void HfpPairResolver_OrdinaryStereo48kCaptureRemainsDirectEligible()
    {
        var capture = new FakeEndpoint(DataFlow.Capture, DeviceState.Active, Guid.NewGuid(),
            new WaveFormat(48000, 16, 2), "stereo-capture")
        {
            TransportClassification = HfpTransportClassification.NotHfp
        };

        var result = new HfpPairResolver(() => new PairEndpointEnumerator(capture))
            .Resolve("stereo-capture");

        Assert.Equal(HfpPairDiscoveryStatus.NotApplicable, result.Status);
        Assert.Equal(HfpTransportClassification.NotHfp, result.TransportClassification);
        Assert.Equal(0, capture.MixFormatAccessCount);
    }

    [Fact]
    public void HfpPairResolver_UsesCaseInsensitiveFullEndpointIdentityOnly()
    {
        var container = Guid.NewGuid();
        var capture = new FakeEndpoint(DataFlow.Capture, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "CAPTURE");
        var render = new FakeEndpoint(DataFlow.Render, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "render");
        var enumerator = new PairEndpointEnumerator(capture, render);
        var resolver = new HfpPairResolver(() => enumerator);

        var caseVariant = resolver.Resolve("capture");
        Assert.Equal(HfpPairDiscoveryStatus.Paired, caseVariant.Status);
        Assert.Equal("render", caseVariant.RenderEndpointId);

        var different = new HfpPairResolver(() => new PairEndpointEnumerator(capture, render))
            .Resolve("capture-other");
        Assert.Equal(HfpPairDiscoveryStatus.NotApplicable, different.Status);
    }

    [Fact]
    public void HfpPairResolver_RequiresExactlyOneCandidateAndNeverUsesNamesOrDefault()
    {
        var container = Guid.NewGuid();
        var capture = new FakeEndpoint(DataFlow.Capture, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "capture");
        var first = new FakeEndpoint(DataFlow.Render, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "render-first");
        var second = new FakeEndpoint(DataFlow.Render, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "render-second");

        var multipleEnumerator = new PairEndpointEnumerator(capture, first, second);
        Assert.Equal(HfpPairDiscoveryStatus.Ambiguous,
            new HfpPairResolver(() => multipleEnumerator).Resolve("capture").Status);
        Assert.Equal(1, capture.DisposeCount);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        Assert.Equal(1, multipleEnumerator.DisposeCount);

        var emptyCapture = new FakeEndpoint(DataFlow.Capture, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "capture-empty-candidates");
        var emptyEnumerator = new PairEndpointEnumerator(emptyCapture);
        Assert.Equal(HfpPairDiscoveryStatus.NoCandidate,
            new HfpPairResolver(() => emptyEnumerator).Resolve("capture-empty-candidates").Status);
        Assert.Equal(1, emptyCapture.DisposeCount);
        Assert.Equal(1, emptyEnumerator.DisposeCount);

        var unrelatedName = new FakeEndpoint(DataFlow.Render, DeviceState.Active, Guid.NewGuid(),
            new WaveFormat(16000, 16, 1), "same-name-as-capture");
        var unrelatedEnumerator = new PairEndpointEnumerator(capture, unrelatedName);
        Assert.Equal(HfpPairDiscoveryStatus.NoCandidate,
            new HfpPairResolver(() => unrelatedEnumerator).Resolve("capture").Status);
        Assert.Equal(1, unrelatedName.DisposeCount);
        Assert.Equal(1, unrelatedEnumerator.DisposeCount);
    }

    [Theory]
    [InlineData(DataFlow.Render, DeviceState.Disabled, 16000, 1)]
    [InlineData(DataFlow.Capture, DeviceState.Active, 16000, 1)]
    [InlineData(DataFlow.Render, DeviceState.Active, 8000, 1)]
    [InlineData(DataFlow.Render, DeviceState.Active, 16000, 2)]
    [InlineData(DataFlow.Render, DeviceState.Active, 48000, 1)]
    public void HfpPairResolver_RejectsInactiveWrongFlowAndIncompatibleFormats(
        DataFlow renderFlow, DeviceState renderState, int sampleRate, int channels)
    {
        var container = Guid.NewGuid();
        var capture = new FakeEndpoint(DataFlow.Capture, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "capture");
        var render = new FakeEndpoint(renderFlow, renderState, container,
            new WaveFormat(sampleRate, 16, channels), "render");

        var result = new HfpPairResolver(() => new PairEndpointEnumerator(capture, render)).Resolve("capture");
        Assert.Equal(renderFlow == DataFlow.Render && renderState == DeviceState.Active
            ? HfpPairDiscoveryStatus.Paired
            : HfpPairDiscoveryStatus.NoCandidate, result.Status);
    }

    [Fact]
    public void HfpPairResolver_ReturnsStructuredEvidenceFailuresWithoutRenderAudioClientAccess()
    {
        var container = Guid.NewGuid();
        var capture = new FakeEndpoint(DataFlow.Capture, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "capture");
        var throwingRender = new FakeEndpoint(DataFlow.Render, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "render")
        {
            ThrowOnMixFormat = true
        };
        var formatEnumerator = new PairEndpointEnumerator(capture, throwingRender);
        var formatResult = new HfpPairResolver(() => formatEnumerator).Resolve("capture");
        Assert.Equal(HfpPairDiscoveryStatus.Paired, formatResult.Status);
        Assert.Equal("render", formatResult.RenderEndpointId);
        Assert.Equal(0, throwingRender.MixFormatAccessCount);
        Assert.Equal(1, capture.DisposeCount);
        Assert.Equal(1, throwingRender.DisposeCount);
        Assert.Equal(1, formatEnumerator.DisposeCount);

        var throwingCapture = new FakeEndpoint(DataFlow.Capture, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "capture-format-failure")
        {
            ThrowOnMixFormat = true
        };
        var captureFormatResult = new HfpPairResolver(() => new PairEndpointEnumerator(throwingCapture))
            .Resolve("capture-format-failure");
        Assert.Equal(HfpPairDiscoveryStatus.EvidenceFailure, captureFormatResult.Status);
        Assert.DoesNotContain("HFP-like", captureFormatResult.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transport", captureFormatResult.Reason, StringComparison.OrdinalIgnoreCase);

        var emptyContainerRender = new FakeEndpoint(DataFlow.Render, DeviceState.Active, Guid.Empty,
            new WaveFormat(16000, 16, 1), "render");
        var emptyContainerEnumerator = new PairEndpointEnumerator(capture, emptyContainerRender);
        Assert.Equal(HfpPairDiscoveryStatus.EvidenceFailure,
            new HfpPairResolver(() => emptyContainerEnumerator).Resolve("capture").Status);
        Assert.Equal(1, emptyContainerRender.DisposeCount);
        Assert.Equal(1, emptyContainerEnumerator.DisposeCount);

        var emptyContainerCapture = new FakeEndpoint(DataFlow.Capture, DeviceState.Active, Guid.Empty,
            new WaveFormat(16000, 16, 1), "capture-empty-container");
        var emptyCaptureResult = new HfpPairResolver(() => new PairEndpointEnumerator(emptyContainerCapture))
            .Resolve("capture-empty-container");
        Assert.Equal(HfpPairDiscoveryStatus.EvidenceFailure, emptyCaptureResult.Status);

        Assert.Equal(HfpPairDiscoveryStatus.EvidenceFailure,
            new HfpPairResolver(() => throw new COMException("enumeration failed"))
                .Resolve("capture").Status);
    }

    [Fact]
    public void HfpPairResolver_EarlyContainerFailureReleasesUnvisitedCandidates()
    {
        var container = Guid.NewGuid();
        var capture = new FakeEndpoint(DataFlow.Capture, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "capture");
        var failed = new FakeEndpoint(DataFlow.Render, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "failed") { ThrowOnContainerId = true };
        var unvisited = new FakeEndpoint(DataFlow.Render, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "unvisited");
        var enumerator = new PairEndpointEnumerator(capture, failed, unvisited);

        Assert.Equal(HfpPairDiscoveryStatus.EvidenceFailure,
            new HfpPairResolver(() => enumerator).Resolve("capture").Status);
        Assert.Equal(1, capture.DisposeCount);
        Assert.Equal(1, failed.DisposeCount);
        Assert.Equal(1, unvisited.DisposeCount);
        Assert.Equal(1, enumerator.DisposeCount);
    }

    [Fact]
    public void HfpPairResolver_NeverReadsRenderMixFormatAndReleasesUnvisitedCandidates()
    {
        var container = Guid.NewGuid();
        var capture = new FakeEndpoint(DataFlow.Capture, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "capture");
        var failed = new FakeEndpoint(DataFlow.Render, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "failed") { ThrowOnMixFormat = true };
        var unvisited = new FakeEndpoint(DataFlow.Render, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "unvisited");
        var enumerator = new PairEndpointEnumerator(capture, failed, unvisited);

        var result = new HfpPairResolver(() => enumerator).Resolve("capture");
        Assert.Equal(HfpPairDiscoveryStatus.Ambiguous, result.Status);
        Assert.Equal(1, capture.DisposeCount);
        Assert.Equal(1, failed.DisposeCount);
        Assert.Equal(1, unvisited.DisposeCount);
        Assert.Equal(1, enumerator.DisposeCount);
    }

    [Fact]
    public void HfpPairResolver_EmptyEndpointIdReleasesWholeCollectionBeforeFallback()
    {
        var container = Guid.NewGuid();
        var capture = new FakeEndpoint(DataFlow.Capture, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "capture");
        var emptyId = new FakeEndpoint(DataFlow.Render, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "");
        var later = new FakeEndpoint(DataFlow.Render, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "later");
        var enumerator = new PairEndpointEnumerator(capture, emptyId, later);

        Assert.Equal(HfpPairDiscoveryStatus.EvidenceFailure,
            new HfpPairResolver(() => enumerator).Resolve("capture").Status);
        Assert.Equal(1, capture.DisposeCount);
        Assert.Equal(1, emptyId.DisposeCount);
        Assert.Equal(1, later.DisposeCount);
        Assert.Equal(1, enumerator.DisposeCount);
    }

    [Fact]
    public void HfpPairResolver_DisposeFailureDoesNotBlockOtherResourcesOrChangeResult()
    {
        var container = Guid.NewGuid();
        var capture = new FakeEndpoint(DataFlow.Capture, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "capture");
        var render = new FakeEndpoint(DataFlow.Render, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "render") { ThrowOnDispose = true };
        var enumerator = new PairEndpointEnumerator(capture, render) { ThrowOnDispose = true };

        Assert.Equal("render", new HfpPairResolver(() => enumerator).Resolve("capture").RenderEndpointId);
        Assert.Equal(1, capture.DisposeCount);
        Assert.Equal(1, render.DisposeCount);
        Assert.Equal(1, enumerator.DisposeCount);
    }

    [Fact]
    public void HfpPairResolver_DuplicateAndSharedEndpointReferencesAreDisposedOnce()
    {
        var container = Guid.NewGuid();
        var capture = new FakeEndpoint(DataFlow.Capture, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "capture");
        var render = new FakeEndpoint(DataFlow.Render, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "render");
        var enumerator = new PairEndpointEnumerator(capture, capture, render, render);

        Assert.Equal(HfpPairDiscoveryStatus.Ambiguous,
            new HfpPairResolver(() => enumerator).Resolve("capture").Status);
        Assert.Equal(1, capture.DisposeCount);
        Assert.Equal(1, render.DisposeCount);
        Assert.Equal(1, enumerator.DisposeCount);
    }

    [Fact]
    public void HfpPairResolver_EnumerationFailureStillReleasesCaptureAndEnumerator()
    {
        var container = Guid.NewGuid();
        var capture = new FakeEndpoint(DataFlow.Capture, DeviceState.Active, container,
            new WaveFormat(16000, 16, 1), "capture");
        var enumerator = new PairEndpointEnumerator(capture)
        {
            ThrowOnEnumeration = true
        };

        Assert.Equal(HfpPairDiscoveryStatus.EvidenceFailure,
            new HfpPairResolver(() => enumerator).Resolve("capture").Status);
        Assert.Equal(1, capture.DisposeCount);
        Assert.Equal(1, enumerator.DisposeCount);
    }

    [Fact]
    public void HfpEndpointCollectionBuilder_PartialRawAcquisitionReleasesTransferredWrappers()
    {
        var firstRaw = new FakeRawEndpoint();
        var firstWrapper = new FakeWrappedEndpoint(firstRaw);
        var secondRaw = new FakeRawEndpoint();

        Assert.Throws<InvalidOperationException>(() => HfpEndpointCollectionBuilder.Build(
            2,
            index => index == 0 ? firstRaw : throw new InvalidOperationException("collection item failed"),
            raw => raw == firstRaw ? firstWrapper : new FakeWrappedEndpoint(raw),
            raw => raw.Dispose(),
            endpoint => endpoint.Dispose()));

        Assert.Equal(1, firstWrapper.DisposeCount);
        Assert.Equal(1, firstRaw.DisposeCount);
        Assert.Equal(0, secondRaw.DisposeCount);
    }

    [Fact]
    public void HfpEndpointCollectionBuilder_WrapperFailureReleasesCurrentRawAndPriorWrappers()
    {
        var firstRaw = new FakeRawEndpoint();
        var secondRaw = new FakeRawEndpoint();
        var firstWrapper = new FakeWrappedEndpoint(firstRaw);

        Assert.Throws<InvalidOperationException>(() => HfpEndpointCollectionBuilder.Build(
            2,
            index => index == 0 ? firstRaw : secondRaw,
            raw => raw == firstRaw
                ? firstWrapper
                : throw new InvalidOperationException("wrapper construction failed"),
            raw => raw.Dispose(),
            endpoint => endpoint.Dispose()));

        Assert.Equal(1, firstWrapper.DisposeCount);
        Assert.Equal(1, firstRaw.DisposeCount);
        Assert.Equal(1, secondRaw.DisposeCount);
    }

    [Fact]
    public void CaptureSession_AutoPairResolvesOnceAndExplicitPairHasPriority()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ah_auto_pair_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var output = Path.Combine(dir, "recording.wav");
            var partial = Path.Combine(dir, "recording.partial.wav");
            var stop = Path.Combine(dir, "stop.signal");
            var capture = new FakeInput();
            var resolver = new CountingPairResolver("auto-render");
            var hfpFactory = new SequenceHfpFactory(() =>
                AudioInputOpenResult.Success(new HfpDuplexAudioInput(capture, new FakeRenderSession(1), "same_container_id", 1)));
            var options = new AudioHelperOptions
            {
                Mode = AudioHelperMode.Capture,
                CaptureEngine = AudioCaptureEngine.WasapiDirect,
                AutoHfpPairDiscovery = true,
                EndpointId = "capture",
                OutputPath = output,
                AllowedRoot = dir,
                StopSignalPath = stop,
                RecordingId = "auto-pair"
            };
            using var cts = new CancellationTokenSource();
            using var watcher = new StopWatcher(stop, cts.Cancel);
            using var session = new CaptureSession(options, Paths(output, partial), Writer(), watcher, cts,
                null, firstPacketTimeout: TimeSpan.FromSeconds(2), hfpFactory: hfpFactory,
                hfpPairResolver: resolver);

            var open = typeof(CaptureSession).GetMethod("OpenInput", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            using var first = ((AudioInputOpenResult)open.Invoke(session, new object[] { TimeSpan.FromSeconds(1) })!).Input;
            using var second = ((AudioInputOpenResult)open.Invoke(session, new object[] { TimeSpan.FromSeconds(1) })!).Input;

            Assert.Equal(1, resolver.ResolveCount);
            Assert.Equal(2, hfpFactory.OpenCount);
            Assert.Equal("auto-render", hfpFactory.LastRenderEndpointId);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void CaptureSession_ExplicitPairSkipsAutomaticResolver()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ah_explicit_pair_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var output = Path.Combine(dir, "recording.wav");
            var partial = Path.Combine(dir, "recording.partial.wav");
            var stop = Path.Combine(dir, "stop.signal");
            var resolver = new CountingPairResolver("auto-render");
            var hfpFactory = new SequenceHfpFactory(() =>
                AudioInputOpenResult.Success(new HfpDuplexAudioInput(new FakeInput(), new FakeRenderSession(1), "same_container_id", 1)));
            var options = new AudioHelperOptions
            {
                Mode = AudioHelperMode.Capture,
                CaptureEngine = AudioCaptureEngine.WasapiDirect,
                AutoHfpPairDiscovery = true,
                EndpointId = "capture",
                HfpRenderEndpointId = "explicit-render",
                OutputPath = output,
                AllowedRoot = dir,
                StopSignalPath = stop,
                RecordingId = "explicit-pair"
            };
            using var cts = new CancellationTokenSource();
            using var watcher = new StopWatcher(stop, cts.Cancel);
            using var session = new CaptureSession(options, Paths(output, partial), Writer(), watcher, cts,
                null, hfpFactory: hfpFactory, hfpPairResolver: resolver);

            var open = typeof(CaptureSession).GetMethod("OpenInput", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            using var input = ((AudioInputOpenResult)open.Invoke(session, new object[] { TimeSpan.FromSeconds(1) })!).Input;

            Assert.Equal(0, resolver.ResolveCount);
            Assert.Equal("explicit-render", hfpFactory.LastRenderEndpointId);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void CaptureSession_AutoPairDiscoveryFailureFailsClosedBeforeDirectFactory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ah_auto_pair_fail_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var output = Path.Combine(dir, "recording.wav");
            var partial = Path.Combine(dir, "recording.partial.wav");
            var stop = Path.Combine(dir, "stop.signal");
            var directOpenCount = 0;
            var options = new AudioHelperOptions
            {
                Mode = AudioHelperMode.Capture,
                CaptureEngine = AudioCaptureEngine.WasapiDirect,
                AutoHfpPairDiscovery = true,
                EndpointId = "capture",
                OutputPath = output,
                AllowedRoot = dir,
                StopSignalPath = stop,
                RecordingId = "auto-pair-failure"
            };
            using var cts = new CancellationTokenSource();
            using var watcher = new StopWatcher(stop, cts.Cancel);
            using var writer = new StringWriter();
            using var session = new CaptureSession(options, Paths(output, partial), new EventWriter(writer, null), watcher, cts,
                _ =>
                {
                    Interlocked.Increment(ref directOpenCount);
                    return (new FakeInput(), null, null);
                },
                firstPacketTimeout: TimeSpan.FromSeconds(2),
                hfpPairResolver: new FixedPairResolver(HfpPairDiscoveryResult.NoCandidate("no active render candidate")));

            Assert.Equal(1, session.Run());
            Assert.Equal(0, directOpenCount);
            var stdout = writer.ToString();
            Assert.DoesNotContain("RESULT: STARTED", stdout);
            Assert.Contains("RESULT: FAIL", stdout);
            Assert.Contains("ErrorCode: audio_hfp_pair_discovery_failed", stdout);
            Assert.Contains("FailureStage: HfpPairDiscovery", stdout);
            Assert.Contains("CaptureStrategy: hfp-auto-pair-discovery", stdout);
            Assert.Contains("PairEvidence: hfp_pair_discovery_failed", stdout);
            Assert.Contains("AutoHfpPairStatus: no_candidate", stdout);
            Assert.Contains("AutoHfpPairResultCode: audio_hfp_pair_discovery_failed", stdout);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void HfpPairValidator_RequiresExactActiveFlowsAndSameContainer()
    {
        var container = Guid.NewGuid();
        var validator = new HfpEndpointPairValidator();

        var ok = validator.Validate("capture", "render", new FakeEndpointEnumerator(
            new FakeEndpoint(DataFlow.Capture, DeviceState.Active, container),
            new FakeEndpoint(DataFlow.Render, DeviceState.Active, container)));
        Assert.True(ok.Ok);
        Assert.Equal("same_container_id", ok.PairEvidence);

        var different = validator.Validate("capture", "render", new FakeEndpointEnumerator(
            new FakeEndpoint(DataFlow.Capture, DeviceState.Active, Guid.NewGuid()),
            new FakeEndpoint(DataFlow.Render, DeviceState.Active, Guid.NewGuid())));
        Assert.False(different.Ok);
        Assert.Equal("unverified", different.PairEvidence);

        var wrongFlow = validator.Validate("capture", "render", new FakeEndpointEnumerator(
            new FakeEndpoint(DataFlow.Render, DeviceState.Active, container),
            new FakeEndpoint(DataFlow.Render, DeviceState.Active, container)));
        Assert.False(wrongFlow.Ok);
        Assert.Equal("unverified", wrongFlow.PairEvidence);
    }

    [Fact]
    public void HfpPairValidator_RejectsInactiveAndContainerQueryFailuresBeforePrime()
    {
        var container = Guid.NewGuid();
        var validator = new HfpEndpointPairValidator();

        var inactive = validator.Validate("capture", "render", new FakeEndpointEnumerator(
            new FakeEndpoint(DataFlow.Capture, DeviceState.Disabled, container),
            new FakeEndpoint(DataFlow.Render, DeviceState.Active, container)));
        Assert.False(inactive.Ok);
        Assert.Equal("unverified", inactive.PairEvidence);

        var missing = validator.Validate("capture", "render", new FakeEndpointEnumerator(
            new FakeEndpoint(DataFlow.Capture, DeviceState.Active, Guid.Empty),
            new FakeEndpoint(DataFlow.Render, DeviceState.Active, container)));
        Assert.False(missing.Ok);
        Assert.Equal("unverified", missing.PairEvidence);
        Assert.Contains("ContainerId", missing.Reason);
    }

    [Fact]
    public void HfpFactory_ValidatesPairThenPrimesRenderThenOpensClassicCapture()
    {
        var log = new List<string>();
        var container = Guid.NewGuid();
        var endpoints = new SequencedEndpointEnumerator(log,
            new FakeEndpoint(DataFlow.Capture, DeviceState.Active, container),
            new FakeEndpoint(DataFlow.Render, DeviceState.Active, container));
        var render = new FakeRenderSession(17);
        var capture = new FakeInput();
        var factory = new HfpDuplexAudioInputFactory(
            () => endpoints,
            new FakeRenderPrimeFactory(log, render),
            (endpointId, _) =>
            {
                log.Add("capture-classic:" + endpointId);
                return AudioInputOpenResult.Success(capture);
            });

        var result = factory.Open("capture", "render", TimeSpan.FromSeconds(1));

        Assert.NotNull(result.Input);
        Assert.Equal(new[] { "resolve:capture", "resolve:render", "render-prime:render", "capture-classic:capture" }, log);
        var metadata = Assert.IsAssignableFrom<IHfpAudioInputMetadata>(result.Input);
        Assert.Equal("same_container_id", metadata.PairEvidence);
        Assert.Equal(17, metadata.RenderPrimeReadyMs);
        result.Input.Dispose();
        Assert.Equal(1, render.DisposeCount);
    }

    [Fact]
    public void HfpFactory_CaptureFailureReleasesPrimedRenderAndDoesNotCreateWavInput()
    {
        var render = new FakeRenderSession(5);
        var captureCalls = 0;
        var container = Guid.NewGuid();
        var factory = new HfpDuplexAudioInputFactory(
            () => new FakeEndpointEnumerator(
                new FakeEndpoint(DataFlow.Capture, DeviceState.Active, container),
                new FakeEndpoint(DataFlow.Render, DeviceState.Active, container)),
            new FakeRenderPrimeFactory(new List<string>(), render),
            (_, _) =>
            {
                captureCalls++;
                return AudioInputOpenResult.Failure("audio_endpoint_inactive", "capture inactive", HfpFailureStages.CaptureOpen);
            });

        var result = factory.Open("capture", "render", TimeSpan.FromSeconds(1));

        Assert.Null(result.Input);
        Assert.Equal("audio_endpoint_inactive", result.ErrorCode);
        Assert.Equal(1, captureCalls);
        Assert.Equal(1, render.DisposeCount);
    }

    [Fact]
    public void HfpFactory_RenderPrimeFailureStopsBeforeCaptureOpen()
    {
        var captureCalls = 0;
        var container = Guid.NewGuid();
        var factory = new HfpDuplexAudioInputFactory(
            () => new FakeEndpointEnumerator(
                new FakeEndpoint(DataFlow.Capture, DeviceState.Active, container),
                new FakeEndpoint(DataFlow.Render, DeviceState.Active, container)),
            new FailingRenderPrimeFactory(),
            (_, _) =>
            {
                captureCalls++;
                return AudioInputOpenResult.Failure("audio_endpoint_not_found", "not expected", HfpFailureStages.CaptureOpen);
            });

        var result = factory.Open("capture", "render", TimeSpan.FromSeconds(1));

        Assert.Null(result.Input);
        Assert.Equal("audio_hfp_render_prime_failed", result.ErrorCode);
        Assert.Equal(0, captureCalls);
    }

    [Fact]
    public void HfpInput_RenderFailureBeforeStart_DoesNotStartCaptureOrEmitData()
    {
        var renderFailure = new HfpRenderFailure("render pump failed", unchecked((int)0x80004005), new InvalidOperationException("pump"));
        var input = new FakeInput();
        using var duplex = new HfpDuplexAudioInput(input, new FakeRenderSession(1, renderFailure), "same_container_id", 1);

        var exception = Assert.Throws<AudioCaptureStartException>(() => duplex.StartRecording());
        Assert.Equal("audio_hfp_render_runtime_failed", exception.ErrorCode);
        Assert.Equal(HfpFailureStages.RenderRuntimePump, exception.Stage);
        Assert.Equal(0, input.StartCount);
        Assert.Equal(0, input.DataCount);
    }

    [Fact]
    public async Task HfpInput_RenderFailureAfterFirstPacket_EmitsOneTerminalFailure()
    {
        var render = new FakeRenderSession(1);
        var input = new FakeInput();
        using var duplex = new HfpDuplexAudioInput(input, render, "same_container_id", 1);
        var dataCount = 0;
        duplex.DataAvailable += (_, _) =>
        {
            Interlocked.Increment(ref dataCount);
        };
        var stoppedCount = 0;
        var stopped = new TaskCompletionSource<StoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        duplex.RecordingStopped += (_, args) =>
        {
            Interlocked.Increment(ref stoppedCount);
            stopped.TrySetResult(args);
        };

        Assert.Equal(StartRecordingResult.Started, duplex.StartRecording());
        render.Failure = new HfpRenderFailure("render pump failed", unchecked((int)0x80004005),
            new InvalidOperationException("pump"));
        Assert.Same(stopped.Task, await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(2))));
        var exception = Assert.IsType<AudioCaptureRuntimeException>((await stopped.Task).Exception);
        Assert.Equal("audio_hfp_render_runtime_failed", exception.ErrorCode);
        Assert.Equal(HfpFailureStages.RenderRuntimePump, exception.Stage);
        Assert.Equal(1, dataCount);
        Assert.Equal(1, stoppedCount);
        Assert.Equal(1, input.StartCount);
    }

    [Fact]
    public void HfpFactory_RenderFailureDuringCaptureOpen_DisposesCaptureAndNeverStartsIt()
    {
        var container = Guid.NewGuid();
        var render = new FakeRenderSession(2);
        var capture = new FakeInput();
        var factory = new HfpDuplexAudioInputFactory(
            () => new FakeEndpointEnumerator(
                new FakeEndpoint(DataFlow.Capture, DeviceState.Active, container),
                new FakeEndpoint(DataFlow.Render, DeviceState.Active, container)),
            new FakeRenderPrimeFactory(new List<string>(), render),
            (_, _) =>
            {
                render.Failure = new HfpRenderFailure("pump failed", unchecked((int)0x80004005));
                return AudioInputOpenResult.Success(capture);
            });

        var result = factory.Open("capture", "render", TimeSpan.FromSeconds(1));

        Assert.Null(result.Input);
        Assert.Equal("audio_hfp_render_runtime_failed", result.ErrorCode);
        Assert.Equal(HfpFailureStages.RenderRuntimePump, result.FailureStage);
        Assert.Equal(0, capture.StartCount);
        Assert.Equal(1, capture.DisposeCount);
        Assert.Equal(1, render.DisposeCount);
    }

    [Fact]
    public void HfpFactory_CaptureOpenExceptionRetainsCaptureStageAndHresult()
    {
        var container = Guid.NewGuid();
        var render = new FakeRenderSession(2);
        var factory = new HfpDuplexAudioInputFactory(
            () => new FakeEndpointEnumerator(
                new FakeEndpoint(DataFlow.Capture, DeviceState.Active, container),
                new FakeEndpoint(DataFlow.Render, DeviceState.Active, container)),
            new FakeRenderPrimeFactory(new List<string>(), render),
            (_, _) => throw new COMException("capture open failed", unchecked((int)0x80070057)));

        var result = factory.Open("capture", "render", TimeSpan.FromSeconds(1));

        Assert.Null(result.Input);
        Assert.Equal("audio_capture_start_failed", result.ErrorCode);
        Assert.Equal(HfpFailureStages.CaptureOpen, result.FailureStage);
        Assert.Equal(unchecked((int)0x80070057), result.Hresult);
        Assert.DoesNotContain("render", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, render.DisposeCount);
    }

    [Fact]
    public async Task HfpInput_MonitorStopFailureIsContainedAndPreservesPrimaryFailure()
    {
        var render = new FakeRenderSession(1);
        var input = new FakeInput { StopException = new InvalidOperationException("capture stop failed") };
        using var duplex = new HfpDuplexAudioInput(input, render, "same_container_id", 1);
        var stopped = new TaskCompletionSource<StoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        duplex.RecordingStopped += (_, args) => stopped.TrySetResult(args);

        Assert.Equal(StartRecordingResult.Started, duplex.StartRecording());
        render.Failure = new HfpRenderFailure("render failed", unchecked((int)0x80004005));
        Assert.Same(stopped.Task, await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(2))));

        var exception = Assert.IsType<AudioCaptureRuntimeException>((await stopped.Task).Exception);
        Assert.Equal(HfpFailureStages.RenderRuntimePump, exception.Stage);
        Assert.NotNull(exception.SecondaryFailure);
        Assert.Equal(HfpFailureStages.CaptureStop, exception.SecondaryFailure!.Stage);
        Assert.Equal("audio_hfp_render_runtime_failed", exception.ErrorCode);
    }

    [Fact]
    public void HfpInput_UnderlyingRecordingStoppedAfterSyntheticTerminal_IsIgnored()
    {
        var render = new FakeRenderSession(1);
        var input = new FakeInput();
        using var duplex = new HfpDuplexAudioInput(input, render, "same_container_id", 1);
        var stoppedCount = 0;
        duplex.RecordingStopped += (_, _) => Interlocked.Increment(ref stoppedCount);

        Assert.Equal(StartRecordingResult.Started, duplex.StartRecording());
        render.Failure = new HfpRenderFailure("render failed", unchecked((int)0x80004005));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref stoppedCount) == 1, TimeSpan.FromSeconds(2)));
        input.StopRecording();

        Assert.Equal(1, stoppedCount);
    }

    [Fact]
    public void HfpInput_StartExceptionRetainsCaptureStartStageAndResourcesReleaseOnce()
    {
        var render = new FakeRenderSession(1);
        var input = new FakeInput { StartException = new COMException("start failed", unchecked((int)0x80070057)) };
        var duplex = new HfpDuplexAudioInput(input, render, "same_container_id", 1);

        var exception = Assert.Throws<AudioCaptureStartException>(() => duplex.StartRecording());
        Assert.Equal("audio_capture_start_failed", exception.ErrorCode);
        Assert.Equal(HfpFailureStages.CaptureStart, exception.Stage);
        Assert.Equal(unchecked((int)0x80070057), exception.Hresult);

        duplex.Dispose();
        duplex.Dispose();
        Assert.Equal(1, input.DisposeCount);
        Assert.Equal(1, render.DisposeCount);
    }

    [Fact]
    public async Task CaptureSession_HfpHealthyPathEmitsStartedAndStoppedWithConsistentMetadata()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ah_hfp_session_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var output = Path.Combine(dir, "recording.wav");
            var partial = Path.Combine(dir, "recording.partial.wav");
            var stop = Path.Combine(dir, "stop.signal");
            var input = new FakeInput();
            var render = new FakeRenderSession(7);
            var hfpFactory = new SequenceHfpFactory(() =>
                AudioInputOpenResult.Success(new HfpDuplexAudioInput(input, render, "same_container_id", 7), "same_container_id"));
            var options = new AudioHelperOptions
            {
                Mode = AudioHelperMode.Capture,
                CaptureEngine = AudioCaptureEngine.WasapiDirect,
                EndpointId = "capture",
                HfpRenderEndpointId = "render",
                OutputPath = output,
                AllowedRoot = dir,
                StopSignalPath = stop,
                RecordingId = "hfp-session"
            };
            var paths = new PathCheckResult
            {
                Ok = true,
                CanonicalPath = output,
                PartialPath = partial,
                OpenPartialStream = () => new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)
            };
            using var cts = new CancellationTokenSource();
            using var watcher = new StopWatcher(stop, cts.Cancel);
            using var writer = new StringWriter();
            using var session = new CaptureSession(options, paths, new EventWriter(writer, null), watcher, cts,
                null, firstPacketTimeout: TimeSpan.FromSeconds(2), hfpFactory: hfpFactory);

            var run = Task.Run(session.Run);
            Assert.True(input.Started.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref input.DataCount) > 0, TimeSpan.FromSeconds(2)));
            session.RequestStop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));

            var stdout = writer.ToString();
            Assert.Equal(1, Occurrences(stdout, "RESULT: STARTED"));
            Assert.Equal(1, Occurrences(stdout, "RESULT: STOPPED"));
            Assert.Equal(2, Occurrences(stdout, "CaptureStrategy: hfp-duplex-prime-classic"));
            Assert.Equal(2, Occurrences(stdout, "PairEvidence: same_container_id"));
            Assert.Equal(2, Occurrences(stdout, "RenderPrimeReadyMs: 7"));
            Assert.True(File.Exists(output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void CaptureSession_HfpRenderFailureBeforeFirstPacketEmitsOnlyFailWithTypedMetadata()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ah_hfp_fail_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var output = Path.Combine(dir, "recording.wav");
            var partial = Path.Combine(dir, "recording.partial.wav");
            var stop = Path.Combine(dir, "stop.signal");
            var inputs = new List<FakeInput>();
            var renderFailure = new HfpRenderFailure("render pump failed", unchecked((int)0x80004005));
            var hfpFactory = new SequenceHfpFactory(() =>
            {
                var input = new FakeInput();
                inputs.Add(input);
                return AudioInputOpenResult.Success(
                    new HfpDuplexAudioInput(input, new FakeRenderSession(1, renderFailure), "unverified", -1),
                    "unverified");
            });
            var options = new AudioHelperOptions
            {
                Mode = AudioHelperMode.Capture,
                CaptureEngine = AudioCaptureEngine.WasapiDirect,
                EndpointId = "capture",
                HfpRenderEndpointId = "render",
                OutputPath = output,
                AllowedRoot = dir,
                StopSignalPath = stop,
                RecordingId = "hfp-failure"
            };
            var paths = new PathCheckResult
            {
                Ok = true,
                CanonicalPath = output,
                PartialPath = partial,
                OpenPartialStream = () => new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)
            };
            using var cts = new CancellationTokenSource();
            using var watcher = new StopWatcher(stop, cts.Cancel);
            using var writer = new StringWriter();
            using var session = new CaptureSession(options, paths, new EventWriter(writer, null), watcher, cts,
                null, firstPacketTimeout: TimeSpan.FromSeconds(2), hfpFactory: hfpFactory);

            Assert.Equal(1, session.Run());
            var stdout = writer.ToString();
            Assert.Equal(0, Occurrences(stdout, "RESULT: STARTED"));
            Assert.Equal(1, Occurrences(stdout, "RESULT: FAIL"));
            Assert.Contains("CaptureStrategy: hfp-duplex-prime-classic", stdout);
            Assert.Contains("PairEvidence: unverified", stdout);
            Assert.DoesNotContain("RenderPrimeReadyMs:", stdout);
            Assert.Contains("FailureStage: HfpRenderRuntimePump", stdout);
            Assert.Contains("HRESULT: 0x80004005", stdout);
            Assert.All(inputs, input => Assert.Equal(0, input.StartCount));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void ClassicHfpCapture_UsesRawMixEventCallbackAndZeroDurations()
    {
        var device = new FakeDevice();
        var probe = new FakeAudioClient(device.Format);
        var captureClient = new FakeAudioCaptureClient();
        var capture = new FakeAudioClient(device.Format) { CaptureClient = captureClient };
        device.Clients.Enqueue(probe);
        device.Clients.Enqueue(capture);

        var result = WasapiAudioInput.TryInitializeClassicCapture(device, "capture", DeviceState.Active);
        Assert.NotNull(result.Input);
        Assert.Null(result.ErrorCode);
        Assert.Equal(AudioClientStreamFlags.EventCallback, capture.InitializeFlags);
        Assert.Equal(0, capture.BufferDuration);
        Assert.Equal(0, capture.Periodicity);
        Assert.Same(device.Format, capture.InitializeFormat);

        result.Input!.StartRecording();
        Assert.Equal(1, capture.SetEventHandleCount);
        Assert.Equal(1, capture.StartCount);
        result.Input.StopRecording();
        result.Input.Dispose();
    }

    [Fact]
    public void ClassicHfpCapture_MixFormatFailureDisposesProbeAndDeviceExactlyOnce()
    {
        var device = new FakeDevice();
        var probe = new FakeAudioClient(device.Format) { MixFormatException = new InvalidOperationException("mix format failed") };
        device.Clients.Enqueue(probe);

        var result = WasapiAudioInput.TryInitializeClassicCapture(device, "capture", DeviceState.Active);

        Assert.Null(result.Input);
        Assert.Equal(1, probe.DisposeCount);
        Assert.Equal(1, device.DisposeCount);
        Assert.Empty(device.Clients);
    }

    [Fact]
    public void ClassicHfpCapture_BufferSizeFailureReleasesAudioClientCaptureAndDevice()
    {
        var device = new FakeDevice();
        var probe = new FakeAudioClient(device.Format);
        var capture = new FakeAudioClient(device.Format) { BufferSizeException = new InvalidOperationException("buffer size failed") };
        var captureClient = new FakeAudioCaptureClient();
        capture.CaptureClient = captureClient;
        device.Clients.Enqueue(probe);
        device.Clients.Enqueue(capture);

        var result = WasapiAudioInput.TryInitializeClassicCapture(device, "capture", DeviceState.Active);

        Assert.Null(result.Input);
        Assert.Equal(1, probe.DisposeCount);
        Assert.Equal(1, capture.DisposeCount);
        Assert.Equal(1, captureClient.DisposeCount);
        Assert.Equal(1, device.DisposeCount);
        Assert.Equal(0, capture.SetEventHandleCount);
    }

    [Fact]
    public void ClassicHfpCapture_FormatValidationFailureDoesNotCreateAnEventHandle()
    {
        var invalidFormat = new WaveFormat(16000, 0, 1);
        var device = new FakeDevice();
        var probe = new FakeAudioClient(invalidFormat);
        var capture = new FakeAudioClient(invalidFormat) { CaptureClient = new FakeAudioCaptureClient() };
        device.Clients.Enqueue(probe);
        device.Clients.Enqueue(capture);

        var result = WasapiAudioInput.TryInitializeClassicCapture(device, "capture", DeviceState.Active);

        Assert.Null(result.Input);
        Assert.Equal(1, capture.DisposeCount);
        Assert.Equal(1, capture.CaptureClient!.DisposeCount);
        Assert.Equal(1, device.DisposeCount);
        Assert.Equal(0, capture.SetEventHandleCount);
    }

    [Fact]
    public void HfpRenderActivation_ProductionActivatorUsesExactMmDeviceEndpointAndIaudioClient2()
    {
        var endpointId = "{0.0.0.exact.render}";
        var log = new List<string>();
        var client = new FakeRenderActivationClient(log);
        var device = new FakeNativeRenderDevice(log, client);
        var api = new FakeNativeRenderApi(log, device);
        var factory = new NAudioHfpRenderActivationFactory(new NativeHfpRenderDeviceActivator(api));

        var result = factory.Activate(endpointId);

        Assert.Same(client, result);
        Assert.Equal(endpointId, api.EndpointId);
        Assert.Equal(new[] { "GetDevice:" + endpointId, "Activate(IAudioClient2)" }, log.Take(2));
        Assert.Equal(NativeAudioGuids.AudioClient2, device.ActivationIid);
        Assert.Equal((uint)23, device.ActivationClsContext);
    }

    [Fact]
    public void HfpRenderPrime_UsesExactActivationAndCommunicationsSequence()
    {
        var log = new List<string>();
        var client = new FakeRenderActivationClient(log);
        var device = new FakeNativeRenderDevice(log, client);
        var api = new FakeNativeRenderApi(log, device);
        var primeFactory = new NAudioHfpRenderPrimeFactory(
            new NAudioHfpRenderActivationFactory(new NativeHfpRenderDeviceActivator(api)),
            new FakeComApartmentFactory(log));

        var result = primeFactory.Prime("{0.0.0.exact.render}", TimeSpan.FromSeconds(1));

        Assert.NotNull(result.Session);
        var operations = log.Where(entry => entry != "com_init" && entry != "device_release" && entry != "com_uninit");
        Assert.Equal(new[]
        {
            "GetDevice:{0.0.0.exact.render}",
            "Activate(IAudioClient2)",
            "set_properties",
            "get_mix_format",
            "is_format_supported",
            "initialize",
            "set_event_handle",
            "get_render_client",
            "buffer_size",
            "get_buffer",
            "release_buffer_silent",
            "start",
            "buffer_size",
            "current_padding",
            "get_buffer",
            "release_buffer_silent"
        }, operations.Take(16));
        Assert.Equal((uint)Marshal.SizeOf<AudioClientProperties>(), client.Properties.cbSize);
        Assert.Equal(0, client.Properties.bIsOffload);
        Assert.Equal(AudioStreamCategory.Communications, client.Properties.eCategory);
        Assert.Equal(AudioClientStreamOptions.None, client.Properties.Options);

        result.Session!.Dispose();
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(1, client.RenderBuffer.DisposeCount);
    }

    [Theory]
    [InlineData("resolve", HfpFailureStages.RenderResolve, unchecked((int)0x80070002))]
    [InlineData("activate", HfpFailureStages.RenderActivation, unchecked((int)0x80070057))]
    [InlineData("properties", HfpFailureStages.RenderSetClientProperties, unchecked((int)0x80004005))]
    public void HfpFactory_RenderActivationStagesFailBeforeClassicCapture(string failure,
        string expectedStage, int expectedHresult)
    {
        var container = Guid.NewGuid();
        var captureCalls = 0;
        var log = new List<string>();
        var client = new FakeRenderActivationClient(log);
        if (failure == "properties")
            client.SetClientPropertiesException = new COMException("properties failed", expectedHresult);
        var activationFailure = failure == "resolve"
            ? new HfpRenderActivationException(HfpFailureStages.RenderResolve, expectedHresult,
                "resolve failed", new COMException("resolve failed", expectedHresult))
            : failure == "activate"
                ? new HfpRenderActivationException(HfpFailureStages.RenderActivation, expectedHresult,
                    "activate failed", new COMException("activate failed", expectedHresult))
                : null;
        var device = new FakeNativeRenderDevice(log, client, activationFailure);
        var nativeApi = new FakeNativeRenderApi(log, device, failure == "resolve" ? activationFailure : null);
        var factory = new HfpDuplexAudioInputFactory(
            () => new FakeEndpointEnumerator(
                new FakeEndpoint(DataFlow.Capture, DeviceState.Active, container),
                new FakeEndpoint(DataFlow.Render, DeviceState.Active, container)),
            new NAudioHfpRenderPrimeFactory(
                new NAudioHfpRenderActivationFactory(new NativeHfpRenderDeviceActivator(nativeApi)),
                new FakeComApartmentFactory(log)),
            (_, _) =>
            {
                captureCalls++;
                return AudioInputOpenResult.Failure("audio_endpoint_not_found", "not expected",
                    HfpFailureStages.CaptureOpen);
            });

        var result = factory.Open("capture", "render", TimeSpan.FromSeconds(1));

        Assert.Null(result.Input);
        Assert.Equal("audio_hfp_render_prime_failed", result.ErrorCode);
        Assert.Equal(expectedStage, result.FailureStage);
        Assert.Equal(expectedHresult, result.Hresult);
        Assert.Equal(0, captureCalls);
        Assert.Equal("same_container_id", result.PairEvidence);
        Assert.DoesNotContain("{0.0.0.exact.render}", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mix-format", false)]
    [InlineData("initialize", false)]
    [InlineData("event-handle", false)]
    [InlineData("get-service", false)]
    [InlineData("prefill", true)]
    [InlineData("start", true)]
    [InlineData("first-refill", true)]
    public void HfpRenderPrime_FailureReleasesAcquiredResourcesExactlyOnce(string failure, bool bufferAcquired)
    {
        var log = new List<string>();
        var client = new FakeRenderActivationClient(log);
        var error = new COMException(failure + " failed", unchecked((int)0x80004005));
        switch (failure)
        {
            case "mix-format": client.MixFormatException = error; break;
            case "initialize": client.InitializeException = error; break;
            case "event-handle": client.SetEventHandleException = error; break;
            case "get-service": client.GetRenderBufferException = error; break;
            case "prefill": client.RenderBuffer.GetBufferException = error; break;
            case "start": client.StartException = error; break;
            case "first-refill": client.CurrentPaddingValue = client.BufferSizeValue; break;
        }

        var result = HfpRenderPrime.CreateAndPrime("render", TimeSpan.FromMilliseconds(25),
            new ImmediateRenderActivationFactory(client));

        Assert.Null(result.Session);
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(bufferAcquired ? 1 : 0, client.RenderBuffer.DisposeCount);
    }

    [Fact]
    public void HfpRenderOwner_ProductionActivatorKeepsApartmentThroughPumpAndRelease()
    {
        var endpointId = "{0.0.0.owner.render}";
        var log = new List<string>();
        var client = new FakeRenderActivationClient(log);
        var device = new FakeNativeRenderDevice(log, client);
        var api = new FakeNativeRenderApi(log, device);
        var apartment = new FakeComApartmentFactory(log);
        var activation = new NAudioHfpRenderActivationFactory(new NativeHfpRenderDeviceActivator(api));

        var result = HfpRenderPrime.CreateAndPrime(endpointId, TimeSpan.FromSeconds(1), activation, apartment);

        Assert.NotNull(result.Session);
        result.Session!.Dispose();

        Assert.Equal(endpointId, api.EndpointId);
        Assert.Equal(NativeAudioGuids.AudioClient2, device.ActivationIid);
        Assert.Equal((uint)23, device.ActivationClsContext);
        Assert.Equal(apartment.InitThreadId, apartment.UninitThreadId);
        Assert.NotEqual(0, apartment.InitThreadId);
        lock (client.CallThreadIds)
            Assert.All(client.CallThreadIds, threadId => Assert.Equal(apartment.InitThreadId, threadId));
        Assert.Equal("com_init", log[0]);
        Assert.Equal("com_uninit", log[^1]);
        Assert.True(log.IndexOf("stop") < log.IndexOf("render_release"));
        Assert.True(log.IndexOf("render_release") < log.IndexOf("client_release"));
        Assert.True(log.IndexOf("client_release") < log.IndexOf("com_uninit"));
        Assert.DoesNotContain("ActivateAudioInterfaceAsync", log);
    }

    [Fact]
    public void HfpRenderOwner_ComInitializationFailureStopsBeforeNativeResolveAndCapture()
    {
        var log = new List<string>();
        var client = new FakeRenderActivationClient(log);
        var device = new FakeNativeRenderDevice(log, client);
        var api = new FakeNativeRenderApi(log, device);
        var apartment = new FakeComApartmentFactory(log,
            new COMException("COM init failed", unchecked((int)0x80004005)));
        var captureCalls = 0;
        var container = Guid.NewGuid();
        var factory = new HfpDuplexAudioInputFactory(
            () => new FakeEndpointEnumerator(
                new FakeEndpoint(DataFlow.Capture, DeviceState.Active, container),
                new FakeEndpoint(DataFlow.Render, DeviceState.Active, container)),
            new NAudioHfpRenderPrimeFactory(
                new NAudioHfpRenderActivationFactory(new NativeHfpRenderDeviceActivator(api)), apartment),
            (_, _) =>
            {
                captureCalls++;
                return AudioInputOpenResult.Failure("audio_endpoint_not_found", "not expected",
                    HfpFailureStages.CaptureOpen);
            });

        var result = factory.Open("capture", "render", TimeSpan.FromSeconds(1));

        Assert.Null(result.Input);
        Assert.Equal("audio_hfp_render_prime_failed", result.ErrorCode);
        Assert.Equal(HfpFailureStages.RenderActivation, result.FailureStage);
        Assert.Equal(unchecked((int)0x80004005), result.Hresult);
        Assert.Equal(0, api.GetDeviceCount);
        Assert.Equal(0, captureCalls);
        Assert.DoesNotContain(log, entry => entry.StartsWith("GetDevice:", StringComparison.Ordinal));
    }

    [Fact]
    public void HfpRenderOwner_RuntimePumpFailureStopsOnOwnerAndReleasesOnce()
    {
        var log = new List<string>();
        var client = new FakeRenderActivationClient(log);
        var device = new FakeNativeRenderDevice(log, client);
        var apartment = new FakeComApartmentFactory(log);
        var result = HfpRenderPrime.CreateAndPrime("render", TimeSpan.FromSeconds(1),
            new NAudioHfpRenderActivationFactory(new NativeHfpRenderDeviceActivator(
                new FakeNativeRenderApi(log, device))), apartment);

        Assert.NotNull(result.Session);
        client.RenderBuffer.GetBufferException = new COMException("pump failed", unchecked((int)0x80004005));
        Assert.True(SpinWait.SpinUntil(() => result.Session!.RuntimeFailure != null, TimeSpan.FromSeconds(2)));
        Assert.Equal(HfpFailureStages.RenderRuntimePump, result.Session!.RuntimeFailure!.Stage);
        result.Session.Dispose();
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(1, client.RenderBuffer.DisposeCount);
        Assert.Equal(apartment.InitThreadId, apartment.UninitThreadId);
    }

    [Fact]
    public void HfpRenderOwner_StopFailureIsTypedAndStillUninitializesAfterRelease()
    {
        var log = new List<string>();
        var client = new FakeRenderActivationClient(log)
        {
            StopException = new COMException("stop failed", unchecked((int)0x80070057))
        };
        var device = new FakeNativeRenderDevice(log, client);
        var apartment = new FakeComApartmentFactory(log);
        var result = HfpRenderPrime.CreateAndPrime("render", TimeSpan.FromSeconds(1),
            new NAudioHfpRenderActivationFactory(new NativeHfpRenderDeviceActivator(
                new FakeNativeRenderApi(log, device))), apartment);

        Assert.NotNull(result.Session);
        result.Session!.Dispose();

        Assert.NotNull(result.Session.RuntimeFailure);
        Assert.Equal(HfpFailureStages.RenderStop, result.Session.RuntimeFailure!.Stage);
        Assert.Equal(unchecked((int)0x80070057), result.Session.RuntimeFailure.Hresult);
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(1, client.RenderBuffer.DisposeCount);
        Assert.Equal("com_uninit", log[^1]);
    }

    [Fact]
    public void HfpRenderOwner_StartupCancellationLeavesNoOwnerThread()
    {
        var client = new FakeRenderActivationClient(new List<string>());
        var activation = new BlockingRenderActivationFactory(client);
        var owner = new HfpRenderOwner("render", TimeSpan.FromSeconds(1), activation,
            new FakeComApartmentFactory(new List<string>()));
        owner.Start();
        Assert.True(activation.Entered.Wait(TimeSpan.FromSeconds(2)));

        owner.Dispose();
        activation.Release.Set();

        Assert.False(owner.IsAlive);
        Assert.NotNull(owner.StartupFailure);
        Assert.Equal(HfpFailureStages.RenderPrime, owner.StartupFailure!.Stage);
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(1, false, true)]
    [InlineData(unchecked((int)0x88890008), false, false)]
    [InlineData(unchecked((int)0x80004005), false, false)]
    public void HfpRenderActivationClient_IsFormatSupportedClassifiesExactHresults(int hresult,
        bool expectedSupported, bool closestMatchReturned)
    {
        var native = new FakeNativeAudioClient2
        {
            FormatSupportHresult = hresult,
            ClosestMatch = closestMatchReturned ? new IntPtr(1234) : IntPtr.Zero
        };
        var memory = new FakeWaveFormatMemory();
        using var client = new NAudioHfpRenderActivationClient(native, memory);

        if (hresult == unchecked((int)0x80004005))
        {
            var exception = Assert.Throws<COMException>(() =>
                client.IsFormatSupported(AudioClientShareMode.Shared, new WaveFormat(16000, 16, 1)));
            Assert.Equal(hresult, exception.HResult);
        }
        else
        {
            Assert.Equal(expectedSupported,
                client.IsFormatSupported(AudioClientShareMode.Shared, new WaveFormat(16000, 16, 1)));
        }

        Assert.Equal(closestMatchReturned ? 2 : 1, memory.Freed.Count);
    }

    [Fact]
    public async Task CaptureSession_WithoutHfpArgument_NeverCallsHfpFactory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ah_hfp_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var output = Path.Combine(dir, "recording.wav");
            var partial = Path.Combine(dir, "recording.partial.wav");
            var stop = Path.Combine(dir, "stop.signal");
            var cts = new CancellationTokenSource();
            using var watcher = new StopWatcher(stop, cts.Cancel);
            var input = new FakeInput();
            var hfpFactory = new CountingHfpFactory();
            var options = new AudioHelperOptions
            {
                Mode = AudioHelperMode.Capture,
                CaptureEngine = AudioCaptureEngine.WasapiDirect,
                EndpointId = "capture",
                OutputPath = output,
                AllowedRoot = dir,
                StopSignalPath = stop,
                RecordingId = "direct"
            };
            var paths = new PathCheckResult
            {
                Ok = true,
                CanonicalPath = output,
                PartialPath = partial,
                OpenPartialStream = () => new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)
            };
            using var session = new CaptureSession(options, paths, new EventWriter(new StringWriter(), null), watcher, cts,
                _ => (input, null, null), firstPacketTimeout: TimeSpan.FromSeconds(2), hfpFactory: hfpFactory);

            var run = Task.Run(session.Run);
            Assert.True(input.Started.Wait(TimeSpan.FromSeconds(2)));
            session.RequestStop();
            Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(3))));
            Assert.Equal(0, hfpFactory.OpenCount);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task CaptureSession_AutoPairNotApplicableKeepsOrdinaryDirectCapture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ah_auto_pair_direct_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var output = Path.Combine(dir, "recording.wav");
            var partial = Path.Combine(dir, "recording.partial.wav");
            var stop = Path.Combine(dir, "stop.signal");
            var input = new FakeInput();
            var ordinaryCapture = new FakeEndpoint(DataFlow.Capture, DeviceState.Active, Guid.NewGuid(),
                new WaveFormat(16000, 16, 1), "capture")
            {
                TransportClassification = HfpTransportClassification.NotHfp
            };
            var ordinaryEnumerator = new PairEndpointEnumerator(ordinaryCapture);
            var options = new AudioHelperOptions
            {
                Mode = AudioHelperMode.Capture,
                CaptureEngine = AudioCaptureEngine.WasapiDirect,
                AutoHfpPairDiscovery = true,
                EndpointId = "capture",
                OutputPath = output,
                AllowedRoot = dir,
                StopSignalPath = stop,
                RecordingId = "auto-pair-direct"
            };
            using var cts = new CancellationTokenSource();
            using var watcher = new StopWatcher(stop, cts.Cancel);
            using var writer = new StringWriter();
            using var session = new CaptureSession(options, Paths(output, partial), new EventWriter(writer, null), watcher, cts,
                _ => (input, null, null),
                firstPacketTimeout: TimeSpan.FromSeconds(2),
                hfpPairResolver: new HfpPairResolver(() => ordinaryEnumerator));

            var run = Task.Run(session.Run);
            Assert.True(input.Started.Wait(TimeSpan.FromSeconds(2)));
            session.RequestStop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));

            var stdout = writer.ToString();
            Assert.Contains("CaptureStrategy: wasapi-direct", stdout);
            Assert.Contains("AutoHfpPairStatus: not_applicable", stdout);
            Assert.Contains("AutoHfpPairTransportClassification: not_hfp", stdout);
            Assert.Equal(1, ordinaryCapture.TransportQueryCount);
            Assert.Equal(0, ordinaryCapture.MixFormatAccessCount);
            Assert.Equal(0, ordinaryEnumerator.EnumerateRenderCount);
            Assert.DoesNotContain("hfp-duplex-prime-classic", stdout);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private sealed class CountingHfpFactory : IHfpDuplexInputFactory
    {
        public int OpenCount;
        public AudioInputOpenResult Open(string captureEndpointId, string renderEndpointId, TimeSpan budget)
        {
            Interlocked.Increment(ref OpenCount);
            return AudioInputOpenResult.Failure("audio_hfp_pair_invalid", "not expected", HfpFailureStages.PairValidation);
        }
    }

    private static PathCheckResult Paths(string output, string partial)
        => new()
        {
            Ok = true,
            CanonicalPath = output,
            PartialPath = partial,
            OpenPartialStream = () => new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)
        };

    private static EventWriter Writer() => new(new StringWriter(), null);

    private sealed class CountingPairResolver : IHfpPairResolver
    {
        private readonly string _renderEndpointId;
        public CountingPairResolver(string renderEndpointId) => _renderEndpointId = renderEndpointId;
        public int ResolveCount;
        public HfpPairDiscoveryResult Resolve(string captureEndpointId)
        {
            Interlocked.Increment(ref ResolveCount);
            return HfpPairDiscoveryResult.Paired(_renderEndpointId);
        }
    }

    private sealed class FixedPairResolver : IHfpPairResolver
    {
        private readonly HfpPairDiscoveryResult _result;
        public FixedPairResolver(HfpPairDiscoveryResult result) => _result = result;
        public HfpPairDiscoveryResult Resolve(string captureEndpointId) => _result;
    }

    private sealed class SequenceHfpFactory : IHfpDuplexInputFactory
    {
        private readonly Func<AudioInputOpenResult> _open;
        public SequenceHfpFactory(Func<AudioInputOpenResult> open) => _open = open;
        public int OpenCount;
        public string? LastRenderEndpointId;
        public AudioInputOpenResult Open(string captureEndpointId, string renderEndpointId, TimeSpan budget)
        {
            Interlocked.Increment(ref OpenCount);
            LastRenderEndpointId = renderEndpointId;
            return _open();
        }
    }

    private sealed class FakeInput : IAudioInput
    {
        public WaveFormat? Format { get; } = new WaveFormat(16000, 16, 1);
        public ManualResetEventSlim Started { get; } = new(false);
        public long DiscontinuityCount => 0;
        public int StartCount;
        public int StopCount;
        public int DataCount;
        public int DisposeCount;
        public Exception? StartException { get; set; }
        public Exception? StopException { get; set; }
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public StartRecordingResult StartRecording()
        {
            Interlocked.Increment(ref StartCount);
            if (StartException != null)
                throw StartException;
            Started.Set();
            Interlocked.Increment(ref DataCount);
            DataAvailable?.Invoke(this, new WaveInEventArgs(new byte[320], 320));
            return StartRecordingResult.Started;
        }
        public void StopRecording()
        {
            Interlocked.Increment(ref StopCount);
            if (StopException != null)
                throw StopException;
            RecordingStopped?.Invoke(this, new StoppedEventArgs());
        }
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    private sealed class FakeEndpointEnumerator : IHfpEndpointEnumerator
    {
        private readonly IHfpEndpoint _capture;
        private readonly IHfpEndpoint _render;
        public FakeEndpointEnumerator(IHfpEndpoint capture, IHfpEndpoint render) { _capture = capture; _render = render; }
        public IHfpEndpoint GetDevice(string endpointId) => endpointId == "capture" ? _capture : _render;
        public IReadOnlyList<IHfpEndpoint> EnumerateRenderEndpoints() => new[] { _render };
        public void Dispose() { }
    }

    private sealed class PairEndpointEnumerator : IHfpEndpointEnumerator
    {
        private readonly IHfpEndpoint _capture;
        private readonly IReadOnlyList<IHfpEndpoint> _renders;
        public PairEndpointEnumerator(IHfpEndpoint capture, params IHfpEndpoint[] renders)
        {
            _capture = capture;
            _renders = renders;
        }
        public int DisposeCount;
        public int EnumerateRenderCount;
        public bool ThrowOnEnumeration { get; set; }
        public bool ThrowOnDispose { get; set; }
        public IHfpEndpoint GetDevice(string endpointId) => _capture;
        public IReadOnlyList<IHfpEndpoint> EnumerateRenderEndpoints()
        {
            Interlocked.Increment(ref EnumerateRenderCount);
            if (ThrowOnEnumeration)
                throw new COMException("render enumeration failed");
            return _renders;
        }
        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCount);
            if (ThrowOnDispose)
                throw new InvalidOperationException("enumerator dispose failed");
        }
    }

    private sealed class SequencedEndpointEnumerator : IHfpEndpointEnumerator
    {
        private readonly List<string> _log;
        private readonly IHfpEndpoint _capture;
        private readonly IHfpEndpoint _render;
        public SequencedEndpointEnumerator(List<string> log, IHfpEndpoint capture, IHfpEndpoint render)
        {
            _log = log; _capture = capture; _render = render;
        }
        public IHfpEndpoint GetDevice(string endpointId)
        {
            _log.Add("resolve:" + endpointId);
            return endpointId == "capture" ? _capture : _render;
        }
        public IReadOnlyList<IHfpEndpoint> EnumerateRenderEndpoints() => new[] { _render };
        public void Dispose() { }
    }

    private sealed class FakeRenderPrimeFactory : IHfpRenderPrimeFactory
    {
        private readonly List<string> _log;
        private readonly IHfpRenderSession _session;
        public FakeRenderPrimeFactory(List<string> log, IHfpRenderSession session) { _log = log; _session = session; }
        public HfpRenderPrimeResult Prime(string renderEndpointId, TimeSpan budget)
        {
            _log.Add("render-prime:" + renderEndpointId);
            return HfpRenderPrimeResult.Success(_session);
        }
    }

    private sealed class FailingRenderPrimeFactory : IHfpRenderPrimeFactory
    {
        public HfpRenderPrimeResult Prime(string renderEndpointId, TimeSpan budget)
            => HfpRenderPrimeResult.Failure("audio_hfp_render_prime_failed", "prime failed", HfpFailureStages.RenderPrime);
    }

    private sealed class FakeRenderSession : IHfpRenderSession
    {
        private int _disposeCount;
        public FakeRenderSession(long readyMs, HfpRenderFailure? failure = null) { ReadyMs = readyMs; Failure = failure; }
        public long ReadyMs { get; }
        public HfpRenderFailure? Failure { get; set; }
        public HfpRenderFailure? RuntimeFailure => Failure;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class FakeEndpoint : IHfpEndpoint
    {
        private readonly Guid _container;
        private readonly WaveFormat _mixFormat;
        public FakeEndpoint(DataFlow flow, DeviceState state, Guid container, WaveFormat? mixFormat = null, string endpointId = "endpoint")
        {
            DataFlow = flow;
            State = state;
            _container = container;
            _mixFormat = mixFormat ?? new WaveFormat(16000, 16, 1);
            EndpointId = endpointId;
        }
        public string EndpointId { get; }
        public DataFlow DataFlow { get; }
        public DeviceState State { get; }
        public int DisposeCount;
        public bool ThrowOnContainerId { get; set; }
        public bool ThrowOnMixFormat { get; set; }
        public bool ThrowOnTransportClassification { get; set; }
        public bool ThrowOnDispose { get; set; }
        public HfpTransportClassification TransportClassification { get; set; } = HfpTransportClassification.HfpCandidate;
        public int TransportQueryCount;
        public int MixFormatAccessCount;
        public WaveFormat MixFormat
        {
            get
            {
                Interlocked.Increment(ref MixFormatAccessCount);
                if (ThrowOnMixFormat)
                    throw new COMException("mix format failed");
                return _mixFormat;
            }
        }
        public bool TryGetContainerId(out Guid containerId, out string failure)
        {
            if (ThrowOnContainerId)
                throw new COMException("container query failed");
            containerId = _container;
            if (_container == Guid.Empty)
            {
                failure = "HFP endpoint ContainerId is missing or empty";
                return false;
            }
            failure = "";
            return true;
        }
        public bool TryGetTransportClassification(out HfpTransportClassification classification, out string failure)
        {
            Interlocked.Increment(ref TransportQueryCount);
            if (ThrowOnTransportClassification)
                throw new COMException("transport property query failed");
            classification = TransportClassification;
            failure = "";
            return true;
        }
        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCount);
            if (ThrowOnDispose)
                throw new InvalidOperationException("endpoint dispose failed");
        }
    }

    private sealed class FakeRawEndpoint
    {
        public int DisposeCount;
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    private sealed class FakeWrappedEndpoint
    {
        private readonly FakeRawEndpoint _raw;
        public FakeWrappedEndpoint(FakeRawEndpoint raw) => _raw = raw;
        public int DisposeCount;
        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCount);
            _raw.Dispose();
        }
    }

    private sealed class FakeDevice : IDevice
    {
        public WaveFormat Format { get; } = new WaveFormat(16000, 16, 1);
        public Queue<FakeAudioClient> Clients { get; } = new();
        public DeviceState State => DeviceState.Active;
        public int DisposeCount;
        public IAudioClient CreateAudioClient() => Clients.Dequeue();
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    private sealed class FakeAudioClient : IAudioClient, IEventDrivenAudioClient
    {
        private readonly WaveFormat _format;
        public FakeAudioClient(WaveFormat format) { _format = format; }
        public WaveFormat MixFormat
        {
            get
            {
                if (MixFormatException != null) throw MixFormatException;
                return _format;
            }
        }
        public int BufferSize
        {
            get
            {
                if (BufferSizeException != null) throw BufferSizeException;
                return 160;
            }
        }
        public Exception? MixFormatException { get; set; }
        public Exception? BufferSizeException { get; set; }
        public int DisposeCount;
        public AudioClientStreamFlags InitializeFlags { get; private set; }
        public long BufferDuration { get; private set; }
        public long Periodicity { get; private set; }
        public WaveFormat? InitializeFormat { get; private set; }
        public int SetEventHandleCount { get; private set; }
        public int StartCount { get; private set; }
        public FakeAudioCaptureClient? CaptureClient { get; set; }
        public void Initialize(AudioClientShareMode shareMode, AudioClientStreamFlags streamFlags, long bufferDuration, long periodicity, WaveFormat format, Guid audioSessionGuid)
        {
            InitializeFlags = streamFlags; BufferDuration = bufferDuration; Periodicity = periodicity; InitializeFormat = format;
        }
        public void SetEventHandle(IntPtr eventHandle) => SetEventHandleCount++;
        public void Start() => StartCount++;
        public void Stop() { }
        public IAudioCaptureClient GetAudioCaptureClient() => CaptureClient ?? new FakeAudioCaptureClient();
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    private sealed class FakeAudioCaptureClient : IAudioCaptureClient
    {
        public int DisposeCount;
        public int GetNextPacketSize() => 0;
        public IntPtr GetBuffer(out int framesAvailable, out AudioClientBufferFlags flags) { framesAvailable = 0; flags = 0; return IntPtr.Zero; }
        public void ReleaseBuffer(int framesRead) { }
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    private sealed class FakeRenderActivationClient : IHfpRenderActivationClient
    {
        private readonly List<string> _log;
        private int _disposeCount;
        public FakeRenderActivationClient(List<string>? log = null)
        {
            _log = log ?? new List<string>();
            CallThreadIds = new List<int>();
            RenderBuffer = new FakeRenderBuffer(_log, CallThreadIds);
        }

        public List<int> CallThreadIds { get; }

        private void Record(string operation)
        {
            lock (CallThreadIds)
                CallThreadIds.Add(Environment.CurrentManagedThreadId);
            lock (_log)
                _log.Add(operation);
        }

        public WaveFormat MixFormat
        {
            get
            {
                Record("get_mix_format");
                if (MixFormatException != null) throw MixFormatException;
                return new WaveFormat(16000, 16, 1);
            }
        }

        public int BufferSize
        {
            get
            {
                Record("buffer_size");
                if (BufferSizeException != null) throw BufferSizeException;
                return BufferSizeValue;
            }
        }

        public int CurrentPadding
        {
            get
            {
                Record("current_padding");
                if (CurrentPaddingException != null) throw CurrentPaddingException;
                return CurrentPaddingValue;
            }
        }

        public int BufferSizeValue { get; set; } = 160;
        public int CurrentPaddingValue { get; set; }
        public AudioClientProperties Properties { get; private set; }
        public Exception? SetClientPropertiesException { get; set; }
        public Exception? MixFormatException { get; set; }
        public Exception? BufferSizeException { get; set; }
        public Exception? CurrentPaddingException { get; set; }
        public Exception? InitializeException { get; set; }
        public Exception? SetEventHandleException { get; set; }
        public Exception? GetRenderBufferException { get; set; }
        public Exception? StartException { get; set; }
        public Exception? StopException { get; set; }
        public FakeRenderBuffer RenderBuffer { get; }
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void SetClientProperties(AudioClientProperties properties)
        {
            Record("set_properties");
            Properties = properties;
            if (SetClientPropertiesException != null) throw SetClientPropertiesException;
        }

        public bool IsFormatSupported(AudioClientShareMode shareMode, WaveFormat format)
        {
            Record("is_format_supported");
            return true;
        }

        public void Initialize(AudioClientShareMode shareMode, AudioClientStreamFlags streamFlags, long bufferDuration,
            long periodicity, WaveFormat format, Guid audioSessionGuid)
        {
            Record("initialize");
            if (InitializeException != null) throw InitializeException;
        }

        public void SetEventHandle(IntPtr eventHandle)
        {
            Record("set_event_handle");
            if (SetEventHandleException != null) throw SetEventHandleException;
        }

        public IHfpRenderBuffer GetRenderBuffer()
        {
            Record("get_render_client");
            if (GetRenderBufferException != null) throw GetRenderBufferException;
            return RenderBuffer;
        }

        public void Start()
        {
            Record("start");
            if (StartException != null) throw StartException;
        }

        public void Stop()
        {
            Record("stop");
            if (StopException != null) throw StopException;
        }
        public void Dispose()
        {
            Record("client_release");
            Interlocked.Increment(ref _disposeCount);
        }
    }

    private sealed class FakeRenderBuffer : IHfpRenderBuffer
    {
        private readonly List<string> _log;
        private readonly List<int> _callThreadIds;
        private int _disposeCount;
        public FakeRenderBuffer(List<string> log, List<int> callThreadIds)
        {
            _log = log;
            _callThreadIds = callThreadIds;
        }
        public Exception? GetBufferException { get; set; }
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        private void Record(string operation)
        {
            lock (_callThreadIds)
                _callThreadIds.Add(Environment.CurrentManagedThreadId);
            lock (_log)
                _log.Add(operation);
        }

        public IntPtr GetBuffer(int framesRequested)
        {
            Record("get_buffer");
            if (GetBufferException != null) throw GetBufferException;
            return IntPtr.Zero;
        }

        public void ReleaseBuffer(int framesWritten, AudioClientBufferFlags flags)
        {
            Record(flags == AudioClientBufferFlags.Silent ? "release_buffer_silent" : "release_buffer");
        }

        public void Dispose()
        {
            Record("render_release");
            Interlocked.Increment(ref _disposeCount);
        }
    }

    private sealed class ImmediateRenderActivationFactory : IHfpRenderActivationFactory
    {
        private readonly IHfpRenderActivationClient _client;
        public ImmediateRenderActivationFactory(IHfpRenderActivationClient client) => _client = client;
        public IHfpRenderActivationClient Activate(string endpointId) => _client;
    }

    private sealed class BlockingRenderActivationFactory : IHfpRenderActivationFactory
    {
        private readonly IHfpRenderActivationClient _client;
        public BlockingRenderActivationFactory(IHfpRenderActivationClient client) => _client = client;
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        public IHfpRenderActivationClient Activate(string endpointId)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(2));
            return _client;
        }
    }

    private sealed class FakeNativeRenderApi : IHfpNativeRenderApi
    {
        private readonly List<string> _log;
        private readonly IHfpNativeRenderDevice _device;
        private readonly Exception? _getDeviceException;

        public FakeNativeRenderApi(List<string> log, IHfpNativeRenderDevice device,
            Exception? getDeviceException = null)
        {
            _log = log;
            _device = device;
            _getDeviceException = getDeviceException;
        }

        public string? EndpointId { get; private set; }
        public int GetDeviceCount { get; private set; }

        public IHfpNativeRenderDevice GetDevice(string endpointId)
        {
            EndpointId = endpointId;
            GetDeviceCount++;
            lock (_log)
                _log.Add("GetDevice:" + endpointId);
            if (_getDeviceException != null)
                throw _getDeviceException;
            return _device;
        }
    }

    private sealed class FakeNativeRenderDevice : IHfpNativeRenderDevice
    {
        private readonly List<string> _log;
        private readonly IHfpRenderActivationClient _client;
        private readonly Exception? _activationException;
        private int _disposeCount;

        public FakeNativeRenderDevice(List<string> log, IHfpRenderActivationClient client,
            Exception? activationException = null)
        {
            _log = log;
            _client = client;
            _activationException = activationException;
        }

        public Guid ActivationIid { get; private set; }
        public uint ActivationClsContext { get; private set; }
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public IHfpRenderActivationClient Activate(Guid iid, uint clsContext)
        {
            ActivationIid = iid;
            ActivationClsContext = clsContext;
            lock (_log)
                _log.Add("Activate(IAudioClient2)");
            if (_activationException != null)
                throw _activationException;
            return _client;
        }

        public void Dispose()
        {
            lock (_log)
                _log.Add("device_release");
            Interlocked.Increment(ref _disposeCount);
        }
    }

    private sealed class FakeComApartmentFactory : IHfpComApartmentFactory
    {
        private readonly List<string> _log;
        private readonly Exception? _failure;
        public FakeComApartmentFactory(List<string> log, Exception? failure = null)
        {
            _log = log;
            _failure = failure;
        }

        public int InitThreadId { get; private set; }
        public int UninitThreadId { get; private set; }

        public IHfpComApartment Enter()
        {
            if (_failure != null)
                throw _failure;
            InitThreadId = Environment.CurrentManagedThreadId;
            lock (_log)
                _log.Add("com_init");
            return new FakeComApartment(_log, this, InitThreadId);
        }

        private sealed class FakeComApartment : IHfpComApartment
        {
            private readonly List<string> _log;
            private readonly FakeComApartmentFactory _owner;
            private int _disposed;

            public FakeComApartment(List<string> log, FakeComApartmentFactory owner, int threadId)
            {
                _log = log;
                _owner = owner;
                ThreadId = threadId;
            }

            public int ThreadId { get; }

            public void Dispose()
            {
                Assert.Equal(ThreadId, Environment.CurrentManagedThreadId);
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _owner.UninitThreadId = Environment.CurrentManagedThreadId;
                    lock (_log)
                        _log.Add("com_uninit");
                }
            }
        }
    }

    private sealed class FakeNativeAudioClient2 : IHfpNativeAudioClient2
    {
        public int FormatSupportHresult { get; set; }
        public IntPtr ClosestMatch { get; set; }
        public int DisposeCount { get; private set; }
        public int SetClientProperties(ref AudioClientProperties properties) => 0;
        public int GetMixFormat(out IntPtr format) { format = IntPtr.Zero; return 0; }
        public int IsFormatSupported(AudioClientShareMode shareMode, IntPtr format, out IntPtr closestMatch)
        {
            closestMatch = ClosestMatch;
            return FormatSupportHresult;
        }
        public int GetBufferSize(out int numBufferFrames) { numBufferFrames = 160; return 0; }
        public int GetCurrentPadding(out int numPaddingFrames) { numPaddingFrames = 0; return 0; }
        public int Initialize(AudioClientShareMode shareMode, AudioClientStreamFlags streamFlags,
            long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid) => 0;
        public int SetEventHandle(IntPtr eventHandle) => 0;
        public int GetService(ref Guid iid, out object service) { service = new object(); return 0; }
        public int Start() => 0;
        public int Stop() => 0;
        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeWaveFormatMemory : IHfpWaveFormatMemory
    {
        private int _nextPointer = 100;
        public List<IntPtr> Freed { get; } = new();
        public IntPtr MarshalToPtr(WaveFormat format) => new(++_nextPointer);
        public WaveFormat MarshalFromPtr(IntPtr format) => new(16000, 16, 1);
        public void Free(IntPtr pointer) => Freed.Add(pointer);
    }
}
