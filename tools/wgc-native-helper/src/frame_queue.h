#pragma once

#include <windows.h>

#include <winrt/Windows.Graphics.Capture.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <mutex>

namespace wgc {

// A queued frame retains the WGC Direct3D11CaptureFrame so the frame pool
// checkout lifetime extends until the worker has finished the GPU->CPU copy.
struct QueuedFrame {
    winrt::Windows::Graphics::Capture::Direct3D11CaptureFrame frame{nullptr};
    int64_t systemRelativeTimeHns = 0;
    int32_t contentWidth = 0;
    int32_t contentHeight = 0;
};

// Thread-safe bounded queue for WGC frames. Push is called from the capture
// callback; Pop from the encoder worker. Shutdown rejects further pushes and
// releases any frames still in the queue.
class FrameQueue {
public:
    explicit FrameQueue(size_t maxSize);

    // Attempts to enqueue the frame. If accepted, the frame is moved out of
    // |frame| and the caller must not touch it again. If rejected (queue full
    // and an old frame was dropped to make room, or shutdown), |frame| is left
    // untouched and the caller retains ownership and must close/release it.
    // Returns true iff the frame was accepted.
    bool Push(QueuedFrame& frame);

    // Pops a frame. Returns false on shutdown or timeout. The caller owns the
    // returned frame and must close/release it.
    bool Pop(QueuedFrame& frame, std::chrono::milliseconds timeout);

    // Stops accepting new frames, wakes all waiters, and closes queued frames.
    void Shutdown();

    int64_t Dropped() const { return dropped_.load(); }

    bool IsAccepting() const { return accepting_.load(); }

private:
    const size_t maxSize_;
    std::mutex mutex_;
    std::condition_variable cv_;
    std::deque<QueuedFrame> queue_;
    std::atomic<int64_t> dropped_{0};
    std::atomic<bool> accepting_{true};
    bool shutdown_ = false;
};

} // namespace wgc
