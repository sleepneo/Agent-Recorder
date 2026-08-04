#pragma once

#include <windows.h>

#include "begin_gate.h"
#include "capture_lifecycle.h"
#include "event_writer.h"
#include "frame_queue.h"
#include "options.h"
#include "progress_scheduler.h"
#include "video_encoder.h"

#include <atomic>
#include <cstdint>
#include <functional>
#include <memory>
#include <string>
#include <vector>

namespace wgc {

enum class CaptureResult {
    Success,
    Stopped,
    Failed
};

struct CaptureOutcome {
    CaptureResult result = CaptureResult::Failed;
    std::string errorCode;
    std::string reason;
    std::string hresult;
    int64_t framesCaptured = 0;
    int64_t framesDropped = 0;
    int64_t durationMs = 0;
    int64_t bytesWritten = 0;
    int width = 0;
    int height = 0;
    std::wstring partialOutputPath;
};

// Emit the terminal IPC event (OK / STOPPED / FAIL) from a complete outcome.
// This is the single production mapping from CaptureOutcome to EventWriter;
// it is shared by main.cpp (the terminal owner) and by tests that drive
// CaptureSession directly.
void WriteTerminalOutcome(EventWriter& writer, const CaptureOutcome& outcome);

// Normalize failure evidence for a Failed outcome using the canonical partial
// path from this capture. Trusts only the disk state of the canonical partial
// file: non-empty partials are reported with real size; empty placeholders are
// deleted and omitted. Success / Stopped outcomes are returned unchanged.
CaptureOutcome NormalizeFailureEvidence(CaptureOutcome outcome,
                                        const std::wstring& canonicalPartialPath);

// Test-only seams for verifying production wiring without starting a real WGC
// capture. When a hook is empty the production behavior is used unchanged.
struct CaptureSessionTestHooks {
    // Test-hook sessions use an instance-owned synthetic platform resource
    // seam so orchestration tests do not require GraphicsCaptureItem to be
    // available in the test host. Production sessions never set test hooks
    // and always use the real WGC path. The real platform path remains covered
    // by the bounded helper probe/integration tests.
    bool useSyntheticPlatformResources = false;

    // Called instead of GraphicsCaptureSession::StartCapture(). Throwing here
    // must surface as a fast, bounded start_capture_failed outcome.
    std::function<void()> onStartCapture;

    // Called instead of CopyFrameToBgra. Return true to provide a synthetic
    // BGRA buffer; return false to simulate a GPU copy failure. The test can
    // count invocations and vary the buffer to exercise late-failure evidence.
    std::function<bool(const QueuedFrame& qf,
                       int destWidth,
                       int destHeight,
                       std::vector<uint8_t>& outPixels)> onCopyFrame;

    // Called after the coordinator transitions to CaptureActive. The test can
    // push frames into the supplied queue to drive the encoder worker.
    std::function<void(FrameQueue& queue)> onCaptureActive;

    // Called instead of encoder.Initialize. Return a non-Ok result to exercise
    // the encoder_init_failed production path without relying on a real sink
    // writer failure.
    std::function<EncoderResult(int width, int height, int fps,
                                const std::wstring& outputPath)> onEncoderInitialize;

    // Called instead of encoder.WriteFrame. Used to exercise late-failure
    // evidence without depending on a real Media Foundation encode.
    std::function<EncoderResult(const std::vector<uint8_t>& bgraPixels,
                                int64_t timestampHns,
                                int64_t durationHns)> onWriteFrame;

    // Called instead of encoder.Finalize.
    std::function<EncoderResult()> onFinalize;

    // Called whenever the main loop emits a progress snapshot. Tests can use
    // this seam to observe the same atomic counters that are used for terminal
    // outcomes without relying on std::cout races.
    std::function<void(const ProgressSnapshot&)> onProgressSnapshot;

    // Called immediately after the STARTED event is emitted. Tests can use this
    // to know when capture is active without parsing redirected stdout.
    std::function<void()> onStarted;

    // Production code reads the partial file size for progress and failure
    // evidence. Tests that do not use a real encoder can supply a deterministic
    // value, but it must still represent "current file size" semantics.
    std::function<int64_t()> onGetOutputBytes;

    // Called exactly once when the terminal RAII guard is about to invoke
    // MarkCaptureEnded(). Used to verify the exactly-once guarantee.
    std::function<void()> onMarkCaptureEnded;

    // Called immediately after the internal pipeline state is created. The test
    // receives a pointer to the lifecycle barrier and an opaque handle that
    // keeps the shared state alive until the test releases it. This allows tests
    // to hold an active callback during teardown without depending on real WGC
    // events.
    std::function<void(CaptureLifecycle*, const std::shared_ptr<void>&)> onStateCreated;
};

// Encapsulates a single display continuous capture session.
// The caller must create the BeginGate and pass it in; StartCapture is only
// called after the gate has authorized begin.
class CaptureSession {
public:
    CaptureSession(const Options& options,
                   const std::wstring& outputPath,
                   const std::wstring& partialOutputPath,
                   BeginGate& gate,
                   EventWriter& writer);

    CaptureOutcome Run();

    // Test-only. Must be called before Run().
    void SetTestHooks(const CaptureSessionTestHooks& hooks);

private:
    Options options_;
    std::wstring outputPath_;
    std::wstring partialOutputPath_;
    BeginGate& gate_;
    EventWriter& writer_;
    CaptureSessionTestHooks hooks_;
};

} // namespace wgc
