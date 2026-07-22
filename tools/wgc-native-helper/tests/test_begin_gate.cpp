#include "test_framework.h"

#include "begin_gate.h"
#include "stop_watcher.h"

#include <atomic>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <thread>
#include <windows.h>

using namespace wgc;

namespace {

std::wstring GetTempDirectory() {
    wchar_t buffer[MAX_PATH + 1] = {};
    DWORD len = ::GetTempPathW(MAX_PATH, buffer);
    return std::wstring(buffer, len);
}

std::wstring MakeTempSignalPath(const std::wstring& suffix) {
    return GetTempDirectory() + L"wgc_begin_gate_" + std::to_wstring(GetCurrentProcessId()) + suffix;
}

void WriteSignal(const std::wstring& path, const std::wstring& content) {
    std::wofstream file(path, std::ios::binary);
    file << content;
}

TEST_REGISTRAR(BeginGateNoSignalTimesOut, []() {
    std::wstring beginPath = MakeTempSignalPath(L"_no_signal_begin.txt");
    std::wstring stopPath = MakeTempSignalPath(L"_no_signal_stop.txt");

    BeginGate gate(beginPath, L"token", stopPath, 100);
    std::atomic<bool> started{false};

    BeginGateResult result = gate.WaitAndRun([&]() { started.store(true); });

    ASSERT_EQ(result, BeginGateResult::Timeout);
    ASSERT_FALSE(started.load());

    std::filesystem::remove(beginPath);
    std::filesystem::remove(stopPath);
});

TEST_REGISTRAR(BeginGateWrongTokenDoesNotStart, []() {
    std::wstring beginPath = MakeTempSignalPath(L"_wrong_token_begin.txt");
    std::wstring stopPath = MakeTempSignalPath(L"_wrong_token_stop.txt");

    WriteSignal(beginPath, L"wrong-token");

    BeginGate gate(beginPath, L"expected-token", stopPath, 1000);
    std::atomic<bool> started{false};

    BeginGateResult result = gate.WaitAndRun([&]() { started.store(true); });

    ASSERT_EQ(result, BeginGateResult::InvalidToken);
    ASSERT_FALSE(started.load());

    std::filesystem::remove(beginPath);
    std::filesystem::remove(stopPath);
});

TEST_REGISTRAR(BeginGateCorrectTokenStartsOnce, []() {
    std::wstring beginPath = MakeTempSignalPath(L"_correct_token_begin.txt");
    std::wstring stopPath = MakeTempSignalPath(L"_correct_token_stop.txt");

    BeginGate gate(beginPath, L"expected-token", stopPath, 1000);
    std::atomic<bool> started{false};
    std::atomic<int> startCount{0};

    std::thread writer([&]() {
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
        WriteSignal(beginPath, L"expected-token");
    });

    BeginGateResult result = gate.WaitAndRun([&]() {
        started.store(true);
        startCount++;
    });

    writer.join();

    ASSERT_EQ(result, BeginGateResult::Started);
    ASSERT_TRUE(started.load());
    ASSERT_EQ(startCount.load(), 1);

    std::filesystem::remove(beginPath);
    std::filesystem::remove(stopPath);
});

TEST_REGISTRAR(BeginGateStopBeforeBeginDoesNotStart, []() {
    std::wstring beginPath = MakeTempSignalPath(L"_stop_before_begin_begin.txt");
    std::wstring stopPath = MakeTempSignalPath(L"_stop_before_begin_stop.txt");

    BeginGate gate(beginPath, L"token", stopPath, 1000);
    std::atomic<bool> started{false};

    std::thread writer([&]() {
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
        WriteSignal(stopPath, L"stop");
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
        WriteSignal(beginPath, L"token");
    });

    BeginGateResult result = gate.WaitAndRun([&]() { started.store(true); });

    writer.join();

    ASSERT_EQ(result, BeginGateResult::CancelledBeforeBegin);
    ASSERT_FALSE(started.load());

    std::filesystem::remove(beginPath);
    std::filesystem::remove(stopPath);
});

TEST_REGISTRAR(BeginGateBeginAndStopTogetherDoesNotStart, []() {
    std::wstring beginPath = MakeTempSignalPath(L"_together_begin.txt");
    std::wstring stopPath = MakeTempSignalPath(L"_together_stop.txt");

    WriteSignal(beginPath, L"token");
    WriteSignal(stopPath, L"stop");

    BeginGate gate(beginPath, L"token", stopPath, 1000);
    std::atomic<bool> started{false};

    BeginGateResult result = gate.WaitAndRun([&]() { started.store(true); });

    ASSERT_EQ(result, BeginGateResult::CancelledBeforeBegin);
    ASSERT_FALSE(started.load());

    std::filesystem::remove(beginPath);
    std::filesystem::remove(stopPath);
});

TEST_REGISTRAR(BeginGateStopAfterBeginIsHandledByWatcher, []() {
    std::wstring beginPath = MakeTempSignalPath(L"_stop_after_begin_begin.txt");
    std::wstring stopPath = MakeTempSignalPath(L"_stop_after_begin_stop.txt");

    BeginGate gate(beginPath, L"token", stopPath, 1000);
    std::atomic<bool> started{false};
    std::atomic<bool> stopRequested{false};
    std::atomic<bool> userStopped{false};

    StopSignalWatcher watcher(stopPath, stopRequested, userStopped);

    std::thread writer([&]() {
        WriteSignal(beginPath, L"token");
        // Wait until the gate has processed begin and invoked the start callback.
        for (int i = 0; i < 200 && !started.load(); ++i) {
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
        WriteSignal(stopPath, L"stop");
    });

    BeginGateResult result = gate.WaitAndRun([&]() {
        started.store(true);
        watcher.Start();
    });

    writer.join();

    // Give the watcher a moment to observe the stop file.
    for (int i = 0; i < 50 && !watcher.Triggered(); ++i) {
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }

    watcher.Stop();

    ASSERT_EQ(result, BeginGateResult::Started);
    ASSERT_TRUE(started.load());
    ASSERT_TRUE(watcher.Triggered());
    ASSERT_TRUE(stopRequested.load());
    ASSERT_TRUE(userStopped.load());

    std::filesystem::remove(beginPath);
    std::filesystem::remove(stopPath);
});

TEST_REGISTRAR(BeginGateStartCallbackExceptionDoesNotStart, []() {
    std::wstring beginPath = MakeTempSignalPath(L"_exception_begin.txt");
    std::wstring stopPath = MakeTempSignalPath(L"_exception_stop.txt");

    WriteSignal(beginPath, L"token");

    BeginGate gate(beginPath, L"token", stopPath, 1000);

    BeginGateResult result = gate.WaitAndRun([]() {
        throw std::runtime_error("start failed");
    });

    ASSERT_EQ(result, BeginGateResult::InternalError);

    std::filesystem::remove(beginPath);
    std::filesystem::remove(stopPath);
});

TEST_REGISTRAR(BeginGateCancelDoesNotStart, []() {
    std::wstring beginPath = MakeTempSignalPath(L"_cancel_begin.txt");
    std::wstring stopPath = MakeTempSignalPath(L"_cancel_stop.txt");

    BeginGate gate(beginPath, L"token", stopPath, 1000);
    std::atomic<bool> started{false};

    std::thread canceller([&]() {
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
        gate.Cancel();
    });

    BeginGateResult result = gate.WaitAndRun([&]() { started.store(true); });

    canceller.join();

    ASSERT_EQ(result, BeginGateResult::Cancelled);
    ASSERT_FALSE(started.load());

    std::filesystem::remove(beginPath);
    std::filesystem::remove(stopPath);
});

TEST_REGISTRAR(BeginGateDoubleStartReturnsAlreadyStarted, []() {
    std::wstring beginPath = MakeTempSignalPath(L"_double_begin.txt");
    std::wstring stopPath = MakeTempSignalPath(L"_double_stop.txt");

    WriteSignal(beginPath, L"token");

    BeginGate gate(beginPath, L"token", stopPath, 1000);

    BeginGateResult first = gate.WaitAndRun([&]() {});
    ASSERT_EQ(first, BeginGateResult::Started);

    BeginGateResult second = gate.WaitAndRun([&]() {});
    ASSERT_EQ(second, BeginGateResult::AlreadyStarted);

    std::filesystem::remove(beginPath);
    std::filesystem::remove(stopPath);
});

} // namespace
