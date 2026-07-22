#include "test_framework.h"

#include <iostream>
#include <stdexcept>

namespace wgc {
namespace test {

namespace {

thread_local bool g_currentTestFailed = false;
thread_local AssertionFailure* g_currentFailure = nullptr;

} // namespace

TestRegistry& TestRegistry::Instance() {
    static TestRegistry instance;
    return instance;
}

void TestRegistry::Register(const std::string& name, std::function<void()> func) {
    tests_.push_back({ name, std::move(func) });
}

void AssertTrue(bool condition, const std::string& message, const char* file, int line) {
    if (!condition) {
        g_currentTestFailed = true;
        if (g_currentFailure) {
            g_currentFailure->message = message;
            g_currentFailure->file = file;
            g_currentFailure->line = line;
        }
        throw std::runtime_error(message);
    }
}

void AssertFalse(bool condition, const std::string& message, const char* file, int line) {
    AssertTrue(!condition, message, file, line);
}

namespace {

bool IsWatchdogTestName(const std::string& name) {
    return name.rfind("WATCHDOG_", 0) == 0;
}

int RunTestSuite(bool watchdogOnly) {
    const auto& tests = TestRegistry::Instance().Tests();
    int passed = 0;
    int failed = 0;
    int skipped = 0;

    for (const auto& test : tests) {
        const bool isWatchdog = IsWatchdogTestName(test.name);
        if (watchdogOnly) {
            if (!isWatchdog) {
                skipped++;
                continue;
            }
        } else {
            if (isWatchdog) {
                skipped++;
                continue;
            }
        }

        std::cerr << "RUNALLTESTS_START " << test.name << "\n";
        g_currentTestFailed = false;
        AssertionFailure failure;
        g_currentFailure = &failure;

        try {
            test.func();
            if (!g_currentTestFailed) {
                passed++;
                std::cout << "[PASS] " << test.name << "\n";
            } else {
                failed++;
                std::cerr << "[FAIL] " << test.name << "\n";
            }
        } catch (const std::exception& ex) {
            failed++;
            std::cerr << "[FAIL] " << test.name << " - " << ex.what() << "\n";
        }
    }

    std::cerr << "RUNALLTESTS_DONE passed=" << passed
              << " failed=" << failed
              << " skipped=" << skipped
              << " total=" << tests.size() << "\n";
    std::cout << "\n" << passed << " passed, " << failed << " failed (" << tests.size() << " total)\n";
    return failed;
}

} // namespace

int RunAllTests() {
    return RunTestSuite(false);
}

int RunWatchdogTests() {
    return RunTestSuite(true);
}

} // namespace test
} // namespace wgc
