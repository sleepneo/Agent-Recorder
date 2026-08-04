#include "test_framework.h"
#include "string_utils.h"

#include <windows.h>
#include <winrt/Windows.Foundation.h>

#include <chrono>
#include <filesystem>
#include <fstream>
#include <future>
#include <iostream>
#include <string>
#include <thread>
#include <utility>
#include <vector>

namespace fs = std::filesystem;

namespace {

// Closes a Windows handle on destruction; safe for null/invalid handles.
class ScopedHandle {
public:
    ScopedHandle() = default;
    explicit ScopedHandle(HANDLE handle) : handle_(handle) {}
    ScopedHandle(const ScopedHandle&) = delete;
    ScopedHandle& operator=(const ScopedHandle&) = delete;
    ScopedHandle(ScopedHandle&& other) noexcept
        : handle_(other.handle_) { other.handle_ = nullptr; }
    ScopedHandle& operator=(ScopedHandle&& other) noexcept {
        if (this != &other) {
            Close();
            handle_ = other.handle_;
            other.handle_ = nullptr;
        }
        return *this;
    }
    ~ScopedHandle() { Close(); }

    HANDLE Get() const { return handle_; }
    void Reset(HANDLE handle = nullptr) {
        Close();
        handle_ = handle;
    }
    HANDLE Release() {
        HANDLE h = handle_;
        handle_ = nullptr;
        return h;
    }
    bool IsValid() const { return handle_ != nullptr && handle_ != INVALID_HANDLE_VALUE; }

private:
    void Close() {
        if (handle_ != nullptr && handle_ != INVALID_HANDLE_VALUE) {
            ::CloseHandle(handle_);
        }
    }
    HANDLE handle_ = nullptr;
};

struct ParsedArgs {
    bool worker = false;
    bool supervise = false;
    bool runWatchdog = false;
    int supervisorTimeoutMs = 600000; // 10 minutes default for full suite.
    bool filterSpecified = false;
    std::string filter;
    std::string parseError;
    int watchdogChildDepth = -1;      // -1 means "not a tree child".
    std::wstring watchdogPidFile;
    std::wstring watchdogReadyFile;
    std::vector<std::wstring> passthrough;
};

ParsedArgs ParseArgs(int argc, wchar_t* argv[]) {
    ParsedArgs result;
    for (int i = 1; i < argc; ++i) {
        std::wstring arg = argv[i];
        if (arg == L"--worker-mode") {
            result.worker = true;
        } else if (arg == L"--supervise-worker") {
            result.supervise = true;
        } else if (arg == L"--run-watchdog") {
            result.runWatchdog = true;
        } else if (arg == L"--supervisor-timeout-ms" && i + 1 < argc) {
            try {
                result.supervisorTimeoutMs = std::stoi(argv[++i]);
            } catch (...) {
                result.supervisorTimeoutMs = 600000;
            }
        } else if (arg == L"--filter") {
            if (i + 1 >= argc || std::wstring(argv[i + 1]).rfind(L"--", 0) == 0) {
                result.parseError = "--filter requires a non-empty value";
                continue;
            }
            result.filterSpecified = true;
            result.filter = wgc::WideToUtf8(argv[++i]);
            if (result.filter.empty()) {
                result.parseError = "--filter requires a non-empty value";
            }
        } else if (arg == L"--watchdog-child-depth" && i + 1 < argc) {
            try {
                result.watchdogChildDepth = std::stoi(argv[++i]);
            } catch (...) {
                result.watchdogChildDepth = -1;
            }
        } else if (!result.watchdogPidFile.empty()) {
            result.watchdogReadyFile = arg;
        } else if (result.watchdogChildDepth >= 0) {
            result.watchdogPidFile = arg;
        } else {
            result.passthrough.push_back(arg);
        }
    }
    return result;
}

std::wstring GetSelfPath() {
    std::wstring path;
    path.resize(MAX_PATH);
    DWORD len = ::GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (len == 0) return L"";
    path.resize(len);
    return path;
}

std::wstring BuildCommandLine(const std::wstring& selfPath,
                              const std::vector<std::wstring>& passthrough,
                              bool runWatchdog,
                              const std::string& filter) {
    std::wstring cmd = L"\"" + selfPath + L"\" --worker-mode";
    if (runWatchdog) {
        cmd += L" --run-watchdog";
    }
    if (!filter.empty()) {
        cmd += L" --filter \"" + wgc::Utf8ToWide(filter) + L"\"";
    }
    for (const auto& a : passthrough) {
        cmd += L" \"" + a + L"\"";
    }
    return cmd;
}

// Helper used by the WATCHDOG_HangsIntentionally fixture to build the second
// and third layers of the process tree. Writes this process PID, optionally
// spawns a deeper child, waits for the deepest child to signal readiness,
// and then hangs so the Job Object supervisor must clean up the tree.
int RunWatchdogTreeChild(int depth,
                         const std::wstring& pidFile,
                         const std::wstring& readyFile) {
    if (pidFile.empty()) {
        std::cerr << "[WATCHDOG CHILD FATAL] missing PID file\n";
        return 1;
    }

    {
        std::ofstream file(pidFile, std::ios::binary | std::ios::app);
        file << ::GetCurrentProcessId() << "\n";
        file.flush();
    }

    if (depth > 0) {
        const std::wstring selfPath = GetSelfPath();
        if (selfPath.empty()) {
            std::cerr << "[WATCHDOG CHILD FATAL] cannot determine self path\n";
            return 1;
        }

        const std::wstring childCmd = L"\"" + selfPath +
            L"\" --worker-mode --run-watchdog --watchdog-child-depth " +
            std::to_wstring(depth - 1) +
            L" \"" + pidFile + L"\" \"" + readyFile + L"\"";

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
            std::cerr << "[WATCHDOG CHILD FATAL] failed to spawn deeper child\n";
            return 1;
        }

        ::CloseHandle(pi.hProcess);
        ::CloseHandle(pi.hThread);

        // Wait for the deepest descendant to create the ready signal before
        // hanging. This prevents the supervisor from killing the root before
        // the full tree has been created.
        const auto readyDeadline = std::chrono::steady_clock::now() +
                                   std::chrono::seconds(10);
        while (!fs::exists(readyFile) &&
               std::chrono::steady_clock::now() < readyDeadline) {
            std::this_thread::sleep_for(std::chrono::milliseconds(50));
        }

        if (!fs::exists(readyFile)) {
            std::cerr << "[WATCHDOG CHILD WARNING] ready signal timeout\n";
        }
    } else {
        // This is the deepest layer; create the ready signal and hang.
        std::ofstream file(readyFile, std::ios::binary);
        file << "ready\n";
        file.flush();
    }

    std::cerr << "WATCHDOG_TREE_PID_FILE " << wgc::WideToUtf8(pidFile) << "\n";
    std::cerr << "WATCHDOG_TREE_DEPTH " << depth << " PID "
              << ::GetCurrentProcessId() << "\n";

    while (true) {
        std::this_thread::sleep_for(std::chrono::seconds(1));
    }
    return 0;
}

std::string ReadAllText(const std::wstring& path) {
    std::ifstream file(path, std::ios::binary);
    if (!file) return "";
    return std::string((std::istreambuf_iterator<char>(file)),
                       std::istreambuf_iterator<char>());
}

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

int RunAsSupervisor(const std::vector<std::wstring>& passthrough,
                    int timeoutMs,
                    bool runWatchdog,
                    const std::string& filter) {
    const std::wstring selfPath = GetSelfPath();
    if (selfPath.empty()) {
        std::cerr << "[SUPERVISOR FATAL] cannot determine self path\n";
        return 1;
    }

    const fs::path tempDir = fs::temp_directory_path() /
        (std::wstring(L"wgc-tests-supervise-") + std::to_wstring(::GetCurrentProcessId()));
    fs::create_directories(tempDir);

    const std::wstring stdoutPath = (tempDir / L"stdout.log").wstring();
    const std::wstring stderrPath = (tempDir / L"stderr.log").wstring();

    SECURITY_ATTRIBUTES sa = {};
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = TRUE;

    ScopedHandle stdoutRead;
    ScopedHandle stdoutWrite;
    ScopedHandle stderrRead;
    ScopedHandle stderrWrite;

    HANDLE hStdOutRead = nullptr;
    HANDLE hStdOutWrite = nullptr;
    if (!::CreatePipe(&hStdOutRead, &hStdOutWrite, &sa, 0)) {
        std::cerr << "[SUPERVISOR FATAL] failed to create output pipes\n";
        fs::remove_all(tempDir);
        return 1;
    }
    stdoutRead.Reset(hStdOutRead);
    stdoutWrite.Reset(hStdOutWrite);

    if (!::SetHandleInformation(stdoutRead.Get(), HANDLE_FLAG_INHERIT, 0)) {
        std::cerr << "[SUPERVISOR FATAL] failed to create output pipes\n";
        fs::remove_all(tempDir);
        return 1;
    }

    HANDLE hStdErrRead = nullptr;
    HANDLE hStdErrWrite = nullptr;
    if (!::CreatePipe(&hStdErrRead, &hStdErrWrite, &sa, 0)) {
        std::cerr << "[SUPERVISOR FATAL] failed to create output pipes\n";
        fs::remove_all(tempDir);
        return 1;
    }
    stderrRead.Reset(hStdErrRead);
    stderrWrite.Reset(hStdErrWrite);

    if (!::SetHandleInformation(stderrRead.Get(), HANDLE_FLAG_INHERIT, 0)) {
        std::cerr << "[SUPERVISOR FATAL] failed to create output pipes\n";
        fs::remove_all(tempDir);
        return 1;
    }

    // Job Object: all processes created by the worker belong to this job.
    // Closing the job handle terminates any remaining descendants.
    ScopedHandle job(::CreateJobObjectW(nullptr, nullptr));
    if (!job.IsValid()) {
        std::cerr << "[SUPERVISOR FATAL] failed to create job object\n";
        fs::remove_all(tempDir);
        return 1;
    }

    JOBOBJECT_EXTENDED_LIMIT_INFORMATION jobLimit = {};
    jobLimit.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!::SetInformationJobObject(job.Get(), JobObjectExtendedLimitInformation,
                                   &jobLimit, sizeof(jobLimit))) {
        std::cerr << "[SUPERVISOR FATAL] failed to configure job object\n";
        fs::remove_all(tempDir);
        return 1;
    }

    STARTUPINFOW si = {};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdOutput = stdoutWrite.Get();
    si.hStdError = stderrWrite.Get();

    PROCESS_INFORMATION pi = {};
    const std::wstring cmdLine = BuildCommandLine(selfPath, passthrough, runWatchdog, filter);

    std::vector<wchar_t> cmdLineBuf(cmdLine.begin(), cmdLine.end());
    cmdLineBuf.push_back(L'\0');

    // Create the worker suspended so we can assign it to the job before it
    // creates any children that might escape the job.
    BOOL created = ::CreateProcessW(
        nullptr, cmdLineBuf.data(), nullptr, nullptr, TRUE,
        CREATE_NO_WINDOW | CREATE_SUSPENDED,
        nullptr, nullptr, &si, &pi);

    stdoutWrite.Reset();
    stderrWrite.Reset();

    if (!created) {
        std::cerr << "[SUPERVISOR FATAL] failed to spawn worker\n";
        fs::remove_all(tempDir);
        return 1;
    }

    ScopedHandle workerProcess(pi.hProcess);
    ScopedHandle workerThread(pi.hThread);

    if (!::AssignProcessToJobObject(job.Get(), workerProcess.Get())) {
        std::cerr << "[SUPERVISOR FATAL] failed to assign worker to job object\n";
        // Worker is still suspended; terminate it cleanly before exiting.
        ::TerminateProcess(workerProcess.Get(), 1);
        ::WaitForSingleObject(workerProcess.Get(), 5000);
        fs::remove_all(tempDir);
        return 1;
    }

    if (::ResumeThread(workerThread.Get()) == static_cast<DWORD>(-1)) {
        std::cerr << "[SUPERVISOR FATAL] failed to resume worker\n";
        ::TerminateJobObject(job.Get(), 1);
        ::WaitForSingleObject(workerProcess.Get(), 5000);
        fs::remove_all(tempDir);
        return 1;
    }

    // Stream output into files so we can report it if the worker hangs, while
    // also forwarding it to the supervisor's own stdout/stderr so the build log
    // remains useful. Each drain runs in its own thread with a bounded future
    // wait so inherited pipe handles cannot block shutdown forever.
    auto DrainToFileAndConsole = [](HANDLE pipe, const std::wstring& path, FILE* console) {
        std::ofstream out(path, std::ios::binary);
        char buffer[4096];
        DWORD read = 0;
        while (::ReadFile(pipe, buffer, sizeof(buffer), &read, nullptr) && read > 0) {
            out.write(buffer, read);
            fwrite(buffer, 1, read, console);
        }
    };

    std::packaged_task<void()> stdoutTask([&]() {
        DrainToFileAndConsole(stdoutRead.Get(), stdoutPath, stdout);
    });
    std::packaged_task<void()> stderrTask([&]() {
        DrainToFileAndConsole(stderrRead.Get(), stderrPath, stderr);
    });

    auto stdoutFuture = stdoutTask.get_future();
    auto stderrFuture = stderrTask.get_future();

    std::thread stdoutThread([&]() { stdoutTask(); });
    std::thread stderrThread([&]() { stderrTask(); });

    const DWORD waitResult = ::WaitForSingleObject(workerProcess.Get(), static_cast<DWORD>(timeoutMs));

    bool timedOut = (waitResult == WAIT_TIMEOUT);
    int exitCode = 0;
    if (timedOut) {
        // Terminate the entire job (worker + all descendants).
        ::TerminateJobObject(job.Get(), 2);
        ::WaitForSingleObject(workerProcess.Get(), 5000);
        exitCode = 1;
    } else {
        DWORD code = 0;
        if (::GetExitCodeProcess(workerProcess.Get(), &code)) {
            exitCode = static_cast<int>(code);
        }
    }

    // Closing the job handle kills any descendants that outlived the root.
    // This must happen before waiting for pipe drains so descendants cannot
    // keep the inherited write handles open.
    job.Reset();

    // Wait for drain threads with a bounded timeout. If a descendant kept a
    // pipe handle open, close our read end to unblock ReadFile, then wait.
    if (stdoutFuture.wait_for(std::chrono::milliseconds(5000)) == std::future_status::timeout) {
        stdoutRead.Reset();
    }
    if (stderrFuture.wait_for(std::chrono::milliseconds(5000)) == std::future_status::timeout) {
        stderrRead.Reset();
    }

    if (stdoutThread.joinable()) stdoutThread.join();
    if (stderrThread.joinable()) stderrThread.join();

    stdoutRead.Reset();
    stderrRead.Reset();

    const std::string stdoutText = ReadAllText(stdoutPath);
    const std::string stderrText = ReadAllText(stderrPath);

    if (timedOut) {
        const std::string lastTest = ExtractLastTestName(stderrText);
        std::cerr << "\n[SUPERVISOR TIMEOUT] worker did not finish within "
                  << timeoutMs << " ms\n";
        if (!lastTest.empty()) {
            std::cerr << "[SUPERVISOR TIMEOUT] last test started: " << lastTest << "\n";
        }
        std::cerr << "[SUPERVISOR TIMEOUT] --- worker stdout tail ---\n"
                  << stdoutText << "\n";
        std::cerr << "[SUPERVISOR TIMEOUT] --- worker stderr tail ---\n"
                  << stderrText << "\n";
        std::cerr << "[SUPERVISOR TIMEOUT] -------------------------\n";
    }

    fs::remove_all(tempDir);
    return exitCode;
}

} // namespace

int wmain(int argc, wchar_t* argv[]) {
    const auto args = ParseArgs(argc, argv);

    if (!args.parseError.empty()) {
        std::cerr << "[TEST ARG ERROR] " << args.parseError << "\n";
        return 2;
    }

    if (args.supervise) {
        return RunAsSupervisor(args.passthrough, args.supervisorTimeoutMs, args.runWatchdog, args.filter);
    }

    // Default behavior: supervise the worker so a hung test cannot block the
    // build forever. Explicit --worker-mode disables supervision (used by the
    // supervisor itself and for debugging).
    if (!args.worker) {
        return RunAsSupervisor(args.passthrough, args.supervisorTimeoutMs, args.runWatchdog, args.filter);
    }

    // Worker mode: if this is a watchdog tree child, build the next layer and
    // hang. This must happen before initializing WinRT because the fixture is
    // intentionally simple and never returns.
    if (args.runWatchdog && args.watchdogChildDepth >= 0) {
        return RunWatchdogTreeChild(args.watchdogChildDepth,
                                    args.watchdogPidFile,
                                    args.watchdogReadyFile);
    }

    // Worker mode: run the actual tests under WinRT MTA.
    // The test executable embeds the same Per-Monitor V2 manifest as the
    // helper, so every thread (including those spawned by CaptureSession tests)
    // enumerates displays in the same physical-pixel coordinate space.
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    std::cerr << "TEST_MAIN_ENTER\n";
    int result = args.runWatchdog
        ? wgc::test::RunWatchdogTests(args.filter)
        : wgc::test::RunAllTests(args.filter);
    std::cerr << "TEST_MAIN_EXIT " << result << "\n";
    winrt::uninit_apartment();
    return result;
}
