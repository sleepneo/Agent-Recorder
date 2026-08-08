#include "test_framework.h"

#include "dpi_context.h"
#include "probe.h"

#include <windows.h>

#include <filesystem>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>

using namespace wgc;

namespace {

namespace fs = std::filesystem;

std::wstring GetHelperExePath() {
    std::wstring path;
    path.resize(MAX_PATH);
    DWORD len = ::GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (len == 0) return L"";
    path.resize(len);
    fs::path testExe = path;
    return (testExe.parent_path() / L"wgc-native-helper.exe").wstring();
}

std::wstring GetHelperProjectRoot() {
    std::wstring path;
    path.resize(MAX_PATH);
    DWORD len = ::GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (len == 0) return L"";
    path.resize(len);
    fs::path testExe = path;
    // testExe is at tools/wgc-native-helper/bin/x64/Release/wgc-native-helper-tests.exe
    return testExe.parent_path().parent_path().parent_path().parent_path().wstring();
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

struct ParsedProbe {
    bool ok = false;
    bool windowCaptureSupportedPresent = false;
    bool windowCaptureSupported = false;
    std::string dpiAwareness;
    size_t monitorCount = 0;
    std::vector<ProbeMonitorInfo> monitors;
};

ParsedProbe ParseProbeOutput(const std::string& text) {
    ParsedProbe result;
    std::istringstream stream(text);
    std::string line;
    while (std::getline(stream, line)) {
        if (!line.empty() && line.back() == '\r') {
            line.pop_back();
        }
        if (line.rfind("RESULT: ", 0) == 0) {
            result.ok = (line == "RESULT: OK");
        } else if (line.rfind("DpiAwareness: ", 0) == 0) {
            result.dpiAwareness = line.substr(14);
        } else if (line.rfind("MonitorCount: ", 0) == 0) {
            try {
                result.monitorCount = static_cast<size_t>(std::stoull(line.substr(14)));
            } catch (...) {
            }
        } else if (line.rfind("WindowCaptureSupported: ", 0) == 0) {
            result.windowCaptureSupportedPresent = true;
            result.windowCaptureSupported = line == "WindowCaptureSupported: true";
        } else if (line.rfind("Monitor[", 0) == 0) {
            // Format: Monitor[i]: x=... y=... width=... height=... primary=...
            ProbeMonitorInfo info;
            size_t pos = 0;
            auto ExtractInt = [&](const std::string& key) -> int {
                size_t p = line.find(key + "=", pos);
                if (p == std::string::npos) return 0;
                p += key.length() + 1;
                size_t end = line.find_first_of(" ", p);
                if (end == std::string::npos) end = line.length();
                try {
                    return std::stoi(line.substr(p, end - p));
                } catch (...) {
                    return 0;
                }
            };
            auto ExtractBool = [&](const std::string& key) -> bool {
                size_t p = line.find(key + "=", pos);
                if (p == std::string::npos) return false;
                p += key.length() + 1;
                size_t end = line.find_first_of(" ", p);
                if (end == std::string::npos) end = line.length();
                return line.substr(p, end - p) == "true";
            };
            info.bounds.x = ExtractInt("x");
            info.bounds.y = ExtractInt("y");
            info.bounds.width = ExtractInt("width");
            info.bounds.height = ExtractInt("height");
            info.primary = ExtractBool("primary");
            result.monitors.push_back(info);
        }
    }
    return result;
}

std::string ReadFileText(const std::wstring& path) {
    std::ifstream file(path, std::ios::binary);
    if (!file) return "";
    return std::string(std::istreambuf_iterator<char>(file),
                       std::istreambuf_iterator<char>());
}

// Fake DPI context for deterministic failure-branch testing.
struct FakeDpiContext : public IDpiContext {
    bool setProcessResult = true;
    unsigned long lastError = 0;
    void* currentContext = nullptr;
    bool compareResult = true;
    bool apiMissing = false;

    bool SetProcessDpiAwarenessContext(void* /*context*/) override {
        if (apiMissing) {
            return false;
        }
        return setProcessResult;
    }

    void* GetThreadDpiAwarenessContext() override {
        if (apiMissing) {
            return nullptr;
        }
        return currentContext;
    }

    bool AreDpiAwarenessContextsEqual(void* a, void* b) override {
        if (apiMissing) {
            return false;
        }
        if (compareResult) {
            return a == b;
        }
        return false;
    }

    unsigned long GetLastError() override {
        if (apiMissing) {
            return ERROR_NOT_SUPPORTED;
        }
        return lastError;
    }
};

} // namespace

TEST_REGISTRAR(DpiContext_InitializeReturnsPerMonitorV2, []() {
    DpiContextResult result = InitializeDpiAwareness();
    ASSERT_TRUE(result.ok);
    ASSERT_EQ(result.awareness, DpiAwareness::PerMonitorV2);
    ASSERT_TRUE(result.errorReason.empty());
});

TEST_REGISTRAR(DpiContext_GetCurrentAwarenessIsPerMonitorV2, []() {
    ASSERT_EQ(GetCurrentDpiAwareness(), DpiAwareness::PerMonitorV2);
});

TEST_REGISTRAR(DpiContext_StringValuesAreStable, []() {
    ASSERT_EQ(std::string(DpiAwarenessToString(DpiAwareness::PerMonitorV2)), "per_monitor_v2");
    ASSERT_EQ(std::string(DpiAwarenessToString(DpiAwareness::PerMonitor)), "per_monitor");
    ASSERT_EQ(std::string(DpiAwarenessToString(DpiAwareness::SystemAware)), "system_aware");
    ASSERT_EQ(std::string(DpiAwarenessToString(DpiAwareness::Unaware)), "unaware");
});

TEST_REGISTRAR(DpiContext_SetterSuccessAndV2Succeeds, []() {
    FakeDpiContext fake;
    fake.setProcessResult = true;
    fake.currentContext = DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2;
    fake.lastError = 0;

    DpiContextResult result = InitializeDpiAwareness(&fake);
    ASSERT_TRUE(result.ok);
    ASSERT_EQ(result.awareness, DpiAwareness::PerMonitorV2);
    ASSERT_TRUE(result.errorReason.empty());
});

TEST_REGISTRAR(DpiContext_AccessDeniedAndV2Succeeds, []() {
    FakeDpiContext fake;
    fake.setProcessResult = false;
    fake.lastError = ERROR_ACCESS_DENIED;
    fake.currentContext = DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2;

    DpiContextResult result = InitializeDpiAwareness(&fake);
    ASSERT_TRUE(result.ok);
    ASSERT_EQ(result.awareness, DpiAwareness::PerMonitorV2);
    ASSERT_TRUE(result.errorReason.empty());
});

TEST_REGISTRAR(DpiContext_AccessDeniedNotV2Fails, []() {
    FakeDpiContext fake;
    fake.setProcessResult = false;
    fake.lastError = ERROR_ACCESS_DENIED;
    fake.currentContext = DPI_AWARENESS_CONTEXT_SYSTEM_AWARE;

    DpiContextResult result = InitializeDpiAwareness(&fake);
    ASSERT_FALSE(result.ok);
    ASSERT_EQ(result.errorCode, "dpi_awareness_init_failed");
    ASSERT_NE(result.errorReason.find("not Per-Monitor V2"), std::string::npos);
});

TEST_REGISTRAR(DpiContext_OtherSetterErrorFails, []() {
    FakeDpiContext fake;
    fake.setProcessResult = false;
    fake.lastError = ERROR_INVALID_PARAMETER;

    DpiContextResult result = InitializeDpiAwareness(&fake);
    ASSERT_FALSE(result.ok);
    ASSERT_EQ(result.errorCode, "dpi_awareness_init_failed");
});

TEST_REGISTRAR(DpiContext_MissingApiFails, []() {
    FakeDpiContext fake;
    fake.apiMissing = true;
    fake.lastError = ERROR_NOT_SUPPORTED;

    DpiContextResult result = InitializeDpiAwareness(&fake);
    ASSERT_FALSE(result.ok);
    ASSERT_EQ(result.errorCode, "dpi_awareness_init_failed");
});

TEST_REGISTRAR(DpiContext_SetterSuccessButNotV2Fails, []() {
    FakeDpiContext fake;
    fake.setProcessResult = true;
    fake.currentContext = DPI_AWARENESS_CONTEXT_SYSTEM_AWARE;

    DpiContextResult result = InitializeDpiAwareness(&fake);
    ASSERT_FALSE(result.ok);
    ASSERT_EQ(result.errorCode, "dpi_awareness_init_failed");
    ASSERT_NE(result.errorReason.find("not Per-Monitor V2"), std::string::npos);
});

TEST_REGISTRAR(Manifest_ContainsPerMonitorV2, []() {
    const std::wstring root = GetHelperProjectRoot();
    const std::wstring manifestPath = root + L"\\src\\wgc-native-helper.exe.manifest";
    const std::string text = ReadFileText(manifestPath);
    ASSERT_NE(text.find("permonitorv2"), std::string::npos);
});

TEST_REGISTRAR(Probe_OutputContainsDpiAwarenessAndMonitors, []() {
    const auto result = RunHelper({L"--probe"}, std::chrono::milliseconds(10000));
    ASSERT_EQ(result.exitCode, 0);

    const ParsedProbe probe = ParseProbeOutput(result.stdoutText);
    ASSERT_TRUE(probe.ok);
    ASSERT_TRUE(probe.windowCaptureSupportedPresent);
    ASSERT_EQ(probe.dpiAwareness, "per_monitor_v2");
    ASSERT_GE(probe.monitorCount, 1u);
    ASSERT_EQ(probe.monitors.size(), probe.monitorCount);

    int primaryCount = 0;
    for (const auto& m : probe.monitors) {
        ASSERT_GT(m.bounds.width, 0);
        ASSERT_GT(m.bounds.height, 0);
        if (m.primary) {
            primaryCount++;
        }
    }
    ASSERT_EQ(primaryCount, 1);
});

TEST_REGISTRAR(ProbeResult_WindowCapabilityFalseKeepsDisplayPrerequisitesReady, []() {
    ProbeResult result;
    result.wgcSupported = true;
    result.d3d11Initialized = true;
    result.encoderCreated = true;
    result.windowCaptureSupported = false;

    ASSERT_TRUE(HasSharedCaptureCapabilities(result));
    ASSERT_FALSE(result.windowCaptureSupported);
});

TEST_REGISTRAR(Probe_PrimaryMonitorBoundsMatchWin32Physical, []() {
    // Query the primary monitor bounds from Win32 in the same Per-Monitor V2
    // awareness context used by the helper.
    HMONITOR primary = ::MonitorFromPoint({0, 0}, MONITOR_DEFAULTTOPRIMARY);
    MONITORINFO mi = {};
    mi.cbSize = sizeof(mi);
    ASSERT_TRUE(::GetMonitorInfoW(primary, &mi));

    const auto result = RunHelper({L"--probe"}, std::chrono::milliseconds(10000));
    ASSERT_EQ(result.exitCode, 0);
    const ParsedProbe probe = ParseProbeOutput(result.stdoutText);
    ASSERT_TRUE(probe.ok);

    const ProbeMonitorInfo* primaryInfo = nullptr;
    for (const auto& m : probe.monitors) {
        if (m.primary) {
            primaryInfo = &m;
            break;
        }
    }
    ASSERT_TRUE(primaryInfo != nullptr);
    ASSERT_EQ(primaryInfo->bounds.x, mi.rcMonitor.left);
    ASSERT_EQ(primaryInfo->bounds.y, mi.rcMonitor.top);
    ASSERT_EQ(primaryInfo->bounds.width, mi.rcMonitor.right - mi.rcMonitor.left);
    ASSERT_EQ(primaryInfo->bounds.height, mi.rcMonitor.bottom - mi.rcMonitor.top);
});
