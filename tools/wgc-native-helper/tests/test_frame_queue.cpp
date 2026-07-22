#include "test_framework.h"

#include "capture_lifecycle.h"
#include "frame_queue.h"

#include <atomic>
#include <chrono>
#include <thread>

using namespace wgc;

namespace {

QueuedFrame MakeNullFrame(int64_t timeHns = 0) {
    QueuedFrame qf;
    qf.frame = nullptr;
    qf.systemRelativeTimeHns = timeHns;
    qf.contentWidth = 64;
    qf.contentHeight = 64;
    return qf;
}

TEST_REGISTRAR(FrameQueuePushPopSingleFrame, []() {
    FrameQueue queue(2);
    auto qf = MakeNullFrame(100);
    ASSERT_TRUE(queue.Push(qf));

    QueuedFrame out;
    ASSERT_TRUE(queue.Pop(out, std::chrono::milliseconds(100)));
    ASSERT_EQ(out.systemRelativeTimeHns, 100);
});

TEST_REGISTRAR(FrameQueuePopTimesOutWhenEmpty, []() {
    FrameQueue queue(2);
    QueuedFrame out;
    ASSERT_FALSE(queue.Pop(out, std::chrono::milliseconds(50)));
});

TEST_REGISTRAR(FrameQueuePushRejectsAfterShutdown, []() {
    FrameQueue queue(2);
    queue.Shutdown();
    auto qf = MakeNullFrame(1);
    ASSERT_FALSE(queue.Push(qf));
    ASSERT_EQ(queue.Dropped(), 0);
});

TEST_REGISTRAR(FrameQueuePopWakesOnShutdown, []() {
    FrameQueue queue(2);

    std::atomic<bool> popReturned{false};
    std::thread consumer([&]() {
        QueuedFrame out;
        queue.Pop(out, std::chrono::milliseconds(2000));
        popReturned.store(true);
    });

    std::this_thread::sleep_for(std::chrono::milliseconds(50));
    queue.Shutdown();

    consumer.join();
    ASSERT_TRUE(popReturned.load());
});

TEST_REGISTRAR(FrameQueueDropsOldestWhenFull, []() {
    FrameQueue queue(2);
    auto a = MakeNullFrame(1);
    auto b = MakeNullFrame(2);
    auto c = MakeNullFrame(3);
    ASSERT_TRUE(queue.Push(a));
    ASSERT_TRUE(queue.Push(b));
    ASSERT_TRUE(queue.Push(c)); // drops a

    ASSERT_EQ(queue.Dropped(), 1);

    QueuedFrame out;
    ASSERT_TRUE(queue.Pop(out, std::chrono::milliseconds(100)));
    ASSERT_EQ(out.systemRelativeTimeHns, 2);
    ASSERT_TRUE(queue.Pop(out, std::chrono::milliseconds(100)));
    ASSERT_EQ(out.systemRelativeTimeHns, 3);
});

TEST_REGISTRAR(FrameQueueConcurrentPushPop, []() {
    // Use capacity >= kCount so the producer never drops frames and the
    // consumer can receive the full sequence.
    constexpr int kCount = 100;
    FrameQueue queue(kCount);

    std::thread producer([&]() {
        for (int i = 0; i < kCount; ++i) {
            auto qf = MakeNullFrame(i);
            while (!queue.Push(qf)) {
                std::this_thread::yield();
            }
        }
    });

    int received = 0;
    int lastValue = -1;
    while (received < kCount) {
        QueuedFrame out;
        if (queue.Pop(out, std::chrono::milliseconds(100))) {
            ASSERT_GT(out.systemRelativeTimeHns, lastValue);
            lastValue = static_cast<int>(out.systemRelativeTimeHns);
            ++received;
        }
    }

    producer.join();
    ASSERT_EQ(received, kCount);
});

TEST_REGISTRAR(FrameQueueRejectedFrameOwnershipRemainsWithCaller, []() {
    FrameQueue queue(1);
    queue.Shutdown();

    auto qf = MakeNullFrame(42);
    ASSERT_FALSE(queue.Push(qf));
    // The caller still owns the frame after rejection.
    ASSERT_EQ(qf.systemRelativeTimeHns, 42);
});

TEST_REGISTRAR(CaptureLifecycleCallbackGuardEntersAndExits, []() {
    CaptureLifecycle lifecycle;
    ASSERT_TRUE(lifecycle.TryEnterCallback());
    lifecycle.ExitCallback();

    // After exit, a new callback can enter.
    ASSERT_TRUE(lifecycle.TryEnterCallback());
    lifecycle.ExitCallback();
});

TEST_REGISTRAR(CaptureLifecycleRejectsAfterStopAccepting, []() {
    CaptureLifecycle lifecycle;
    lifecycle.StopAccepting();
    ASSERT_FALSE(lifecycle.TryEnterCallback());
});

TEST_REGISTRAR(CaptureLifecycleWaitsForActiveCallbacks, []() {
    CaptureLifecycle lifecycle;
    ASSERT_TRUE(lifecycle.TryEnterCallback());

    std::atomic<bool> waitReturned{false};
    std::thread waiter([&]() {
        waitReturned.store(lifecycle.WaitForCallbacks(std::chrono::milliseconds(500)));
    });

    std::this_thread::sleep_for(std::chrono::milliseconds(50));
    ASSERT_FALSE(waitReturned.load());

    lifecycle.ExitCallback();
    waiter.join();

    ASSERT_TRUE(waitReturned.load());
});

TEST_REGISTRAR(CaptureLifecycleWaitsForMultipleCallbacks, []() {
    CaptureLifecycle lifecycle;
    ASSERT_TRUE(lifecycle.TryEnterCallback());
    ASSERT_TRUE(lifecycle.TryEnterCallback());

    std::thread waiter([&]() {
        lifecycle.WaitForCallbacks(std::chrono::milliseconds(500));
    });

    std::this_thread::sleep_for(std::chrono::milliseconds(50));
    lifecycle.ExitCallback();
    std::this_thread::sleep_for(std::chrono::milliseconds(50));
    lifecycle.ExitCallback();

    waiter.join();
    ASSERT_EQ(lifecycle.activeCallbacks.load(), 0);
});

TEST_REGISTRAR(CallbackGuardAutomaticallyExits, []() {
    CaptureLifecycle lifecycle;
    {
        CallbackGuard guard(lifecycle);
        ASSERT_TRUE(guard.Entered());
        ASSERT_EQ(lifecycle.activeCallbacks.load(), 1);
    }
    ASSERT_EQ(lifecycle.activeCallbacks.load(), 0);
});

} // namespace
