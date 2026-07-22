#include "test_framework.h"
#include "string_utils.h"

#include <windows.h>

#include <chrono>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

namespace fs = std::filesystem;

namespace {

std::wstring GetSelfPath() {
    std::wstring path;
    path.resize(MAX_PATH);
    DWORD len = ::GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (len == 0) return L"";
    path.resize(len);
    return path;
}

std::string ReadAllText(HANDLE handle) {
    std::string output;
    char buffer[4096];
    DWORD read = 0;
    while (::ReadFile(handle, buffer, sizeof(buffer), &read, nullptr) && read > 0) {
        output.append(buffer, read);
    }
    return output;
}

std::vector<int> ReadDistinctPids(const std::wstring& path) {
    std::vector<int> pids;
    if (!fs::exists(path)) return pids;

    std::ifstream file(path);
    std::string line;
    while (std::getline(file, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        try {
            int pid = std::stoi(line);
            if (pid > 0 && std::find(pids.begin(), pids.end(), pid) == pids.end()) {
                pids.push_back(pid);
            }
        } catch (...) {
        }
    }
    return pids;
}

bool IsProcessRunning(int pid) {
    HANDLE h = ::OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, static_cast<DWORD>(pid));
    if (!h) return false;
    DWORD code = 0;
    BOOL got = ::GetExitCodeProcess(h, &code);
    ::CloseHandle(h);
    return got && code == STILL_ACTIVE;
}

void KillPidIfRunning(int pid) {
    HANDLE h = ::OpenProcess(PROCESS_TERMINATE, FALSE, static_cast<DWORD>(pid));
    if (h) {
        ::TerminateProcess(h, 1);
        ::WaitForSingleObject(h, 1000);
        ::CloseHandle(h);
    }
}

struct ProcessResult {
    int exitCode = -1;
    bool timedOut = false;
    std::string stdoutText;
    std::string stderrText;
};

std::string ExtractLastTestName(const std::string& stderrText) {
    std::string last;
    std::istringstream stream(stderrText);
    std::string line;
    while (std::getline(stream, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        const std::string prefix = "RUNALLTESTS_START ";
        if (line.rfind(prefix, 0) == 0) {
            last = line.substr(prefix.length());
        }
    }
    return last;
}

std::wstring ExtractWatchdogPidFile(const std::string& stderrText) {
    std::istringstream stream(stderrText);
    std::string line;
    while (std::getline(stream, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        const std::string prefix = "WATCHDOG_TREE_PID_FILE ";
        if (line.rfind(prefix, 0) == 0) {
            return wgc::TrimWide(wgc::Utf8ToWide(line.substr(prefix.length())));
        }
    }
    return {};
}

ProcessResult RunSupervisedWatchdog(int supervisorTimeoutMs) {
    ProcessResult result;

    const std::wstring selfPath = GetSelfPath();
    std::wstring cmdLine = L"\"" + selfPath +
                           L"\" --supervise-worker --supervisor-timeout-ms " +
                           std::to_wstring(supervisorTimeoutMs) +
                           L" --run-watchdog";

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
    std::vector<wchar_t> cmdLineBuf(cmdLine.begin(), cmdLine.end());
    cmdLineBuf.push_back(L'\0');

    BOOL created = ::CreateProcessW(
        nullptr, cmdLineBuf.data(), nullptr, nullptr, TRUE,
        CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP,
        nullptr, nullptr, &si, &pi);

    ::CloseHandle(stdoutWrite);
    ::CloseHandle(stderrWrite);

    if (!created) {
        ::CloseHandle(stdoutRead);
        ::CloseHandle(stderrRead);
        result.stderrText = "CreateProcess failed";
        return result;
    }

    // The supervisor itself should exit quickly after killing the hung worker.
    const DWORD waitResult = ::WaitForSingleObject(pi.hProcess, 30000);
    if (waitResult == WAIT_TIMEOUT) {
        ::TerminateProcess(pi.hProcess, 1);
        ::WaitForSingleObject(pi.hProcess, 5000);
        result.timedOut = true;
        result.exitCode = -1;
    } else {
        DWORD code = 0;
        if (::GetExitCodeProcess(pi.hProcess, &code)) {
            result.exitCode = static_cast<int>(code);
        }
    }

    result.stdoutText = ReadAllText(stdoutRead);
    result.stderrText = ReadAllText(stderrRead);

    ::CloseHandle(pi.hProcess);
    ::CloseHandle(pi.hThread);
    ::CloseHandle(stdoutRead);
    ::CloseHandle(stderrRead);

    return result;
}

} // namespace

// Spawns a three-layer process tree (parent -> child -> grandchild), writes
// every PID to a single file, creates a ready signal, and then hangs. This
// fixture is excluded from normal test runs and is only executed under the
// Job Object supervisor.
TEST_REGISTRAR(WATCHDOG_HangsIntentionally, []() {
    const fs::path tempDir = fs::temp_directory_path() /
        (std::wstring(L"wgc-watchdog-tree-") + std::to_wstring(::GetCurrentProcessId()));
    fs::create_directories(tempDir);

    const std::wstring pidFile = (tempDir / L"pids.txt").wstring();
    const std::wstring readyFile = (tempDir / L"ready.signal").wstring();

    std::cerr << "WATCHDOG_HANGS_INTENTIONALLY_ENTER\n";
    std::cerr << "WATCHDOG_TREE_PID_FILE " << wgc::WideToUtf8(pidFile) << "\n";

    // Write this process PID first (parent).
    {
        std::ofstream file(pidFile, std::ios::binary | std::ios::app);
        file << ::GetCurrentProcessId() << "\n";
        file.flush();
    }

    // Launch child that will launch grandchild.
    std::wstring selfPath = GetSelfPath();
    if (selfPath.empty()) {
        std::cerr << "WATCHDOG_HANGS_CANNOT_GET_SELF_PATH\n";
        while (true) {
            std::this_thread::sleep_for(std::chrono::seconds(1));
        }
    }

    std::wstring childCmd = L"\"" + selfPath +
        L"\" --worker-mode --run-watchdog --watchdog-child-depth 1 \"" +
        pidFile + L"\" \"" + readyFile + L"\"";

    SECURITY_ATTRIBUTES sa = {};
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = TRUE;

    STARTUPINFOW si = {};
    si.cb = sizeof(si);

    PROCESS_INFORMATION pi = {};
    std::vector<wchar_t> cmdLineBuf(childCmd.begin(), childCmd.end());
    cmdLineBuf.push_back(L'\0');

    BOOL created = ::CreateProcessW(
        nullptr, cmdLineBuf.data(), nullptr, nullptr, TRUE,
        CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi);

    if (!created) {
        std::cerr << "WATCHDOG_HANGS_CHILD_CREATE_FAILED\n";
        while (true) {
            std::this_thread::sleep_for(std::chrono::seconds(1));
        }
    }

    ::CloseHandle(pi.hProcess);
    ::CloseHandle(pi.hThread);

    // Wait for the grandchild to create the ready signal.
    const auto readyDeadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
    while (!fs::exists(readyFile) && std::chrono::steady_clock::now() < readyDeadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
    }

    if (!fs::exists(readyFile)) {
        std::cerr << "WATCHDOG_HANGS_READY_TIMEOUT\n";
    }

    // Hang so the supervisor must terminate the whole tree.
    while (true) {
        std::this_thread::sleep_for(std::chrono::seconds(1));
    }
});

// Helper fixture used by WATCHDOG_HangsIntentionally to build the second and
// third layers. It is invoked with --watchdog-child-depth and two paths.
TEST_REGISTRAR(WATCHDOG_TreeChild, []() {
    // This test is never registered for direct execution; it is parsed by
    // worker-mode main from the command line. We keep a minimal registrar so
    // the name exists in the registry, but the actual tree logic is handled
    // in test_main.cpp when it sees --watchdog-child-depth.
});

// Spawns the test executable as a supervisor running only the deliberate hang
// fixture, and verifies the supervisor kills the parent/child/grandchild tree
// within the configured timeout.
TEST_REGISTRAR(WatchdogSupervisor_KillsHangingWorker, []() {
    const auto start = std::chrono::steady_clock::now();
    const auto result = RunSupervisedWatchdog(3000);
    const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - start).count();

    // Supervisor must detect the hang and return non-zero.
    ASSERT_NE(result.exitCode, 0);

    // Supervisor stderr must identify the hanging test and the timeout.
    ASSERT_NE(result.stderrText.find("WATCHDOG_HangsIntentionally"), std::string::npos);
    ASSERT_NE(result.stderrText.find("SUPERVISOR TIMEOUT"), std::string::npos);

    // Parse the PID file written by the fixture and assert all three layers
    // were captured and are no longer running.
    const std::wstring pidFile = ExtractWatchdogPidFile(result.stderrText);
    const fs::path tempDir = pidFile.empty() ? fs::path() : fs::path(pidFile).parent_path();

    std::cerr << "[WATCHDOG_PID_FILE] " << wgc::WideToUtf8(pidFile) << "\n";

    if (!tempDir.empty()) {
        std::cerr << "[WATCHDOG_TEMP_DIR] " << wgc::WideToUtf8(tempDir.wstring())
                  << " exists=" << fs::exists(tempDir) << "\n";
        if (fs::exists(tempDir)) {
            for (const auto& entry : fs::directory_iterator(tempDir)) {
                std::cerr << "[WATCHDOG_DIR_ENTRY] " << wgc::WideToUtf8(entry.path().wstring()) << "\n";
            }
        }
    }

    std::vector<int> pids;
    const auto pidDeadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    while (pids.size() < 3 && std::chrono::steady_clock::now() < pidDeadline) {
        pids = ReadDistinctPids(pidFile);
        const DWORD attrs = ::GetFileAttributesW(pidFile.c_str());
        std::cerr << "[WATCHDOG_POLL] exists=" << fs::exists(pidFile)
                  << " size=" << (fs::exists(pidFile) ? fs::file_size(pidFile) : 0)
                  << " pids=" << pids.size()
                  << " attrs=" << attrs
                  << " lasterr=" << ::GetLastError() << "\n";
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
    }

    std::ostringstream report;
    report << "elapsed_ms=" << elapsed
           << " supervisor_exit=" << result.exitCode
           << " pid_count=" << pids.size();
    for (size_t i = 0; i < pids.size(); ++i) {
        report << " " << (i == 0 ? "parent" : i == 1 ? "child" : "grandchild")
               << "=" << pids[i];
    }
    std::cerr << "[WATCHDOG_REPORT] " << report.str() << "\n";

    struct WatchdogCleanup {
        std::vector<int>& pids;
        fs::path tempDir;

        ~WatchdogCleanup() {
            for (int pid : pids) {
                KillPidIfRunning(pid);
            }
            try { fs::remove_all(tempDir); } catch (...) {}
        }
    } cleanup{pids, tempDir};

    ASSERT_EQ(pids.size(), 3u);
    ASSERT_TRUE(pids[0] > 0 && pids[1] > 0 && pids[2] > 0);
    ASSERT_NE(pids[0], pids[1]);
    ASSERT_NE(pids[1], pids[2]);
    ASSERT_NE(pids[0], pids[2]);

    for (size_t i = 0; i < pids.size(); ++i) {
        const std::string label = i == 0 ? "parent" : i == 1 ? "child" : "grandchild";
        const bool running = IsProcessRunning(pids[i]);
        if (running) {
            std::cerr << "[WATCHDOG EVIDENCE] " << label << " process "
                      << pids[i] << " is still alive. " << report.str() << "\n";
        }
        ASSERT_TRUE(!running);
    }

});
