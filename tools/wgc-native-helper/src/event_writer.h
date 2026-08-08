#pragma once

#include <cstdint>
#include <iosfwd>
#include <mutex>
#include <string>

namespace wgc {

// Writes blank-line-delimited IPC v2 events to stdout. All methods flush immediately.
// Complete event blocks are serialized against each other: a block emitted from
// the encoder worker (FIRST_FRAME) can never interleave line-by-line with a
// PROGRESS or terminal block emitted from the main thread.
class EventWriter {
public:
    EventWriter();

    void Started(const std::string& recordingId,
                 const std::wstring& outputPath,
                 int fps,
                 int width,
                 int height,
                 const std::string& captureMethod = "WGC_D3D11_FRAME_STREAM");

    // Explicit first-frame evidence: a source frame has arrived, was accepted
    // by the timeline, and was copied/staged successfully. This is emitted
    // exactly once per session and is intentionally independent from
    // FramesCaptured, which keeps its encoded-output meaning.
    void FirstFrame(int64_t frameNumber,
                    int64_t elapsedMs);

    void Progress(int64_t framesCaptured,
                  int64_t framesDropped,
                  int64_t elapsedMs,
                  int64_t bytesWritten);

    void Ok(int64_t framesCaptured,
            int64_t framesDropped,
            int64_t durationMs,
            int64_t fileSize,
            int width,
            int height);

    void Stopped(int64_t framesCaptured,
                 int64_t framesDropped,
                 int64_t durationMs,
                 int64_t fileSize,
                 int width,
                 int height);

    void Fail(const std::string& errorCode,
              const std::string& reason,
              const std::string& hresult,
              const std::wstring& partialOutputPath,
              int64_t framesCaptured,
              int64_t bytesWritten);

    void WriteRaw(const std::string& text);

private:
    void Flush();
    void WriteLine(const std::string& key, const std::string& value);
    void WriteLine(const std::string& key, const std::wstring& value);
    void WriteLine(const std::string& key, int64_t value);
    void WriteLine(const std::string& key, int value);
    void EndBlock();

    // Serializes complete event blocks (all lines plus the blank-line
    // terminator and flush) so concurrent producers never interleave lines.
    std::mutex writeMutex_;
};

} // namespace wgc
