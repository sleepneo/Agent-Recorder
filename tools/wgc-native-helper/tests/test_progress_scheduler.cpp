#include "test_framework.h"

#include "progress_scheduler.h"

#include <atomic>
#include <chrono>
#include <thread>
#include <vector>

using namespace wgc;

namespace {

TEST_REGISTRAR(ProgressSchedulerEmitsAfterIntervalAndChange, []() {
    std::vector<ProgressSnapshot> emitted;
    ProgressScheduler scheduler([&](const ProgressSnapshot& s) {
        emitted.push_back(s);
    }, std::chrono::milliseconds(50));

    scheduler.Start(1000);
    ASSERT_FALSE(scheduler.HasEmittedProgress());

    // Before interval: no emission.
    scheduler.MaybeEmit({ 1, 0, 10, 100 });
    ASSERT_EQ(emitted.size(), 0u);

    std::this_thread::sleep_for(std::chrono::milliseconds(70));

    // After interval with changed values: emit once.
    scheduler.MaybeEmit({ 1, 0, 80, 100 });
    ASSERT_EQ(emitted.size(), 1u);
    ASSERT_EQ(emitted[0].framesCaptured, 1);
    ASSERT_EQ(emitted[0].bytesWritten, 100);

    // No change in frames/bytes: no emission even after interval.
    std::this_thread::sleep_for(std::chrono::milliseconds(70));
    scheduler.MaybeEmit({ 1, 0, 160, 100 });
    ASSERT_EQ(emitted.size(), 1u);

    // Wait for the next interval boundary, then bytes changed: emit again.
    std::this_thread::sleep_for(std::chrono::milliseconds(70));
    scheduler.MaybeEmit({ 1, 0, 170, 200 });
    ASSERT_EQ(emitted.size(), 2u);
});

TEST_REGISTRAR(ProgressSchedulerIgnoresCallsBeforeStart, []() {
    std::atomic<int> count{0};
    ProgressScheduler scheduler([&](const ProgressSnapshot&) {
        count.fetch_add(1);
    }, std::chrono::milliseconds(10));

    scheduler.MaybeEmit({ 1, 0, 10, 100 });
    std::this_thread::sleep_for(std::chrono::milliseconds(30));
    scheduler.MaybeEmit({ 2, 0, 40, 200 });

    ASSERT_EQ(count.load(), 0);
});

TEST_REGISTRAR(ProgressSchedulerStopsEmittingAfterStop, []() {
    std::atomic<int> count{0};
    ProgressScheduler scheduler([&](const ProgressSnapshot&) {
        count.fetch_add(1);
    }, std::chrono::milliseconds(10));

    scheduler.Start(0);
    std::this_thread::sleep_for(std::chrono::milliseconds(30));
    scheduler.MaybeEmit({ 1, 0, 50, 100 });
    ASSERT_EQ(count.load(), 1);

    scheduler.Stop();
    std::this_thread::sleep_for(std::chrono::milliseconds(30));
    scheduler.MaybeEmit({ 2, 0, 100, 200 });
    ASSERT_EQ(count.load(), 1);
});

TEST_REGISTRAR(ProgressSchedulerSequenceStartedProgressOk, []() {
    std::vector<std::string> stages;
    ProgressScheduler scheduler([&](const ProgressSnapshot&) {
        stages.push_back("PROGRESS");
    }, std::chrono::milliseconds(10));

    stages.push_back("STARTED");
    scheduler.Start(0);

    std::this_thread::sleep_for(std::chrono::milliseconds(30));
    scheduler.MaybeEmit({ 1, 0, 40, 100 });

    // Wait for the next interval boundary before the second PROGRESS.
    std::this_thread::sleep_for(std::chrono::milliseconds(15));
    scheduler.MaybeEmit({ 2, 0, 50, 200 });

    stages.push_back("OK");

    ASSERT_EQ(stages.size(), 4u);
    ASSERT_EQ(stages[0], "STARTED");
    ASSERT_EQ(stages[1], "PROGRESS");
    ASSERT_EQ(stages[2], "PROGRESS");
    ASSERT_EQ(stages[3], "OK");
});

} // namespace
