#pragma once

#include <atomic>
#include <functional>
#include <string>

namespace wgc {

enum class BeginGateResult {
    Started,
    Timeout,
    Cancelled,
    InvalidToken,
    AlreadyStarted,
    CancelledBeforeBegin, // stop signal existed before a valid begin
    InternalError
};

// Control gate that enforces the Consent Invariant: StartCapture callback is only
// invoked after the begin signal file exists, contains the expected token, and no
// stop signal is already present.
class BeginGate {
public:
    BeginGate(std::wstring beginSignalPath,
              std::wstring expectedToken,
              std::wstring stopSignalPath,
              int timeoutMs);

    using StartCallback = std::function<void()>;

    // Blocking wait for begin authorization. Returns the outcome. The start callback
    // is invoked at most once when authorization is granted. Post-begin stop detection
    // is the caller's duty (see StopSignalWatcher).
    BeginGateResult WaitAndRun(StartCallback onStart);

    // Request cancellation from another thread.
    void Cancel();

    bool WasStarted() const { return started_.load(); }

private:
    std::wstring beginSignalPath_;
    std::wstring expectedToken_;
    std::wstring stopSignalPath_;
    int timeoutMs_;
    std::atomic<bool> cancelled_{false};
    std::atomic<bool> started_{false};
};

// Reads the entire text content of a file; returns empty string on failure.
std::wstring ReadSignalFile(std::wstring_view path);

// Returns true if the file exists (any non-directory attributes).
bool SignalFileExists(std::wstring_view path);

} // namespace wgc
