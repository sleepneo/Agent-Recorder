#include "video_encoder.h"

#include "pixel_utils.h"
#include "size_policy.h"

#include <windows.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#pragma warning(push)
#pragma warning(disable : 4229)
#include <mfreadwrite.h>
#pragma warning(pop)
#include <codecapi.h>
#include <wrl/client.h>

#include <format>
#include <memory>

namespace wgc {

namespace {

std::string HresultToString(HRESULT hr) {
    return std::format("0x{:08X}", static_cast<unsigned>(hr));
}

EncoderResult MakeError(EncoderStatus status, const std::string& message, HRESULT hr) {
    EncoderResult result;
    result.status = status;
    result.error = message;
    result.hresult = HresultToString(hr);
    return result;
}

} // namespace

// RAII guard for Media Foundation lifetime. Ensures MFShutdown is always called
// exactly once for each successful MFStartup, even if Finalize fails.
struct MediaFoundationLifetime {
    bool started = false;

    ~MediaFoundationLifetime() { Shutdown(); }

    MediaFoundationLifetime() = default;
    MediaFoundationLifetime(const MediaFoundationLifetime&) = delete;
    MediaFoundationLifetime& operator=(const MediaFoundationLifetime&) = delete;
    MediaFoundationLifetime(MediaFoundationLifetime&& other) noexcept
        : started(other.started) {
        other.started = false;
    }
    MediaFoundationLifetime& operator=(MediaFoundationLifetime&& other) noexcept {
        if (this != &other) {
            Shutdown();
            started = other.started;
            other.started = false;
        }
        return *this;
    }

    HRESULT Startup() {
        HRESULT hr = MFStartup(MF_VERSION);
        if (SUCCEEDED(hr)) {
            started = true;
        }
        return hr;
    }

    void Shutdown() {
        if (started) {
            MFShutdown();
            started = false;
        }
    }
};

struct VideoEncoder::Impl {
    Microsoft::WRL::ComPtr<IMFSinkWriter> sinkWriter;
    DWORD streamIndex = 0;
    bool started = false;
    MediaFoundationLifetime mfLifetime;
};

VideoEncoder::VideoEncoder() : impl_(new Impl()) {}

VideoEncoder::~VideoEncoder() {
    if (initialized_ && !finalized_) {
        Finalize();
    }
    delete impl_;
}

EncoderResult VideoEncoder::Initialize(int width, int height, int fps, std::wstring outputPath) {
    if (initialized_) {
        return MakeError(EncoderStatus::AlreadyInitialized, "Encoder already initialized", S_OK);
    }

    NormalizeEncoderDimensions(width, height);
    width_ = width;
    height_ = height;
    fps_ = fps;
    stride_ = width_ * 4;

    HRESULT hr = impl_->mfLifetime.Startup();
    if (FAILED(hr)) {
        return MakeError(EncoderStatus::InitializeFailed, "MFStartup failed", hr);
    }

    Microsoft::WRL::ComPtr<IMFAttributes> sinkAttributes;
    hr = MFCreateAttributes(&sinkAttributes, 1);
    if (FAILED(hr)) {
        return MakeError(EncoderStatus::InitializeFailed, "MFCreateAttributes failed", hr);
    }

    hr = sinkAttributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, FALSE);
    if (FAILED(hr)) {
        return MakeError(EncoderStatus::InitializeFailed, "Set sink attribute failed", hr);
    }

    hr = MFCreateSinkWriterFromURL(outputPath.c_str(), nullptr, sinkAttributes.Get(),
                                   impl_->sinkWriter.GetAddressOf());
    if (FAILED(hr)) {
        return MakeError(EncoderStatus::InitializeFailed, "MFCreateSinkWriterFromURL failed", hr);
    }

    // Output type: H.264
    Microsoft::WRL::ComPtr<IMFMediaType> outputType;
    hr = MFCreateMediaType(&outputType);
    if (FAILED(hr)) return MakeError(EncoderStatus::InitializeFailed, "MFCreateMediaType failed", hr);

    hr = outputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (FAILED(hr)) return MakeError(EncoderStatus::InitializeFailed, "Set major type failed", hr);

    hr = outputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
    if (FAILED(hr)) return MakeError(EncoderStatus::InitializeFailed, "Set subtype failed", hr);

    hr = outputType->SetUINT32(MF_MT_AVG_BITRATE, 8000000);
    if (FAILED(hr)) return MakeError(EncoderStatus::InitializeFailed, "Set bitrate failed", hr);

    hr = MFSetAttributeSize(outputType.Get(), MF_MT_FRAME_SIZE, width_, height_);
    if (FAILED(hr)) return MakeError(EncoderStatus::InitializeFailed, "Set frame size failed", hr);

    hr = MFSetAttributeRatio(outputType.Get(), MF_MT_FRAME_RATE, fps_, 1);
    if (FAILED(hr)) return MakeError(EncoderStatus::InitializeFailed, "Set frame rate failed", hr);

    hr = outputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (FAILED(hr)) return MakeError(EncoderStatus::InitializeFailed, "Set interlace mode failed", hr);

    hr = outputType->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Main);
    if (FAILED(hr)) return MakeError(EncoderStatus::InitializeFailed, "Set H.264 profile failed", hr);

    // Input type: RGB32 (top-down).
    Microsoft::WRL::ComPtr<IMFMediaType> inputType;
    hr = MFCreateMediaType(&inputType);
    if (FAILED(hr)) return MakeError(EncoderStatus::InitializeFailed, "MFCreateMediaType input failed", hr);

    hr = inputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (FAILED(hr)) return MakeError(EncoderStatus::InitializeFailed, "Set input major type failed", hr);

    hr = inputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
    if (FAILED(hr)) return MakeError(EncoderStatus::InitializeFailed, "Set input subtype failed", hr);

    hr = MFSetAttributeSize(inputType.Get(), MF_MT_FRAME_SIZE, width_, height_);
    if (FAILED(hr)) return MakeError(EncoderStatus::InitializeFailed, "Set input frame size failed", hr);

    hr = MFSetAttributeRatio(inputType.Get(), MF_MT_FRAME_RATE, fps_, 1);
    if (FAILED(hr)) return MakeError(EncoderStatus::InitializeFailed, "Set input frame rate failed", hr);

    hr = inputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (FAILED(hr)) return MakeError(EncoderStatus::InitializeFailed, "Set input interlace mode failed", hr);

    // Media Foundation convention: positive MF_MT_DEFAULT_STRIDE means top-down
    // RGB. The WGC CPU buffer is copied top row first, so the stride is positive.
    hr = inputType->SetUINT32(MF_MT_DEFAULT_STRIDE, static_cast<UINT32>(stride_));
    if (FAILED(hr)) return MakeError(EncoderStatus::InitializeFailed, "Set default stride failed", hr);

    hr = impl_->sinkWriter->AddStream(outputType.Get(), &impl_->streamIndex);
    if (FAILED(hr)) {
        return MakeError(EncoderStatus::InitializeFailed, "AddStream failed", hr);
    }

    hr = impl_->sinkWriter->SetInputMediaType(impl_->streamIndex, inputType.Get(), nullptr);
    if (FAILED(hr)) {
        return MakeError(EncoderStatus::InitializeFailed, "SetInputMediaType failed", hr);
    }

    hr = impl_->sinkWriter->BeginWriting();
    if (FAILED(hr)) {
        return MakeError(EncoderStatus::InitializeFailed, "BeginWriting failed", hr);
    }

    impl_->started = true;
    initialized_ = true;
    return { EncoderStatus::Ok };
}

EncoderResult VideoEncoder::WriteFrame(const std::vector<uint8_t>& bgraPixels,
                                       int64_t timestampHns,
                                       int64_t durationHns) {
    if (!initialized_ || finalized_) {
        return MakeError(EncoderStatus::WriteFailed, "Encoder not initialized or already finalized", S_OK);
    }

    const size_t expected = static_cast<size_t>(width_) * static_cast<size_t>(height_) * 4;
    if (bgraPixels.size() < expected) {
        return MakeError(EncoderStatus::WriteFailed, "Frame pixel buffer too small", S_OK);
    }
    if (timestampHns < 0 || durationHns <= 0) {
        return MakeError(EncoderStatus::WriteFailed, "Invalid frame timestamp or duration", S_OK);
    }

    const std::vector<uint8_t> rgb32 = CopyBgraToRgb32(bgraPixels, width_, height_);
    if (rgb32.empty()) {
        return MakeError(EncoderStatus::WriteFailed, "Failed to prepare RGB32 buffer", S_OK);
    }
    const DWORD bufferSize = static_cast<DWORD>(rgb32.size());

    Microsoft::WRL::ComPtr<IMFMediaBuffer> buffer;
    HRESULT hr = MFCreateMemoryBuffer(bufferSize, buffer.GetAddressOf());
    if (FAILED(hr)) {
        return MakeError(EncoderStatus::WriteFailed, "MFCreateMemoryBuffer failed", hr);
    }

    BYTE* data = nullptr;
    DWORD maxLength = 0;
    DWORD currentLength = 0;
    hr = buffer->Lock(&data, &maxLength, &currentLength);
    if (FAILED(hr)) {
        return MakeError(EncoderStatus::WriteFailed, "Buffer lock failed", hr);
    }
    std::memcpy(data, rgb32.data(), bufferSize);
    buffer->Unlock();
    hr = buffer->SetCurrentLength(bufferSize);
    if (FAILED(hr)) {
        return MakeError(EncoderStatus::WriteFailed, "SetCurrentLength failed", hr);
    }

    Microsoft::WRL::ComPtr<IMFSample> sample;
    hr = MFCreateSample(&sample);
    if (FAILED(hr)) {
        return MakeError(EncoderStatus::WriteFailed, "MFCreateSample failed", hr);
    }

    hr = sample->AddBuffer(buffer.Get());
    if (FAILED(hr)) {
        return MakeError(EncoderStatus::WriteFailed, "AddBuffer failed", hr);
    }

    hr = sample->SetSampleTime(timestampHns);
    if (FAILED(hr)) {
        return MakeError(EncoderStatus::WriteFailed, "SetSampleTime failed", hr);
    }

    hr = sample->SetSampleDuration(durationHns);
    if (FAILED(hr)) {
        return MakeError(EncoderStatus::WriteFailed, "SetSampleDuration failed", hr);
    }

    hr = impl_->sinkWriter->WriteSample(impl_->streamIndex, sample.Get());
    if (FAILED(hr)) {
        return MakeError(EncoderStatus::WriteFailed, "WriteSample failed", hr);
    }

    frameCount_.fetch_add(1);
    return { EncoderStatus::Ok };
}

EncoderResult VideoEncoder::Finalize() {
    if (!initialized_ || finalized_) {
        return { EncoderStatus::Ok };
    }

    HRESULT hr = S_OK;
    if (impl_->started) {
        hr = impl_->sinkWriter->Finalize();
    }

    // Always release the sink writer and shut down Media Foundation, even if
    // sink writer finalization failed. This guarantees MFStartup/MFShutdown
    // pairing and prevents resource leaks on error paths.
    finalized_ = true;
    impl_->sinkWriter.Reset();
    impl_->mfLifetime.Shutdown();

    if (FAILED(hr)) {
        return MakeError(EncoderStatus::FinalizeFailed, "Sink writer finalize failed", hr);
    }
    return { EncoderStatus::Ok };
}

} // namespace wgc
