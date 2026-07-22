#include "startup_coordinator.h"

namespace wgc {

StartupCoordinator::StartupCoordinator() = default;

void StartupCoordinator::AuthorizeBegin() {
    {
        std::unique_lock<std::mutex> lock(mutex_);
        if (state_.load() != StartupState::Idle) return;
        state_.store(StartupState::BeginAuthorized);
        state_.store(StartupState::EncoderInitializing);
    }
    cv_.notify_all();
}

void StartupCoordinator::SignalEncoderReady() {
    {
        std::unique_lock<std::mutex> lock(mutex_);
        if (state_.load() != StartupState::EncoderInitializing) return;
        state_.store(StartupState::EncoderReady);
    }
    cv_.notify_all();
}

void StartupCoordinator::SignalEncoderFailed(const std::string& error,
                                             const std::string& hresult) {
    {
        std::unique_lock<std::mutex> lock(mutex_);
        if (state_.load() != StartupState::EncoderInitializing) return;
        encoderError_ = error;
        encoderHresult_ = hresult;
        state_.store(StartupState::EncoderFailed);
    }
    cv_.notify_all();
}

void StartupCoordinator::SignalCaptureStarted(int64_t beginTimeMs) {
    {
        std::unique_lock<std::mutex> lock(mutex_);
        if (state_.load() != StartupState::EncoderReady) return;
        beginTimeMs_.store(beginTimeMs);
        state_.store(StartupState::CaptureActive);
    }
    cv_.notify_all();
}

void StartupCoordinator::RequestStop() {
    StartupState expected = StartupState::CaptureActive;
    if (state_.compare_exchange_strong(expected, StartupState::Stopping)) {
        cv_.notify_all();
        return;
    }

    // Allow stop to be requested while the encoder is still initializing or
    // ready but not yet active. This prevents a long encoder-init timeout from
    // delaying a user/external stop decision.
    expected = StartupState::EncoderReady;
    if (state_.compare_exchange_strong(expected, StartupState::Stopping)) {
        cv_.notify_all();
        return;
    }

    expected = StartupState::EncoderInitializing;
    if (state_.compare_exchange_strong(expected, StartupState::Stopping)) {
        cv_.notify_all();
        return;
    }

    expected = StartupState::BeginAuthorized;
    if (state_.compare_exchange_strong(expected, StartupState::Stopping)) {
        cv_.notify_all();
        return;
    }

    // If already stopped or failed, still wake any waiters.
    cv_.notify_all();
}

void StartupCoordinator::MarkCaptureEnded() {
    {
        std::unique_lock<std::mutex> lock(mutex_);
        state_.store(StartupState::Stopped);
    }
    cv_.notify_all();
}

bool StartupCoordinator::WaitForBeginAuthorization(std::chrono::milliseconds timeout) {
    std::unique_lock<std::mutex> lock(mutex_);
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    while (state_.load() == StartupState::Idle) {
        if (cv_.wait_until(lock, deadline) == std::cv_status::timeout) {
            return false;
        }
    }
    return state_.load() != StartupState::Idle;
}

bool StartupCoordinator::WaitForCaptureActive(std::chrono::milliseconds timeout) {
    std::unique_lock<std::mutex> lock(mutex_);
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    while (state_.load() != StartupState::CaptureActive &&
           state_.load() != StartupState::Stopping &&
           state_.load() != StartupState::Stopped &&
           state_.load() != StartupState::EncoderFailed) {
        if (cv_.wait_until(lock, deadline) == std::cv_status::timeout) {
            return false;
        }
    }
    return state_.load() == StartupState::CaptureActive;
}

EncoderInitResult StartupCoordinator::WaitForEncoderInit(std::chrono::milliseconds timeout) {
    std::unique_lock<std::mutex> lock(mutex_);
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    while (state_.load() != StartupState::EncoderReady &&
           state_.load() != StartupState::EncoderFailed &&
           state_.load() != StartupState::Stopping &&
           state_.load() != StartupState::Stopped) {
        if (cv_.wait_until(lock, deadline) == std::cv_status::timeout) {
            EncoderInitResult result;
            result.status = EncoderInitStatus::TimedOut;
            return result;
        }
    }

    EncoderInitResult result;
    const auto state = state_.load();
    if (state == StartupState::EncoderReady) {
        result.status = EncoderInitStatus::Ready;
    } else if (state == StartupState::EncoderFailed) {
        result.status = EncoderInitStatus::Failed;
        result.error = encoderError_;
        result.hresult = encoderHresult_;
    } else {
        result.status = EncoderInitStatus::Stopped;
    }
    return result;
}

} // namespace wgc
