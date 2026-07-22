#pragma once

#include <string>

namespace wgc {

enum class DpiAwareness {
    Unknown,
    Unaware,
    SystemAware,
    PerMonitor,
    PerMonitorV2
};

struct DpiContextResult {
    bool ok = false;
    std::string errorCode;
    std::string errorReason;
    DpiAwareness awareness = DpiAwareness::Unknown;
};

// Abstract seam for Win32 DPI calls. Production code uses
// ProductionDpiContext; tests inject a fake implementation to exercise
// failure branches deterministically without depending on the current
// process manifest or Windows version.
class IDpiContext {
public:
    virtual ~IDpiContext() = default;

    // Wraps SetProcessDpiAwarenessContext. Returns true on success.
    virtual bool SetProcessDpiAwarenessContext(void* context) = 0;

    // Wraps GetThreadDpiAwarenessContext. Returns nullptr on failure.
    virtual void* GetThreadDpiAwarenessContext() = 0;

    // Wraps AreDpiAwarenessContextsEqual. Returns true if the two contexts
    // represent the same DPI awareness.
    virtual bool AreDpiAwarenessContextsEqual(void* a, void* b) = 0;

    // Wraps GetLastError.
    virtual unsigned long GetLastError() = 0;
};

// Production Win32 implementation. All calls are resolved dynamically via
// GetProcAddress so the helper can report a clean failure when an API is
// missing instead of failing to load.
class ProductionDpiContext : public IDpiContext {
public:
    bool SetProcessDpiAwarenessContext(void* context) override;
    void* GetThreadDpiAwarenessContext() override;
    bool AreDpiAwarenessContextsEqual(void* a, void* b) override;
    unsigned long GetLastError() override;
};

// Ensures the process runs in Per-Monitor V2 DPI awareness.
// Uses production Win32 APIs when context is nullptr; otherwise uses the
// supplied seam. If the process manifest already declares PerMonitorV2,
// SetProcessDpiAwarenessContext returns ERROR_ACCESS_DENIED; this function
// then reads the current context and verifies it is equivalent. Any other
// state is treated as a failure so the helper cannot silently fall back to
// an inconsistent coordinate space.
DpiContextResult InitializeDpiAwareness(IDpiContext* context = nullptr);

// Returns the current thread's DPI awareness context. The thread context
// mirrors the process context after initialization. Used by --probe and by
// tests that need to audit the actual awareness without changing it.
DpiAwareness GetCurrentDpiAwareness(IDpiContext* context = nullptr);

const char* DpiAwarenessToString(DpiAwareness awareness);

} // namespace wgc
