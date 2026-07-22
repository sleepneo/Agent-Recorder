#pragma once

#include <optional>
#include <string>

namespace wgc {

enum class CaptureMode {
    None,
    ContinuousDisplay,
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

} // namespace wgc