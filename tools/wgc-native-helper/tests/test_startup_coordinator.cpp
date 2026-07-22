#include "test_framework.h"

#include "startup_coordinator.h"

#include <atomic>
#include <chrono>
#include <string>
#include <thread>

using namespace wgc;

namespace {

TEST_REGISTRAR(StartupCoordinatorNormalSequence, []() {
    StartupCoordinator coordinator;

    std::atomic<bool> workerBeganInit{false};
    std::atomic<bool> workerReady{false};
    std::atomic<int64_t> observedBeginTime{0};

    std::thread worker([&]() {
        if (!coordinator.WaitForBeginAuthorization(std::chrono::milliseconds(1000))) {
            return;
        }
        workerBeganInit.store(true);
        coordinator.SignalEncoderReady();
        if (!coordinator.WaitForCaptureActive(std::chrono::milliseconds(1000))) {
            return;
        }
        workerReady.store(true);
        observedBeginTime.store(coordinator.BeginTimeMs());
    });

    coordinator.AuthorizeBegin();
    auto initResult = coordinator.WaitForEncoderInit(std::chrono::milliseconds(1000));
    ASSERT_EQ(initResult.status, EncoderInitStatus::Ready);
    ASSERT_TRUE(initResult.error.empty());

    const int64_t beginTimeMs = 12345;
    coordinator.SignalCaptureStarted(beginTimeMs);

    worker.join();

    ASSERT_TRUE(workerBeganInit.load());
    ASSERT_TRUE(workerReady.load());
    ASSERT_EQ(observedBeginTime.load(), beginTimeMs);
    ASSERT_EQ(coordinator.State(), StartupState::CaptureActive);
});

TEST_REGISTRAR(StartupCoordinatorWorkerWakesBeforeAuthorization, []() {
    StartupCoordinator coordinator;

    std::thread worker([&]() {
        coordinator.WaitForBeginAuthorization(std::chrono::milliseconds(2000));
    });

    // Give the worker a moment to start waiting, then authorize.
    std::this_thread::sleep_for(std::chrono::milliseconds(50));
    coordinator.AuthorizeBegin();

    worker.join();
    ASSERT_NE(coordinator.State(), StartupState::Idle);
});

TEST_REGISTRAR(StartupCoordinatorEncoderInitFailure, []() {
    StartupCoordinator coordinator;

    std::thread worker([&]() {
        if (!coordinator.WaitForBeginAuthorization(std::chrono::milliseconds(1000))) {
            return;
        }
        coordinator.SignalEncoderFailed("sink_writer_rejected", "0x80004005");
    });

    coordinator.AuthorizeBegin();
    auto initResult = coordinator.WaitForEncoderInit(std::chrono::milliseconds(1000));
    ASSERT_EQ(initResult.status, EncoderInitStatus::Failed);
    ASSERT_EQ(initResult.error, "sink_writer_rejected");
    ASSERT_EQ(initResult.hresult, "0x80004005");
    ASSERT_TRUE(coordinator.IsFailed());

    worker.join();
});

TEST_REGISTRAR(StartupCoordinatorEncoderInitTimeout, []() {
    StartupCoordinator coordinator;

    std::atomic<bool> authorized{false};
    std::thread worker([&]() {
        authorized.store(coordinator.WaitForBeginAuthorization(std::chrono::milliseconds(2000)));
        // Never signal ready or failed.
    });

    coordinator.AuthorizeBegin();
    auto initResult = coordinator.WaitForEncoderInit(std::chrono::milliseconds(100));
    ASSERT_EQ(initResult.status, EncoderInitStatus::TimedOut);
    ASSERT_FALSE(coordinator.IsFailed());

    // The worker is still waiting for begin authorization; let it exit cleanly.
    worker.join();
    ASSERT_TRUE(authorized.load());
});

TEST_REGISTRAR(StartupCoordinatorCancelBeforeCaptureActive, []() {
    StartupCoordinator coordinator;

    std::atomic<bool> sawActive{false};
    std::thread worker([&]() {
        sawActive.store(coordinator.WaitForCaptureActive(std::chrono::milliseconds(1000)));
    });

    coordinator.AuthorizeBegin();
    coordinator.SignalEncoderReady();
    coordinator.RequestStop();

    worker.join();

    ASSERT_FALSE(sawActive.load());
    ASSERT_EQ(coordinator.State(), StartupState::Stopping);
});

TEST_REGISTRAR(StartupCoordinatorDoubleAuthorizationIsIdempotent, []() {
    StartupCoordinator coordinator;

    std::thread worker([&]() {
        if (!coordinator.WaitForBeginAuthorization(std::chrono::milliseconds(1000))) {
            return;
        }
        coordinator.SignalEncoderReady();
    });

    coordinator.AuthorizeBegin();
    coordinator.AuthorizeBegin();

    auto initResult = coordinator.WaitForEncoderInit(std::chrono::milliseconds(1000));
    ASSERT_EQ(initResult.status, EncoderInitStatus::Ready);
    worker.join();
});

TEST_REGISTRAR(StartupCoordinatorStoppedBeforeReady, []() {
    StartupCoordinator coordinator;

    std::thread worker([&]() {
        coordinator.WaitForBeginAuthorization(std::chrono::milliseconds(1000));
        // Exit without signaling ready or failed.
    });

    coordinator.AuthorizeBegin();
    coordinator.RequestStop();
    auto initResult = coordinator.WaitForEncoderInit(std::chrono::milliseconds(1000));
    ASSERT_EQ(initResult.status, EncoderInitStatus::Stopped);

    worker.join();
});

} // namespace
