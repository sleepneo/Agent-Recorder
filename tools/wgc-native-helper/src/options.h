#pragma once

#include <optional>
#include <cstdint>
#include <string>
#include <string_view>

namespace wgc {

enum class CaptureMode {
    None,
    ContinuousDisplay,
    ContinuousWindow,
    ContinuousRegion,
    Probe,
    Help,
    Version
};

struct Rect {
    int x = 0;
    int y = 0;
    int width = 0;
    int height = 0;
};

struct Options {
    CaptureMode mode = CaptureMode::None;

    Rect displayBounds;
    Rect regionBounds;
    bool hasDisplayBounds = false;
    bool hasRegionBounds = false;
    std::uint64_t windowHwnd = 0;
    std::wstring recordingId;
    std::wstring outputPath;
    int durationMs = 0;
    int fps = 0;

    std::wstring beginSignalPath;
    std::wstring beginToken;
    int beginTimeoutMs = 0;

    std::wstring stopSignalPath;

    bool hasConsentFlag = false;
};

struct ParseResult {
    bool ok = false;
    std::string error;
    Options options;
};

ParseResult ParseArguments(int argc, wchar_t* argv[]);

bool IsValidRecordingId(std::wstring_view id);

// Parses the exact HWND token accepted by the native CLI. Both decimal and
// 0x-prefixed hexadecimal are accepted; signs, whitespace, trailing text,
// overflow, and zero are rejected.
bool ParseWindowHwnd(std::wstring_view text, std::uint64_t& out);

} // namespace wgc
