#pragma once

#include <atomic>
#include <chrono>
#include <thread>

namespace wgc {

// Tracks whether frame callbacks are still allowed and how many are currently
// executing. Used to implement a teardown barrier: after stopping acceptance,
// the owner waits for in-flight callbacks to finish before destroying shared
// state.
struct CaptureLifecycle {
    std::atomic<bool> acceptingFrames{true};
    std::atomic<int32_t> activeCallbacks{0};

    // Stop accepting new frame callbacks.
    void StopAccepting() { acceptingFrames.store(false); }

    // Try to enter a frame callback. Returns false if acceptance has stopped.
    bool TryEnterCallback() {
        if (!acceptingFrames.load(std::memory_order_acquire)) {
            return false;
        }
        activeCallbacks.fetch_add(1, std::memory_order_acq_rel);
        if (!acceptingFrames.load(std::memory_order_acquire)) {
            activeCallbacks.fetch_sub(1, std::memory_order_acq_rel);
            return false;
        }
        return true;
    }

    void ExitCallback() {
        activeCallbacks.fetch_sub(1, std::memory_order_acq_rel);
    }

    // Wait up to timeout for all active callbacks to exit. Returns false on
    // timeout; the caller should still proceed with teardown to avoid hanging.
    bool WaitForCallbacks(std::chrono::milliseconds timeout) {
        const auto deadline = std::chrono::steady_clock::now() + timeout;
        while (activeCallbacks.load(std::memory_order_acquire) > 0) {
            if (std::chrono::steady_clock::now() >= deadline) {
                return false;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
        return true;
    }
};

// RAII guard that enters/exits a frame callback.
class CallbackGuard {
public:
    explicit CallbackGuard(CaptureLifecycle& lifecycle)
        : lifecycle_(lifecycle), entered_(lifecycle.TryEnterCallback()) {}

    ~CallbackGuard() {
        if (entered_) {
            lifecycle_.ExitCallback();
        }
    }

    bool Entered() const { return entered_; }

    CallbackGuard(const CallbackGuard&) = delete;
    CallbackGuard& operator=(const CallbackGuard&) = delete;

private:
    CaptureLifecycle& lifecycle_;
    bool entered_;
};

} // namespace wgc
