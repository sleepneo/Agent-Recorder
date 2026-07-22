#include "options.h"

#include "string_utils.h"

#include <algorithm>
#include <cwctype>
#include <format>
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
            opts.mode = CaptureMode::Probe;
            continue;
        }
        if (EqualsArg(arg, L"capture-continuous-display")) {
            opts.mode = CaptureMode::ContinuousDisplay;
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
            std::wstring value;
            if (!takeNext(value, L"display-bounds")) return result;
            if (!ParseDisplayBounds(value, opts.displayBounds)) {
                result.error = "Invalid display-bounds format; expected x,y,width,height";
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