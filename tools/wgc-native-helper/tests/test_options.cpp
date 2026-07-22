#include "test_framework.h"

#include "options.h"

using namespace wgc;

namespace {

ParseResult ParseArgs(std::initializer_list<const wchar_t*> args) {
    std::vector<wchar_t*> argv;
    argv.push_back(const_cast<wchar_t*>(L"wgc-native-helper.exe"));
    for (const auto* arg : args) {
        argv.push_back(const_cast<wchar_t*>(arg));
    }
    return ParseArguments(static_cast<int>(argv.size()), argv.data());
}

TEST_REGISTRAR(OptionsHelpReturnsHelpMode, []() {
    auto result = ParseArgs({ L"--help" });
    ASSERT_TRUE(result.error.empty());
    ASSERT_EQ(result.options.mode, CaptureMode::Help);
});

TEST_REGISTRAR(OptionsVersionReturnsVersionMode, []() {
    auto result = ParseArgs({ L"--version" });
    ASSERT_TRUE(result.error.empty());
    ASSERT_EQ(result.options.mode, CaptureMode::Version);
});

TEST_REGISTRAR(OptionsProbeReturnsProbeMode, []() {
    auto result = ParseArgs({ L"--probe" });
    ASSERT_TRUE(result.error.empty());
    ASSERT_EQ(result.options.mode, CaptureMode::Probe);
});

TEST_REGISTRAR(OptionsContinuousDisplayParsesAllFields, []() {
    auto result = ParseArgs({
        L"--capture-continuous-display",
        L"--display-bounds", L"0,0,1920,1080",
        L"--recording-id", L"test-recording-1",
        L"--output", L"C:\\temp\\out.mp4",
        L"--duration-ms", L"5000",
        L"--fps", L"30",
        L"--begin-signal", L"C:\\temp\\begin.txt",
        L"--begin-token", L"token-123",
        L"--begin-timeout-ms", L"10000",
        L"--stop-signal", L"C:\\temp\\stop.txt",
        L"--i-understand-this-captures-screen"
    });
    ASSERT_TRUE(result.error.empty());
    ASSERT_EQ(result.options.mode, CaptureMode::ContinuousDisplay);
    ASSERT_EQ(result.options.displayBounds.x, 0);
    ASSERT_EQ(result.options.displayBounds.y, 0);
    ASSERT_EQ(result.options.displayBounds.width, 1920);
    ASSERT_EQ(result.options.displayBounds.height, 1080);
    ASSERT_EQ(result.options.recordingId, L"test-recording-1");
    ASSERT_EQ(result.options.outputPath, L"C:\\temp\\out.mp4");
    ASSERT_EQ(result.options.durationMs, 5000);
    ASSERT_EQ(result.options.fps, 30);
    ASSERT_EQ(result.options.beginSignalPath, L"C:\\temp\\begin.txt");
    ASSERT_EQ(result.options.beginToken, L"token-123");
    ASSERT_EQ(result.options.beginTimeoutMs, 10000);
    ASSERT_EQ(result.options.stopSignalPath, L"C:\\temp\\stop.txt");
    ASSERT_TRUE(result.options.hasConsentFlag);
});

TEST_REGISTRAR(OptionsMissingValueReportsError, []() {
    auto result = ParseArgs({ L"--capture-continuous-display", L"--duration-ms" });
    ASSERT_FALSE(result.error.empty());
});

TEST_REGISTRAR(OptionsInvalidDisplayBoundsFormatFails, []() {
    auto result = ParseArgs({ L"--display-bounds", L"0,0,1920" });
    ASSERT_FALSE(result.error.empty());
});

TEST_REGISTRAR(OptionsDisplayBoundsWithNonNumericFails, []() {
    auto result = ParseArgs({ L"--display-bounds", L"0,0,abc,1080" });
    ASSERT_FALSE(result.error.empty());
});

TEST_REGISTRAR(OptionsDurationTooShortFails, []() {
    auto result = ParseArgs({ L"--duration-ms", L"500" });
    ASSERT_FALSE(result.error.empty());
});

TEST_REGISTRAR(OptionsDurationTooLongFails, []() {
    auto result = ParseArgs({ L"--duration-ms", L"20000" });
    ASSERT_FALSE(result.error.empty());
});

TEST_REGISTRAR(OptionsFpsTooLowFails, []() {
    auto result = ParseArgs({ L"--fps", L"0" });
    ASSERT_FALSE(result.error.empty());
});

TEST_REGISTRAR(OptionsFpsTooHighFails, []() {
    auto result = ParseArgs({ L"--fps", L"61" });
    ASSERT_FALSE(result.error.empty());
});

TEST_REGISTRAR(OptionsBeginTimeoutTooShortFails, []() {
    auto result = ParseArgs({ L"--begin-timeout-ms", L"50" });
    ASSERT_FALSE(result.error.empty());
});

TEST_REGISTRAR(OptionsBeginTimeoutTooLongFails, []() {
    auto result = ParseArgs({ L"--begin-timeout-ms", L"400000" });
    ASSERT_FALSE(result.error.empty());
});

TEST_REGISTRAR(OptionsUnknownArgumentFails, []() {
    auto result = ParseArgs({ L"--unknown-flag" });
    ASSERT_FALSE(result.error.empty());
});

TEST_REGISTRAR(RecordingIdEmptyIsInvalid, []() {
    ASSERT_FALSE(IsValidRecordingId(L""));
});

TEST_REGISTRAR(RecordingIdTooLongIsInvalid, []() {
    ASSERT_FALSE(IsValidRecordingId(std::wstring(65, L'a')));
});

TEST_REGISTRAR(RecordingIdWithInvalidCharIsInvalid, []() {
    ASSERT_FALSE(IsValidRecordingId(L"test/recording"));
});

TEST_REGISTRAR(RecordingIdWithValidCharsIsValid, []() {
    ASSERT_TRUE(IsValidRecordingId(L"test-recording_1.mp4"));
});

} // namespace
