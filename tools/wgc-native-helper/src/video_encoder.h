#pragma once

#include <atomic>
#include <cstdint>
#include <string>
#include <vector>

namespace wgc {

enum class EncoderStatus {
    Ok,
    AlreadyInitialized,
    InitializeFailed,
    WriteFailed,
    FinalizeFailed
};

struct EncoderResult {
    EncoderStatus status = EncoderStatus::Ok;
    std::string error;
    std::string hresult;
};

// Software H.264 MP4 encoder using Media Foundation Sink Writer.
class VideoEncoder {
public:
    VideoEncoder();
    ~VideoEncoder();

    EncoderResult Initialize(int width, int height, int fps, std::wstring outputPath);

    // Pixel data must be 32-bit BGRA (which maps directly to MF RGB32/X8R8G8B8),
    // width*height*4 bytes, top-down.
    EncoderResult WriteFrame(const std::vector<uint8_t>& bgraPixels,
                             int64_t timestampHns,
                             int64_t durationHns);

    EncoderResult Finalize();

    int FrameCount() const { return frameCount_.load(); }

private:
    struct Impl;
    Impl* impl_;

    int width_ = 0;
    int height_ = 0;
    int fps_ = 0;
    int stride_ = 0;
    std::atomic<int> frameCount_{0};
    bool initialized_ = false;
    bool finalized_ = false;
};

} // namespace wgc
