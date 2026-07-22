#pragma once

#include <functional>
#include <string>
#include <vector>

namespace wgc {
namespace test {

struct TestCase {
    std::string name;
    std::function<void()> func;
};

struct TestRegistry {
    static TestRegistry& Instance();
    void Register(const std::string& name, std::function<void()> func);
    const std::vector<TestCase>& Tests() const { return tests_; }

private:
    std::vector<TestCase> tests_;
};

struct TestRegistrar {
    TestRegistrar(const std::string& name, std::function<void()> func) {
        TestRegistry::Instance().Register(name, func);
    }
};

struct AssertionFailure {
    std::string message;
    std::string file;
    int line;
};

void AssertTrue(bool condition, const std::string& message, const char* file, int line);
void AssertFalse(bool condition, const std::string& message, const char* file, int line);

template <typename A, typename B>
void AssertEqImpl(const A& a, const B& b, const char* aExpr, const char* bExpr, const char* file, int line) {
    if (!(a == b)) {
        AssertTrue(false, std::string("Expected ") + aExpr + " == " + bExpr, file, line);
    }
}

template <typename A, typename B>
void AssertNeImpl(const A& a, const B& b, const char* aExpr, const char* bExpr, const char* file, int line) {
    if (!(a != b)) {
        AssertTrue(false, std::string("Expected ") + aExpr + " != " + bExpr, file, line);
    }
}

#define ASSERT_TRUE(cond) ::wgc::test::AssertTrue((cond), #cond " is false", __FILE__, __LINE__)
#define ASSERT_FALSE(cond) ::wgc::test::AssertFalse((cond), #cond " is true", __FILE__, __LINE__)

#define ASSERT_EQ(a, b) ::wgc::test::AssertEqImpl((a), (b), #a, #b, __FILE__, __LINE__)
#define ASSERT_NE(a, b) ::wgc::test::AssertNeImpl((a), (b), #a, #b, __FILE__, __LINE__)

#define ASSERT_GE(a, b) ::wgc::test::AssertTrue((a) >= (b), #a " >= " #b, __FILE__, __LINE__)
#define ASSERT_GT(a, b) ::wgc::test::AssertTrue((a) > (b), #a " > " #b, __FILE__, __LINE__)
#define ASSERT_LE(a, b) ::wgc::test::AssertTrue((a) <= (b), #a " <= " #b, __FILE__, __LINE__)
#define ASSERT_LT(a, b) ::wgc::test::AssertTrue((a) < (b), #a " < " #b, __FILE__, __LINE__)

int RunAllTests();
int RunWatchdogTests();

} // namespace test
} // namespace wgc

#define TEST_REGISTRAR(name, ...) \
    static void Test_##name() { __VA_ARGS__(); } \
    static ::wgc::test::TestRegistrar g_registrar_##name(#name, Test_##name)
