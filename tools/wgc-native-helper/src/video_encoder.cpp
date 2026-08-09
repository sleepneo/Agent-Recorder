#include "video_encoder.h"

#include "pixel_utils.h"
#include "size_policy.h"
#include "mft_activation.h"

#include <windows.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mftransform.h>
#pragma warning(push)
#pragma warning(disable : 4229)
#include <mfreadwrite.h>
#pragma warning(pop)
#include <codecapi.h>
#include <wrl/client.h>

#include <format>
#include <cstring>
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

EncoderResult AttemptToResult(const EncoderAttempt& attempt) {
    EncoderResult result;
    result.status = attempt.status;
    result.error = attempt.error;
    result.hresult = attempt.hresult;
    return result;
}

EncoderResult SelectionFailure(
    const EncoderAttempt& attempt,
    EncoderSelectionReason reason,
    const std::string& detail,
    const EncoderCleanupResult* cleanup = nullptr) {
    EncoderResult result = AttemptToResult(attempt);
    result.status = EncoderStatus::InitializeFailed;
    result.encoderMode = EncoderMode::Software;
    result.selectionReason = reason;
    result.selectionKnown = false;
    if (!detail.empty()) {
        if (!result.error.empty()) result.error += "; ";
        result.error += detail;
    }
    if (cleanup && !cleanup->Succeeded()) {
        if (!result.error.empty()) result.error += "; ";
        result.error += "failed attempt cleanup failed";
        if (!cleanup->error.empty()) result.error += ": " + cleanup->error;
        if (!cleanup->hresult.empty()) result.hresult = cleanup->hresult;
    }
    return result;
}

EncoderResult ResolveEncoderSelectionImpl(
    EncoderMode requestedMode,
    const EncoderSelectionOperations& operations) {
    if (!operations.attempt || !operations.cleanupFailedAttempt) {
        EncoderResult result = MakeError(
            EncoderStatus::InitializeFailed,
            "Encoder selection operations are incomplete",
            E_INVALIDARG);
        result.selectionKnown = false;
        return result;
    }

    auto classifySoftwareAttempt = [](const EncoderAttempt& attempt) {
        if (attempt.status != EncoderStatus::Ok) return std::string();
        if (attempt.transformClass == EncoderTransformClass::HardwareH264) {
            return std::string("software request resolved to hardware H.264 transform");
        }
        if (attempt.transformClass != EncoderTransformClass::SoftwareH264) {
            return std::string("software H.264 transform could not be classified");
        }
        return std::string();
    };

    if (requestedMode == EncoderMode::Software) {
        const EncoderAttempt software = operations.attempt(EncoderMode::Software);
        const std::string classificationError = classifySoftwareAttempt(software);
        if (software.status == EncoderStatus::Ok && classificationError.empty()) {
            EncoderResult result = AttemptToResult(software);
            result.encoderMode = EncoderMode::Software;
            result.selectionReason = EncoderSelectionReason::SoftwareDefault;
            result.selectionKnown = true;
            return result;
        }

        const EncoderCleanupResult cleanup = operations.cleanupFailedAttempt();
        return SelectionFailure(
            software,
            EncoderSelectionReason::SoftwareDefault,
            classificationError,
            &cleanup);
    }

    const EncoderAttempt hardware = operations.attempt(EncoderMode::HardwarePreferred);
    const bool hardwareSelected =
        hardware.status == EncoderStatus::Ok &&
        hardware.transformClass == EncoderTransformClass::HardwareH264;
    if (hardwareSelected) {
        EncoderResult result = AttemptToResult(hardware);
        result.encoderMode = EncoderMode::Hardware;
        result.selectionReason = EncoderSelectionReason::HardwareSelected;
        result.selectionKnown = true;
        return result;
    }

    if (hardware.hardwareActivationShutdownFailed) {
        const EncoderCleanupResult cleanup = operations.cleanupFailedAttempt();
        return SelectionFailure(
            hardware,
            EncoderSelectionReason::HardwareInitFailedFallback,
            "hardware MFT activation ShutdownObject failed; software fallback was not attempted",
            &cleanup);
    }

    EncoderSelectionReason fallbackReason = EncoderSelectionReason::HardwareInitFailedFallback;
    if (hardware.hardwareEnumerationAttempted && !hardware.hardwareCandidateAvailable) {
        fallbackReason = EncoderSelectionReason::HardwareUnavailableFallback;
    } else if (hardware.writerStarted) {
        fallbackReason = EncoderSelectionReason::HardwareUnverifiedFallback;
    }

    const EncoderCleanupResult hardwareCleanup = operations.cleanupFailedAttempt();
    if (!hardwareCleanup.Succeeded()) {
        return SelectionFailure(
            hardware,
            fallbackReason,
            "software fallback was not attempted because hardware cleanup did not complete",
            &hardwareCleanup);
    }

    const EncoderAttempt software = operations.attempt(EncoderMode::Software);
    const std::string classificationError = classifySoftwareAttempt(software);
    if (software.status == EncoderStatus::Ok && classificationError.empty()) {
        EncoderResult result = AttemptToResult(software);
        result.encoderMode = EncoderMode::Software;
        result.selectionReason = fallbackReason;
        result.selectionKnown = true;
        return result;
    }

    const EncoderCleanupResult softwareCleanup = operations.cleanupFailedAttempt();
    return SelectionFailure(
        software,
        fallbackReason,
        classificationError.empty()
            ? "software fallback initialization failed"
            : classificationError,
        &softwareCleanup);
}

struct HardwareH264ActivationEvidence {
    bool enumerationSucceeded = false;
    MftActivationLifecycleResult lifecycle;
};

HardwareH264ActivationEvidence EnumerateHardwareH264() {
    HardwareH264ActivationEvidence evidence;
    MFT_REGISTER_TYPE_INFO outputType = {};
    outputType.guidMajorType = MFMediaType_Video;
    outputType.guidSubtype = MFVideoFormat_H264;

    IMFActivate** activates = nullptr;
    UINT32 count = 0;
    const HRESULT hr = MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER,
                                 MFT_ENUM_FLAG_HARDWARE,
                                 nullptr, &outputType, &activates, &count);
    const auto operations = MakeNativeMftActivationOperations(activates);
    if (FAILED(hr)) {
        evidence.lifecycle = RunMftActivationCleanupOnly(
            reinterpret_cast<void**>(activates), count, operations);
        return evidence;
    }

    evidence.enumerationSucceeded = true;
    evidence.lifecycle = RunMftActivationLifecycle(
        reinterpret_cast<void**>(activates), count, operations, {},
        MftActivationProcessingPolicy::ProcessAll);
    return evidence;
}

bool IsH264OutputType(IMFMediaType* type) {
    if (!type) return false;
    GUID subtype = GUID_NULL;
    return SUCCEEDED(type->GetGUID(MF_MT_SUBTYPE, &subtype)) && subtype == MFVideoFormat_H264;
}

bool HasHardwareUrl(IMFTransform* transform) {
    if (!transform) return false;
    Microsoft::WRL::ComPtr<IMFAttributes> attributes;
    if (FAILED(transform->GetAttributes(&attributes)) || !attributes) return false;

    UINT32 length = 0;
    WCHAR url[512] = {};
    const HRESULT hr = attributes->GetString(MFT_ENUM_HARDWARE_URL_Attribute,
                                               url, static_cast<UINT32>(std::size(url)), &length);
    return SUCCEEDED(hr) && length > 0;
}

bool PathExists(const std::wstring& path) {
    return !path.empty() && ::GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES;
}

bool GetOutputStreamIds(IMFTransform* transform, std::vector<DWORD>& outputIds) {
    outputIds.clear();
    if (!transform) return false;

    DWORD inputCount = 0;
    DWORD outputCount = 0;
    if (FAILED(transform->GetStreamCount(&inputCount, &outputCount)) || outputCount == 0) {
        return false;
    }

    outputIds.resize(outputCount);
    std::vector<DWORD> inputIds(inputCount);
    const HRESULT hr = transform->GetStreamIDs(
        inputCount,
        inputIds.empty() ? nullptr : inputIds.data(),
        outputCount,
        outputIds.data());
    if (SUCCEEDED(hr)) return true;

    // MFTs that use the default stream IDs may return E_NOTIMPL. The single
    // output stream contract defines ID 0 in that case; for multiple outputs
    // fail closed instead of guessing a consecutive stream-ID layout.
    if (hr == E_NOTIMPL && outputCount == 1) {
        outputIds[0] = 0;
        return true;
    }
    outputIds.clear();
    return false;
}

bool HasH264OutputCurrentType(IMFTransform* transform) {
    std::vector<DWORD> outputIds;
    if (!GetOutputStreamIds(transform, outputIds)) return false;
    for (DWORD outputId : outputIds) {
        Microsoft::WRL::ComPtr<IMFMediaType> outputType;
        const HRESULT hr = transform->GetOutputCurrentType(
            outputId, outputType.GetAddressOf());
        if (SUCCEEDED(hr) && IsH264OutputType(outputType.Get())) return true;
    }
    return false;
}

EncoderTransformClass ClassifyH264TransformChain(IMFSinkWriter* sinkWriter, DWORD streamIndex) {
    if (!sinkWriter) return EncoderTransformClass::Unknown;
    Microsoft::WRL::ComPtr<IMFSinkWriterEx> writerEx;
    if (FAILED(sinkWriter->QueryInterface(IID_PPV_ARGS(writerEx.GetAddressOf()))) || !writerEx) {
        return EncoderTransformClass::Unknown;
    }

    // GetTransformForStream exposes the transforms selected by the Sink Writer,
    // rather than the transforms merely available in the registry. The actual
    // transform must be a video encoder and its current output type must be
    // H.264. Only the same transform's SDK hardware URL classifies it as
    // hardware; a request flag, D3D manager, or candidate enumeration alone is
    // never sufficient.
    bool sawSoftwareH264Encoder = false;
    for (DWORD transformIndex = 0; transformIndex < 32; ++transformIndex) {
        Microsoft::WRL::ComPtr<IMFTransform> transform;
        GUID category = GUID_NULL;
        const HRESULT hr = writerEx->GetTransformForStream(
            streamIndex, transformIndex, &category, transform.GetAddressOf());
        if (hr == MF_E_NO_MORE_TYPES) break;
        if (FAILED(hr) || !transform) continue;
        if (category != MFT_CATEGORY_VIDEO_ENCODER ||
            !HasH264OutputCurrentType(transform.Get())) {
            continue;
        }
        if (HasHardwareUrl(transform.Get())) return EncoderTransformClass::HardwareH264;
        sawSoftwareH264Encoder = true;
    }
    return sawSoftwareH264Encoder
        ? EncoderTransformClass::SoftwareH264
        : EncoderTransformClass::Unknown;
}

} // namespace

EncoderResult ResolveEncoderSelection(
    EncoderMode requestedMode,
    const EncoderSelectionOperations& operations) {
    return ResolveEncoderSelectionImpl(requestedMode, operations);
}

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
    return Initialize(width, height, fps, std::move(outputPath), EncoderMode::Software);
}

EncoderResult VideoEncoder::Initialize(int width, int height, int fps, std::wstring outputPath,
                                       EncoderMode requestedMode) {
    if (initialized_) {
        return MakeError(EncoderStatus::AlreadyInitialized, "Encoder already initialized", S_OK);
    }

    NormalizeEncoderDimensions(width, height);
    width_ = width;
    height_ = height;
    fps_ = fps;
    stride_ = width_ * 4;

    auto cleanupFailedAttempt = [&]() -> EncoderCleanupResult {
        impl_->sinkWriter.Reset();
        impl_->started = false;
        impl_->mfLifetime.Shutdown();
        initialized_ = false;
        finalized_ = false;
        if (outputPath.empty()) {
            return { EncoderCleanupStatus::AlreadyAbsent, {}, {} };
        }

        ::SetLastError(ERROR_SUCCESS);
        if (::DeleteFileW(outputPath.c_str())) {
            if (PathExists(outputPath)) {
                return { EncoderCleanupStatus::Failed,
                         "DeleteFileW reported success but the output still exists",
                         HresultToString(HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS)) };
            }
            return { EncoderCleanupStatus::Removed, {}, {} };
        }

        const DWORD error = ::GetLastError();
        if ((error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) &&
            !PathExists(outputPath)) {
            return { EncoderCleanupStatus::AlreadyAbsent, {}, HresultToString(HRESULT_FROM_WIN32(error)) };
        }
        return { EncoderCleanupStatus::Failed,
                 "DeleteFileW failed",
                 HresultToString(HRESULT_FROM_WIN32(error)) };
    };

    auto initializeAttempt = [&](bool requestHardware, bool& hardwareCandidateAvailable,
                                 bool& hardwareProof, bool& hardwareWriterStarted,
                                 bool& hardwareEnumerationRan,
                                 bool& hardwareActivationShutdownFailed,
                                 bool& writerCreated,
                                 EncoderTransformClass& transformClass) -> EncoderResult {
        hardwareCandidateAvailable = false;
        hardwareProof = false;
        hardwareWriterStarted = false;
        hardwareEnumerationRan = false;
        hardwareActivationShutdownFailed = false;
        writerCreated = false;
        transformClass = EncoderTransformClass::Unknown;

        HRESULT hr = impl_->mfLifetime.Startup();
        if (FAILED(hr)) {
            return MakeError(EncoderStatus::InitializeFailed, "MFStartup failed", hr);
        }

        if (requestHardware) {
            hardwareEnumerationRan = true;
            const HardwareH264ActivationEvidence hardwareEvidence = EnumerateHardwareH264();
            hardwareCandidateAvailable = hardwareEvidence.enumerationSucceeded &&
                hardwareEvidence.lifecycle.usableCandidateCount > 0;
            hardwareActivationShutdownFailed =
                hardwareEvidence.lifecycle.shutdownFailureCount > 0;
            if (hardwareActivationShutdownFailed) {
                const HRESULT shutdownHr = hardwareEvidence.lifecycle.shutdownFailureHresult != S_OK
                    ? hardwareEvidence.lifecycle.shutdownFailureHresult
                    : E_FAIL;
                return MakeError(
                    EncoderStatus::InitializeFailed,
                    "Hardware H.264 activation ShutdownObject failed",
                    shutdownHr);
            }
            if (!hardwareCandidateAvailable) {
                return MakeError(EncoderStatus::InitializeFailed,
                                 "No hardware H.264 encoder candidate is available", REGDB_E_CLASSNOTREG);
            }
        }

        Microsoft::WRL::ComPtr<IMFAttributes> sinkAttributes;
        hr = MFCreateAttributes(&sinkAttributes, 1);
        if (FAILED(hr)) {
            return MakeError(EncoderStatus::InitializeFailed, "MFCreateAttributes failed", hr);
        }

        hr = sinkAttributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS,
                                       requestHardware ? TRUE : FALSE);
        if (FAILED(hr)) {
            return MakeError(EncoderStatus::InitializeFailed, "Set sink attribute failed", hr);
        }

        hr = MFCreateSinkWriterFromURL(outputPath.c_str(), nullptr, sinkAttributes.Get(),
                                       impl_->sinkWriter.GetAddressOf());
        if (FAILED(hr)) {
            return MakeError(EncoderStatus::InitializeFailed, "MFCreateSinkWriterFromURL failed", hr);
        }
        writerCreated = true;

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
        hardwareWriterStarted = requestHardware;
        transformClass = ClassifyH264TransformChain(impl_->sinkWriter.Get(), impl_->streamIndex);
        if (requestHardware) {
            hardwareProof = transformClass == EncoderTransformClass::HardwareH264;
            if (!hardwareProof) {
                return MakeError(EncoderStatus::InitializeFailed,
                                 "Sink Writer H.264 transform could not be verified as hardware", E_NOINTERFACE);
            }
        } else if (transformClass != EncoderTransformClass::SoftwareH264) {
            return MakeError(EncoderStatus::InitializeFailed,
                             transformClass == EncoderTransformClass::HardwareH264
                                 ? "Software request resolved to a hardware H.264 transform"
                                 : "Software H.264 transform could not be classified",
                             E_NOINTERFACE);
        }
        initialized_ = true;
        return { EncoderStatus::Ok };
    };

    selectedMode_ = EncoderMode::Software;
    selectionReason_ = EncoderSelectionReason::SoftwareDefault;
    selectionKnown_ = false;
    frameCount_.store(0);

    EncoderSelectionOperations operations;
    operations.attempt = [&](EncoderMode operationMode) {
        bool hardwareAvailable = false;
        bool hardwareProof = false;
        bool hardwareWriterStarted = false;
        bool hardwareEnumerationRan = false;
        bool hardwareActivationShutdownFailed = false;
        bool writerCreated = false;
        EncoderTransformClass transformClass = EncoderTransformClass::Unknown;
        const bool requestHardware = operationMode == EncoderMode::HardwarePreferred;
        const EncoderResult result = initializeAttempt(
            requestHardware,
            hardwareAvailable,
            hardwareProof,
            hardwareWriterStarted,
            hardwareEnumerationRan,
            hardwareActivationShutdownFailed,
            writerCreated,
            transformClass);

        EncoderAttempt attempt;
        attempt.status = result.status;
        attempt.error = result.error;
        attempt.hresult = result.hresult;
        attempt.hardwareEnumerationAttempted = hardwareEnumerationRan;
        attempt.hardwareCandidateAvailable = hardwareAvailable;
        attempt.hardwareActivationShutdownFailed = hardwareActivationShutdownFailed;
        attempt.writerCreated = writerCreated;
        attempt.writerStarted = hardwareWriterStarted;
        attempt.transformClass = transformClass;
        return attempt;
    };
    operations.cleanupFailedAttempt = cleanupFailedAttempt;

    EncoderResult result = ResolveEncoderSelection(requestedMode, operations);
    if (result.status == EncoderStatus::Ok) {
        selectedMode_ = result.encoderMode;
        selectionReason_ = result.selectionReason;
        selectionKnown_ = result.selectionKnown;
    }
    return result;
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
