#include "test_framework.h"

#include "video_encoder.h"
#include "pixel_utils.h"
#include "string_utils.h"
#include "path_policy.h"
#include "timeline.h"

#include <windows.h>
#include <objbase.h>

#include <cstdint>
#include <cstdio>
#include <future>
#include <iostream>
#include <memory>
#include <stdexcept>
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

// Mutually-exclusive failure categories for child-process invocations.
// The numeric values are not part of the contract and exist only for testing.
enum class CommandStatus {
    Ok,
    PathResolutionFailed,
    PipeSetupFailed,
    ProcessCreationFailed,
    WaitFailed,
    TimedOut,
    ExitCodeNonZero,
    StdoutEmpty,
};

// Detailed result from running a child process so test failures can distinguish
// path resolution, process creation, pipe setup, wait, timeout, non-zero exit,
// and empty-output cases.
struct CommandResult {
    CommandStatus status = CommandStatus::PathResolutionFailed;
    DWORD win32Error = 0;
    DWORD exitCode = static_cast<DWORD>(-1);
    std::string stdoutText;
    std::string stderrText;
    std::string detail;

    bool Ok() const { return status == CommandStatus::Ok; }

    std::string StatusLabel() const {
        switch (status) {
            case CommandStatus::Ok: return "ok";
            case CommandStatus::PathResolutionFailed: return "path_resolution_failed";
            case CommandStatus::PipeSetupFailed: return "pipe_setup_failed";
            case CommandStatus::ProcessCreationFailed: return "process_creation_failed";
            case CommandStatus::WaitFailed: return "wait_failed";
            case CommandStatus::TimedOut: return "timed_out";
            case CommandStatus::ExitCodeNonZero: return "exit_code_nonzero";
            case CommandStatus::StdoutEmpty: return "stdout_empty";
        }
        return "unknown";
    }

    std::string Diagnostics(const std::string& label) const {
        std::string diag = label + ": " + StatusLabel();
        if (win32Error != 0) {
            diag += " (win32=" + std::to_string(win32Error) + ")";
        }
        if (exitCode != static_cast<DWORD>(-1)) {
            diag += " (exit=" + std::to_string(exitCode) + ")";
        }
        if (!detail.empty()) {
            diag += "; " + detail;
        }
        if (!stdoutText.empty()) {
            diag += "; stdout=[" + stdoutText + "]";
        }
        if (!stderrText.empty()) {
            diag += "; stderr=[" + stderrText + "]";
        }
        return diag;
    }
};

// Quotes a single command-line argument according to CommandLineToArgvW rules.
std::wstring QuoteArgument(const std::wstring& arg) {
    if (arg.empty()) {
        return L"\"\"";
    }

    bool needsQuotes = false;
    for (wchar_t c : arg) {
        if (c == L' ' || c == L'\t' || c == L'"') {
            needsQuotes = true;
            break;
        }
    }
    if (!needsQuotes) {
        return arg;
    }

    std::wstring out;
    out.push_back(L'"');
    for (size_t i = 0; i < arg.size(); ++i) {
        int backslashCount = 0;
        while (i < arg.size() && arg[i] == L'\\') {
            ++backslashCount;
            ++i;
        }
        if (i == arg.size()) {
            // Backslashes at the end must be doubled before the closing quote.
            out.append(static_cast<size_t>(backslashCount) * 2, L'\\');
            break;
        }
        if (arg[i] == L'"') {
            // Backslashes before a quote must be doubled, then escape the quote.
            out.append(static_cast<size_t>(backslashCount) * 2 + 1, L'\\');
            out.push_back(L'"');
        } else {
            out.append(static_cast<size_t>(backslashCount), L'\\');
            out.push_back(arg[i]);
        }
    }
    out.push_back(L'"');
    return out;
}

// Builds a mutable command-line string with the application name as argv[0]
// followed by quoted arguments.
std::wstring BuildCommandLine(const std::wstring& applicationName,
                              const std::vector<std::wstring>& arguments) {
    std::wstring cmd = QuoteArgument(applicationName);
    for (const auto& arg : arguments) {
        cmd += L" " + QuoteArgument(arg);
    }
    return cmd;
}

CommandResult RunCommandAndCaptureOutput(const std::wstring& applicationName,
                                          const std::vector<std::wstring>& arguments,
                                          const std::wstring& workingDir = {},
                                          DWORD timeoutMs = 30000) {
    CommandResult result;
    const std::wstring commandLine = BuildCommandLine(applicationName, arguments);

    SECURITY_ATTRIBUTES sa = {};
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = TRUE;

    // stdout pipe
    HANDLE hStdoutRead = nullptr;
    HANDLE hStdoutWrite = nullptr;
    if (!::CreatePipe(&hStdoutRead, &hStdoutWrite, &sa, 0)) {
        result.status = CommandStatus::PipeSetupFailed;
        result.win32Error = ::GetLastError();
        return result;
    }
    if (!::SetHandleInformation(hStdoutRead, HANDLE_FLAG_INHERIT, 0)) {
        result.status = CommandStatus::PipeSetupFailed;
        result.win32Error = ::GetLastError();
        ::CloseHandle(hStdoutRead);
        ::CloseHandle(hStdoutWrite);
        return result;
    }

    // stderr pipe
    HANDLE hStderrRead = nullptr;
    HANDLE hStderrWrite = nullptr;
    if (!::CreatePipe(&hStderrRead, &hStderrWrite, &sa, 0)) {
        result.status = CommandStatus::PipeSetupFailed;
        result.win32Error = ::GetLastError();
        ::CloseHandle(hStdoutRead);
        ::CloseHandle(hStdoutWrite);
        return result;
    }
    if (!::SetHandleInformation(hStderrRead, HANDLE_FLAG_INHERIT, 0)) {
        result.status = CommandStatus::PipeSetupFailed;
        result.win32Error = ::GetLastError();
        ::CloseHandle(hStdoutRead);
        ::CloseHandle(hStdoutWrite);
        ::CloseHandle(hStderrRead);
        ::CloseHandle(hStderrWrite);
        return result;
    }

    // stdin pipe: give the child an empty/closed stdin so it does not inherit
    // an unrelated console handle. We close our write end immediately.
    HANDLE hStdinRead = nullptr;
    HANDLE hStdinWrite = nullptr;
    if (!::CreatePipe(&hStdinRead, &hStdinWrite, &sa, 0)) {
        result.status = CommandStatus::PipeSetupFailed;
        result.win32Error = ::GetLastError();
        ::CloseHandle(hStdoutRead);
        ::CloseHandle(hStdoutWrite);
        ::CloseHandle(hStderrRead);
        ::CloseHandle(hStderrWrite);
        return result;
    }
    if (!::SetHandleInformation(hStdinWrite, HANDLE_FLAG_INHERIT, 0)) {
        result.status = CommandStatus::PipeSetupFailed;
        result.win32Error = ::GetLastError();
        ::CloseHandle(hStdoutRead);
        ::CloseHandle(hStdoutWrite);
        ::CloseHandle(hStderrRead);
        ::CloseHandle(hStderrWrite);
        ::CloseHandle(hStdinRead);
        ::CloseHandle(hStdinWrite);
        return result;
    }
    ::CloseHandle(hStdinWrite);
    hStdinWrite = nullptr;

    STARTUPINFOW si = {};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdInput = hStdinRead;
    si.hStdOutput = hStdoutWrite;
    si.hStdError = hStderrWrite;

    PROCESS_INFORMATION pi = {};
    std::vector<wchar_t> mutableCommandLine(commandLine.begin(), commandLine.end());
    mutableCommandLine.push_back(L'\0');

    BOOL created = ::CreateProcessW(
        applicationName.c_str(), mutableCommandLine.data(), nullptr, nullptr,
        TRUE, CREATE_NO_WINDOW, nullptr,
        workingDir.empty() ? nullptr : workingDir.c_str(),
        &si, &pi);

    // Capture the creation error immediately, before any other Win32 call can
    // overwrite GetLastError().
    if (!created) {
        result.status = CommandStatus::ProcessCreationFailed;
        result.win32Error = ::GetLastError();
        ::CloseHandle(hStdoutWrite);
        ::CloseHandle(hStderrWrite);
        ::CloseHandle(hStdinRead);
        ::CloseHandle(hStdoutRead);
        ::CloseHandle(hStderrRead);
        return result;
    }

    ::CloseHandle(hStdoutWrite);
    ::CloseHandle(hStderrWrite);
    ::CloseHandle(hStdinRead);

    auto ReadPipe = [](HANDLE handle) -> std::string {
        std::string output;
        char buffer[4096];
        DWORD bytesRead = 0;
        while (::ReadFile(handle, buffer, sizeof(buffer), &bytesRead, nullptr) && bytesRead > 0) {
            output.append(buffer, bytesRead);
        }
        return output;
    };

    std::future<std::string> stdoutFuture = std::async(std::launch::async, ReadPipe, hStdoutRead);
    std::future<std::string> stderrFuture = std::async(std::launch::async, ReadPipe, hStderrRead);

    const DWORD waitResult = ::WaitForSingleObject(pi.hProcess, timeoutMs);

    if (waitResult == WAIT_FAILED) {
        result.status = CommandStatus::WaitFailed;
        result.win32Error = ::GetLastError();
        ::TerminateProcess(pi.hProcess, 1);
        ::WaitForSingleObject(pi.hProcess, 1000);
    } else if (waitResult == WAIT_TIMEOUT) {
        result.status = CommandStatus::TimedOut;
        ::TerminateProcess(pi.hProcess, 1);
        const DWORD termWait = ::WaitForSingleObject(pi.hProcess, 1000);
        if (termWait == WAIT_TIMEOUT) {
            result.detail = "child did not exit after TerminateProcess";
        }
    } else {
        DWORD code = 0;
        if (!::GetExitCodeProcess(pi.hProcess, &code)) {
            result.status = CommandStatus::WaitFailed;
            result.win32Error = ::GetLastError();
        } else {
            result.exitCode = code;
            result.status = (code == 0) ? CommandStatus::Ok : CommandStatus::ExitCodeNonZero;
        }
    }

    ::CloseHandle(pi.hProcess);
    ::CloseHandle(pi.hThread);

    result.stdoutText = stdoutFuture.get();
    result.stderrText = stderrFuture.get();

    ::CloseHandle(hStdoutRead);
    ::CloseHandle(hStderrRead);
    return result;
}

std::wstring GetSystemDirectoryPath() {
    wchar_t buffer[MAX_PATH + 1] = {};
    UINT len = ::GetSystemDirectoryW(buffer, MAX_PATH);
    if (len == 0 || len >= MAX_PATH) return {};
    return std::wstring(buffer, len);
}

std::wstring GetCmdExePath() {
    std::wstring sys = GetSystemDirectoryPath();
    if (sys.empty()) return {};
    return sys + L"\\cmd.exe";
}

std::wstring GetExecutablePathForDiagnostics() {
    std::wstring path;
    path.resize(MAX_PATH);
    DWORD len = ::GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (len == 0 || len >= MAX_PATH) return {};
    path.resize(len);
    return path;
}

std::wstring GetFfprobePath(std::string& diagnostics) {
    std::wstring repoRoot = FindRepositoryRoot();
    diagnostics += "exe=" + WideToUtf8(GetExecutablePathForDiagnostics()) + "; ";
    diagnostics += "repoRoot=" + WideToUtf8(repoRoot) + "; ";
    if (repoRoot.empty()) {
        diagnostics += "FindRepositoryRoot empty";
        return {};
    }
    std::wstring path = repoRoot + L"tools\\ffmpeg\\bin\\ffprobe.exe";
    diagnostics += "candidate=" + WideToUtf8(path) + "; exists=" + std::to_string(FileExists(path));
    return FileExists(path) ? path : std::wstring{};
}

std::wstring GetFfmpegPath() {
    std::wstring repoRoot = FindRepositoryRoot();
    if (repoRoot.empty()) return {};
    std::wstring path = repoRoot + L"tools\\ffmpeg\\bin\\ffmpeg.exe";
    return FileExists(path) ? path : std::wstring{};
}

std::wstring GetFfmpegBinDirectory() {
    std::wstring repoRoot = FindRepositoryRoot();
    if (repoRoot.empty()) return {};
    return repoRoot + L"tools\\ffmpeg\\bin";
}

// Reclassifies a successful result with empty stdout as StdoutEmpty so callers
// can distinguish "ffprobe exited cleanly but produced no data" from other
// failure modes.
CommandResult RequireNonEmptyStdout(CommandResult result, const std::string& label) {
    if (result.status == CommandStatus::Ok && result.stdoutText.empty()) {
        result.status = CommandStatus::StdoutEmpty;
        result.detail = label + " produced no stdout";
    }
    return result;
}

// Throws a runtime_error with diagnostics so the test failure message is
// explicit about which subprocess step failed.
void ThrowIfCommandFailed(const CommandResult& result, const std::string& label) {
    if (!result.Ok()) {
        throw std::runtime_error(result.Diagnostics(label));
    }
}

CommandResult RunFfprobe(const std::wstring& mediaPath, const std::wstring& arguments) {
    CommandResult result;
    std::string pathDiagnostics;
    std::wstring ffprobe = GetFfprobePath(pathDiagnostics);
    if (ffprobe.empty()) {
        result.status = CommandStatus::PathResolutionFailed;
        result.detail = "ffprobe path resolution failed: " + pathDiagnostics;
        return result;
    }

    std::vector<std::wstring> args = SplitWide(arguments, L' ');
    args.insert(args.begin(), L"-v");
    args.insert(args.begin() + 1, L"error");
    args.push_back(mediaPath);

    result = RunCommandAndCaptureOutput(ffprobe, args, GetFfmpegBinDirectory());
    return RequireNonEmptyStdout(result, "ffprobe");
}

CommandResult RunFfmpegExtractFirstFrame(const std::wstring& inputPath, const std::wstring& outputPath) {
    CommandResult result;
    std::wstring ffmpeg = GetFfmpegPath();
    if (ffmpeg.empty()) {
        result.status = CommandStatus::PathResolutionFailed;
        result.detail = "ffmpeg path resolution failed";
        return result;
    }

    std::vector<std::wstring> args = {
        L"-y", L"-v", L"error", L"-ss", L"0", L"-i", inputPath,
        L"-vframes", L"1", L"-f", L"rawvideo", L"-pix_fmt", L"bgr24",
        outputPath
    };
    return RunCommandAndCaptureOutput(ffmpeg, args, GetFfmpegBinDirectory());
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
    CommandResult formatResult = RunFfprobe(
        path,
        L"-show_entries format=format_name -of default=noprint_wrappers=1:nokey=1");
    ThrowIfCommandFailed(formatResult, "ffprobe format");
    ASSERT_FALSE(formatResult.stdoutText.empty());
    ASSERT_TRUE(formatResult.stdoutText.find("mov,mp4,m4a,3gp,3g2,mj2") != std::string::npos ||
                formatResult.stdoutText.find("mp4") != std::string::npos);

    // Verify approximate duration (30 frames @ 30 fps).
    CommandResult durationResult = RunFfprobe(
        path,
        L"-show_entries format=duration -of default=noprint_wrappers=1:nokey=1");
    ThrowIfCommandFailed(durationResult, "ffprobe duration");
    ASSERT_FALSE(durationResult.stdoutText.empty());
    const double duration = ParseFirstDouble(durationResult.stdoutText);
    ASSERT_GE(duration, 0.85);
    ASSERT_LE(duration, 1.20);

    // Verify H.264 stream and dimensions.
    CommandResult streamResult = RunFfprobe(
        path,
        L"-show_entries stream=codec_name,width,height -of default=noprint_wrappers=1");
    ThrowIfCommandFailed(streamResult, "ffprobe stream");
    ASSERT_FALSE(streamResult.stdoutText.empty());
    ASSERT_TRUE(streamResult.stdoutText.find("h264") != std::string::npos);
    ASSERT_TRUE(streamResult.stdoutText.find("width=64") != std::string::npos);
    ASSERT_TRUE(streamResult.stdoutText.find("height=64") != std::string::npos);

    // Verify the first decoded frame's media time is near zero.
    CommandResult firstFrameResult = RunFfprobe(
        path,
        L"-select_streams v:0 -show_entries frame=pkt_pts_time -of default=noprint_wrappers=1:nokey=1");
    ThrowIfCommandFailed(firstFrameResult, "ffprobe first frame");
    ASSERT_FALSE(firstFrameResult.stdoutText.empty());
    const double firstPts = ParseFirstDouble(firstFrameResult.stdoutText);
    ASSERT_GE(firstPts, -0.01);
    ASSERT_LE(firstPts, 0.05);

    // Extract first decoded frame and assert color orientation (BGR24: B, G, R).
    CommandResult extractResult = RunFfmpegExtractFirstFrame(path, rawPath);
    ThrowIfCommandFailed(extractResult, "ffmpeg extract first frame");
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

TEST_REGISTRAR(VideoEncoderBoundedIrregularTimelineEndsAtCaptureDeadline, []() {
    ComGuard com;

    std::wstring path = MakeTempMp4Path(L"_bounded_timeline.mp4");
    TempFileGuard guard({ path });

    VideoEncoder encoder;
    auto initResult = encoder.Initialize(64, 64, 30, path);
    ASSERT_EQ(initResult.status, EncoderStatus::Ok);

    constexpr int64_t kCaptureEndHns = 100'080'000LL;
    std::vector<int64_t> rawTimes;
    for (int i = 0; i <= 250; ++i) {
        rawTimes.push_back(static_cast<int64_t>(i) * 333'333LL);
    }
    rawTimes.push_back(83'500'000LL);
    rawTimes.push_back(91'750'000LL);
    rawTimes.push_back(kCaptureEndHns);
    const auto testFrame = MakeAsymmetricTestFrame(64, 64);
    FrameTimeline timeline(30);
    int64_t pendingMediaTimeHns = 0;
    bool hasPending = false;

    for (int64_t rawTime : rawTimes) {
        int64_t mediaTimeHns = 0;
        int64_t nominalDurationHns = 0;
        if (!timeline.SubmitFrame(rawTime, &mediaTimeHns, &nominalDurationHns, kCaptureEndHns)) {
            continue;
        }

        if (hasPending) {
            auto writeResult = encoder.WriteFrame(
                testFrame,
                pendingMediaTimeHns,
                mediaTimeHns - pendingMediaTimeHns);
            ASSERT_EQ(writeResult.status, EncoderStatus::Ok);
        }
        pendingMediaTimeHns = mediaTimeHns;
        hasPending = true;
    }

    ASSERT_TRUE(hasPending);
    int64_t finalMediaTimeHns = 0;
    int64_t finalDurationHns = 0;
    ASSERT_TRUE(timeline.FinalizeAt(
        kCaptureEndHns, &finalMediaTimeHns, &finalDurationHns));
    ASSERT_EQ(finalMediaTimeHns, pendingMediaTimeHns);
    ASSERT_EQ(finalDurationHns, 8'330'000LL);

    auto finalWriteResult = encoder.WriteFrame(
        testFrame, finalMediaTimeHns, finalDurationHns);
    ASSERT_EQ(finalWriteResult.status, EncoderStatus::Ok);
    auto finalizeResult = encoder.Finalize();
    ASSERT_EQ(finalizeResult.status, EncoderStatus::Ok);
    ASSERT_TRUE(FileExists(path));

    CommandResult durationResult = RunFfprobe(
        path,
        L"-show_entries format=duration -of default=noprint_wrappers=1:nokey=1");
    ThrowIfCommandFailed(durationResult, "ffprobe bounded timeline duration");
    const double duration = ParseFirstDouble(durationResult.stdoutText);
    ASSERT_GE(duration, 9.80);
    ASSERT_LE(duration, 10.20);
});

// --- Subprocess failure-classification tests ---------------------------------

TEST_REGISTRAR(CommandResult_DiagnosticsContainsStatusAndError, []() {
    CommandResult r;
    r.status = CommandStatus::ProcessCreationFailed;
    r.win32Error = ERROR_FILE_NOT_FOUND;
    r.detail = "exe missing";
    std::string diag = r.Diagnostics("tool");
    ASSERT_TRUE(diag.find("process_creation_failed") != std::string::npos);
    ASSERT_TRUE(diag.find("win32=2") != std::string::npos);
    ASSERT_TRUE(diag.find("exe missing") != std::string::npos);
});

TEST_REGISTRAR(CommandResult_EmptyStdoutIsClassifiedAsStdoutEmpty, []() {
    CommandResult r;
    r.status = CommandStatus::Ok;
    r.stdoutText = "";
    r.stderrText = "some warning";
    auto classified = RequireNonEmptyStdout(r, "ffprobe format");
    ASSERT_EQ(classified.status, CommandStatus::StdoutEmpty);
    ASSERT_FALSE(classified.stderrText.empty());
    ASSERT_TRUE(classified.Diagnostics("ffprobe format").find("stdout_empty") != std::string::npos);
});

TEST_REGISTRAR(Subprocess_CommandNotFound_ReturnsProcessCreationFailedWithError, []() {
    std::wstring tempRoot = GetTempDirectory();
    std::wstring bogus = tempRoot + L"wgc_nonexistent_tool_" + std::to_wstring(GetCurrentProcessId()) + L".exe";
    CommandResult result = RunCommandAndCaptureOutput(bogus, { L"arg" });
    ASSERT_EQ(result.status, CommandStatus::ProcessCreationFailed);
    ASSERT_EQ(result.win32Error, static_cast<DWORD>(ERROR_FILE_NOT_FOUND));
});

TEST_REGISTRAR(Subprocess_ZeroExitWithOutput_ReturnsOk, []() {
    std::wstring cmd = GetCmdExePath();
    ASSERT_FALSE(cmd.empty());
    CommandResult result = RunCommandAndCaptureOutput(
        cmd, { L"/c", L"echo", L"hello" }, {}, 5000);
    ASSERT_TRUE(result.Ok());
    ASSERT_EQ(result.status, CommandStatus::Ok);
    ASSERT_TRUE(result.stdoutText.find("hello") != std::string::npos);
});

TEST_REGISTRAR(Subprocess_NonZeroExit_ReturnsExitCodeNonZero, []() {
    std::wstring cmd = GetCmdExePath();
    ASSERT_FALSE(cmd.empty());
    CommandResult result = RunCommandAndCaptureOutput(
        cmd, { L"/c", L"exit", L"/b", L"42" }, {}, 5000);
    ASSERT_EQ(result.status, CommandStatus::ExitCodeNonZero);
    ASSERT_EQ(result.exitCode, 42u);
});

TEST_REGISTRAR(Subprocess_Timeout_ReturnsTimedOut, []() {
    std::wstring cmd = GetCmdExePath();
    ASSERT_FALSE(cmd.empty());
    CommandResult result = RunCommandAndCaptureOutput(
        cmd, { L"/c", L"ping", L"-n", L"5", L"127.0.0.1", L">nul" }, {}, 100);
    ASSERT_EQ(result.status, CommandStatus::TimedOut);
});

} // namespace
