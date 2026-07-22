#include "test_framework.h"

#include "begin_gate.h"
#include "capture_lifecycle.h"
#include "capture_session.h"
#include "event_writer.h"
#include "options.h"
#include "path_policy.h"
#include "stop_watcher.h"
#include "string_utils.h"

#include <windows.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <future>
#include <iostream>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
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

std::wstring JoinPath(const std::wstring& dir, const std::wstring& name) {
    if (dir.empty()) return name;
    if (dir.back() == L'\\' || dir.back() == L'/') return dir + name;
    return dir + L"\\" + name;
}

Rect GetPrimaryMonitorBounds() {
    Rect rect{};
    auto callback = [](HMONITOR, HDC, LPRECT lprc, LPARAM data) -> BOOL {
        auto* out = reinterpret_cast<Rect*>(data);
        out->x = lprc->left;
        out->y = lprc->top;
        out->width = lprc->right - lprc->left;
        out->height = lprc->bottom - lprc->top;
        return FALSE; // stop at first
    };
    ::EnumDisplayMonitors(nullptr, nullptr, callback, reinterpret_cast<LPARAM>(&rect));
    return rect;
}

Options MakeContinuousOptions(const Rect& bounds,
                              const std::wstring& outputPath,
                              const std::wstring& beginSignalPath,
                              const std::wstring& stopSignalPath,
                              int durationMs = 10000) {
    Options opts;
    opts.mode = CaptureMode::ContinuousDisplay;
    opts.hasConsentFlag = true;
    opts.displayBounds = bounds;
    opts.recordingId = L"test-recording";
    opts.outputPath = outputPath;
    opts.durationMs = durationMs;
    opts.fps = 30;
    opts.beginSignalPath = beginSignalPath;
    opts.beginToken = L"test-token-175c";
    opts.beginTimeoutMs = 300000;
    opts.stopSignalPath = stopSignalPath;
    return opts;
}

std::wstring ValidateControlPathOrFail(const std::wstring& path) {
    PathPolicy policy = PathPolicy::CreateDefault();
    PathCheckResult result = ValidateControlPath(path, policy);
    ASSERT_TRUE(result.ok);
    return result.canonicalPath;
}

std::wstring ValidateOutputPathOrFail(const std::wstring& path) {
    PathPolicy policy = PathPolicy::CreateDefault();
    PathCheckResult result = ValidateOutputPath(path, policy);
    ASSERT_TRUE(result.ok);
    return result.canonicalPath;
}

bool FileExists(const std::wstring& path) {
    return ::GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES;
}

int64_t FileSize(const std::wstring& path) {
    WIN32_FILE_ATTRIBUTE_DATA attrs = {};
    if (!::GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attrs)) return 0;
    if (attrs.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) return 0;
    LARGE_INTEGER size = {};
    size.LowPart = attrs.nFileSizeLow;
    size.HighPart = attrs.nFileSizeHigh;
    return size.QuadPart;
}

// Tests that bypass the real encoder with onWriteFrame/onFinalize hooks do not
// create a partial file on disk. Because the production publish path moves the
// partial file to the final output, these tests must seed a synthetic partial
// file so the publish succeeds.
void WriteSyntheticPartial(const std::wstring& partialPath) {
    std::ofstream file(partialPath, std::ios::binary);
    file << "synthetic partial mp4 content";
}

// RAII wrapper for a std::thread that deterministically joins on destruction.
struct ScopedJoiningThread {
    std::thread thread;

    ~ScopedJoiningThread() {
        if (thread.joinable()) {
            thread.join();
        }
    }
};

// Runs a CaptureSession with a timeout. If the timeout fires, the cancel
// callback is invoked (e.g., writing the stop signal) and the runner is given
// a bounded cooperative shutdown window. The function never detaches. If the
// runner still has not exited after cancel, the test process is terminated
// fail-closed rather than allowing the suite to hang forever.
CaptureOutcome RunWithTimeout(CaptureSession& session,
                              const std::function<void()>& cancel,
                              std::chrono::milliseconds timeout) {
    std::packaged_task<CaptureOutcome()> task([&]() { return session.Run(); });
    auto future = task.get_future();
    std::thread runner(std::move(task));

    if (future.wait_for(timeout) == std::future_status::timeout) {
        cancel();
        // Bounded cooperative shutdown window. Well-behaved tests exit here.
        if (future.wait_for(std::chrono::milliseconds(5000)) == std::future_status::timeout) {
            std::cerr << "[TEST FATAL] RunWithTimeout: runner did not exit after cancel; "
                         "waiting for outer supervisor to terminate this worker\n";
            // Keep this function alive so the runner thread's captured references
            // remain valid. The outer supervisor will kill the worker process if a
            // production regression truly hangs here.
            while (true) {
                std::this_thread::sleep_for(std::chrono::seconds(1));
            }
        }
    }

    CaptureOutcome outcome = future.get();
    if (runner.joinable()) runner.join();
    return outcome;
}

CaptureOutcome RunWithStopCancel(CaptureSession& session,
                                 const std::wstring& stopPath,
                                 std::chrono::milliseconds timeout) {
    return RunWithTimeout(session,
                          [&]() { WriteFile(stopPath, L"stop"); },
                          timeout);
}

TEST_REGISTRAR(CaptureSessionStartCaptureExceptionReturnsFast, []() {
    TempDir dir(L"wgc-test-start-exc");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");

    WriteFile(beginPath, L"test-token-175c");

    Rect bounds = GetPrimaryMonitorBounds();
    Options opts = MakeContinuousOptions(bounds, outputPath, beginPath, stopPath);

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {
        throw std::runtime_error("injected StartCapture failure");
    };
    session.SetTestHooks(hooks);

    const auto start = std::chrono::steady_clock::now();
    CaptureOutcome outcome = RunWithStopCancel(session, stopPath, std::chrono::milliseconds(5000));
    const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - start).count();

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "start_capture_failed");
    ASSERT_LT(elapsed, 2000);
});

TEST_REGISTRAR(CaptureSessionStopSignalWakesMainLoop, []() {
    TempDir dir(L"wgc-test-stop-wake");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");

    WriteFile(beginPath, L"test-token-175c");

    Rect bounds = GetPrimaryMonitorBounds();
    Options opts = MakeContinuousOptions(bounds, outputPath, beginPath, stopPath);

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    std::atomic<bool> started{false};
    std::mutex startedMutex;
    std::condition_variable startedCv;

    std::atomic<bool> captureActiveFinished{false};

    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {
        // Do not start real capture; the watcher and main loop still run.
    };
    hooks.onCopyFrame = [](const QueuedFrame&, int width, int height,
                           std::vector<uint8_t>& outPixels) -> bool {
        outPixels.assign(static_cast<size_t>(width) * height * 4, 0xAB);
        return true;
    };
    hooks.onWriteFrame = [](const std::vector<uint8_t>&,
                            int64_t,
                            int64_t) -> EncoderResult {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onStarted = [&]() {
        {
            std::lock_guard<std::mutex> lock(startedMutex);
            started.store(true);
        }
        startedCv.notify_all();
    };
    hooks.onCaptureActive = [&](FrameQueue& queue) {
        // Pump a small number of frames quickly. Using fewer frames and
        // shorter delays keeps setup time well under the 500 ms duration
        // budget, while still guaranteeing the encoder sees at least one
        // frame before the stop signal arrives.
        int64_t timeHns = 0;
        for (int i = 0; i < 4; ++i) {
            QueuedFrame qf;
            qf.frame = nullptr;
            qf.systemRelativeTimeHns = timeHns;
            qf.contentWidth = 64;
            qf.contentHeight = 64;
            if (queue.Push(qf)) {
                timeHns += 333'333LL;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
        captureActiveFinished.store(true);
    };
    session.SetTestHooks(hooks);
    WriteSyntheticPartial(partialPath);

    std::packaged_task<CaptureOutcome()> task([&]() { return session.Run(); });
    auto future = task.get_future();
    ScopedJoiningThread runner{std::thread(std::move(task))};

    {
        std::unique_lock<std::mutex> lock(startedMutex);
        ASSERT_TRUE(startedCv.wait_for(lock, std::chrono::milliseconds(5000),
                                       [&]() { return started.load(); }));
    }

    // Wait until onCaptureActive has returned so the main loop is running
    // before the stop signal is issued.
    const auto activeDeadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(1000);
    while (!captureActiveFinished.load() && std::chrono::steady_clock::now() < activeDeadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    ASSERT_TRUE(captureActiveFinished.load());

    const auto stopWriteStart = std::chrono::steady_clock::now();
    WriteFile(stopPath, L"stop");

    ASSERT_EQ(future.wait_for(std::chrono::milliseconds(5000)), std::future_status::ready);
    CaptureOutcome outcome = future.get();

    const auto stopResponseMs = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - stopWriteStart).count();

    ASSERT_EQ(outcome.result, CaptureResult::Stopped);
    ASSERT_GT(outcome.framesCaptured, 0);
    // Main-loop stop decision must be fast; finalize/publish may add a small
    // constant, so the full response is allowed a bit more time.
    ASSERT_LT(outcome.durationMs, 500);
    ASSERT_LT(stopResponseMs, 1500);
});

TEST_REGISTRAR(CaptureSessionProgressEventIsWired, []() {
    TempDir dir(L"wgc-test-progress");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");

    WriteFile(beginPath, L"test-token-175c");

    Rect bounds = GetPrimaryMonitorBounds();
    Options opts = MakeContinuousOptions(bounds, outputPath, beginPath, stopPath);

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    std::atomic<int> writeCount{0};
    std::atomic<int> progressCount{0};
    std::atomic<int64_t> lastProgressBytes{0};
    std::atomic<int64_t> progressPartialSize{0};
    std::atomic<bool> started{false};

    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {};
    hooks.onCopyFrame = [](const QueuedFrame&, int width, int height,
                           std::vector<uint8_t>& outPixels) -> bool {
        outPixels.assign(static_cast<size_t>(width) * height * 4, 0xAB);
        return true;
    };
    hooks.onWriteFrame = [&](const std::vector<uint8_t>&,
                             int64_t,
                             int64_t) -> EncoderResult {
        writeCount.fetch_add(1);
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onStarted = [&]() { started.store(true); };
    hooks.onProgressSnapshot = [&](const ProgressSnapshot& snapshot) {
        progressCount.fetch_add(1);
        lastProgressBytes.store(snapshot.bytesWritten);
        progressPartialSize.store(FileSize(partialPath));
    };
    hooks.onCaptureActive = [](FrameQueue& queue) {
        int64_t timeHns = 0;
        for (int i = 0; i < 16; ++i) {
            QueuedFrame qf;
            qf.frame = nullptr;
            qf.systemRelativeTimeHns = timeHns;
            qf.contentWidth = 64;
            qf.contentHeight = 64;
            if (queue.Push(qf)) {
                timeHns += 333'333LL;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(30));
        }
    };
    session.SetTestHooks(hooks);
    WriteSyntheticPartial(partialPath);

    std::packaged_task<CaptureOutcome()> task([&]() { return session.Run(); });
    auto future = task.get_future();
    ScopedJoiningThread runner{std::thread(std::move(task))};

    // Wait until capture is active.
    const auto startedDeadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(5000);
    while (!started.load() && std::chrono::steady_clock::now() < startedDeadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    ASSERT_TRUE(started.load());

    // Wait long enough for at least one PROGRESS event to be emitted by the
    // production main loop (progress interval is 1000 ms).
    std::this_thread::sleep_for(std::chrono::milliseconds(1300));
    WriteFile(stopPath, L"stop");

    ASSERT_EQ(future.wait_for(std::chrono::milliseconds(5000)), std::future_status::ready);
    CaptureOutcome outcome = future.get();

    ASSERT_EQ(outcome.result, CaptureResult::Stopped);
    ASSERT_GT(progressCount.load(), 0);
    ASSERT_GT(writeCount.load(), 0);
    // PROGRESS bytes must come from the real partial file, not from raw input
    // pixel counts (64*64*4*writeCount would be in the hundreds of kilobytes).
    // Capture the partial size at snapshot time because the publish path moves
    // the partial to the final output before this assertion runs.
    ASSERT_EQ(lastProgressBytes.load(), progressPartialSize.load());
    ASSERT_LT(lastProgressBytes.load(), static_cast<int64_t>(64) * 64 * 4 * writeCount.load());
});

TEST_REGISTRAR(CaptureSessionEncoderInitFailedDistinctFromTimeout, []() {
    TempDir dir(L"wgc-test-encoder-fail");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");

    WriteFile(beginPath, L"test-token-175c");

    Rect bounds = GetPrimaryMonitorBounds();
    Options opts = MakeContinuousOptions(bounds, outputPath, beginPath, stopPath);

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        EncoderResult result;
        result.status = EncoderStatus::InitializeFailed;
        result.error = "sink_writer_rejected";
        result.hresult = "0x80004005";
        return result;
    };
    session.SetTestHooks(hooks);

    CaptureOutcome outcome = RunWithStopCancel(session, stopPath, std::chrono::milliseconds(5000));

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "encoder_init_failed");
    ASSERT_EQ(outcome.reason, "sink_writer_rejected");
    ASSERT_EQ(outcome.hresult, "0x80004005");
});

TEST_REGISTRAR(CaptureSessionLateFailurePreservesEvidence, []() {
    TempDir dir(L"wgc-test-late-evidence");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");

    WriteFile(beginPath, L"test-token-175c");

    Rect bounds = GetPrimaryMonitorBounds();
    Options opts = MakeContinuousOptions(bounds, outputPath, beginPath, stopPath);

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    constexpr int kFailAfterFrames = 5;
    std::atomic<int> copyCount{0};
    std::atomic<int> writeCount{0};

    CaptureSessionTestHooks hooks;
    hooks.onStartCapture = []() {};
    hooks.onCopyFrame = [&](const QueuedFrame&, int width, int height,
                            std::vector<uint8_t>& outPixels) -> bool {
        const int n = copyCount.fetch_add(1) + 1;
        if (n > kFailAfterFrames) {
            return false; // simulated GPU copy failure after some successes
        }
        outPixels.assign(static_cast<size_t>(width) * height * 4, static_cast<uint8_t>(n));
        return true;
    };
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onWriteFrame = [&](const std::vector<uint8_t>&,
                             int64_t,
                             int64_t) -> EncoderResult {
        writeCount.fetch_add(1);
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onCaptureActive = [](FrameQueue& queue) {
        int64_t timeHns = 0;
        for (int i = 0; i < 32; ++i) {
            QueuedFrame qf;
            qf.frame = nullptr;
            qf.systemRelativeTimeHns = timeHns;
            qf.contentWidth = 64;
            qf.contentHeight = 64;
            if (queue.Push(qf)) {
                timeHns += 333'333LL;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(30));
        }
    };
    session.SetTestHooks(hooks);
    WriteSyntheticPartial(partialPath);

    CaptureOutcome outcome = RunWithStopCancel(session, stopPath, std::chrono::milliseconds(5000));

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "d3d_copy_failed");
    ASSERT_EQ(outcome.framesCaptured, kFailAfterFrames);
    ASSERT_EQ(writeCount.load(), kFailAfterFrames);
    ASSERT_GT(outcome.bytesWritten, 0);
    ASSERT_EQ(outcome.bytesWritten, FileSize(partialPath));
    ASSERT_GT(outcome.durationMs, 0);
});

TEST_REGISTRAR(CaptureSessionStopPublishesFinalAndRemovesPartial, []() {
    TempDir dir(L"wgc-test-stop-publish");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");

    WriteFile(beginPath, L"test-token-175c");

    Rect bounds = GetPrimaryMonitorBounds();
    Options opts = MakeContinuousOptions(bounds, outputPath, beginPath, stopPath);

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    std::atomic<int> writeCount{0};
    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {};
    hooks.onCopyFrame = [](const QueuedFrame&, int width, int height,
                           std::vector<uint8_t>& outPixels) -> bool {
        outPixels.assign(static_cast<size_t>(width) * height * 4, 0xCD);
        return true;
    };
    hooks.onWriteFrame = [&](const std::vector<uint8_t>&,
                             int64_t,
                             int64_t) -> EncoderResult {
        writeCount.fetch_add(1);
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onCaptureActive = [](FrameQueue& queue) {
        int64_t timeHns = 0;
        for (int i = 0; i < 8; ++i) {
            QueuedFrame qf;
            qf.frame = nullptr;
            qf.systemRelativeTimeHns = timeHns;
            qf.contentWidth = 64;
            qf.contentHeight = 64;
            if (queue.Push(qf)) {
                timeHns += 333'333LL;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(30));
        }
    };
    session.SetTestHooks(hooks);
    WriteSyntheticPartial(partialPath);

    std::packaged_task<CaptureOutcome()> task([&]() { return session.Run(); });
    auto future = task.get_future();
    ScopedJoiningThread runner{std::thread(std::move(task))};

    // Wait until some frames have been written, then stop.
    while (writeCount.load() < 3) {
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    WriteFile(stopPath, L"stop");

    ASSERT_EQ(future.wait_for(std::chrono::milliseconds(5000)), std::future_status::ready);
    CaptureOutcome outcome = future.get();

    ASSERT_EQ(outcome.result, CaptureResult::Stopped);
    ASSERT_GT(outcome.framesCaptured, 0);
    ASSERT_TRUE(FileExists(outputPath));
    ASSERT_FALSE(FileExists(partialPath));
    ASSERT_EQ(FileSize(outputPath), outcome.bytesWritten);
});

TEST_REGISTRAR(CaptureSessionZeroFrameStopDoesNotReportSuccess, []() {
    TempDir dir(L"wgc-test-zero-stop");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");

    WriteFile(beginPath, L"test-token-175c");

    Rect bounds = GetPrimaryMonitorBounds();
    Options opts = MakeContinuousOptions(bounds, outputPath, beginPath, stopPath);

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    std::atomic<bool> started{false};
    std::mutex startedMutex;
    std::condition_variable startedCv;

    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {};
    hooks.onStarted = [&]() {
        {
            std::lock_guard<std::mutex> lock(startedMutex);
            started.store(true);
        }
        startedCv.notify_all();
    };
    session.SetTestHooks(hooks);

    std::packaged_task<CaptureOutcome()> task([&]() { return session.Run(); });
    auto future = task.get_future();
    ScopedJoiningThread runner{std::thread(std::move(task))};

    // Wait until the session is in the active loop before stopping. This
    // avoids racing with encoder initialization, which could otherwise report
    // stopped_before_capture instead of zero_frames.
    {
        std::unique_lock<std::mutex> lock(startedMutex);
        ASSERT_TRUE(startedCv.wait_for(lock, std::chrono::milliseconds(5000),
                                       [&]() { return started.load(); }));
    }

    // Stop without ever pushing a frame.
    WriteFile(stopPath, L"stop");

    ASSERT_EQ(future.wait_for(std::chrono::milliseconds(5000)), std::future_status::ready);
    CaptureOutcome outcome = future.get();

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "zero_frames");
    ASSERT_FALSE(FileExists(outputPath));
    ASSERT_FALSE(FileExists(partialPath));
});

TEST_REGISTRAR(CaptureSessionStoppedPublishFailureRetainsEvidence, []() {
    TempDir dir(L"wgc-test-publish-fail");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");

    WriteFile(beginPath, L"test-token-175c");

    Rect bounds = GetPrimaryMonitorBounds();
    Options opts = MakeContinuousOptions(bounds, outputPath, beginPath, stopPath);

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    std::atomic<int> writeCount{0};
    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {};
    hooks.onCopyFrame = [](const QueuedFrame&, int width, int height,
                           std::vector<uint8_t>& outPixels) -> bool {
        outPixels.assign(static_cast<size_t>(width) * height * 4, 0xEF);
        return true;
    };
    hooks.onWriteFrame = [&](const std::vector<uint8_t>&,
                             int64_t,
                             int64_t) -> EncoderResult {
        writeCount.fetch_add(1);
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onCaptureActive = [](FrameQueue& queue) {
        int64_t timeHns = 0;
        for (int i = 0; i < 8; ++i) {
            QueuedFrame qf;
            qf.frame = nullptr;
            qf.systemRelativeTimeHns = timeHns;
            qf.contentWidth = 64;
            qf.contentHeight = 64;
            if (queue.Push(qf)) {
                timeHns += 333'333LL;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(30));
        }
    };
    session.SetTestHooks(hooks);
    WriteSyntheticPartial(partialPath);

    // Make the output path a directory so MoveFileEx fails deterministically.
    fs::create_directories(outputPath);

    std::packaged_task<CaptureOutcome()> task([&]() { return session.Run(); });
    auto future = task.get_future();
    ScopedJoiningThread runner{std::thread(std::move(task))};

    while (writeCount.load() < 3) {
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    WriteFile(stopPath, L"stop");

    ASSERT_EQ(future.wait_for(std::chrono::milliseconds(5000)), std::future_status::ready);
    CaptureOutcome outcome = future.get();

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "publish_failed");
    ASSERT_GT(outcome.framesCaptured, 0);
    ASSERT_GT(outcome.bytesWritten, 0);
    ASSERT_EQ(outcome.bytesWritten, FileSize(partialPath));
});

TEST_REGISTRAR(CaptureSessionFrameCountIsAtomicAndMonotonic, []() {
    TempDir dir(L"wgc-test-atomic-count");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");

    WriteFile(beginPath, L"test-token-175c");

    Rect bounds = GetPrimaryMonitorBounds();
    Options opts = MakeContinuousOptions(bounds, outputPath, beginPath, stopPath);

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    std::atomic<int> writeCount{0};
    std::atomic<int> progressCount{0};
    std::atomic<int64_t> lastProgressFrames{0};
    std::atomic<int64_t> lastProgressBytes{0};
    std::atomic<int64_t> progressPartialSize{0};
    std::mutex progressMutex;
    std::condition_variable progressCv;
    bool sawProgress = false;

    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {};
    hooks.onCopyFrame = [](const QueuedFrame&, int width, int height,
                           std::vector<uint8_t>& outPixels) -> bool {
        outPixels.assign(static_cast<size_t>(width) * height * 4, 0x12);
        return true;
    };
    hooks.onWriteFrame = [&](const std::vector<uint8_t>&,
                             int64_t,
                             int64_t) -> EncoderResult {
        writeCount.fetch_add(1);
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onProgressSnapshot = [&](const ProgressSnapshot& snapshot) {
        progressCount.fetch_add(1);
        // The snapshot must never regress and must use the same source as the
        // terminal outcome.
        ASSERT_GE(snapshot.framesCaptured, lastProgressFrames.load());
        lastProgressFrames.store(snapshot.framesCaptured);
        ASSERT_GE(snapshot.bytesWritten, lastProgressBytes.load());
        lastProgressBytes.store(snapshot.bytesWritten);
        progressPartialSize.store(FileSize(partialPath));
        {
            std::lock_guard<std::mutex> lock(progressMutex);
            sawProgress = true;
        }
        progressCv.notify_all();
    };
    hooks.onCaptureActive = [](FrameQueue& queue) {
        int64_t timeHns = 0;
        for (int i = 0; i < 16; ++i) {
            QueuedFrame qf;
            qf.frame = nullptr;
            qf.systemRelativeTimeHns = timeHns;
            qf.contentWidth = 64;
            qf.contentHeight = 64;
            if (queue.Push(qf)) {
                timeHns += 333'333LL;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(30));
        }
    };
    session.SetTestHooks(hooks);
    WriteSyntheticPartial(partialPath);

    CaptureOutcome outcome = RunWithStopCancel(session, stopPath, std::chrono::milliseconds(5000));

    ASSERT_EQ(outcome.result, CaptureResult::Stopped);
    ASSERT_GT(outcome.framesCaptured, 0);
    ASSERT_EQ(outcome.framesCaptured, writeCount.load());
    ASSERT_EQ(outcome.framesCaptured, lastProgressFrames.load());
    // Compare against the partial size captured at snapshot time; the publish
    // path moves the partial to the final output before this assertion runs.
    ASSERT_EQ(lastProgressBytes.load(), progressPartialSize.load());
    {
        std::unique_lock<std::mutex> lock(progressMutex);
        ASSERT_TRUE(progressCv.wait_for(lock, std::chrono::milliseconds(100),
                                        [&]() { return sawProgress; }));
    }
    ASSERT_GT(progressCount.load(), 0);
});

TEST_REGISTRAR(CaptureSessionEncoderInitTimeoutReturnsBounded, []() {
    TempDir dir(L"wgc-test-init-timeout");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");

    WriteFile(beginPath, L"test-token-175c");

    Rect bounds = GetPrimaryMonitorBounds();
    // Use a short timeout so the test does not wait for the default minutes.
    Options opts = MakeContinuousOptions(bounds, outputPath, beginPath, stopPath);
    opts.beginTimeoutMs = 100;

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) -> EncoderResult {
        // The production wait timeout is beginTimeoutMs + 1000 ms. Sleep longer
        // than that so WaitForEncoderInit definitely times out.
        std::this_thread::sleep_for(std::chrono::milliseconds(1200));
        return EncoderResult{EncoderStatus::Ok};
    };
    session.SetTestHooks(hooks);

    const auto start = std::chrono::steady_clock::now();
    CaptureOutcome outcome = RunWithStopCancel(session, stopPath, std::chrono::milliseconds(5000));
    const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - start).count();

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "encoder_init_timeout");
    // Must be bounded: well under the default 300 s timeout plus margin.
    ASSERT_LT(elapsed, 3000);
});

TEST_REGISTRAR(StopSignalWatcherUsesCanonicalPath, []() {
    TempDir dir(L"wgc-test-watcher-path");
    const std::wstring alias = JoinPath(dir.path, L"subdir\\..\\stop.signal");
    const std::wstring canonical = ValidateControlPathOrFail(alias);

    std::atomic<bool> stopFlag{false};
    std::atomic<bool> userStopFlag{false};
    StopSignalWatcher watcher(alias, stopFlag, userStopFlag, nullptr);

    ASSERT_EQ(watcher.Path(), canonical);

    // Verify the watcher can actually observe the canonical stop file.
    watcher.Start();
    WriteFile(canonical, L"stop");
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(1000);
    while (std::chrono::steady_clock::now() < deadline && !stopFlag.load()) {
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    watcher.Stop();
    ASSERT_TRUE(stopFlag.load());
});

TEST_REGISTRAR(CaptureLifecycleSharedStateSurvivesDrainTimeout, []() {
    // Verifies that a callback holding a shared reference keeps the state alive
    // even after WaitForCallbacks times out, and that the state is destroyed
    // only after the callback exits.
    struct TrackedState {
        std::atomic<bool>& destroyed;
        explicit TrackedState(std::atomic<bool>& d) : destroyed(d) {}
        ~TrackedState() { destroyed.store(true); }
    };

    CaptureLifecycle lifecycle;
    std::atomic<bool> destroyed{false};
    auto shared = std::make_shared<TrackedState>(destroyed);

    std::atomic<bool> entered{false};
    std::atomic<bool> exitCallback{false};
    std::atomic<bool> drainTimedOut{false};

    std::thread callback([&]() {
        if (!lifecycle.TryEnterCallback()) {
            return;
        }
        entered.store(true);
        auto localRef = shared; // keep state alive during callback
        while (!exitCallback.load()) {
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
        lifecycle.ExitCallback();
    });

    // Wait until the callback has definitely entered.
    while (!entered.load()) {
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }

    std::thread drainer([&]() {
        lifecycle.StopAccepting();
        drainTimedOut.store(!lifecycle.WaitForCallbacks(std::chrono::milliseconds(200)));
    });

    drainer.join();
    ASSERT_TRUE(drainTimedOut.load());
    ASSERT_FALSE(destroyed.load());

    exitCallback.store(true);
    callback.join();

    // After the callback exits, the lifecycle and the test release their refs.
    shared.reset();
    ASSERT_TRUE(destroyed.load());
});

TEST_REGISTRAR(CaptureSessionCallbackDrainTimeoutMapsToTerminal, []() {
    TempDir dir(L"wgc-test-cb-drain");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");

    WriteFile(beginPath, L"test-token-175c");

    Rect bounds = GetPrimaryMonitorBounds();
    Options opts = MakeContinuousOptions(bounds, outputPath, beginPath, stopPath);

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    CaptureLifecycle* lifecyclePtr = nullptr;
    std::shared_ptr<void> stateHandle;
    std::atomic<bool> callbackEntered{false};
    std::atomic<bool> releaseCallback{false};
    std::thread callbackThread;

    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {};
    hooks.onStateCreated = [&](CaptureLifecycle* l, const std::shared_ptr<void>& h) {
        lifecyclePtr = l;
        stateHandle = h;
    };
    hooks.onCaptureActive = [&](FrameQueue& queue) {
        // Hold an active callback across teardown. This drives the production
        // WaitForCallbacks timeout path without requiring a real WGC event.
        callbackThread = std::thread([&]() {
            if (!lifecyclePtr || !lifecyclePtr->TryEnterCallback()) {
                return;
            }
            callbackEntered.store(true);
            while (!releaseCallback.load()) {
                std::this_thread::sleep_for(std::chrono::milliseconds(10));
            }
            lifecyclePtr->ExitCallback();
        });

        // Pump one frame so the session reaches the active loop.
        QueuedFrame qf;
        qf.frame = nullptr;
        qf.systemRelativeTimeHns = 0;
        qf.contentWidth = 64;
        qf.contentHeight = 64;
        queue.Push(qf);
    };
    hooks.onWriteFrame = [](const std::vector<uint8_t>&, int64_t, int64_t) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    session.SetTestHooks(hooks);

    std::packaged_task<CaptureOutcome()> task([&]() { return session.Run(); });
    auto future = task.get_future();
    ScopedJoiningThread runner{std::thread(std::move(task))};

    const auto enteredDeadline = std::chrono::steady_clock::now() +
                                 std::chrono::milliseconds(2000);
    while (!callbackEntered.load() &&
           std::chrono::steady_clock::now() < enteredDeadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    ASSERT_TRUE(callbackEntered.load());

    WriteFile(stopPath, L"stop");

    ASSERT_EQ(future.wait_for(std::chrono::milliseconds(8000)),
              std::future_status::ready);
    CaptureOutcome outcome = future.get();

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "callback_drain_timeout");

    releaseCallback.store(true);
    if (callbackThread.joinable()) callbackThread.join();
    stateHandle.reset();
});

TEST_REGISTRAR(CaptureSessionTerminalMarkCaptureEndedOnce, []() {
    TempDir dir(L"wgc-test-terminal-once");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");

    WriteFile(beginPath, L"test-token-175c");

    Rect bounds = GetPrimaryMonitorBounds();
    Options opts = MakeContinuousOptions(bounds, outputPath, beginPath, stopPath);

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    std::atomic<int> markCaptureEndedCount{0};
    std::atomic<int> writeCount{0};

    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {};
    hooks.onCopyFrame = [](const QueuedFrame&, int width, int height,
                           std::vector<uint8_t>& outPixels) -> bool {
        outPixels.assign(static_cast<size_t>(width) * height * 4, 0x34);
        return true;
    };
    hooks.onWriteFrame = [&](const std::vector<uint8_t>&, int64_t, int64_t) {
        writeCount.fetch_add(1);
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onMarkCaptureEnded = [&]() { markCaptureEndedCount.fetch_add(1); };
    hooks.onCaptureActive = [](FrameQueue& queue) {
        int64_t timeHns = 0;
        for (int i = 0; i < 4; ++i) {
            QueuedFrame qf;
            qf.frame = nullptr;
            qf.systemRelativeTimeHns = timeHns;
            qf.contentWidth = 64;
            qf.contentHeight = 64;
            if (queue.Push(qf)) {
                timeHns += 333'333LL;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(30));
        }
    };
    session.SetTestHooks(hooks);
    WriteSyntheticPartial(partialPath);

    CaptureOutcome outcome = RunWithStopCancel(session, stopPath,
                                               std::chrono::milliseconds(5000));

    ASSERT_EQ(outcome.result, CaptureResult::Stopped);
    ASSERT_GT(writeCount.load(), 0);
    ASSERT_EQ(markCaptureEndedCount.load(), 1);
});

TEST_REGISTRAR(CaptureSessionEncodingFailureRetainsEvidence, []() {
    TempDir dir(L"wgc-test-encode-fail");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");

    WriteFile(beginPath, L"test-token-175c");

    Rect bounds = GetPrimaryMonitorBounds();
    Options opts = MakeContinuousOptions(bounds, outputPath, beginPath, stopPath);

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    constexpr int kFailAfterFrames = 5;
    std::atomic<int> writeCount{0};

    CaptureSessionTestHooks hooks;
    hooks.onStartCapture = []() {};
    hooks.onCopyFrame = [](const QueuedFrame&, int width, int height,
                           std::vector<uint8_t>& outPixels) -> bool {
        outPixels.assign(static_cast<size_t>(width) * height * 4, 0x56);
        return true;
    };
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onWriteFrame = [&](const std::vector<uint8_t>&, int64_t, int64_t) {
        const int n = writeCount.fetch_add(1) + 1;
        if (n > kFailAfterFrames) {
            EncoderResult result;
            result.status = EncoderStatus::WriteFailed;
            result.error = "sample_rejected";
            result.hresult = "0xC00D36D5";
            return result;
        }
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onCaptureActive = [](FrameQueue& queue) {
        int64_t timeHns = 0;
        for (int i = 0; i < 32; ++i) {
            QueuedFrame qf;
            qf.frame = nullptr;
            qf.systemRelativeTimeHns = timeHns;
            qf.contentWidth = 64;
            qf.contentHeight = 64;
            if (queue.Push(qf)) {
                timeHns += 333'333LL;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(30));
        }
    };
    session.SetTestHooks(hooks);
    WriteSyntheticPartial(partialPath);

    CaptureOutcome outcome = RunWithStopCancel(session, stopPath,
                                               std::chrono::milliseconds(5000));

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "encoding_error");
    ASSERT_EQ(outcome.reason, "sample_rejected");
    ASSERT_EQ(outcome.hresult, "0xC00D36D5");
    ASSERT_EQ(outcome.framesCaptured, kFailAfterFrames);
    ASSERT_GT(outcome.bytesWritten, 0);
    ASSERT_EQ(outcome.bytesWritten, FileSize(partialPath));
});

// Captures std::cout for full-path stdout evidence tests below. The terminal
// event is emitted by the test after session.Run() returns (mirroring the
// contract in main.cpp); the capture object is kept alive until after the
// runner has joined, so a plain ostringstream buffer is sufficient.
struct StdoutCapture {
    std::ostringstream buffer;
    std::streambuf* old = nullptr;

    StdoutCapture() {
        old = std::cout.rdbuf(buffer.rdbuf());
    }

    ~StdoutCapture() {
        std::cout.rdbuf(old);
    }

    std::string str() {
        return buffer.str();
    }
};

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
        if (!inBlock) {
            continue;
        }
        if (line.empty()) {
            break;
        }
        const auto colon = line.find(": ");
        if (colon == std::string::npos) {
            continue;
        }
        const std::string key = line.substr(0, colon);
        const std::string value = line.substr(colon + 2);
        try {
            if (key == "ErrorCode") {
                event.errorCode = value;
            } else if (key == "Reason") {
                event.reason = value;
            } else if (key == "HRESULT") {
                event.hresult = value;
            } else if (key == "PartialOutputPath") {
                event.partialOutputPath = value;
            } else if (key == "FramesCaptured") {
                event.framesCaptured = std::stoll(value);
            } else if (key == "BytesWritten") {
                event.bytesWritten = std::stoll(value);
            }
        } catch (...) {
        }
    }
    return event;
}

struct StoppedEvent {
    bool found = false;
    int64_t framesCaptured = -1;
    int64_t fileSize = -1;
};

StoppedEvent ParseStoppedEvent(const std::string& output) {
    StoppedEvent event;
    std::istringstream stream(output);
    std::string line;
    bool inBlock = false;
    while (std::getline(stream, line)) {
        if (!line.empty() && line.back() == '\r') {
            line.pop_back();
        }
        if (line == "RESULT: STOPPED") {
            inBlock = true;
            event.found = true;
            continue;
        }
        if (!inBlock) {
            continue;
        }
        if (line.empty()) {
            break;
        }
        const auto colon = line.find(": ");
        if (colon == std::string::npos) {
            continue;
        }
        const std::string key = line.substr(0, colon);
        const std::string value = line.substr(colon + 2);
        try {
            if (key == "FramesCaptured") {
                event.framesCaptured = std::stoll(value);
            } else if (key == "FileSize") {
                // "12345 bytes" -> parse leading number.
                event.fileSize = std::stoll(value);
            }
        } catch (...) {
        }
    }
    return event;
}

TEST_REGISTRAR(CaptureSessionStdoutFailEvidenceOnPublishFailure, []() {
    TempDir dir(L"wgc-test-stdout-pub-fail");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");

    WriteFile(beginPath, L"test-token-175c");

    Rect bounds = GetPrimaryMonitorBounds();
    Options opts = MakeContinuousOptions(bounds, outputPath, beginPath, stopPath);

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    std::atomic<int> writeCount{0};
    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {};
    hooks.onCopyFrame = [](const QueuedFrame&, int width, int height,
                           std::vector<uint8_t>& outPixels) -> bool {
        outPixels.assign(static_cast<size_t>(width) * height * 4, 0xEF);
        return true;
    };
    hooks.onWriteFrame = [&](const std::vector<uint8_t>&,
                             int64_t,
                             int64_t) -> EncoderResult {
        writeCount.fetch_add(1);
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onCaptureActive = [](FrameQueue& queue) {
        int64_t timeHns = 0;
        for (int i = 0; i < 8; ++i) {
            QueuedFrame qf;
            qf.frame = nullptr;
            qf.systemRelativeTimeHns = timeHns;
            qf.contentWidth = 64;
            qf.contentHeight = 64;
            if (queue.Push(qf)) {
                timeHns += 333'333LL;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(30));
        }
    };
    session.SetTestHooks(hooks);
    WriteSyntheticPartial(partialPath);

    // Make the output path a directory so MoveFileEx fails deterministically.
    fs::create_directories(outputPath);

    StdoutCapture capture;
    CaptureOutcome outcome = RunWithStopCancel(session, stopPath,
                                               std::chrono::milliseconds(5000));
    WriteTerminalOutcome(writer, outcome);
    const std::string stdoutText = capture.str();

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "publish_failed");

    FailEvent failEvent = ParseFailEvent(stdoutText);
    if (!failEvent.found) {
        std::cerr << "DEBUG CAPTURED STDOUT:\n" << stdoutText << "\nEND DEBUG\n";
    }
    ASSERT_TRUE(failEvent.found);
    ASSERT_EQ(failEvent.errorCode, "publish_failed");
    ASSERT_GT(failEvent.framesCaptured, 0);
    ASSERT_EQ(failEvent.framesCaptured, outcome.framesCaptured);
    ASSERT_GT(failEvent.bytesWritten, 0);
    ASSERT_EQ(failEvent.bytesWritten, outcome.bytesWritten);
    ASSERT_EQ(failEvent.bytesWritten, FileSize(partialPath));
    ASSERT_FALSE(failEvent.partialOutputPath.empty());
    ASSERT_EQ(WideToUtf8(partialPath), failEvent.partialOutputPath);
});

TEST_REGISTRAR(CaptureSessionStdoutFailEvidenceOnEncodingFailure, []() {
    TempDir dir(L"wgc-test-stdout-enc-fail");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");

    WriteFile(beginPath, L"test-token-175c");

    Rect bounds = GetPrimaryMonitorBounds();
    Options opts = MakeContinuousOptions(bounds, outputPath, beginPath, stopPath);

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    constexpr int kFailAfterFrames = 5;
    std::atomic<int> writeCount{0};

    CaptureSessionTestHooks hooks;
    hooks.onStartCapture = []() {};
    hooks.onCopyFrame = [](const QueuedFrame&, int width, int height,
                           std::vector<uint8_t>& outPixels) -> bool {
        outPixels.assign(static_cast<size_t>(width) * height * 4, 0x56);
        return true;
    };
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onWriteFrame = [&](const std::vector<uint8_t>&, int64_t, int64_t) {
        const int n = writeCount.fetch_add(1) + 1;
        if (n > kFailAfterFrames) {
            EncoderResult result;
            result.status = EncoderStatus::WriteFailed;
            result.error = "sample_rejected";
            result.hresult = "0xC00D36D5";
            return result;
        }
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onCaptureActive = [](FrameQueue& queue) {
        int64_t timeHns = 0;
        for (int i = 0; i < 32; ++i) {
            QueuedFrame qf;
            qf.frame = nullptr;
            qf.systemRelativeTimeHns = timeHns;
            qf.contentWidth = 64;
            qf.contentHeight = 64;
            if (queue.Push(qf)) {
                timeHns += 333'333LL;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(30));
        }
    };
    session.SetTestHooks(hooks);
    WriteSyntheticPartial(partialPath);

    StdoutCapture capture;
    CaptureOutcome outcome = RunWithStopCancel(session, stopPath,
                                               std::chrono::milliseconds(5000));
    WriteTerminalOutcome(writer, outcome);
    const std::string stdoutText = capture.str();

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "encoding_error");

    FailEvent failEvent = ParseFailEvent(stdoutText);
    ASSERT_TRUE(failEvent.found);
    ASSERT_EQ(failEvent.errorCode, "encoding_error");
    ASSERT_EQ(failEvent.reason, "sample_rejected");
    ASSERT_EQ(failEvent.hresult, "0xC00D36D5");
    ASSERT_EQ(failEvent.framesCaptured, kFailAfterFrames);
    ASSERT_EQ(failEvent.framesCaptured, outcome.framesCaptured);
    ASSERT_GT(failEvent.bytesWritten, 0);
    ASSERT_EQ(failEvent.bytesWritten, outcome.bytesWritten);
    ASSERT_EQ(failEvent.bytesWritten, FileSize(partialPath));
    ASSERT_FALSE(failEvent.partialOutputPath.empty());
    ASSERT_EQ(WideToUtf8(partialPath), failEvent.partialOutputPath);
});

TEST_REGISTRAR(CaptureSessionStdoutZeroFrameOmitsPartial, []() {
    TempDir dir(L"wgc-test-stdout-zero");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");

    WriteFile(beginPath, L"test-token-175c");

    Rect bounds = GetPrimaryMonitorBounds();
    Options opts = MakeContinuousOptions(bounds, outputPath, beginPath, stopPath);

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    std::atomic<bool> started{false};
    std::mutex startedMutex;
    std::condition_variable startedCv;

    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {};
    hooks.onStarted = [&]() {
        {
            std::lock_guard<std::mutex> lock(startedMutex);
            started.store(true);
        }
        startedCv.notify_all();
    };
    session.SetTestHooks(hooks);

    StdoutCapture capture;
    std::packaged_task<CaptureOutcome()> task([&]() { return session.Run(); });
    auto future = task.get_future();
    ScopedJoiningThread runner{std::thread(std::move(task))};

    {
        std::unique_lock<std::mutex> lock(startedMutex);
        ASSERT_TRUE(startedCv.wait_for(lock, std::chrono::milliseconds(5000),
                                       [&]() { return started.load(); }));
    }

    WriteFile(stopPath, L"stop");

    ASSERT_EQ(future.wait_for(std::chrono::milliseconds(5000)), std::future_status::ready);
    CaptureOutcome outcome = future.get();
    WriteTerminalOutcome(writer, outcome);
    const std::string stdoutText = capture.str();

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "zero_frames");
    ASSERT_FALSE(FileExists(outputPath));
    ASSERT_FALSE(FileExists(partialPath));

    FailEvent failEvent = ParseFailEvent(stdoutText);
    ASSERT_TRUE(failEvent.found);
    ASSERT_EQ(failEvent.errorCode, "zero_frames");
    ASSERT_EQ(failEvent.framesCaptured, 0);
    ASSERT_EQ(failEvent.bytesWritten, 0);
    ASSERT_TRUE(failEvent.partialOutputPath.empty());
});

TEST_REGISTRAR(CaptureSessionStdoutStoppedTerminalEvidence, []() {
    TempDir dir(L"wgc-test-stdout-stopped");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");

    WriteFile(beginPath, L"test-token-175c");

    Rect bounds = GetPrimaryMonitorBounds();
    Options opts = MakeContinuousOptions(bounds, outputPath, beginPath, stopPath);

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    std::atomic<int> writeCount{0};
    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {};
    hooks.onCopyFrame = [](const QueuedFrame&, int width, int height,
                           std::vector<uint8_t>& outPixels) -> bool {
        outPixels.assign(static_cast<size_t>(width) * height * 4, 0xCD);
        return true;
    };
    hooks.onWriteFrame = [&](const std::vector<uint8_t>&,
                             int64_t,
                             int64_t) -> EncoderResult {
        writeCount.fetch_add(1);
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onCaptureActive = [](FrameQueue& queue) {
        int64_t timeHns = 0;
        for (int i = 0; i < 8; ++i) {
            QueuedFrame qf;
            qf.frame = nullptr;
            qf.systemRelativeTimeHns = timeHns;
            qf.contentWidth = 64;
            qf.contentHeight = 64;
            if (queue.Push(qf)) {
                timeHns += 333'333LL;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(30));
        }
    };
    session.SetTestHooks(hooks);
    WriteSyntheticPartial(partialPath);

    StdoutCapture capture;
    std::packaged_task<CaptureOutcome()> task([&]() { return session.Run(); });
    auto future = task.get_future();
    ScopedJoiningThread runner{std::thread(std::move(task))};

    while (writeCount.load() < 3) {
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    WriteFile(stopPath, L"stop");

    ASSERT_EQ(future.wait_for(std::chrono::milliseconds(5000)), std::future_status::ready);
    CaptureOutcome outcome = future.get();
    WriteTerminalOutcome(writer, outcome);
    const std::string stdoutText = capture.str();

    ASSERT_EQ(outcome.result, CaptureResult::Stopped);
    ASSERT_TRUE(FileExists(outputPath));
    ASSERT_FALSE(FileExists(partialPath));

    StoppedEvent stoppedEvent = ParseStoppedEvent(stdoutText);
    ASSERT_TRUE(stoppedEvent.found);
    ASSERT_GT(stoppedEvent.framesCaptured, 0);
    ASSERT_EQ(stoppedEvent.framesCaptured, outcome.framesCaptured);
    ASSERT_GT(stoppedEvent.fileSize, 0);
    ASSERT_EQ(stoppedEvent.fileSize, outcome.bytesWritten);
    ASSERT_EQ(stoppedEvent.fileSize, FileSize(outputPath));
});

void WriteBytes(const std::wstring& path, const std::string& content) {
    std::ofstream file(path, std::ios::binary);
    file.write(content.data(), static_cast<std::streamsize>(content.size()));
}

int CountSubstring(const std::string& text, const std::string& substring) {
    int count = 0;
    size_t pos = 0;
    while ((pos = text.find(substring, pos)) != std::string::npos) {
        ++count;
        ++pos;
    }
    return count;
}

TEST_REGISTRAR(NormalizeFailureEvidence_UsesCanonicalPartialSize, []() {
    TempDir dir(L"wgc-test-norm-canonical");
    const std::wstring canonicalPartial = JoinPath(dir.path, L"canonical.partial.mp4");
    const std::string content(37, 'x');
    ASSERT_EQ(content.size(), 37u);
    WriteBytes(canonicalPartial, content);

    CaptureOutcome outcome;
    outcome.result = CaptureResult::Failed;
    outcome.errorCode = "encoding_error";
    outcome.reason = "injected";
    // Outcome carries no partial path and no byte count.

    CaptureOutcome normalized = NormalizeFailureEvidence(outcome, canonicalPartial);

    ASSERT_EQ(normalized.result, CaptureResult::Failed);
    ASSERT_EQ(normalized.errorCode, "encoding_error");
    ASSERT_EQ(normalized.partialOutputPath, canonicalPartial);
    ASSERT_EQ(normalized.bytesWritten, 37);

    // Terminal output must reflect the canonical evidence.
    StdoutCapture capture;
    EventWriter writer;
    WriteTerminalOutcome(writer, normalized);
    const std::string stdoutText = capture.str();

    FailEvent failEvent = ParseFailEvent(stdoutText);
    ASSERT_TRUE(failEvent.found);
    ASSERT_EQ(failEvent.bytesWritten, 37);
    ASSERT_EQ(failEvent.partialOutputPath, WideToUtf8(canonicalPartial));
    ASSERT_EQ(CountSubstring(stdoutText, "RESULT: FAIL"), 1);
});

TEST_REGISTRAR(NormalizeFailureEvidence_OverwritesStaleEvidence, []() {
    TempDir dir(L"wgc-test-norm-stale");
    const std::wstring canonicalPartial = JoinPath(dir.path, L"canonical.partial.mp4");
    const std::wstring stalePartial = JoinPath(dir.path, L"stale.partial.mp4");
    WriteBytes(canonicalPartial, "canonical content is the real evidence");
    WriteBytes(stalePartial, "stale");

    CaptureOutcome outcome;
    outcome.result = CaptureResult::Failed;
    outcome.errorCode = "capture_failed";
    outcome.reason = "injected";
    outcome.partialOutputPath = stalePartial;
    outcome.bytesWritten = 99999;

    CaptureOutcome normalized = NormalizeFailureEvidence(outcome, canonicalPartial);

    ASSERT_EQ(normalized.partialOutputPath, canonicalPartial);
    ASSERT_EQ(normalized.bytesWritten, static_cast<int64_t>(FileSize(canonicalPartial)));
    ASSERT_NE(normalized.bytesWritten, 99999);
});

TEST_REGISTRAR(NormalizeFailureEvidence_RemovesEmptyPlaceholder, []() {
    TempDir dir(L"wgc-test-norm-empty");
    const std::wstring canonicalPartial = JoinPath(dir.path, L"empty.partial.mp4");

    // Create a 0-byte placeholder.
    std::ofstream placeholder(canonicalPartial, std::ios::binary);
    placeholder.close();
    ASSERT_TRUE(FileExists(canonicalPartial));

    CaptureOutcome outcome;
    outcome.result = CaptureResult::Failed;
    outcome.errorCode = "display_not_found";
    outcome.reason = "injected";
    outcome.partialOutputPath = canonicalPartial;
    outcome.bytesWritten = 12345;

    CaptureOutcome normalized = NormalizeFailureEvidence(outcome, canonicalPartial);

    ASSERT_TRUE(normalized.partialOutputPath.empty());
    ASSERT_EQ(normalized.bytesWritten, 0);
    ASSERT_FALSE(FileExists(canonicalPartial));
});

TEST_REGISTRAR(NormalizeFailureEvidence_LeavesSuccessAndStoppedUnchanged, []() {
    TempDir dir(L"wgc-test-norm-success");
    const std::wstring canonicalPartial = JoinPath(dir.path, L"ignored.partial.mp4");
    WriteBytes(canonicalPartial, "should be ignored");

    CaptureOutcome success;
    success.result = CaptureResult::Success;
    success.bytesWritten = 999;
    CaptureOutcome normalizedSuccess = NormalizeFailureEvidence(success, canonicalPartial);
    ASSERT_EQ(normalizedSuccess.result, CaptureResult::Success);
    ASSERT_EQ(normalizedSuccess.bytesWritten, 999);
    ASSERT_TRUE(normalizedSuccess.partialOutputPath.empty());

    CaptureOutcome stopped;
    stopped.result = CaptureResult::Stopped;
    stopped.bytesWritten = 111;
    CaptureOutcome normalizedStopped = NormalizeFailureEvidence(stopped, canonicalPartial);
    ASSERT_EQ(normalizedStopped.result, CaptureResult::Stopped);
    ASSERT_EQ(normalizedStopped.bytesWritten, 111);
    ASSERT_TRUE(normalizedStopped.partialOutputPath.empty());
});

TEST_REGISTRAR(WriteTerminalOutcome_UnknownResultEmitsInternalError, []() {
    CaptureOutcome outcome;
    outcome.result = static_cast<CaptureResult>(99);
    outcome.errorCode = "something";
    outcome.reason = "should be ignored";
    outcome.partialOutputPath = L"ignored";
    outcome.bytesWritten = 42;

    StdoutCapture capture;
    EventWriter writer;
    WriteTerminalOutcome(writer, outcome);
    const std::string stdoutText = capture.str();

    ASSERT_EQ(CountSubstring(stdoutText, "RESULT: FAIL"), 1);
    FailEvent failEvent = ParseFailEvent(stdoutText);
    ASSERT_TRUE(failEvent.found);
    ASSERT_EQ(failEvent.errorCode, "internal_error");
    ASSERT_NE(failEvent.reason.find("unexpected"), std::string::npos);
});

} // namespace
