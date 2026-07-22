#include "frame_queue.h"

#include <winrt/Windows.Foundation.h>

namespace wgc {

FrameQueue::FrameQueue(size_t maxSize) : maxSize_(maxSize == 0 ? 1 : maxSize) {}

bool FrameQueue::Push(QueuedFrame& frame) {
    // Reject new frames once shutdown has been requested. The caller retains
    // ownership of |frame| and must close/release it.
    if (!accepting_.load()) {
        return false;
    }

    std::unique_lock<std::mutex> lock(mutex_);
    if (shutdown_ || !accepting_) {
        return false;
    }

    if (queue_.size() >= maxSize_) {
        // Drop the oldest frame to make room. Close it so the frame pool can
        // reuse the surface.
        QueuedFrame old = std::move(queue_.front());
        queue_.pop_front();
        if (old.frame) {
            try {
                old.frame.Close();
            } catch (...) {
            }
        }
        dropped_.fetch_add(1);
    }

    queue_.push_back(std::move(frame));
    cv_.notify_one();
    return true;
}

bool FrameQueue::Pop(QueuedFrame& frame, std::chrono::milliseconds timeout) {
    std::unique_lock<std::mutex> lock(mutex_);
    if (!cv_.wait_for(lock, timeout, [this] { return shutdown_ || !queue_.empty(); })) {
        return false;
    }
    if (queue_.empty()) {
        return false;
    }

    frame = std::move(queue_.front());
    queue_.pop_front();
    return true;
}

void FrameQueue::Shutdown() {
    std::deque<QueuedFrame> local;
    {
        std::unique_lock<std::mutex> lock(mutex_);
        accepting_.store(false);
        shutdown_ = true;
        local = std::move(queue_);
        queue_.clear();
    }
    cv_.notify_all();

    for (auto& qf : local) {
        if (qf.frame) {
            try {
                qf.frame.Close();
            } catch (...) {
            }
        }
    }
}

} // namespace wgc
