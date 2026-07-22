#include "event_writer.h"

#include "string_utils.h"

#include <cstdio>
#include <format>
#include <iostream>

namespace wgc {

namespace {

std::string WStringToUtf8Line(const std::wstring& value) {
    return WideToUtf8(value);
}

} // namespace

EventWriter::EventWriter() = default;

void EventWriter::Started(const std::string& recordingId,
                          const std::wstring& outputPath,
                          int fps,
                          int width,
                          int height) {
    WriteLine("RESULT", "STARTED");
    WriteLine("Stage", "SessionStarted");
    WriteLine("RecordingId", recordingId);
    WriteLine("Output", outputPath);
    WriteLine("Container", "mp4");
    WriteLine("Codec", "h264");
    WriteLine("Fps", fps);
    WriteLine("Width", width);
    WriteLine("Height", height);
    WriteLine("CaptureMethod", "WGC_D3D11_FRAME_STREAM");
    EndBlock();
}

void EventWriter::Progress(int64_t framesCaptured,
                           int64_t framesDropped,
                           int64_t elapsedMs,
                           int64_t bytesWritten) {
    WriteLine("RESULT", "PROGRESS");
    WriteLine("Stage", "Capturing");
    WriteLine("FramesCaptured", framesCaptured);
    WriteLine("FramesDropped", framesDropped);
    WriteLine("ElapsedMs", elapsedMs);
    WriteLine("BytesWritten", bytesWritten);
    EndBlock();
}

void EventWriter::Ok(int64_t framesCaptured,
                     int64_t framesDropped,
                     int64_t durationMs,
                     int64_t fileSize,
                     int width,
                     int height) {
    WriteLine("RESULT", "OK");
    WriteLine("Stage", "Complete");
    WriteLine("FramesCaptured", framesCaptured);
    WriteLine("FramesDropped", framesDropped);
    WriteLine("DurationMs", durationMs);
    WriteLine("FileSize", std::format("{} bytes", fileSize));
    WriteLine("Width", width);
    WriteLine("Height", height);
    EndBlock();
}

void EventWriter::Stopped(int64_t framesCaptured,
                          int64_t framesDropped,
                          int64_t durationMs,
                          int64_t fileSize,
                          int width,
                          int height) {
    WriteLine("RESULT", "STOPPED");
    WriteLine("StopReason", "user_requested");
    WriteLine("FramesCaptured", framesCaptured);
    WriteLine("FramesDropped", framesDropped);
    WriteLine("DurationMs", durationMs);
    WriteLine("FileSize", std::format("{} bytes", fileSize));
    WriteLine("Width", width);
    WriteLine("Height", height);
    EndBlock();
}

void EventWriter::Fail(const std::string& errorCode,
                       const std::string& reason,
                       const std::string& hresult,
                       const std::wstring& partialOutputPath,
                       int64_t framesCaptured,
                       int64_t bytesWritten) {
    WriteLine("RESULT", "FAIL");
    if (!errorCode.empty()) {
        WriteLine("ErrorCode", errorCode);
    }
    if (!reason.empty()) {
        WriteLine("Reason", reason);
    }
    if (!hresult.empty()) {
        WriteLine("HRESULT", hresult);
    }
    if (!partialOutputPath.empty()) {
        WriteLine("PartialOutputPath", partialOutputPath);
    }
    if (framesCaptured >= 0) {
        WriteLine("FramesCaptured", framesCaptured);
    }
    if (bytesWritten >= 0) {
        WriteLine("BytesWritten", bytesWritten);
    }
    EndBlock();
}

void EventWriter::WriteRaw(const std::string& text) {
    std::cout << text;
    Flush();
}

void EventWriter::Flush() {
    std::cout << std::flush;
}

void EventWriter::WriteLine(const std::string& key, const std::string& value) {
    std::cout << key << ": " << value << '\n';
}

void EventWriter::WriteLine(const std::string& key, const std::wstring& value) {
    std::cout << key << ": " << WStringToUtf8Line(value) << '\n';
}

void EventWriter::WriteLine(const std::string& key, int64_t value) {
    std::cout << key << ": " << value << '\n';
}

void EventWriter::WriteLine(const std::string& key, int value) {
    std::cout << key << ": " << value << '\n';
}

void EventWriter::EndBlock() {
    std::cout << '\n';
    Flush();
}

} // namespace wgc