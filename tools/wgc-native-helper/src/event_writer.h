#pragma once

#include <cstdint>
#include <iosfwd>
#include <string>

namespace wgc {

// Writes blank-line-delimited IPC v2 events to stdout. All methods flush immediately.
class EventWriter {
public:
    EventWriter();

    void Started(const std::string& recordingId,
                 const std::wstring& outputPath,
                 int fps,
                 int width,
                 int height);

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
};

} // namespace wgc