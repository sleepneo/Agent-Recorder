#pragma once

#include <atomic>
#include <functional>
#include <string>
#include <thread>

namespace wgc {

// Watches for a stop signal file after capture has begun. The first time the
// file is observed, both stop flags are set, the optional onTriggered callback
// is invoked, and the watcher thread exits.
class StopSignalWatcher {
public:
    StopSignalWatcher(std::wstring path,
                      std::atomic<bool>& stopFlag,
                      std::atomic<bool>& userStopFlag,
                      std::function<void()> onTriggered = nullptr);

    void Start();
    void Stop();

    // For testing: true if the watcher observed the stop file.
    bool Triggered() const { return triggered_.load(); }

    // Exposes the resolved path the watcher is polling. Used to verify that
    // the runtime uses the canonical path, not a raw CLI alias.
    const std::wstring& Path() const { return path_; }

private:
    std::wstring path_;
    std::atomic<bool>& stopFlag_;
    std::atomic<bool>& userStopFlag_;
    std::function<void()> onTriggered_;
    std::atomic<bool> cancelled_{false};
    std::atomic<bool> triggered_{false};
    std::thread thread_;
};

} // namespace wgc
