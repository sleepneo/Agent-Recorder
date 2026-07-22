#pragma once

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <mutex>
#include <string>

namespace wgc {

enum class StartupState {
    Idle,
    BeginAuthorized,
    EncoderInitializing,
    EncoderReady,
    EncoderFailed,
    CaptureStarted,
    CaptureActive,
    Stopping,
    Stopped
};

enum class EncoderInitStatus {
    Ready,
    Failed,
    TimedOut,
    Stopped
};

struct EncoderInitResult {
    EncoderInitStatus status = EncoderInitStatus::TimedOut;
    std::string error;
    std::string hresult;
};

// Coordinates the handshake between the main thread, the capture callback, and
// the encoder worker so that:
//   - the encoder is initialized on its own thread before StartCapture is called,
//   - begin time is recorded strictly after encoder ready and before StartCapture,
//   - STARTED is emitted before any PROGRESS,
//   - the worker cannot see a default/zero begin time,
//   - encoder failures are distinguished from timeouts.
class StartupCoordinator {
public:
    StartupCoordinator();

    // Main thread: called after BeginGate has verified the token. Wakes the
    // encoder worker to initialize the sink writer.
    void AuthorizeBegin();

    // Worker thread: signals that encoder initialization succeeded and the sink
    // writer is ready for frames.
    void SignalEncoderReady();

    // Worker thread: signals that encoder initialization failed. The main
    // thread will not call StartCapture.
    void SignalEncoderFailed(const std::string& error, const std::string& hresult);

    // Main thread: records the single begin/capture clock and transitions to
    // CaptureActive. Must be called after StartCapture has returned successfully.
    void SignalCaptureStarted(int64_t beginTimeMs);

    // Main thread: request stop from outside (e.g., stop signal, size change).
    void RequestStop();

    // Main thread: marks capture as definitively ended and wakes waiters.
    void MarkCaptureEnded();

    // Worker thread waits.
    bool WaitForBeginAuthorization(std::chrono::milliseconds timeout);
    bool WaitForCaptureActive(std::chrono::milliseconds timeout);

    // Main thread waits for the encoder to become ready, fail, or stop.
    // Returns a typed result so caller can distinguish failure from timeout.
    EncoderInitResult WaitForEncoderInit(std::chrono::milliseconds timeout);

    StartupState State() const { return state_.load(); }
    bool IsFailed() const { return state_.load() == StartupState::EncoderFailed; }
    std::string EncoderError() const { return encoderError_; }
    std::string EncoderHresult() const { return encoderHresult_; }
    int64_t BeginTimeMs() const { return beginTimeMs_.load(); }

private:
    std::mutex mutex_;
    std::condition_variable cv_;
    std::atomic<StartupState> state_{StartupState::Idle};
    std::atomic<int64_t> beginTimeMs_{0};
    std::string encoderError_;
    std::string encoderHresult_;
};

} // namespace wgc
