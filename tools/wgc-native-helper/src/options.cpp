#include "options.h"

#include "string_utils.h"

#include <algorithm>
#include <cwctype>
#include <format>
#include <limits>
#include <string>

namespace wgc {

namespace {

std::wstring ToLowerWide(std::wstring_view text) {
    std::wstring result(text);
    std::transform(result.begin(), result.end(), result.begin(),
                   [](wchar_t c) { return static_cast<wchar_t>(std::towlower(static_cast<wint_t>(c))); });
    return result;
}

bool EqualsArg(std::wstring_view arg, std::wstring_view name) {
    if (arg.size() < 2 || arg[0] != L'-') return false;
    std::wstring_view body = arg.substr(1);
    if (!body.empty() && body[0] == L'-') {
        body = body.substr(1);
    }
    return ToLowerWide(body) == ToLowerWide(name);
}

bool ParseDisplayBounds(std::wstring_view text, Rect& out) {
    // Expected format: x,y,width,height
    const auto parts = SplitWide(text, L',');
    if (parts.size() != 4) return false;
    int values[4] = {};
    for (size_t i = 0; i < 4; ++i) {
        if (!ParseInt(TrimWide(parts[i]), values[i])) return false;
    }
    out = { values[0], values[1], values[2], values[3] };
    return true;
}

bool SetCaptureMode(Options& opts, CaptureMode mode, ParseResult& result) {
    if (opts.mode == CaptureMode::Probe || opts.mode == CaptureMode::Help ||
        opts.mode == CaptureMode::Version) {
        result.error = "Capture mode conflicts with probe/help/version mode";
        return false;
    }
    if (opts.mode != CaptureMode::None) {
        result.error = "Capture target modes are mutually exclusive";
        return false;
    }
    opts.mode = mode;
    return true;
}

bool ParseWindowHwndStrict(std::wstring_view text, std::uint64_t& out) {
    if (text.empty() || text.front() == L'+' || text.front() == L'-') {
        return false;
    }

    std::size_t index = 0;
    int base = 10;
    if (text.size() >= 2 && text[0] == L'0' && (text[1] == L'x' || text[1] == L'X')) {
        base = 16;
        index = 2;
    }
    if (index == text.size()) return false;

    std::uint64_t value = 0;
    for (; index < text.size(); ++index) {
        const wchar_t c = text[index];
        int digit = -1;
        if (c >= L'0' && c <= L'9') {
            digit = static_cast<int>(c - L'0');
        } else if (base == 16 && c >= L'a' && c <= L'f') {
            digit = static_cast<int>(c - L'a') + 10;
        } else if (base == 16 && c >= L'A' && c <= L'F') {
            digit = static_cast<int>(c - L'A') + 10;
        }
        if (digit < 0 || digit >= base) return false;
        if (value > (std::numeric_limits<std::uint64_t>::max() - static_cast<std::uint64_t>(digit)) /
                        static_cast<std::uint64_t>(base)) {
            return false;
        }
        value = value * static_cast<std::uint64_t>(base) + static_cast<std::uint64_t>(digit);
    }

    if (value == 0) return false;
    out = value;
    return true;
}

std::string WError(std::wstring_view msg) {
    return WideToUtf8(msg);
}

} // namespace

bool IsValidRecordingId(std::wstring_view id) {
    if (id.empty() || id.size() > 64) return false;
    for (const wchar_t c : id) {
        const bool ok = (c >= L'a' && c <= L'z') || (c >= L'A' && c <= L'Z') ||
                        (c >= L'0' && c <= L'9') || c == L'-' || c == L'_' || c == L'.';
        if (!ok) return false;
    }
    return true;
}

bool ParseWindowHwnd(std::wstring_view text, std::uint64_t& out) {
    return ParseWindowHwndStrict(text, out);
}

ParseResult ParseArguments(int argc, wchar_t* argv[]) {
    ParseResult result;
    Options& opts = result.options;

    for (int i = 1; i < argc; ++i) {
        const std::wstring arg = argv[i];

        if (EqualsArg(arg, L"help") || arg == L"/?") {
            opts.mode = CaptureMode::Help;
            return result;
        }
        if (EqualsArg(arg, L"version")) {
            opts.mode = CaptureMode::Version;
            return result;
        }
        if (EqualsArg(arg, L"probe")) {
            if (opts.mode != CaptureMode::None) {
                result.error = "Capture mode conflicts with --probe";
                return result;
            }
            opts.mode = CaptureMode::Probe;
            continue;
        }
        if (EqualsArg(arg, L"capture-continuous-display")) {
            if (!SetCaptureMode(opts, CaptureMode::ContinuousDisplay, result)) return result;
            continue;
        }
        if (EqualsArg(arg, L"capture-continuous-window")) {
            if (!SetCaptureMode(opts, CaptureMode::ContinuousWindow, result)) return result;
            continue;
        }
        if (EqualsArg(arg, L"capture-continuous-region")) {
            if (!SetCaptureMode(opts, CaptureMode::ContinuousRegion, result)) return result;
            continue;
        }
        if (EqualsArg(arg, L"i-understand-this-captures-screen")) {
            opts.hasConsentFlag = true;
            continue;
        }

        auto takeNext = [&](std::wstring& target, std::wstring_view name) -> bool {
            if (i + 1 >= argc) {
                result.error = std::format("Missing value for argument --{}", WideToUtf8(name));
                return false;
            }
            target = argv[++i];
            return true;
        };

        if (EqualsArg(arg, L"display-bounds")) {
            if (opts.hasDisplayBounds) {
                result.error = "Duplicate --display-bounds";
                return result;
            }
            std::wstring value;
            if (!takeNext(value, L"display-bounds")) return result;
            if (!ParseDisplayBounds(value, opts.displayBounds)) {
                result.error = "Invalid display-bounds format; expected x,y,width,height";
                return result;
            }
            opts.hasDisplayBounds = true;
        } else if (EqualsArg(arg, L"region-bounds")) {
            if (opts.hasRegionBounds) {
                result.error = "Duplicate --region-bounds";
                return result;
            }
            std::wstring value;
            if (!takeNext(value, L"region-bounds")) return result;
            if (!ParseDisplayBounds(value, opts.regionBounds)) {
                result.error = "Invalid region-bounds format; expected x,y,width,height";
                return result;
            }
            opts.hasRegionBounds = true;
        } else if (EqualsArg(arg, L"window-hwnd")) {
            std::wstring value;
            if (!takeNext(value, L"window-hwnd")) return result;
            if (!ParseWindowHwnd(value, opts.windowHwnd)) {
                result.error = "Invalid window-hwnd; expected a non-zero 64-bit HWND";
                return result;
            }
        } else if (EqualsArg(arg, L"recording-id")) {
            if (!takeNext(opts.recordingId, L"recording-id")) return result;
        } else if (EqualsArg(arg, L"output")) {
            if (!takeNext(opts.outputPath, L"output")) return result;
        } else if (EqualsArg(arg, L"duration-ms")) {
            std::wstring value;
            if (!takeNext(value, L"duration-ms")) return result;
            if (!ParseInt(TrimWide(value), opts.durationMs) || opts.durationMs < 1000 || opts.durationMs > 10000) {
                result.error = "Invalid duration-ms; expected 1000..10000";
                return result;
            }
        } else if (EqualsArg(arg, L"fps")) {
            std::wstring value;
            if (!takeNext(value, L"fps")) return result;
            if (!ParseInt(TrimWide(value), opts.fps) || opts.fps < 1 || opts.fps > 60) {
                result.error = "Invalid fps; expected 1..60";
                return result;
            }
        } else if (EqualsArg(arg, L"encoder-mode")) {
            if (opts.hasEncoderMode) {
                result.error = "Duplicate --encoder-mode";
                return result;
            }
            std::wstring value;
            if (!takeNext(value, L"encoder-mode")) return result;
            if (!TryParseEncoderMode(value, opts.encoderMode) ||
                opts.encoderMode == EncoderMode::Hardware) {
                result.error = "Invalid encoder-mode; expected software or hardware-preferred";
                return result;
            }
            opts.hasEncoderMode = true;
        } else if (EqualsArg(arg, L"begin-signal")) {
            if (!takeNext(opts.beginSignalPath, L"begin-signal")) return result;
        } else if (EqualsArg(arg, L"begin-token")) {
            if (!takeNext(opts.beginToken, L"begin-token")) return result;
        } else if (EqualsArg(arg, L"begin-timeout-ms")) {
            std::wstring value;
            if (!takeNext(value, L"begin-timeout-ms")) return result;
            if (!ParseInt(TrimWide(value), opts.beginTimeoutMs) || opts.beginTimeoutMs < 100 || opts.beginTimeoutMs > 300000) {
                result.error = "Invalid begin-timeout-ms; expected 100..300000";
                return result;
            }
        } else if (EqualsArg(arg, L"stop-signal")) {
            if (!takeNext(opts.stopSignalPath, L"stop-signal")) return result;
        } else {
            result.error = std::format("Unknown argument: {}", WideToUtf8(arg));
            return result;
        }
    }

    result.ok = true;
    return result;
}

} // namespace wgc
