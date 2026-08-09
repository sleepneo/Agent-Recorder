#include "test_framework.h"

#include "event_writer.h"

#include <iostream>
#include <sstream>
#include <thread>
#include <vector>

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

TEST_REGISTRAR(EventWriterWindowStartedUsesDistinctCaptureMethod, []() {
    StdoutCapture capture;
    EventWriter writer;
    writer.Started("rec-window", L"C:\\temp\\out.mp4", 30, 1280, 720,
                   "WGC_D3D11_WINDOW_FRAME_STREAM");

    ASSERT_NE(capture.Get().find("CaptureMethod: WGC_D3D11_WINDOW_FRAME_STREAM"), std::string::npos);
});

TEST_REGISTRAR(EventWriterEncoderSelectionFieldsAreEmittedOnlyWithProof, []() {
    StdoutCapture capture;
    EventWriter writer;
    writer.Started("rec-hw", L"C:\\temp\\out.mp4", 30, 1920, 1080,
                   "WGC_D3D11_FRAME_STREAM", EncoderMode::Hardware,
                   EncoderSelectionReason::HardwareSelected, true);
    writer.Ok(1, 0, 1000, 100, 1920, 1080,
              EncoderMode::Hardware, EncoderSelectionReason::HardwareSelected, true);

    const std::string output = capture.Get();
    ASSERT_NE(output.find("EncoderMode: hardware"), std::string::npos);
    ASSERT_NE(output.find("EncoderSelectionReason: hardware_selected"), std::string::npos);
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

TEST_REGISTRAR(EventWriterFirstFrameContainsRequiredFields, []() {
    StdoutCapture capture;
    EventWriter writer;
    writer.FirstFrame(1, 42);

    std::string output = capture.Get();
    ASSERT_NE(output.find("RESULT: FIRST_FRAME"), std::string::npos);
    ASSERT_NE(output.find("Stage: Capturing"), std::string::npos);
    ASSERT_NE(output.find("FrameNumber: 1"), std::string::npos);
    ASSERT_NE(output.find("ElapsedMs: 42"), std::string::npos);
    // A FIRST_FRAME block must not carry encoded-output fields.
    ASSERT_EQ(output.find("FramesCaptured"), std::string::npos);
});

TEST_REGISTRAR(EventWriterConcurrentBlocksDoNotInterleave, []() {
    StdoutCapture capture;
    EventWriter writer;

    constexpr int kThreads = 4;
    constexpr int kIterations = 50;
    std::vector<std::thread> threads;
    threads.reserve(kThreads);
    for (int t = 0; t < kThreads; ++t) {
        threads.emplace_back([&writer, t]() {
            for (int i = 0; i < kIterations; ++i) {
                switch ((t + i) % 3) {
                    case 0:
                        writer.FirstFrame(1, i);
                        break;
                    case 1:
                        writer.Progress(i, 0, i, i * 100);
                        break;
                    default:
                        writer.Stopped(i, 0, i, i * 100, 1920, 1080);
                        break;
                }
            }
        });
    }
    for (auto& thread : threads) {
        thread.join();
    }

    const std::string output = capture.Get();

    // Split into blank-line-delimited blocks. Every block must be a complete,
    // intact event: exactly one RESULT line and no foreign RESULT line inside.
    std::istringstream stream(output);
    std::string line;
    std::vector<std::string> block;
    int blockCount = 0;
    int resultLinesInCurrentBlock = 0;
    auto flushBlock = [&]() {
        if (block.empty()) return;
        blockCount++;
        ASSERT_EQ(resultLinesInCurrentBlock, 1);
        const std::string& resultLine = block.front();
        ASSERT_EQ(resultLine.rfind("RESULT: ", 0), 0);
        block.clear();
        resultLinesInCurrentBlock = 0;
    };

    while (std::getline(stream, line)) {
        if (line.empty()) {
            flushBlock();
            continue;
        }
        // A RESULT line may only appear as the first line of a block; if one
        // appears mid-block, two events interleaved line-by-line.
        if (line.rfind("RESULT: ", 0) == 0) {
            ASSERT_TRUE(block.empty());
            resultLinesInCurrentBlock++;
        }
        block.push_back(line);
    }
    flushBlock();

    ASSERT_EQ(blockCount, kThreads * kIterations);
});

} // namespace
