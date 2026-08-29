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
#include <d3d11.h>
#include <wrl/client.h>

#include <atomic>
#include <chrono>
#include <climits>
#include <cstdlib>
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

TEST_REGISTRAR(CopyTextureRegionToBgra_UsesProductionGpuCropWithNonZeroOffset, []() {
    Microsoft::WRL::ComPtr<ID3D11Device> device;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context;
    const UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    const D3D_FEATURE_LEVEL featureLevels[] = {
        D3D_FEATURE_LEVEL_11_1,
        D3D_FEATURE_LEVEL_11_0,
        D3D_FEATURE_LEVEL_10_1,
        D3D_FEATURE_LEVEL_10_0
    };

    HRESULT hr = D3D11CreateDevice(
        nullptr,
        D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        flags,
        featureLevels,
        static_cast<UINT>(std::size(featureLevels)),
        D3D11_SDK_VERSION,
        device.GetAddressOf(),
        nullptr,
        context.GetAddressOf());
    if (FAILED(hr)) {
        // WARP is still a real D3D11 device and exercises the production GPU
        // staging/copy path. It is an explicit fallback for hosts without a
        // hardware adapter, never a CPU crop substitute.
        hr = D3D11CreateDevice(
            nullptr,
            D3D_DRIVER_TYPE_WARP,
            nullptr,
            flags,
            featureLevels,
            static_cast<UINT>(std::size(featureLevels)),
            D3D11_SDK_VERSION,
            device.ReleaseAndGetAddressOf(),
            nullptr,
            context.ReleaseAndGetAddressOf());
    }
    ASSERT_TRUE(SUCCEEDED(hr));
    ASSERT_TRUE(device != nullptr);
    ASSERT_TRUE(context != nullptr);

    constexpr int sourceWidth = 8;
    constexpr int sourceHeight = 8;
    constexpr UINT sourceRowPitch = sourceWidth * 4 + 16;
    std::vector<uint8_t> source(static_cast<size_t>(sourceRowPitch) * sourceHeight, 0xEE);
    for (int y = 0; y < sourceHeight; ++y) {
        for (int x = 0; x < sourceWidth; ++x) {
            const size_t offset = static_cast<size_t>(y) * sourceRowPitch + static_cast<size_t>(x) * 4;
            source[offset + 0] = static_cast<uint8_t>(0x10 + x);
            source[offset + 1] = static_cast<uint8_t>(0x20 + y);
            source[offset + 2] = static_cast<uint8_t>(0x80 + x + y);
            source[offset + 3] = 0xFF;
        }
    }

    D3D11_TEXTURE2D_DESC sourceDesc = {};
    sourceDesc.Width = sourceWidth;
    sourceDesc.Height = sourceHeight;
    sourceDesc.MipLevels = 1;
    sourceDesc.ArraySize = 1;
    sourceDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    sourceDesc.SampleDesc.Count = 1;
    sourceDesc.Usage = D3D11_USAGE_DEFAULT;
    D3D11_SUBRESOURCE_DATA initialData = {};
    initialData.pSysMem = source.data();
    initialData.SysMemPitch = sourceRowPitch;

    Microsoft::WRL::ComPtr<ID3D11Texture2D> texture;
    hr = device->CreateTexture2D(&sourceDesc, &initialData, texture.GetAddressOf());
    ASSERT_TRUE(SUCCEEDED(hr));
    ASSERT_TRUE(texture != nullptr);

    std::vector<uint8_t> pixels;
    hr = CopyTextureRegionToBgra(device.Get(), texture.Get(), 2, 3, 3, 2, pixels);
    ASSERT_TRUE(SUCCEEDED(hr));
    ASSERT_EQ(pixels.size(), static_cast<size_t>(3 * 2 * 4));

    for (int y = 0; y < 2; ++y) {
        for (int x = 0; x < 3; ++x) {
            const size_t offset = static_cast<size_t>(y * 3 + x) * 4;
            ASSERT_EQ(pixels[offset + 0], static_cast<uint8_t>(0x12 + x));
            ASSERT_EQ(pixels[offset + 1], static_cast<uint8_t>(0x23 + y));
            ASSERT_EQ(pixels[offset + 2], static_cast<uint8_t>(0x85 + x + y));
            ASSERT_EQ(pixels[offset + 3], 0xFF);
        }
    }

    auto expectFailureAndClear = [&](int offsetX, int offsetY, int width, int height) {
        pixels.assign(11, 0xAB);
        const HRESULT invalid = CopyTextureRegionToBgra(
            device.Get(), texture.Get(), offsetX, offsetY, width, height, pixels);
        ASSERT_TRUE(FAILED(invalid));
        ASSERT_TRUE(pixels.empty());
    };
    expectFailureAndClear(-1, 0, 2, 2);
    expectFailureAndClear(0, -1, 2, 2);
    expectFailureAndClear(0, 0, 0, 2);
    expectFailureAndClear(0, 0, 2, 0);
    expectFailureAndClear(INT_MAX, 0, INT_MAX, 2);
    expectFailureAndClear(7, 7, 2, 2);
});

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

Options MakeContinuousWindowOptions(const Rect& bounds,
                                    const std::wstring& outputPath,
                                    const std::wstring& beginSignalPath,
                                    const std::wstring& stopSignalPath,
                                    int durationMs = 10000) {
    Options opts = MakeContinuousOptions(bounds, outputPath, beginSignalPath,
                                         stopSignalPath, durationMs);
    opts.mode = CaptureMode::ContinuousWindow;
    opts.windowHwnd = 0x1234;
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

const char* CaptureResultName(CaptureResult result) {
    switch (result) {
        case CaptureResult::Success: return "success";
        case CaptureResult::Stopped: return "stopped";
        case CaptureResult::Failed: return "failed";
        default: return "unknown";
    }
}

void PrintOutcomeDiagnostics(const char* testName, const CaptureOutcome& outcome,
                            const std::wstring& partialPath) {
    std::cerr << "[CAPTURE_OUTCOME] test=" << testName
              << " result=" << CaptureResultName(outcome.result)
              << " error_code=" << outcome.errorCode
              << " reason=" << outcome.reason
              << " hresult=" << outcome.hresult
              << " frames=" << outcome.framesCaptured
              << " dropped=" << outcome.framesDropped
              << " duration_ms=" << outcome.durationMs
              << " bytes=" << outcome.bytesWritten
              << " width=" << outcome.width
              << " height=" << outcome.height
              << " partial_present=" << (FileExists(partialPath) ? "true" : "false")
              << " partial_size=" << FileSize(partialPath)
              << "\n";
}

void PrintReadyFutureOutcome(const char* testName, std::future<CaptureOutcome>& future,
                             const std::wstring& partialPath) {
    if (future.wait_for(std::chrono::milliseconds(0)) == std::future_status::ready) {
        const CaptureOutcome outcome = future.get();
        PrintOutcomeDiagnostics(testName, outcome, partialPath);
    } else {
        std::cerr << "[CAPTURE_OUTCOME] test=" << testName
                  << " result=not_ready error_code=not_returned\n";
    }
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
            HANDLE nativeThread = reinterpret_cast<HANDLE>(thread.native_handle());
            if (::WaitForSingleObject(nativeThread, 5000) == WAIT_OBJECT_0) {
                thread.join();
            } else {
                std::cerr << "[TEST FATAL] runner thread did not exit within cleanup deadline\n";
                ::TerminateProcess(::GetCurrentProcess(), 2);
                std::abort();
            }
        }
    }
};

void RunDisplayUnavailableSignalTest(CaptureMode mode, const wchar_t* prefix) {
    TempDir dir(prefix);
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");
    WriteFile(beginPath, L"test-token-175c");

    Rect bounds = GetPrimaryMonitorBounds();
    Options opts = MakeContinuousOptions(bounds, outputPath, beginPath, stopPath, 5000);
    opts.mode = mode;
    opts.regionBounds = bounds;
    if (mode == CaptureMode::ContinuousRegion) {
        opts.regionBounds.x = bounds.x;
        opts.regionBounds.y = bounds.y;
        opts.regionBounds.width = 64;
        opts.regionBounds.height = 64;
    }

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    std::function<void()> signalUnavailable;
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
    hooks.onTestSignalsCreated = [&](const CaptureSessionTestSignals& signals) {
        signalUnavailable = signals.signalDisplayUnavailable;
    };
    hooks.onCopyFrame = [](const QueuedFrame&, int width, int height,
                           std::vector<uint8_t>& outPixels) {
        outPixels.assign(static_cast<size_t>(width) * height * 4, 0x71);
        return true;
    };
    hooks.onWriteFrame = [](const std::vector<uint8_t>&, int64_t, int64_t) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onCaptureActive = [](FrameQueue& queue) {
        QueuedFrame frame;
        frame.systemRelativeTimeHns = 0;
        frame.contentWidth = 64;
        frame.contentHeight = 64;
        queue.Push(frame);
    };
    session.SetTestHooks(hooks);
    WriteSyntheticPartial(partialPath);

    std::packaged_task<CaptureOutcome()> task([&]() { return session.Run(); });
    auto future = task.get_future();
    ScopedJoiningThread runner{std::thread(std::move(task))};

    {
        std::unique_lock<std::mutex> lock(startedMutex);
        ASSERT_TRUE(startedCv.wait_for(lock, std::chrono::milliseconds(3000),
                                       [&]() { return started.load(); }));
    }
    ASSERT_TRUE(static_cast<bool>(signalUnavailable));
    signalUnavailable();
    signalUnavailable();
    ASSERT_EQ(future.wait_for(std::chrono::milliseconds(3000)), std::future_status::ready);
    CaptureOutcome outcome = future.get();

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "display_unavailable");
    ASSERT_FALSE(FileExists(outputPath));
    ASSERT_TRUE(FileExists(partialPath));
    ASSERT_GT(outcome.bytesWritten, 0);
}

void RunDisplayMonitorScenario(CaptureMode mode,
                               bool targetGone,
                               bool queryThrows,
                               const wchar_t* prefix) {
    TempDir dir(prefix);
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");
    WriteFile(beginPath, L"test-token-175c");

    Rect displayBounds = GetPrimaryMonitorBounds();
    Options opts = MakeContinuousOptions(displayBounds, outputPath, beginPath, stopPath, 5000);
    opts.mode = mode;
    opts.regionBounds = displayBounds;
    if (mode == CaptureMode::ContinuousRegion) {
        opts.regionBounds.x = displayBounds.x;
        opts.regionBounds.y = displayBounds.y;
        opts.regionBounds.width = 64;
        opts.regionBounds.height = 64;
    }

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    std::atomic<bool> started{false};
    std::atomic<bool> gone{false};
    std::atomic<int> queryCount{0};
    std::atomic<int> markCaptureEndedCount{0};
    std::mutex startedMutex;
    std::condition_variable startedCv;
    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {};
    hooks.onMarkCaptureEnded = [&]() {
        markCaptureEndedCount.fetch_add(1);
    };
    hooks.onStarted = [&]() {
        started.store(true);
        startedCv.notify_all();
    };
    hooks.onDisplayAvailabilityQuery = [&](const Rect&) {
        queryCount.fetch_add(1);
        if (queryThrows) {
            throw std::runtime_error("injected display topology query failure");
        }
        return !gone.load();
    };
    hooks.onCopyFrame = [](const QueuedFrame&, int width, int height,
                           std::vector<uint8_t>& outPixels) {
        outPixels.assign(static_cast<size_t>(width) * height * 4, 0x72);
        return true;
    };
    hooks.onWriteFrame = [](const std::vector<uint8_t>&, int64_t, int64_t) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onCaptureActive = [](FrameQueue& queue) {
        QueuedFrame frame;
        frame.systemRelativeTimeHns = 0;
        frame.contentWidth = 64;
        frame.contentHeight = 64;
        queue.Push(frame);
    };
    session.SetTestHooks(hooks);
    WriteSyntheticPartial(partialPath);

    std::packaged_task<CaptureOutcome()> task([&]() { return session.Run(); });
    auto future = task.get_future();
    ScopedJoiningThread runner{std::thread(std::move(task))};

    {
        std::unique_lock<std::mutex> lock(startedMutex);
        ASSERT_TRUE(startedCv.wait_for(lock, std::chrono::milliseconds(3000),
                                       [&]() { return started.load(); }));
    }

    if (targetGone) {
        gone.store(true);
    } else if (!queryThrows) {
        std::this_thread::sleep_for(std::chrono::milliseconds(150));
        ASSERT_GT(queryCount.load(), 0);
        ASSERT_EQ(future.wait_for(std::chrono::milliseconds(0)), std::future_status::timeout);
        WriteFile(stopPath, L"stop");
    }

    ASSERT_EQ(future.wait_for(std::chrono::milliseconds(3000)), std::future_status::ready);
    CaptureOutcome outcome = future.get();
    ASSERT_GT(queryCount.load(), 0);

    if (targetGone || queryThrows) {
        ASSERT_EQ(outcome.result, CaptureResult::Failed);
        ASSERT_EQ(outcome.errorCode, "display_unavailable");
        ASSERT_FALSE(FileExists(outputPath));
        ASSERT_TRUE(FileExists(partialPath));
        ASSERT_GT(outcome.bytesWritten, 0);
        ASSERT_EQ(markCaptureEndedCount.load(), 1);
    } else {
        ASSERT_EQ(outcome.result, CaptureResult::Stopped);
        ASSERT_TRUE(FileExists(outputPath));
        ASSERT_FALSE(FileExists(partialPath));
        ASSERT_EQ(markCaptureEndedCount.load(), 1);
    }
}

[[noreturn]] void TerminateTestProcess(const char* reason) {
    std::cerr << "[TEST FATAL] " << reason << "\n";
    ::TerminateProcess(::GetCurrentProcess(), 2);
    std::abort();
}

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
            TerminateTestProcess("RunWithTimeout runner did not exit after cancel");
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
    PrintOutcomeDiagnostics("CaptureSessionStartCaptureExceptionReturnsFast", outcome, partialPath);
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
        if (!started.load()) {
            PrintReadyFutureOutcome("CaptureSessionStopSignalWakesMainLoop", future, partialPath);
        }
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
    if (!started.load()) {
        PrintReadyFutureOutcome("CaptureSessionProgressEventIsWired", future, partialPath);
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
    PrintOutcomeDiagnostics("CaptureSessionEncoderInitFailedDistinctFromTimeout", outcome, partialPath);

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "encoder_init_failed");
    ASSERT_EQ(outcome.reason, "sink_writer_rejected");
    ASSERT_EQ(outcome.hresult, "0x80004005");
});

TEST_REGISTRAR(CaptureSessionLegacyEncoderHookRejectedForHardwarePreferred, []() {
    TempDir dir(L"wgc-test-legacy-hardware-policy");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");

    WriteFile(beginPath, L"test-token-175c");
    Rect bounds = GetPrimaryMonitorBounds();
    Options opts = MakeContinuousOptions(bounds, outputPath, beginPath, stopPath);
    opts.encoderMode = EncoderMode::HardwarePreferred;

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);
    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    session.SetTestHooks(hooks);

    CaptureOutcome outcome = RunWithStopCancel(session, stopPath, std::chrono::milliseconds(5000));

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "encoder_init_failed");
    ASSERT_TRUE(outcome.reason.find("encoder_selection_policy_mismatch") != std::string::npos);
    ASSERT_FALSE(FileExists(outputPath));
    ASSERT_FALSE(FileExists(partialPath));
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
    PrintOutcomeDiagnostics("CaptureSessionLateFailurePreservesEvidence", outcome, partialPath);

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
    const auto writeDeadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(5000);
    while (writeCount.load() < 3 && std::chrono::steady_clock::now() < writeDeadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    ASSERT_GE(writeCount.load(), 3);
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

    const auto writeDeadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(5000);
    while (writeCount.load() < 3 && std::chrono::steady_clock::now() < writeDeadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    ASSERT_GE(writeCount.load(), 3);
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
        const auto callbackDeadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(5000);
        while (!exitCallback.load() && std::chrono::steady_clock::now() < callbackDeadline) {
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
        lifecycle.ExitCallback();
    });

    // Wait until the callback has definitely entered.
    const auto enteredDeadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(2000);
    while (!entered.load() && std::chrono::steady_clock::now() < enteredDeadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    if (!entered.load()) {
        exitCallback.store(true);
        if (callback.joinable()) callback.join();
    }
    ASSERT_TRUE(entered.load());

    std::thread drainer([&]() {
        lifecycle.StopAccepting();
        drainTimedOut.store(!lifecycle.WaitForCallbacks(std::chrono::milliseconds(200)));
    });

    drainer.join();
    ASSERT_TRUE(drainTimedOut.load());
    ASSERT_FALSE(destroyed.load());

    exitCallback.store(true);
    if (callback.joinable()) callback.join();

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
            const auto callbackDeadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(10000);
            while (!releaseCallback.load() && std::chrono::steady_clock::now() < callbackDeadline) {
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
    if (!callbackEntered.load()) {
        releaseCallback.store(true);
        if (callbackThread.joinable()) callbackThread.join();
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

    const auto writeDeadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(5000);
    while (writeCount.load() < 3 && std::chrono::steady_clock::now() < writeDeadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    ASSERT_GE(writeCount.load(), 3);
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

TEST_REGISTRAR(CaptureSessionTargetCreationRoutesDisplayAndWindowRequests, []() {
    TempDir dir(L"wgc-test-target-routing");

    auto run = [&](CaptureMode mode, const Rect& bounds, std::uint64_t hwnd,
                   CaptureSessionTestTargetRequest& observed) {
        const std::wstring suffix = mode == CaptureMode::ContinuousWindow
            ? L"window" : (mode == CaptureMode::ContinuousRegion ? L"region" : L"display");
        const std::wstring outputPath = JoinPath(dir.path, suffix + L".mp4");
        const std::wstring partialPath = JoinPath(dir.path, suffix + L".partial.mp4");
        const std::wstring beginPath = JoinPath(dir.path, suffix + L".begin");
        const std::wstring stopPath = JoinPath(dir.path, suffix + L".stop");

        Options opts = MakeContinuousOptions(bounds, outputPath, beginPath, stopPath, 1000);
        opts.mode = mode;
        opts.windowHwnd = hwnd;
        if (mode == CaptureMode::ContinuousRegion) {
            opts.regionBounds = {bounds.x + 10, bounds.y + 20, 640, 480};
        }

        EventWriter writer;
        BeginGate gate(opts.beginSignalPath, opts.beginToken,
                       opts.stopSignalPath, opts.beginTimeoutMs);
        CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                               partialPath, gate, writer);

        CaptureSessionTestHooks hooks;
        hooks.onCreateCaptureItem = [&](const CaptureSessionTestTargetRequest& request) {
            observed = request;
            return E_FAIL;
        };
        session.SetTestHooks(hooks);
        CaptureOutcome outcome = session.Run();
        ASSERT_EQ(outcome.result, CaptureResult::Failed);
    };

    CaptureSessionTestTargetRequest displayRequest;
    const Rect displayBounds{11, -22, 1920, 1080};
    run(CaptureMode::ContinuousDisplay, displayBounds, 0, displayRequest);
    ASSERT_EQ(displayRequest.mode, CaptureMode::ContinuousDisplay);
    ASSERT_EQ(displayRequest.displayBounds.x, displayBounds.x);
    ASSERT_EQ(displayRequest.displayBounds.y, displayBounds.y);
    ASSERT_EQ(displayRequest.displayBounds.width, displayBounds.width);
    ASSERT_EQ(displayRequest.displayBounds.height, displayBounds.height);
    ASSERT_EQ(displayRequest.windowHwnd, 0u);

    CaptureSessionTestTargetRequest windowRequest;
    run(CaptureMode::ContinuousWindow, displayBounds, 0x1234, windowRequest);
    ASSERT_EQ(windowRequest.mode, CaptureMode::ContinuousWindow);
    ASSERT_EQ(windowRequest.windowHwnd, 0x1234u);

    CaptureSessionTestTargetRequest regionRequest;
    run(CaptureMode::ContinuousRegion, displayBounds, 0, regionRequest);
    ASSERT_EQ(regionRequest.mode, CaptureMode::ContinuousRegion);
    ASSERT_EQ(regionRequest.displayBounds.x, displayBounds.x);
    ASSERT_EQ(regionRequest.displayBounds.y, displayBounds.y);
    ASSERT_EQ(regionRequest.displayBounds.width, displayBounds.width);
    ASSERT_EQ(regionRequest.displayBounds.height, displayBounds.height);
    ASSERT_EQ(regionRequest.regionBounds.x, displayBounds.x + 10);
    ASSERT_EQ(regionRequest.regionBounds.y, displayBounds.y + 20);
    ASSERT_EQ(regionRequest.regionBounds.width, 640);
    ASSERT_EQ(regionRequest.regionBounds.height, 480);
    ASSERT_EQ(regionRequest.windowHwnd, 0u);
});

TEST_REGISTRAR(CaptureSessionWindowBeforeBegin_DoesNotStartCaptureOrWrite, []() {
    TempDir dir(L"wgc-test-window-before-begin");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");

    Options opts = MakeContinuousWindowOptions({10, 20, 800, 600}, outputPath,
                                                beginPath, stopPath, 1000);
    opts.beginTimeoutMs = 100;

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    std::atomic<int> startCount{0};
    std::atomic<int> writeCount{0};
    CaptureSessionTestHooks hooks;
    hooks.onStartCapture = [&]() { startCount.fetch_add(1); };
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onWriteFrame = [&](const std::vector<uint8_t>&, int64_t, int64_t) {
        writeCount.fetch_add(1);
        return EncoderResult{EncoderStatus::Ok};
    };
    session.SetTestHooks(hooks);

    CaptureOutcome outcome = session.Run();

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "timeout");
    ASSERT_EQ(startCount.load(), 0);
    ASSERT_EQ(writeCount.load(), 0);
    ASSERT_FALSE(FileExists(outputPath));
    ASSERT_FALSE(FileExists(partialPath));
});

TEST_REGISTRAR(CaptureSessionRegionBeforeBegin_DoesNotStartCaptureOrCopy, []() {
    TempDir dir(L"wgc-test-region-before-begin");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");

    Options opts = MakeContinuousOptions({10, 20, 800, 600}, outputPath,
                                          beginPath, stopPath, 1000);
    opts.mode = CaptureMode::ContinuousRegion;
    opts.regionBounds = {110, 40, 640, 480};
    opts.beginTimeoutMs = 100;

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    std::atomic<int> startCount{0};
    std::atomic<int> copyCount{0};
    CaptureSessionTestHooks hooks;
    hooks.onStartCapture = [&]() { startCount.fetch_add(1); };
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onCopyFrame = [&](const QueuedFrame&, int, int,
                            std::vector<uint8_t>&) {
        copyCount.fetch_add(1);
        return true;
    };
    session.SetTestHooks(hooks);

    CaptureOutcome outcome = session.Run();

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "timeout");
    ASSERT_EQ(startCount.load(), 0);
    ASSERT_EQ(copyCount.load(), 0);
    ASSERT_FALSE(FileExists(outputPath));
    ASSERT_FALSE(FileExists(partialPath));
});

TEST_REGISTRAR(CaptureSessionTimelineIntegrationBoundsSparseFinalSample, []() {
    TempDir dir(L"wgc-test-timeline-integration");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");
    WriteFile(beginPath, L"test-token-175c");

    Options opts = MakeContinuousOptions({10, 20, 800, 600}, outputPath,
                                          beginPath, stopPath, 1000);
    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    struct WrittenSample {
        int64_t mediaTimeHns = 0;
        int64_t durationHns = 0;
    };
    std::mutex samplesMutex;
    std::vector<WrittenSample> samples;
    std::atomic<int> finalizeCount{0};

    CaptureSessionTestHooks hooks;
    hooks.useSyntheticPlatformResources = true;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {};
    hooks.onCopyFrame = [](const QueuedFrame&, int width, int height,
                           std::vector<uint8_t>& outPixels) {
        outPixels.assign(static_cast<size_t>(width) * height * 4, 0x7A);
        return true;
    };
    hooks.onWriteFrame = [&](const std::vector<uint8_t>&,
                             int64_t mediaTimeHns,
                             int64_t durationHns) {
        std::lock_guard<std::mutex> lock(samplesMutex);
        samples.push_back({mediaTimeHns, durationHns});
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = [&]() {
        finalizeCount.fetch_add(1);
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onCaptureActive = [](FrameQueue& queue) {
        // The final queued timestamp is at the one-second boundary. The
        // preceding 825 ms gap must close the previous sample, not become a
        // second tail on the final sample.
        const int64_t rawTimes[] = {
            0,
            333'333LL,
            666'666LL,
            8'350'000LL,
            9'175'000LL,
            10'000'000LL,
        };
        for (int64_t rawTime : rawTimes) {
            QueuedFrame frame;
            frame.systemRelativeTimeHns = rawTime;
            frame.contentWidth = 64;
            frame.contentHeight = 64;
            ASSERT_TRUE(queue.Push(frame));
            // The production queue intentionally has a one-frame capacity.
            // Pace this synthetic producer so the integration test observes
            // every accepted timestamp instead of testing queue overflow.
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
        }
    };
    session.SetTestHooks(hooks);
    WriteSyntheticPartial(partialPath);

    CaptureOutcome outcome = session.Run();

    ASSERT_EQ(outcome.result, CaptureResult::Success);
    ASSERT_EQ(outcome.errorCode, "");
    ASSERT_EQ(finalizeCount.load(), 1);
    ASSERT_TRUE(FileExists(outputPath));
    ASSERT_FALSE(FileExists(partialPath));
    ASSERT_GT(outcome.bytesWritten, 0);

    std::vector<WrittenSample> observed;
    {
        std::lock_guard<std::mutex> lock(samplesMutex);
        observed = samples;
    }
    std::cerr << "[TIMELINE_INTEGRATION] duration_ms=" << outcome.durationMs
              << " samples=";
    for (const WrittenSample& sample : observed) {
        std::cerr << sample.mediaTimeHns << "/" << sample.durationHns << ",";
    }
    std::cerr << " final_end_hns="
              << (observed.empty() ? 0 : observed.back().mediaTimeHns +
                  observed.back().durationHns) << std::endl;
    ASSERT_GE(observed.size(), 5u);
    ASSERT_LE(observed.size(), 6u);
    ASSERT_EQ(observed.front().mediaTimeHns, 0LL);
    bool foundSparseBoundary = false;
    for (size_t i = 0; i < observed.size(); ++i) {
        ASSERT_GT(observed[i].durationHns, 0LL);
        if (i > 0) {
            ASSERT_GT(observed[i].mediaTimeHns, observed[i - 1].mediaTimeHns);
        }
        if (observed[i].mediaTimeHns == 9'175'000LL) {
            ASSERT_EQ(observed[i].durationHns, 825'000LL);
            foundSparseBoundary = true;
        }
    }
    ASSERT_TRUE(foundSparseBoundary);

    const int64_t finalMediaEndHns = observed.back().mediaTimeHns +
                                     observed.back().durationHns;
    // CaptureSession's encoder worker polls the queue at most every 50 ms;
    // 2 ms covers integer millisecond conversion on top of that bounded poll.
    const int64_t sessionEndHns = outcome.durationMs * 10'000LL;
    ASSERT_LE(finalMediaEndHns, sessionEndHns + 520'000LL);
    ASSERT_GE(finalMediaEndHns, sessionEndHns - 520'000LL);
    ASSERT_LE(observed.back().mediaTimeHns, 10'000'000LL);
    if (observed.back().mediaTimeHns == 10'000'000LL) {
        ASSERT_LE(observed.back().durationHns, 520'000LL);
    }
});

TEST_REGISTRAR(CaptureSessionWindowClosedAfterStart_FailsWithPartialEvidence, []() {
    TempDir dir(L"wgc-test-window-closed");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");
    WriteFile(beginPath, L"test-token-175c");

    Options opts = MakeContinuousWindowOptions({10, 20, 800, 600}, outputPath,
                                                beginPath, stopPath, 5000);
    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    std::function<void()> signalClosed;
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
    hooks.onTestSignalsCreated = [&](const CaptureSessionTestSignals& signals) {
        signalClosed = signals.signalWindowClosed;
    };
    hooks.onCopyFrame = [](const QueuedFrame&, int width, int height,
                           std::vector<uint8_t>& outPixels) {
        outPixels.assign(static_cast<size_t>(width) * height * 4, 0x44);
        return true;
    };
    hooks.onWriteFrame = [](const std::vector<uint8_t>&, int64_t, int64_t) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onCaptureActive = [](FrameQueue& queue) {
        QueuedFrame frame;
        frame.systemRelativeTimeHns = 0;
        frame.contentWidth = 64;
        frame.contentHeight = 64;
        queue.Push(frame);
    };
    session.SetTestHooks(hooks);
    WriteSyntheticPartial(partialPath);

    std::packaged_task<CaptureOutcome()> task([&]() { return session.Run(); });
    auto future = task.get_future();
    ScopedJoiningThread runner{std::thread(std::move(task))};

    {
        std::unique_lock<std::mutex> lock(startedMutex);
        ASSERT_TRUE(startedCv.wait_for(lock, std::chrono::milliseconds(3000),
                                       [&]() { return started.load(); }));
    }
    ASSERT_TRUE(static_cast<bool>(signalClosed));
    const auto stopStart = std::chrono::steady_clock::now();
    signalClosed();
    ASSERT_EQ(future.wait_for(std::chrono::milliseconds(3000)), std::future_status::ready);
    const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - stopStart).count();
    CaptureOutcome outcome = future.get();

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "window_closed");
    ASSERT_LT(elapsed, 2500);
    ASSERT_FALSE(FileExists(outputPath));
    ASSERT_TRUE(FileExists(partialPath));
    ASSERT_GT(FileSize(partialPath), 0);
    ASSERT_GT(outcome.bytesWritten, 0);
});

TEST_REGISTRAR(CaptureSessionWindowSizeChangedAfterStart_FailsClosedWithPartialEvidence, []() {
    TempDir dir(L"wgc-test-window-size-changed");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");
    WriteFile(beginPath, L"test-token-175c");

    Options opts = MakeContinuousWindowOptions({10, 20, 800, 600}, outputPath,
                                                beginPath, stopPath, 5000);
    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    std::function<void()> signalSizeChanged;
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
    hooks.onTestSignalsCreated = [&](const CaptureSessionTestSignals& signals) {
        signalSizeChanged = signals.signalSizeChanged;
    };
    hooks.onCopyFrame = [](const QueuedFrame&, int width, int height,
                           std::vector<uint8_t>& outPixels) {
        outPixels.assign(static_cast<size_t>(width) * height * 4, 0x55);
        return true;
    };
    hooks.onWriteFrame = [](const std::vector<uint8_t>&, int64_t, int64_t) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onCaptureActive = [](FrameQueue& queue) {
        QueuedFrame frame;
        frame.systemRelativeTimeHns = 0;
        frame.contentWidth = 64;
        frame.contentHeight = 64;
        queue.Push(frame);
    };
    session.SetTestHooks(hooks);
    WriteSyntheticPartial(partialPath);

    std::packaged_task<CaptureOutcome()> task([&]() { return session.Run(); });
    auto future = task.get_future();
    ScopedJoiningThread runner{std::thread(std::move(task))};

    {
        std::unique_lock<std::mutex> lock(startedMutex);
        ASSERT_TRUE(startedCv.wait_for(lock, std::chrono::milliseconds(3000),
                                       [&]() { return started.load(); }));
    }
    ASSERT_TRUE(static_cast<bool>(signalSizeChanged));
    const auto stopStart = std::chrono::steady_clock::now();
    signalSizeChanged();
    ASSERT_EQ(future.wait_for(std::chrono::milliseconds(3000)), std::future_status::ready);
    const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - stopStart).count();
    CaptureOutcome outcome = future.get();

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "size_changed");
    ASSERT_LT(elapsed, 2500);
    ASSERT_FALSE(FileExists(outputPath));
    ASSERT_TRUE(FileExists(partialPath));
    ASSERT_GT(FileSize(partialPath), 0);
    ASSERT_GT(outcome.bytesWritten, 0);
});

TEST_REGISTRAR(CaptureSessionDisplayClosedAfterStart_FailsDisplayUnavailable, []() {
    RunDisplayUnavailableSignalTest(CaptureMode::ContinuousDisplay,
                                    L"wgc-test-display-unavailable");
});

TEST_REGISTRAR(CaptureSessionRegionClosedAfterStart_FailsDisplayUnavailable, []() {
    RunDisplayUnavailableSignalTest(CaptureMode::ContinuousRegion,
                                    L"wgc-test-region-display-unavailable");
});

TEST_REGISTRAR(CaptureSessionDisplayMonitor_TargetPresentDoesNotStop, []() {
    RunDisplayMonitorScenario(CaptureMode::ContinuousDisplay, false, false,
                              L"wgc-test-display-monitor-present");
});

TEST_REGISTRAR(CaptureSessionRegionMonitor_TargetGoneFailsOnce, []() {
    RunDisplayMonitorScenario(CaptureMode::ContinuousRegion, true, false,
                              L"wgc-test-region-monitor-gone");
});

TEST_REGISTRAR(CaptureSessionDisplayMonitor_QueryExceptionFailsClosed, []() {
    RunDisplayMonitorScenario(CaptureMode::ContinuousDisplay, false, true,
                              L"wgc-test-display-monitor-exception");
});

TEST_REGISTRAR(CaptureSessionWindowClosedByHwndMonitor_FailsPromptly, []() {
    TempDir dir(L"wgc-test-window-monitor-closed");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");
    WriteFile(beginPath, L"test-token-175c");

    Options opts = MakeContinuousWindowOptions({10, 20, 800, 600}, outputPath,
                                                beginPath, stopPath, 5000);
    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    std::atomic<bool> started{false};
    std::atomic<bool> closed{false};
    std::atomic<int> queryCount{0};
    std::mutex startedMutex;
    std::condition_variable startedCv;
    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {};
    hooks.onStarted = [&]() {
        started.store(true);
        startedCv.notify_all();
    };
    hooks.onWindowStateQuery = [&](std::uint64_t hwnd) {
        ASSERT_EQ(hwnd, opts.windowHwnd);
        queryCount.fetch_add(1);
        return CaptureSessionTestWindowState{!closed.load(), false};
    };
    hooks.onCopyFrame = [](const QueuedFrame&, int width, int height,
                           std::vector<uint8_t>& outPixels) {
        outPixels.assign(static_cast<size_t>(width) * height * 4, 0x61);
        return true;
    };
    hooks.onWriteFrame = [](const std::vector<uint8_t>&, int64_t, int64_t) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onCaptureActive = [](FrameQueue& queue) {
        QueuedFrame frame;
        frame.systemRelativeTimeHns = 0;
        frame.contentWidth = 64;
        frame.contentHeight = 64;
        queue.Push(frame);
    };
    session.SetTestHooks(hooks);
    WriteSyntheticPartial(partialPath);

    std::packaged_task<CaptureOutcome()> task([&]() { return session.Run(); });
    auto future = task.get_future();
    ScopedJoiningThread runner{std::thread(std::move(task))};
    {
        std::unique_lock<std::mutex> lock(startedMutex);
        ASSERT_TRUE(startedCv.wait_for(lock, std::chrono::milliseconds(3000),
                                       [&]() { return started.load(); }));
    }

    const auto stopStart = std::chrono::steady_clock::now();
    closed.store(true);
    ASSERT_EQ(future.wait_for(std::chrono::milliseconds(3000)), std::future_status::ready);
    const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - stopStart).count();
    CaptureOutcome outcome = future.get();

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "window_closed");
    ASSERT_GT(queryCount.load(), 0);
    ASSERT_LT(elapsed, 2000);
    ASSERT_FALSE(FileExists(outputPath));
    ASSERT_TRUE(FileExists(partialPath));
    ASSERT_GT(outcome.bytesWritten, 0);
});

TEST_REGISTRAR(CaptureSessionWindowMinimizedByHwndMonitor_FailsPromptly, []() {
    TempDir dir(L"wgc-test-window-monitor-minimized");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");
    WriteFile(beginPath, L"test-token-175c");

    Options opts = MakeContinuousWindowOptions({10, 20, 800, 600}, outputPath,
                                                beginPath, stopPath, 5000);
    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    std::atomic<bool> started{false};
    std::atomic<bool> minimized{false};
    std::mutex startedMutex;
    std::condition_variable startedCv;
    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {};
    hooks.onStarted = [&]() {
        started.store(true);
        startedCv.notify_all();
    };
    hooks.onWindowStateQuery = [&](std::uint64_t hwnd) {
        ASSERT_EQ(hwnd, opts.windowHwnd);
        return CaptureSessionTestWindowState{true, minimized.load()};
    };
    hooks.onCopyFrame = [](const QueuedFrame&, int width, int height,
                           std::vector<uint8_t>& outPixels) {
        outPixels.assign(static_cast<size_t>(width) * height * 4, 0x62);
        return true;
    };
    hooks.onWriteFrame = [](const std::vector<uint8_t>&, int64_t, int64_t) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onCaptureActive = [](FrameQueue& queue) {
        QueuedFrame frame;
        frame.systemRelativeTimeHns = 0;
        frame.contentWidth = 64;
        frame.contentHeight = 64;
        queue.Push(frame);
    };
    session.SetTestHooks(hooks);
    WriteSyntheticPartial(partialPath);

    std::packaged_task<CaptureOutcome()> task([&]() { return session.Run(); });
    auto future = task.get_future();
    ScopedJoiningThread runner{std::thread(std::move(task))};
    {
        std::unique_lock<std::mutex> lock(startedMutex);
        ASSERT_TRUE(startedCv.wait_for(lock, std::chrono::milliseconds(3000),
                                       [&]() { return started.load(); }));
    }

    const auto stopStart = std::chrono::steady_clock::now();
    minimized.store(true);
    ASSERT_EQ(future.wait_for(std::chrono::milliseconds(3000)), std::future_status::ready);
    const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - stopStart).count();
    CaptureOutcome outcome = future.get();

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "window_minimized");
    ASSERT_LT(elapsed, 2000);
    ASSERT_FALSE(FileExists(outputPath));
    ASSERT_TRUE(FileExists(partialPath));
    ASSERT_GT(outcome.bytesWritten, 0);
});

TEST_REGISTRAR(CaptureSessionDisplayDoesNotQueryHwndLifecycle, []() {
    TempDir dir(L"wgc-test-display-no-hwnd-monitor");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");
    WriteFile(beginPath, L"test-token-175c");

    Options opts = MakeContinuousOptions({10, 20, 800, 600}, outputPath,
                                          beginPath, stopPath, 5000);
    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    std::atomic<int> queryCount{0};
    std::atomic<bool> started{false};
    CaptureSessionTestHooks hooks;
    hooks.onWindowStateQuery = [&](std::uint64_t) {
        queryCount.fetch_add(1);
        return CaptureSessionTestWindowState{};
    };
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {};
    hooks.onStarted = [&]() { started.store(true); };
    hooks.onCopyFrame = [](const QueuedFrame&, int width, int height,
                           std::vector<uint8_t>& outPixels) {
        outPixels.assign(static_cast<size_t>(width) * height * 4, 0x63);
        return true;
    };
    hooks.onWriteFrame = [](const std::vector<uint8_t>&, int64_t, int64_t) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onCaptureActive = [](FrameQueue& queue) {
        QueuedFrame frame;
        frame.systemRelativeTimeHns = 0;
        frame.contentWidth = 64;
        frame.contentHeight = 64;
        queue.Push(frame);
    };
    session.SetTestHooks(hooks);
    WriteSyntheticPartial(partialPath);

    std::packaged_task<CaptureOutcome()> task([&]() { return session.Run(); });
    auto future = task.get_future();
    ScopedJoiningThread runner{std::thread(std::move(task))};
    const auto startedDeadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(3000);
    while (!started.load() && std::chrono::steady_clock::now() < startedDeadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    ASSERT_TRUE(started.load());
    std::this_thread::sleep_for(std::chrono::milliseconds(100));
    WriteFile(stopPath, L"stop");

    ASSERT_EQ(future.wait_for(std::chrono::milliseconds(3000)), std::future_status::ready);
    CaptureOutcome outcome = future.get();
    ASSERT_EQ(outcome.result, CaptureResult::Stopped);
    ASSERT_EQ(queryCount.load(), 0);
    ASSERT_TRUE(FileExists(outputPath));
    ASSERT_FALSE(FileExists(partialPath));
});

TEST_REGISTRAR(CaptureSessionWindowMovementWithoutSizeChangeDoesNotFail, []() {
    TempDir dir(L"wgc-test-window-movement");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");
    WriteFile(beginPath, L"test-token-175c");

    Options opts = MakeContinuousWindowOptions({10, 20, 800, 600}, outputPath,
                                                beginPath, stopPath, 5000);
    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);
    CaptureSessionTestHooks hooks;
    hooks.onWindowStateQuery = [](std::uint64_t) {
        return CaptureSessionTestWindowState{true, false};
    };
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {};
    hooks.onCopyFrame = [](const QueuedFrame&, int width, int height,
                           std::vector<uint8_t>& outPixels) {
        outPixels.assign(static_cast<size_t>(width) * height * 4, 0x64);
        return true;
    };
    hooks.onWriteFrame = [](const std::vector<uint8_t>&, int64_t, int64_t) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onCaptureActive = [](FrameQueue& queue) {
        for (int i = 0; i < 4; ++i) {
            QueuedFrame frame;
            frame.systemRelativeTimeHns = i * 333'333LL;
            frame.contentWidth = 64;
            frame.contentHeight = 64;
            queue.Push(frame);
        }
    };
    session.SetTestHooks(hooks);
    WriteSyntheticPartial(partialPath);

    std::packaged_task<CaptureOutcome()> task([&]() { return session.Run(); });
    auto future = task.get_future();
    ScopedJoiningThread runner{std::thread(std::move(task))};
    std::this_thread::sleep_for(std::chrono::milliseconds(150));
    WriteFile(stopPath, L"stop");

    ASSERT_EQ(future.wait_for(std::chrono::milliseconds(3000)), std::future_status::ready);
    CaptureOutcome outcome = future.get();
    ASSERT_EQ(outcome.result, CaptureResult::Stopped);
    ASSERT_TRUE(FileExists(outputPath));
    ASSERT_FALSE(FileExists(partialPath));
});

TEST_REGISTRAR(CaptureSessionFirstFrameEmittedPromptlyForStaticSingleFrame, []() {
    TempDir dir(L"wgc-test-first-frame-static");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");
    WriteFile(beginPath, L"test-token-175c");

    Rect bounds = GetPrimaryMonitorBounds();
    Options opts = MakeContinuousOptions(bounds, outputPath, beginPath, stopPath, 1500);

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    std::atomic<int> writeCount{0};
    std::atomic<int> firstFrameCalls{0};
    std::atomic<int64_t> firstFrameNumber{0};
    std::atomic<int64_t> firstFrameElapsedMs{-1};
    // Number of encoder writes that had happened at the moment FIRST_FRAME was
    // emitted. For a static single-frame source this must be zero: the explicit
    // event fires while FramesCaptured is still 0.
    std::atomic<int> writesAtFirstFrame{-1};
    std::atomic<bool> sessionReturned{false};
    std::atomic<bool> firstFrameBeforeReturn{false};

    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {};
    hooks.onCopyFrame = [](const QueuedFrame&, int width, int height,
                           std::vector<uint8_t>& outPixels) {
        outPixels.assign(static_cast<size_t>(width) * height * 4, 0x77);
        return true;
    };
    hooks.onWriteFrame = [&](const std::vector<uint8_t>&, int64_t, int64_t) {
        writeCount.fetch_add(1);
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onFirstFrame = [&](int64_t frameNumber, int64_t elapsedMs) {
        firstFrameCalls.fetch_add(1);
        firstFrameNumber.store(frameNumber);
        firstFrameElapsedMs.store(elapsedMs);
        writesAtFirstFrame.store(writeCount.load());
        if (!sessionReturned.load()) {
            firstFrameBeforeReturn.store(true);
        }
    };
    hooks.onCaptureActive = [](FrameQueue& queue) {
        // A static WGC source delivers exactly one useful frame.
        QueuedFrame qf;
        qf.frame = nullptr;
        qf.systemRelativeTimeHns = 0;
        qf.contentWidth = 64;
        qf.contentHeight = 64;
        queue.Push(qf);
    };
    session.SetTestHooks(hooks);
    WriteSyntheticPartial(partialPath);

    // Let the session run to its natural duration end (no stop signal) so the
    // single pending frame is only written during finalization.
    std::packaged_task<CaptureOutcome()> task([&]() { return session.Run(); });
    auto future = task.get_future();
    ScopedJoiningThread runner{std::thread(std::move(task))};

    ASSERT_EQ(future.wait_for(std::chrono::milliseconds(8000)), std::future_status::ready);
    CaptureOutcome outcome = future.get();
    sessionReturned.store(true);
    PrintOutcomeDiagnostics("CaptureSessionFirstFrameEmittedPromptlyForStaticSingleFrame", outcome, partialPath);

    // Exactly one explicit FIRST_FRAME, emitted promptly (before the session
    // returned) while the encoder had not yet written any sample.
    ASSERT_EQ(firstFrameCalls.load(), 1);
    ASSERT_EQ(firstFrameNumber.load(), 1);
    ASSERT_GE(firstFrameElapsedMs.load(), 0);
    ASSERT_EQ(writesAtFirstFrame.load(), 0);
    ASSERT_TRUE(firstFrameBeforeReturn.load());

    // The explicit event does not change encoded frame counts: the single
    // static frame is committed exactly once at finalization.
    ASSERT_EQ(outcome.result, CaptureResult::Success);
    ASSERT_EQ(outcome.framesCaptured, 1);
    ASSERT_EQ(writeCount.load(), 1);
    ASSERT_GE(outcome.durationMs, 1000);
    // The first-frame elapsed evidence must be bounded by the session duration.
    ASSERT_LE(firstFrameElapsedMs.load(), outcome.durationMs);
});

TEST_REGISTRAR(CaptureSessionFirstFrameEmittedExactlyOnceWithMultipleFrames, []() {
    TempDir dir(L"wgc-test-first-frame-multi");
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
    std::atomic<int> firstFrameCalls{0};

    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {};
    hooks.onCopyFrame = [](const QueuedFrame&, int width, int height,
                           std::vector<uint8_t>& outPixels) {
        outPixels.assign(static_cast<size_t>(width) * height * 4, 0x42);
        return true;
    };
    hooks.onWriteFrame = [&](const std::vector<uint8_t>&, int64_t, int64_t) {
        writeCount.fetch_add(1);
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onFirstFrame = [&](int64_t, int64_t) {
        firstFrameCalls.fetch_add(1);
    };
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
            std::this_thread::sleep_for(std::chrono::milliseconds(20));
        }
    };
    session.SetTestHooks(hooks);
    WriteSyntheticPartial(partialPath);

    CaptureOutcome outcome = RunWithStopCancel(session, stopPath, std::chrono::milliseconds(5000));
    PrintOutcomeDiagnostics("CaptureSessionFirstFrameEmittedExactlyOnceWithMultipleFrames", outcome, partialPath);

    ASSERT_EQ(firstFrameCalls.load(), 1);
    ASSERT_EQ(outcome.result, CaptureResult::Stopped);
    ASSERT_GT(outcome.framesCaptured, 0);
});

TEST_REGISTRAR(CaptureSessionFirstFrameNotEmittedOnCopyFailure, []() {
    TempDir dir(L"wgc-test-first-frame-copy-fail");
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

    std::atomic<int> firstFrameCalls{0};

    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {};
    hooks.onCopyFrame = [](const QueuedFrame&, int, int,
                           std::vector<uint8_t>&) {
        return false; // GPU copy fails on the very first frame
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onFirstFrame = [&](int64_t, int64_t) {
        firstFrameCalls.fetch_add(1);
    };
    hooks.onCaptureActive = [](FrameQueue& queue) {
        QueuedFrame qf;
        qf.frame = nullptr;
        qf.systemRelativeTimeHns = 0;
        qf.contentWidth = 64;
        qf.contentHeight = 64;
        queue.Push(qf);
    };
    session.SetTestHooks(hooks);

    CaptureOutcome outcome = RunWithStopCancel(session, stopPath, std::chrono::milliseconds(5000));
    PrintOutcomeDiagnostics("CaptureSessionFirstFrameNotEmittedOnCopyFailure", outcome, partialPath);

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "d3d_copy_failed");
    ASSERT_EQ(firstFrameCalls.load(), 0);
});

TEST_REGISTRAR(CaptureSessionFirstFrameNotEmittedOnTimelineRejection, []() {
    TempDir dir(L"wgc-test-first-frame-timeline-reject");
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

    std::atomic<int> firstFrameCalls{0};

    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {};
    hooks.onCopyFrame = [](const QueuedFrame&, int width, int height,
                           std::vector<uint8_t>& outPixels) {
        outPixels.assign(static_cast<size_t>(width) * height * 4, 0x11);
        return true;
    };
    hooks.onFinalize = []() { return EncoderResult{EncoderStatus::Ok}; };
    hooks.onFirstFrame = [&](int64_t, int64_t) {
        firstFrameCalls.fetch_add(1);
    };
    hooks.onCaptureActive = [](FrameQueue& queue) {
        // A negative source timestamp is rejected by the timeline before any
        // copy is attempted, so no first-frame evidence may be published.
        QueuedFrame qf;
        qf.frame = nullptr;
        qf.systemRelativeTimeHns = -1;
        qf.contentWidth = 64;
        qf.contentHeight = 64;
        queue.Push(qf);
    };
    session.SetTestHooks(hooks);

    CaptureOutcome outcome = RunWithStopCancel(session, stopPath, std::chrono::milliseconds(5000));
    PrintOutcomeDiagnostics("CaptureSessionFirstFrameNotEmittedOnTimelineRejection", outcome, partialPath);

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "zero_frames");
    ASSERT_EQ(firstFrameCalls.load(), 0);
});

TEST_REGISTRAR(CaptureSessionFirstFrameNotEmittedWhenBeginTimesOut, []() {
    TempDir dir(L"wgc-test-first-frame-no-begin");
    const std::wstring outputPath = JoinPath(dir.path, L"out.mp4");
    const std::wstring partialPath = JoinPath(dir.path, L"out.partial.mp4");
    const std::wstring beginPath = JoinPath(dir.path, L"begin.signal");
    const std::wstring stopPath = JoinPath(dir.path, L"stop.signal");
    // No begin signal: authorization never happens.

    Rect bounds = GetPrimaryMonitorBounds();
    Options opts = MakeContinuousOptions(bounds, outputPath, beginPath, stopPath);
    opts.beginTimeoutMs = 200;

    EventWriter writer;
    BeginGate gate(opts.beginSignalPath, opts.beginToken,
                   opts.stopSignalPath, opts.beginTimeoutMs);
    CaptureSession session(opts, ValidateOutputPathOrFail(outputPath),
                           partialPath, gate, writer);

    std::atomic<int> firstFrameCalls{0};

    CaptureSessionTestHooks hooks;
    hooks.onEncoderInitialize = [](int, int, int, const std::wstring&) {
        return EncoderResult{EncoderStatus::Ok};
    };
    hooks.onStartCapture = []() {};
    hooks.onFirstFrame = [&](int64_t, int64_t) {
        firstFrameCalls.fetch_add(1);
    };
    session.SetTestHooks(hooks);

    CaptureOutcome outcome = RunWithStopCancel(session, stopPath, std::chrono::milliseconds(5000));
    PrintOutcomeDiagnostics("CaptureSessionFirstFrameNotEmittedWhenBeginTimesOut", outcome, partialPath);

    ASSERT_EQ(outcome.result, CaptureResult::Failed);
    ASSERT_EQ(outcome.errorCode, "timeout");
    ASSERT_EQ(firstFrameCalls.load(), 0);
});

} // namespace
