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


}
