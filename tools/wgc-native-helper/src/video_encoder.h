#pragma once

#include <atomic>
#include <cstdint>
#include <functional>
#include <string>
#include <vector>

#include "encoder_policy.h"

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
    EncoderMode encoderMode = EncoderMode::Software;
    EncoderSelectionReason selectionReason = EncoderSelectionReason::SoftwareDefault;
    bool selectionKnown = false;
};

enum class EncoderTransformClass {
    Unknown,
    SoftwareH264,
    HardwareH264
};

struct EncoderAttempt {
    EncoderStatus status = EncoderStatus::InitializeFailed;
    std::string error;
    std::string hresult;
    bool hardwareEnumerationAttempted = false;
    bool hardwareCandidateAvailable = false;
    bool hardwareActivationShutdownFailed = false;
    bool writerCreated = false;
    bool writerStarted = false;
    EncoderTransformClass transformClass = EncoderTransformClass::Unknown;
};

enum class EncoderCleanupStatus {
    Removed,
    AlreadyAbsent,
    Failed
};

struct EncoderCleanupResult {
    EncoderCleanupStatus status = EncoderCleanupStatus::Failed;
    std::string error;
    std::string hresult;

    bool Succeeded() const {
        return status == EncoderCleanupStatus::Removed ||
               status == EncoderCleanupStatus::AlreadyAbsent;
    }
};

// Deterministic selection seam used by production VideoEncoder and native
// tests. The callbacks own the actual Media Foundation attempt and the failed
// attempt cleanup; this layer owns the requested-vs-actual policy and fallback
// ordering.
struct EncoderSelectionOperations {
    std::function<EncoderAttempt(EncoderMode)> attempt;
    std::function<EncoderCleanupResult()> cleanupFailedAttempt;
};

EncoderResult ResolveEncoderSelection(
    EncoderMode requestedMode,
    const EncoderSelectionOperations& operations);

// Software H.264 MP4 encoder using Media Foundation Sink Writer.
class VideoEncoder {
public:
    VideoEncoder();
    ~VideoEncoder();

    EncoderResult Initialize(int width, int height, int fps, std::wstring outputPath);
    EncoderResult Initialize(int width, int height, int fps, std::wstring outputPath,
                             EncoderMode requestedMode);

    EncoderMode SelectedMode() const { return selectedMode_; }
    EncoderSelectionReason SelectionReason() const { return selectionReason_; }
    bool SelectionKnown() const { return selectionKnown_; }

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
    EncoderMode selectedMode_ = EncoderMode::Software;
    EncoderSelectionReason selectionReason_ = EncoderSelectionReason::SoftwareDefault;
    bool selectionKnown_ = false;
};

} // namespace wgc
