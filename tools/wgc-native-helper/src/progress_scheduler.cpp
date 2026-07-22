#include "progress_scheduler.h"

namespace wgc {

ProgressScheduler::ProgressScheduler(std::function<void(const ProgressSnapshot&)> emitProgress,
                                     std::chrono::milliseconds interval)
    : emitProgress_(std::move(emitProgress)), interval_(interval) {}

void ProgressScheduler::Start(int64_t beginTimeMs) {
    started_ = true;
    stopped_ = false;
    hasEmittedProgress_ = false;
    beginTimeMs_ = beginTimeMs;
    nextProgressTime_ = std::chrono::steady_clock::now() + interval_;
    lastFrames_ = -1;
    lastBytes_ = -1;
}

void ProgressScheduler::MaybeEmit(const ProgressSnapshot& snapshot) {
    if (!started_ || stopped_) {
        return;
    }

    const auto now = std::chrono::steady_clock::now();
    if (now < nextProgressTime_) {
        return;
    }

    if (snapshot.framesCaptured > lastFrames_ || snapshot.bytesWritten > lastBytes_) {
        emitProgress_(snapshot);
        hasEmittedProgress_ = true;
        lastFrames_ = snapshot.framesCaptured;
        lastBytes_ = snapshot.bytesWritten;
    }

    nextProgressTime_ = now + interval_;
}

void ProgressScheduler::Stop() {
    stopped_ = true;
}

} // namespace wgc
