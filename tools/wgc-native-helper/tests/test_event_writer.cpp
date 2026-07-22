#include "test_framework.h"

#include "event_writer.h"

#include <iostream>
#include <sstream>

using namespace wgc;

namespace {

// Redirects stdout within the scope of a test and restores it on destruction.
class StdoutCapture {
public:
    StdoutCapture() {
        original_ = std::cout.rdbuf();
        std::cout.rdbuf(buffer_.rdbuf());
    }

    ~StdoutCapture() {
        std::cout.rdbuf(original_);
    }

    std::string Get() const {
        return buffer_.str();
    }

private:
    std::streambuf* original_;
    std::ostringstream buffer_;
};

TEST_REGISTRAR(EventWriterStartedContainsRequiredFields, []() {
    StdoutCapture capture;
    EventWriter writer;
    writer.Started("rec-1", L"C:\\temp\\out.mp4", 30, 1920, 1080);

    std::string output = capture.Get();
    ASSERT_NE(output.find("RESULT: STARTED"), std::string::npos);
    ASSERT_NE(output.find("Stage: SessionStarted"), std::string::npos);
    ASSERT_NE(output.find("RecordingId: rec-1"), std::string::npos);
    ASSERT_NE(output.find("Output: C:\\temp\\out.mp4"), std::string::npos);
    ASSERT_NE(output.find("Container: mp4"), std::string::npos);
    ASSERT_NE(output.find("Codec: h264"), std::string::npos);
    ASSERT_NE(output.find("Fps: 30"), std::string::npos);
    ASSERT_NE(output.find("Width: 1920"), std::string::npos);
    ASSERT_NE(output.find("Height: 1080"), std::string::npos);
    ASSERT_NE(output.find("CaptureMethod: WGC_D3D11_FRAME_STREAM"), std::string::npos);
});

TEST_REGISTRAR(EventWriterProgressContainsRequiredFields, []() {
    StdoutCapture capture;
    EventWriter writer;
    writer.Progress(10, 2, 1000, 12345);

    std::string output = capture.Get();
    ASSERT_NE(output.find("RESULT: PROGRESS"), std::string::npos);
    ASSERT_NE(output.find("Stage: Capturing"), std::string::npos);
    ASSERT_NE(output.find("FramesCaptured: 10"), std::string::npos);
    ASSERT_NE(output.find("FramesDropped: 2"), std::string::npos);
    ASSERT_NE(output.find("ElapsedMs: 1000"), std::string::npos);
    ASSERT_NE(output.find("BytesWritten: 12345"), std::string::npos);
});

TEST_REGISTRAR(EventWriterOkContainsRequiredFields, []() {
    StdoutCapture capture;
    EventWriter writer;
    writer.Ok(300, 5, 10000, 987654, 1920, 1080);

    std::string output = capture.Get();
    ASSERT_NE(output.find("RESULT: OK"), std::string::npos);
    ASSERT_NE(output.find("Stage: Complete"), std::string::npos);
    ASSERT_NE(output.find("FramesCaptured: 300"), std::string::npos);
    ASSERT_NE(output.find("FramesDropped: 5"), std::string::npos);
    ASSERT_NE(output.find("DurationMs: 10000"), std::string::npos);
    ASSERT_NE(output.find("FileSize: 987654 bytes"), std::string::npos);
    ASSERT_NE(output.find("Width: 1920"), std::string::npos);
    ASSERT_NE(output.find("Height: 1080"), std::string::npos);
});

TEST_REGISTRAR(EventWriterStoppedContainsStopReason, []() {
    StdoutCapture capture;
    EventWriter writer;
    writer.Stopped(150, 1, 5000, 54321, 1920, 1080);

    std::string output = capture.Get();
    ASSERT_NE(output.find("RESULT: STOPPED"), std::string::npos);
    ASSERT_NE(output.find("StopReason: user_requested"), std::string::npos);
    ASSERT_NE(output.find("FramesCaptured: 150"), std::string::npos);
    ASSERT_NE(output.find("DurationMs: 5000"), std::string::npos);
    ASSERT_NE(output.find("FileSize: 54321 bytes"), std::string::npos);
});

TEST_REGISTRAR(EventWriterFailContainsErrorDetails, []() {
    StdoutCapture capture;
    EventWriter writer;
    writer.Fail("timeout", "Begin signal not received", "0x800705B4", L"C:\\temp\\out.partial.mp4", 0, 0);

    std::string output = capture.Get();
    ASSERT_NE(output.find("RESULT: FAIL"), std::string::npos);
    ASSERT_NE(output.find("ErrorCode: timeout"), std::string::npos);
    ASSERT_NE(output.find("Reason: Begin signal not received"), std::string::npos);
    ASSERT_NE(output.find("HRESULT: 0x800705B4"), std::string::npos);
    ASSERT_NE(output.find("PartialOutputPath: C:\\temp\\out.partial.mp4"), std::string::npos);
    ASSERT_NE(output.find("FramesCaptured: 0"), std::string::npos);
    ASSERT_NE(output.find("BytesWritten: 0"), std::string::npos);
});

TEST_REGISTRAR(EventWriterBlocksAreBlankLineDelimited, []() {
    StdoutCapture capture;
    EventWriter writer;
    writer.Started("rec-1", L"C:\\temp\\out.mp4", 30, 1920, 1080);
    writer.Ok(1, 0, 1, 100, 1920, 1080);

    std::string output = capture.Get();
    // Each event block ends with a blank line. Between two blocks there should be
    // at least one empty line (two consecutive newlines after the first block).
    ASSERT_NE(output.find("\n\nRESULT: OK"), std::string::npos);
});

} // namespace
