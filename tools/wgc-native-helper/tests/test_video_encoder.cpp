#include "test_framework.h"

#include "video_encoder.h"
#include "pixel_utils.h"
#include "string_utils.h"
#include "path_policy.h"

#include <windows.h>
#include <objbase.h>

#include <cstdint>
#include <cstdio>
#include <iostream>
#include <memory>
#include <string>
#include <vector>

using namespace wgc;

namespace {

struct ComGuard {
    bool need = false;
    ComGuard() {
        HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        need = SUCCEEDED(hr);
    }
    ~ComGuard() { if (need) CoUninitialize(); }
};

std::wstring GetTempDirectory() {
    wchar_t buffer[MAX_PATH + 1] = {};
    DWORD len = ::GetTempPathW(MAX_PATH, buffer);
    return std::wstring(buffer, len);
}

std::wstring MakeTempMp4Path(const std::wstring& suffix) {
    return GetTempDirectory() + L"wgc_encoder_test_" +
           std::to_wstring(GetCurrentProcessId()) + suffix;
}

// RAII guard that deletes one or more files on destruction, even if an
// assertion throws.
class TempFileGuard {
public:
    explicit TempFileGuard(std::initializer_list<std::wstring> paths) : paths_(paths) {}
    ~TempFileGuard() {
        for (const auto& path : paths_) {
            ::DeleteFileW(path.c_str());
        }
    }

    void Add(std::wstring path) { paths_.push_back(std::move(path)); }

private:
    std::vector<std::wstring> paths_;
};

bool FileExists(const std::wstring& path) {
    return ::GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES;
}

std::string RunCommandAndCaptureOutput(const std::wstring& commandLine,
                                        DWORD timeoutMs = 30000) {
    SECURITY_ATTRIBUTES sa = {};
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = TRUE;
    HANDLE hRead = nullptr;
    HANDLE hWrite = nullptr;
    if (!::CreatePipe(&hRead, &hWrite, &sa, 0)) {
        return {};
    }
    ::SetHandleInformation(hRead, HANDLE_FLAG_INHERIT, 0);

    STARTUPINFOW si = {};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdOutput = hWrite;
    si.hStdError = hWrite;

    PROCESS_INFORMATION pi = {};
    std::wstring mutableCommandLine = commandLine;
    BOOL created = ::CreateProcessW(nullptr, mutableCommandLine.data(), nullptr, nullptr,
                                    TRUE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi);
    ::CloseHandle(hWrite);
    if (!created) {
        ::CloseHandle(hRead);
        return {};
    }

    std::string output;
    char buffer[4096];
    DWORD bytesRead = 0;
    while (::ReadFile(hRead, buffer, sizeof(buffer), &bytesRead, nullptr) && bytesRead > 0) {
        output.append(buffer, bytesRead);
    }

    const DWORD waitResult = ::WaitForSingleObject(pi.hProcess, timeoutMs);
    if (waitResult == WAIT_TIMEOUT) {
        ::TerminateProcess(pi.hProcess, 1);
        ::WaitForSingleObject(pi.hProcess, 1000);
        output.append("\n[command timed out]");
    }

    ::CloseHandle(pi.hProcess);
    ::CloseHandle(pi.hThread);
    ::CloseHandle(hRead);
    return output;
}

std::wstring GetFfprobePath() {
    std::wstring repoRoot = FindRepositoryRoot();
    if (repoRoot.empty()) return {};
    std::wstring path = repoRoot + L"tools\\ffmpeg\\bin\\ffprobe.exe";
    return FileExists(path) ? path : std::wstring{};
}

std::wstring GetFfmpegPath() {
    std::wstring repoRoot = FindRepositoryRoot();
    if (repoRoot.empty()) return {};
    std::wstring path = repoRoot + L"tools\\ffmpeg\\bin\\ffmpeg.exe";
    return FileExists(path) ? path : std::wstring{};
}

std::string RunFfprobe(const std::wstring& mediaPath, const std::wstring& arguments) {
    std::wstring ffprobe = GetFfprobePath();
    if (ffprobe.empty()) return {};
    std::wstring cmd = L"\"" + ffprobe + L"\" -v error " + arguments + L" \"" + mediaPath + L"\"";
    return RunCommandAndCaptureOutput(cmd);
}

std::string RunFfmpegExtractFirstFrame(const std::wstring& inputPath, const std::wstring& outputPath) {
    std::wstring ffmpeg = GetFfmpegPath();
    if (ffmpeg.empty()) {
        return "[ffmpeg path empty]";
    }
    std::wstring cmd = L"\"" + ffmpeg +
                       L"\" -y -v error -ss 0 -i \"" + inputPath +
                       L"\" -vframes 1 -f rawvideo -pix_fmt bgr24 \"" +
                       outputPath + L"\"";
    std::string output = RunCommandAndCaptureOutput(cmd);
    if (output.empty()) {
        return "[empty output]";
    }
    return output;
}

// Generates a 64x64 asymmetric test pattern:
//   - Top half (rows 0..31): red background, with a green vertical marker
//     on the leftmost 8 columns of the top half.
//   - Bottom half (rows 32..63): blue background.
// Pixel format is BGRA32 top-down.
std::vector<uint8_t> MakeAsymmetricTestFrame(int width, int height) {
    std::vector<uint8_t> pixels(static_cast<size_t>(width) * height * 4);
    const int half = height / 2;
    const int markerWidth = width / 8;
    for (int y = 0; y < height; ++y) {
        for (int x = 0; x < width; ++x) {
            const size_t idx = static_cast<size_t>(y * width + x) * 4;
            if (y < half) {
                // Top half: red, with a green marker on the far left.
                if (x < markerWidth) {
                    pixels[idx + 0] = 0;     // B
                    pixels[idx + 1] = 255;   // G
                    pixels[idx + 2] = 0;     // R
                    pixels[idx + 3] = 0;     // A
                } else {
                    pixels[idx + 0] = 0;     // B
                    pixels[idx + 1] = 0;     // G
                    pixels[idx + 2] = 255;   // R
                    pixels[idx + 3] = 0;     // A
                }
            } else {
                // Bottom half: blue.
                pixels[idx + 0] = 255;       // B
                pixels[idx + 1] = 0;         // G
                pixels[idx + 2] = 0;         // R
                pixels[idx + 3] = 0;         // A
            }
        }
    }
    return pixels;
}

// Parses the first floating-point token found anywhere in the text. This is
// robust against ffprobe output that prefixes lines with labels or non-numeric
// values such as container format names.
double ParseFirstDouble(const std::string& text) {
    try {
        for (size_t pos = 0; pos < text.size();) {
            // Skip leading non-numeric characters (including letters, punctuation,
            // whitespace) but keep minus and plus signs and decimal points.
            while (pos < text.size() &&
                   !(std::isdigit(static_cast<unsigned char>(text[pos])) ||
                     text[pos] == '-' || text[pos] == '+' || text[pos] == '.')) {
                ++pos;
            }
            if (pos >= text.size()) break;

            size_t idx = 0;
            const double value = std::stod(text.substr(pos), &idx);
            if (idx > 0) return value;
            ++pos;
        }
        return -1.0;
    } catch (...) {
        return -1.0;
    }
}

TEST_REGISTRAR(VideoEncoderSyntheticAsymmetricPatternVerifiedByFfprobe, []() {
    ComGuard com;

    std::wstring path = MakeTempMp4Path(L"_ffprobe.mp4");
    std::wstring rawPath = MakeTempMp4Path(L"_ffprobe_frame.raw");

    TempFileGuard guard({ path, rawPath });

    VideoEncoder encoder;
    auto initResult = encoder.Initialize(64, 64, 30, path);
    ASSERT_EQ(initResult.status, EncoderStatus::Ok);

    constexpr int kFrameCount = 30;
    constexpr int64_t kFrameDurationHns = 10000000LL / 30;
    auto testFrame = MakeAsymmetricTestFrame(64, 64);
    for (int i = 0; i < kFrameCount; ++i) {
        auto writeResult = encoder.WriteFrame(testFrame,
                                              static_cast<int64_t>(i) * kFrameDurationHns,
                                              kFrameDurationHns);
        ASSERT_EQ(writeResult.status, EncoderStatus::Ok);
    }

    auto finalizeResult = encoder.Finalize();
    ASSERT_EQ(finalizeResult.status, EncoderStatus::Ok);

    ASSERT_TRUE(FileExists(path));

    // Verify container format.
    std::string formatOutput = RunFfprobe(
        path,
        L"-show_entries format=format_name -of default=noprint_wrappers=1:nokey=1");
    ASSERT_FALSE(formatOutput.empty());
    ASSERT_TRUE(formatOutput.find("mov,mp4,m4a,3gp,3g2,mj2") != std::string::npos ||
                formatOutput.find("mp4") != std::string::npos);

    // Verify approximate duration (30 frames @ 30 fps).
    std::string durationOutput = RunFfprobe(
        path,
        L"-show_entries format=duration -of default=noprint_wrappers=1:nokey=1");
    ASSERT_FALSE(durationOutput.empty());
    const double duration = ParseFirstDouble(durationOutput);
    ASSERT_GE(duration, 0.85);
    ASSERT_LE(duration, 1.20);

    // Verify H.264 stream and dimensions.
    std::string streamOutput = RunFfprobe(
        path,
        L"-show_entries stream=codec_name,width,height -of default=noprint_wrappers=1");
    ASSERT_FALSE(streamOutput.empty());
    ASSERT_TRUE(streamOutput.find("h264") != std::string::npos);
    ASSERT_TRUE(streamOutput.find("width=64") != std::string::npos);
    ASSERT_TRUE(streamOutput.find("height=64") != std::string::npos);

    // Verify the first decoded frame's media time is near zero.
    std::string firstFrameOutput = RunFfprobe(
        path,
        L"-select_streams v:0 -show_entries frame=pkt_pts_time -of default=noprint_wrappers=1:nokey=1");
    ASSERT_FALSE(firstFrameOutput.empty());
    const double firstPts = ParseFirstDouble(firstFrameOutput);
    ASSERT_GE(firstPts, -0.01);
    ASSERT_LE(firstPts, 0.05);

    // Extract first decoded frame and assert color orientation (BGR24: B, G, R).
    // ffmpeg with -v error writes nothing to stderr/stdout on success; the
    // meaningful verification is that the raw frame file was created.
    std::string extractOutput = RunFfmpegExtractFirstFrame(path, rawPath);
    ASSERT_TRUE(FileExists(rawPath));

    constexpr size_t kRawFrameBytes = 64 * 64 * 3;
    std::vector<uint8_t> rawPixels(kRawFrameBytes);
    FILE* f = nullptr;
    errno_t openErr = _wfopen_s(&f, rawPath.c_str(), L"rb");
    ASSERT_EQ(openErr, 0);
    ASSERT_NE(f, nullptr);
    size_t read = std::fread(rawPixels.data(), 1, rawPixels.size(), f);
    std::fclose(f);
    ASSERT_EQ(read, rawPixels.size());

    // Sampling positions (well inside each region, away from compression edges).
    constexpr int kTopY = 8;
    constexpr int kBottomY = 56;
    constexpr int kLeftX = 2;
    constexpr int kCenterX = 32;

    auto sample = [&](int x, int y) -> const uint8_t* {
        return rawPixels.data() + (static_cast<size_t>(y) * 64 + x) * 3;
    };

    const uint8_t* topMarker = sample(kLeftX, kTopY);
    const uint8_t* topRed = sample(kCenterX, kTopY);
    const uint8_t* bottomBlue = sample(kCenterX, kBottomY);

    // Top-left marker should be green: B low, G high, R low.
    ASSERT_LT(topMarker[0], 40u);   // B
    ASSERT_GT(topMarker[1], 200u);  // G
    ASSERT_LT(topMarker[2], 40u);   // R

    // Top-center should be red: B low, G low, R high.
    ASSERT_LT(topRed[0], 40u);      // B
    ASSERT_LT(topRed[1], 40u);      // G
    ASSERT_GT(topRed[2], 200u);     // R

    // Bottom-center should be blue: B high, G low, R low.
    ASSERT_GT(bottomBlue[0], 200u); // B
    ASSERT_LT(bottomBlue[1], 40u);  // G
    ASSERT_LT(bottomBlue[2], 40u);  // R
});

} // namespace
