#include "stop_watcher.h"

#include "path_policy.h"

#include <windows.h>

#include <chrono>
#include <thread>

namespace wgc {

StopSignalWatcher::StopSignalWatcher(std::wstring path,
                                     std::atomic<bool>& stopFlag,
                                     std::atomic<bool>& userStopFlag,
                                     std::function<void()> onTriggered)
    : path_(CanonicalPath(path)),
      stopFlag_(stopFlag),
      userStopFlag_(userStopFlag),
      onTriggered_(std::move(onTriggered)) {}

void StopSignalWatcher::Start() {
    if (path_.empty()) return;
    thread_ = std::thread([this]() {
        while (!cancelled_.load()) {
            const DWORD attribs = ::GetFileAttributesW(path_.c_str());
            if (attribs != INVALID_FILE_ATTRIBUTES && (attribs & FILE_ATTRIBUTE_DIRECTORY) == 0) {
                triggered_.store(true);
                userStopFlag_.store(true);
                stopFlag_.store(true);
                if (onTriggered_) {
                    try {
                        onTriggered_();
                    } catch (...) {
                        // The callback must not throw; swallow to keep the
                        // watcher thread from terminating unexpectedly.
                    }
                }
                return;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(50));
        }
    });
}

void StopSignalWatcher::Stop() {
    cancelled_.store(true);
    if (thread_.joinable()) thread_.join();
}

} // namespace wgc
