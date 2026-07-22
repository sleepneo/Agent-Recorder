#pragma once

#include <chrono>
#include <cstdint>
#include <functional>

namespace wgc {

// Snapshot of capture progress at a point in time. Values are expected to be
// monotonic (non-decreasing) for frames/bytes and elapsedMs.
struct ProgressSnapshot {
    int64_t framesCaptured = 0;
    int64_t framesDropped = 0;
    int64_t elapsedMs = 0;
    int64_t bytesWritten = 0;
};

// Schedules IPC v2 PROGRESS events after a STARTED event. The scheduler is
// deliberately simple and stateless regarding the capture itself: the caller
// provides fresh snapshots and the scheduler decides whether enough time has
// passed and whether the values have changed enough to emit another PROGRESS.
class ProgressScheduler {
public:
    explicit ProgressScheduler(std::function<void(const ProgressSnapshot&)> emitProgress,
                               std::chrono::milliseconds interval);

    // Records the single capture begin time. Must be called before any
    // PROGRESS can be emitted.
    void Start(int64_t beginTimeMs);

    // Possibly emits a PROGRESS event if the interval has elapsed and the
    // snapshot shows progress since the last emission.
    void MaybeEmit(const ProgressSnapshot& snapshot);

    // Stops the scheduler. Subsequent MaybeEmit calls are ignored.
    void Stop();

    bool HasStarted() const { return started_; }
    bool HasEmittedProgress() const { return hasEmittedProgress_; }

    // Returns the earliest time at which the next PROGRESS may be emitted.
    // Useful for sleeping until the next scheduling point.
    std::chrono::steady_clock::time_point NextProgressTime() const { return nextProgressTime_; }

private:
    std::function<void(const ProgressSnapshot&)> emitProgress_;
    std::chrono::milliseconds interval_;

    bool started_ = false;
    bool stopped_ = false;
    bool hasEmittedProgress_ = false;

    int64_t beginTimeMs_ = 0;
    std::chrono::steady_clock::time_point nextProgressTime_;
    int64_t lastFrames_ = -1;
    int64_t lastBytes_ = -1;
};

} // namespace wgc
