#include "begin_gate.h"

#include "string_utils.h"

#include <windows.h>
#include <chrono>
#include <fstream>
#include <sstream>
#include <thread>

namespace wgc {

namespace {

std::wstring TrimNewlines(std::wstring text) {
    while (!text.empty() && (text.back() == L'\n' || text.back() == L'\r')) {
        text.pop_back();
    }
    return text;
}

} // namespace

std::wstring ReadSignalFile(std::wstring_view path) {
    const std::wstring p(path);
    std::wifstream file(p, std::ios::binary);
    if (!file.is_open()) return {};
    std::wostringstream oss;
    oss << file.rdbuf();
    return TrimNewlines(oss.str());
}

bool SignalFileExists(std::wstring_view path) {
    if (path.empty()) return false;
    const DWORD attribs = ::GetFileAttributesW(path.data());
    return attribs != INVALID_FILE_ATTRIBUTES && (attribs & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

BeginGate::BeginGate(std::wstring beginSignalPath,
                     std::wstring expectedToken,
                     std::wstring stopSignalPath,
                     int timeoutMs)
    : beginSignalPath_(std::move(beginSignalPath)),
      expectedToken_(std::move(expectedToken)),
      stopSignalPath_(std::move(stopSignalPath)),
      timeoutMs_(timeoutMs) {}

void BeginGate::Cancel() {
    cancelled_.store(true);
}

BeginGateResult BeginGate::WaitAndRun(StartCallback onStart) {
    if (started_.exchange(true)) {
        return BeginGateResult::AlreadyStarted;
    }

    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeoutMs_);
    constexpr int pollMs = 50;

    while (true) {
        if (cancelled_.load()) {
            started_.store(false);
            return BeginGateResult::Cancelled;
        }

        const auto now = std::chrono::steady_clock::now();
        if (now >= deadline) {
            started_.store(false);
            return BeginGateResult::Timeout;
        }

        // Safety ordering: stop-before-begin must prevent start.
        if (!stopSignalPath_.empty() && SignalFileExists(stopSignalPath_)) {
            started_.store(false);
            return BeginGateResult::CancelledBeforeBegin;
        }

        const std::wstring content = ReadSignalFile(beginSignalPath_);
        if (!content.empty()) {
            if (content != expectedToken_) {
                started_.store(false);
                return BeginGateResult::InvalidToken;
            }

            // A valid begin token while stop already exists is treated as a
            // safety-default cancellation.
            if (!stopSignalPath_.empty() && SignalFileExists(stopSignalPath_)) {
                started_.store(false);
                return BeginGateResult::CancelledBeforeBegin;
            }

            // Invoke start callback exactly once. Wrap in try/catch so an
            // exception cannot escape as an unhandled crash.
            if (onStart) {
                try {
                    onStart();
                } catch (...) {
                    started_.store(false);
                    return BeginGateResult::InternalError;
                }
            }

            return BeginGateResult::Started;
        }

        const auto remaining = deadline - std::chrono::steady_clock::now();
        if (remaining <= std::chrono::milliseconds::zero()) {
            started_.store(false);
            return BeginGateResult::Timeout;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(pollMs));
    }
}

} // namespace wgc
