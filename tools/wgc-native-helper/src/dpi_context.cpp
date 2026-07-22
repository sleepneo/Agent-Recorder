#include "dpi_context.h"

#include <windows.h>

namespace wgc {

namespace {

using SetProcessDpiAwarenessContextFn = BOOL (WINAPI*)(DPI_AWARENESS_CONTEXT);
using GetThreadDpiAwarenessContextFn = DPI_AWARENESS_CONTEXT (WINAPI*)();
using AreDpiAwarenessContextsEqualFn = BOOL (WINAPI*)(DPI_AWARENESS_CONTEXT, DPI_AWARENESS_CONTEXT);

// Maps a DPI awareness context handle to the enum using the provided seam.
// Never compares handles by value; uses AreDpiAwarenessContextsEqual so the
// helper is robust across Windows versions where the numeric values differ.
DpiAwareness MapDpiAwarenessContext(void* context, IDpiContext* ctx) {
    if (!context) {
        return DpiAwareness::Unknown;
    }

    if (ctx->AreDpiAwarenessContextsEqual(context, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) {
        return DpiAwareness::PerMonitorV2;
    }
    if (ctx->AreDpiAwarenessContextsEqual(context, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE)) {
        return DpiAwareness::PerMonitor;
    }
    if (ctx->AreDpiAwarenessContextsEqual(context, DPI_AWARENESS_CONTEXT_SYSTEM_AWARE)) {
        return DpiAwareness::SystemAware;
    }
    if (ctx->AreDpiAwarenessContextsEqual(context, DPI_AWARENESS_CONTEXT_UNAWARE) ||
        ctx->AreDpiAwarenessContextsEqual(context, DPI_AWARENESS_CONTEXT_UNAWARE_GDISCALED)) {
        return DpiAwareness::Unaware;
    }
    return DpiAwareness::Unknown;
}

} // namespace

bool ProductionDpiContext::SetProcessDpiAwarenessContext(void* context) {
    HMODULE user32 = ::GetModuleHandleW(L"user32.dll");
    if (!user32) {
        ::SetLastError(ERROR_MOD_NOT_FOUND);
        return false;
    }

    auto fn = reinterpret_cast<SetProcessDpiAwarenessContextFn>(
        ::GetProcAddress(user32, "SetProcessDpiAwarenessContext"));
    if (!fn) {
        ::SetLastError(ERROR_NOT_SUPPORTED);
        return false;
    }

    return fn(static_cast<DPI_AWARENESS_CONTEXT>(context)) != 0;
}

void* ProductionDpiContext::GetThreadDpiAwarenessContext() {
    HMODULE user32 = ::GetModuleHandleW(L"user32.dll");
    if (!user32) {
        return nullptr;
    }

    auto fn = reinterpret_cast<GetThreadDpiAwarenessContextFn>(
        ::GetProcAddress(user32, "GetThreadDpiAwarenessContext"));
    if (!fn) {
        return nullptr;
    }

    return fn();
}

bool ProductionDpiContext::AreDpiAwarenessContextsEqual(void* a, void* b) {
    HMODULE user32 = ::GetModuleHandleW(L"user32.dll");
    if (!user32) {
        return false;
    }

    auto fn = reinterpret_cast<AreDpiAwarenessContextsEqualFn>(
        ::GetProcAddress(user32, "AreDpiAwarenessContextsEqual"));
    if (!fn) {
        return false;
    }

    return fn(static_cast<DPI_AWARENESS_CONTEXT>(a),
              static_cast<DPI_AWARENESS_CONTEXT>(b)) != 0;
}

unsigned long ProductionDpiContext::GetLastError() {
    return ::GetLastError();
}

DpiContextResult InitializeDpiAwareness(IDpiContext* context) {
    ProductionDpiContext production;
    IDpiContext* ctx = context ? context : &production;

    DpiContextResult result;
    result.ok = false;
    result.errorCode = "dpi_awareness_init_failed";

    void* target = DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2;

    if (!ctx->SetProcessDpiAwarenessContext(target)) {
        const unsigned long err = ctx->GetLastError();
        if (err == ERROR_ACCESS_DENIED) {
            // The process DPI awareness is already fixed (normally by the
            // embedded manifest). Verify it is exactly Per-Monitor V2.
            result.awareness = MapDpiAwarenessContext(ctx->GetThreadDpiAwarenessContext(), ctx);
            if (result.awareness == DpiAwareness::PerMonitorV2) {
                result.ok = true;
                result.errorCode.clear();
                result.errorReason.clear();
            } else {
                result.errorReason = "Process DPI awareness is fixed but not Per-Monitor V2";
            }
        } else if (err == ERROR_NOT_SUPPORTED) {
            result.errorReason = "SetProcessDpiAwarenessContext is not available on this system";
        } else {
            result.errorReason = "SetProcessDpiAwarenessContext failed";
        }
        return result;
    }

    // Setter succeeded; confirm the resulting context is actually V2.
    result.awareness = MapDpiAwarenessContext(ctx->GetThreadDpiAwarenessContext(), ctx);
    if (result.awareness == DpiAwareness::PerMonitorV2) {
        result.ok = true;
        result.errorCode.clear();
        result.errorReason.clear();
    } else {
        result.errorReason = "SetProcessDpiAwarenessContext succeeded but context is not Per-Monitor V2";
    }
    return result;
}

DpiAwareness GetCurrentDpiAwareness(IDpiContext* context) {
    ProductionDpiContext production;
    IDpiContext* ctx = context ? context : &production;
    return MapDpiAwarenessContext(ctx->GetThreadDpiAwarenessContext(), ctx);
}

const char* DpiAwarenessToString(DpiAwareness awareness) {
    switch (awareness) {
        case DpiAwareness::Unaware: return "unaware";
        case DpiAwareness::SystemAware: return "system_aware";
        case DpiAwareness::PerMonitor: return "per_monitor";
        case DpiAwareness::PerMonitorV2: return "per_monitor_v2";
        default: return "unknown";
    }
}

} // namespace wgc
