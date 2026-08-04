#include "test_framework.h"

#include <iostream>
#include <stdexcept>
#include <string_view>

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

int RunTestSuite(bool watchdogOnly, const std::string& filter) {
    const auto& tests = TestRegistry::Instance().Tests();
    int passed = 0;
    int failed = 0;
    int skipped = 0;
    int selected = 0;

    for (const auto& test : tests) {
        if (!filter.empty() && test.name.find(filter) == std::string::npos) {
            skipped++;
            continue;
        }
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
        selected++;

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
              << " selected=" << selected
              << " total=" << tests.size() << "\n";
    std::cout << "\n" << passed << " passed, " << failed << " failed ("
              << selected << " selected, " << tests.size() << " total)\n";
    if (!filter.empty() && selected == 0) {
        std::cerr << "[FILTER_NO_MATCH] " << filter << "\n";
        return 2;
    }
    return failed;
}

} // namespace

int RunAllTests(const std::string& filter) {
    return RunTestSuite(false, filter);
}

int RunWatchdogTests(const std::string& filter) {
    return RunTestSuite(true, filter);
}

} // namespace test
} // namespace wgc
