#include "test_framework.h"

#include <windows.h>

#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <sstream>
#include <string>
#include <vector>

using namespace wgc;

namespace {

namespace fs = std::filesystem;

struct TempDir {
    std::wstring path;

    explicit TempDir(const std::wstring& prefix) {
        const fs::path temp = fs::temp_directory_path();
        const uint32_t pid = ::GetCurrentProcessId();
        const auto ticks = std::chrono::steady_clock::now().time_since_epoch().count();
        path = (temp / (prefix + L"_" + std::to_wstring(pid) + L"_" + std::to_wstring(ticks))).wstring();
        fs::create_directories(path);
    }

    ~TempDir() {
        try {
            fs::remove_all(path);
        } catch (...) {
        }
    }
};

void WriteFile(const std::wstring& path, const std::wstring& content) {
    std::wofstream file(path, std::ios::binary);
    file << content;
}

std::wstring GetHelperExePath() {
    std::wstring path;
    path.resize(MAX_PATH);
    DWORD len = ::GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (len == 0) return L"wgc-native-helper.exe";
    path.resize(len);
    fs::path testExe = path;
    fs::path helper = testExe.parent_path() / L"wgc-native-helper.exe";
    return helper.wstring();
}

struct ProcessResult {
    int exitCode = -1;
    std::string stdoutText;
    std::string stderrText;
};

ProcessResult RunHelper(const std::vector<std::wstring>& args,
                        std::chrono::milliseconds timeout) {
    ProcessResult result;

    const std::wstring helperExe = GetHelperExePath();
    std::wstring commandLine = L"\"" + helperExe + L"\"";
    for (const auto& a : args) {
        commandLine += L" \"" + a + L"\"";
    }

    SECURITY_ATTRIBUTES sa = {};
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = TRUE;

    HANDLE stdoutRead = nullptr;
    HANDLE stdoutWrite = nullptr;
    HANDLE stderrRead = nullptr;
    HANDLE stderrWrite = nullptr;
    ::CreatePipe(&stdoutRead, &stdoutWrite, &sa, 0);
    ::SetHandleInformation(stdoutRead, HANDLE_FLAG_INHERIT, 0);
    ::CreatePipe(&stderrRead, &stderrWrite, &sa, 0);
    ::SetHandleInformation(stderrRead, HANDLE_FLAG_INHERIT, 0);

    STARTUPINFOW si = {};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdOutput = stdoutWrite;
    si.hStdError = stderrWrite;

    PROCESS_INFORMATION pi = {};
    BOOL created = ::CreateProcessW(
        nullptr, commandLine.data(), nullptr, nullptr, TRUE,
        CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi);

    ::CloseHandle(stdoutWrite);
    ::CloseHandle(stderrWrite);

    if (!created) {
        ::CloseHandle(stdoutRead);
        ::CloseHandle(stderrRead);
        result.stderrText = "CreateProcess failed";
        return result;
    }

    const DWORD timeoutMs = static_cast<DWORD>(timeout.count());
    const DWORD waitResult = ::WaitForSingleObject(pi.hProcess, timeoutMs);

    if (waitResult == WAIT_TIMEOUT) {
        ::TerminateProcess(pi.hProcess, 1);
        ::WaitForSingleObject(pi.hProcess, 5000);
        result.exitCode = -1;
    } else {
        DWORD code = 0;
        if (::GetExitCodeProcess(pi.hProcess, &code)) {
            result.exitCode = static_cast<int>(code);
        }
    }

    auto ReadPipe = [](HANDLE handle) -> std::string {
        std::string output;
        char buffer[4096];
        DWORD read = 0;
        while (::ReadFile(handle, buffer, sizeof(buffer), &read, nullptr) && read > 0) {
            output.append(buffer, read);
        }
        return output;
    };

    result.stdoutText = ReadPipe(stdoutRead);
    result.stderrText = ReadPipe(stderrRead);

    ::CloseHandle(pi.hProcess);
    ::CloseHandle(pi.hThread);
    ::CloseHandle(stdoutRead);
    ::CloseHandle(stderrRead);

    return result;
}

struct FailEvent {
    bool found = false;
    std::string errorCode;
    std::string reason;
    std::string hresult;
    std::string partialOutputPath;
    int64_t framesCaptured = -1;
    int64_t bytesWritten = -1;
};

FailEvent ParseFailEvent(const std::string& output) {
    FailEvent event;
    std::istringstream stream(output);
    std::string line;
    bool inBlock = false;
    while (std::getline(stream, line)) {
        if (!line.empty() && line.back() == '\r') {
            line.pop_back();
        }
        if (line == "RESULT: FAIL") {
            inBlock = true;
            event.found = true;
            continue;
        }
        if (!inBlock) continue;
        if (line.empty()) break;
        const auto colon = line.find(": ");
        if (colon == std::string::npos) continue;
        const std::string key = line.substr(0, colon);
        const std::string value = line.substr(colon + 2);
        try {
            if (key == "ErrorCode") event.errorCode = value;
            else if (key == "Reason") event.reason = value;
            else if (key == "HRESULT") event.hresult = value;
            else if (key == "PartialOutputPath") event.partialOutputPath = value;
            else if (key == "FramesCaptured") event.framesCaptured = std::stoll(value);
            else if (key == "BytesWritten") event.bytesWritten = std::stoll(value);
        } catch (...) {
        }
    }
    return event;
}

int CountOccurrences(const std::string& text, const std::string& needle) {
    int count = 0;
    size_t pos = 0;
    while ((pos = text.find(needle, pos)) != std::string::npos) {
        ++count;
        pos += needle.length();
    }
    return count;
}

bool FileExists(const std::wstring& path) {
    return ::GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES;
}

} // namespace

TEST_REGISTRAR(HelperEarlyFailure_DisplayNotFound_NoStarted_NoPartial, []() {
    TempDir dir(L"wgc-test-helper-display-not-found");
    const std::wstring outputPath = dir.path + L"\\out.mp4";
    const std::wstring partialPath = dir.path + L"\\out.partial.mp4";
    const std::wstring beginPath = dir.path + L"\\begin.signal";
    const std::wstring stopPath = dir.path + L"\\stop.signal";

    WriteFile(beginPath, L"test-token-175f");

    // Use bounds that cannot match any real display.
    std::vector<std::wstring> args = {
        L"--capture-continuous-display",
        L"--display-bounds", L"99999,99999,64,64",
        L"--recording-id", L"test-recording-175f",
        L"--output", outputPath,
        L"--duration-ms", L"2000",
        L"--fps", L"30",
        L"--begin-signal", beginPath,
        L"--begin-token", L"test-token-175f",
        L"--begin-timeout-ms", L"1000",
        L"--stop-signal", stopPath,
        L"--i-understand-this-captures-screen"
    };

    const auto result = RunHelper(args, std::chrono::milliseconds(10000));

    // 1. exit code indicates failure.
    ASSERT_NE(result.exitCode, 0);

    // 2. Exactly one RESULT: FAIL.
    ASSERT_EQ(CountOccurrences(result.stdoutText, "RESULT: FAIL"), 1);

    // 3. ErrorCode is display_not_found.
    const FailEvent failEvent = ParseFailEvent(result.stdoutText);
    ASSERT_TRUE(failEvent.found);
    ASSERT_EQ(failEvent.errorCode, "display_not_found");

    // 4. No RESULT: STARTED.
    ASSERT_EQ(CountOccurrences(result.stdoutText, "RESULT: STARTED"), 0);

    // 5. final and partial do not exist.
    ASSERT_FALSE(FileExists(outputPath));
    ASSERT_FALSE(FileExists(partialPath));

    // 6. zero frames / bytes, no partial path.
    ASSERT_EQ(failEvent.framesCaptured, 0);
    ASSERT_EQ(failEvent.bytesWritten, 0);
    ASSERT_TRUE(failEvent.partialOutputPath.empty());
});

TEST_REGISTRAR(HelperEarlyFailure_BeginTimeout_NoStarted_NoPartial, []() {
    TempDir dir(L"wgc-test-helper-begin-timeout");
    const std::wstring outputPath = dir.path + L"\\out.mp4";
    const std::wstring partialPath = dir.path + L"\\out.partial.mp4";
    const std::wstring beginPath = dir.path + L"\\begin.signal";
    const std::wstring stopPath = dir.path + L"\\stop.signal";

    // Do NOT write begin signal; the helper will time out waiting for it.
    // Use a real display bounds so we pass display selection.
    HMONITOR primary = ::MonitorFromPoint({0, 0}, MONITOR_DEFAULTTOPRIMARY);
    MONITORINFO mi = {};
    mi.cbSize = sizeof(mi);
    std::wstring boundsArg = L"0,0,64,64";
    if (::GetMonitorInfoW(primary, &mi)) {
        const int width = mi.rcMonitor.right - mi.rcMonitor.left;
        const int height = mi.rcMonitor.bottom - mi.rcMonitor.top;
        boundsArg = std::to_wstring(mi.rcMonitor.left) + L"," +
                    std::to_wstring(mi.rcMonitor.top) + L"," +
                    std::to_wstring(width) + L"," + std::to_wstring(height);
    }

    std::vector<std::wstring> args = {
        L"--capture-continuous-display",
        L"--display-bounds", boundsArg,
        L"--recording-id", L"test-recording-175f",
        L"--output", outputPath,
        L"--duration-ms", L"2000",
        L"--fps", L"30",
        L"--begin-signal", beginPath,
        L"--begin-token", L"test-token-175f",
        L"--begin-timeout-ms", L"100",
        L"--stop-signal", stopPath,
        L"--i-understand-this-captures-screen"
    };

    const auto result = RunHelper(args, std::chrono::milliseconds(10000));

    ASSERT_NE(result.exitCode, 0);
    ASSERT_EQ(CountOccurrences(result.stdoutText, "RESULT: FAIL"), 1);
    ASSERT_EQ(CountOccurrences(result.stdoutText, "RESULT: STARTED"), 0);

    const FailEvent failEvent = ParseFailEvent(result.stdoutText);
    ASSERT_TRUE(failEvent.found);
    ASSERT_EQ(failEvent.errorCode, "timeout");
    ASSERT_FALSE(FileExists(outputPath));
    ASSERT_FALSE(FileExists(partialPath));
    ASSERT_EQ(failEvent.framesCaptured, 0);
    ASSERT_EQ(failEvent.bytesWritten, 0);
    ASSERT_TRUE(failEvent.partialOutputPath.empty());
});
