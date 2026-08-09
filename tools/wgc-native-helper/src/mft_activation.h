#pragma once

#include <windows.h>
#include <mfidl.h>
#include <mftransform.h>

#include <cstdint>
#include <functional>

namespace wgc {

enum class MftActivationFailureKind {
    None,
    Activation,
    Inspection,
    Shutdown
};

enum class MftActivationProcessingPolicy {
    ProcessAll,
    StopAfterFirstUsable
};

// The callbacks deliberately use opaque pointers so the lifecycle policy can
// be tested without a GPU MFT or a real IMFActivate implementation. Production
// code uses MakeNativeMftActivationOperations below.
struct MftActivationOperations {
    std::function<HRESULT(void* activation, void** transform)> activateObject;
    std::function<HRESULT(void* activation)> shutdownObject;
    std::function<void(void* transform)> releaseTransform;
    std::function<void(void* activation)> releaseActivation;
    std::function<void()> freeActivationArray;
};

struct MftActivationLifecycleResult {
    std::uint32_t returnedCandidateCount = 0;
    std::uint32_t activationSuccessCount = 0;
    std::uint32_t usableCandidateCount = 0;
    std::uint32_t activationFailureCount = 0;
    std::uint32_t inspectionFailureCount = 0;
    std::uint32_t shutdownFailureCount = 0;
    HRESULT activationFailureHresult = S_OK;
    HRESULT inspectionFailureHresult = S_OK;
    HRESULT shutdownFailureHresult = S_OK;
    HRESULT firstFailureHresult = S_OK;
    MftActivationFailureKind firstFailureKind = MftActivationFailureKind::None;
    bool activationArrayFreed = false;
};

using MftTransformInspector = std::function<bool(void* transform)>;

namespace detail {

class MftActivationArrayGuard {
public:
    MftActivationArrayGuard(
        void** activations,
        std::uint32_t count,
        const MftActivationOperations& operations,
        bool* arrayFreed)
        : activations_(activations),
          count_(count),
          operations_(operations),
          arrayFreed_(arrayFreed) {}

    MftActivationArrayGuard(const MftActivationArrayGuard&) = delete;
    MftActivationArrayGuard& operator=(const MftActivationArrayGuard&) = delete;

    ~MftActivationArrayGuard() {
        Finish();
    }

    void Finish() {
        if (finished_) return;
        ReleaseRemaining();
        if (operations_.freeActivationArray) {
            operations_.freeActivationArray();
            if (arrayFreed_) *arrayFreed_ = true;
        }
        finished_ = true;
    }

    void ReleaseAt(std::uint32_t index) {
        if (!activations_ || index >= count_ || !activations_[index]) return;
        void* activation = activations_[index];
        // Mark the slot before invoking COM Release so an error path cannot
        // release the same activation a second time.
        activations_[index] = nullptr;
        if (operations_.releaseActivation) {
            operations_.releaseActivation(activation);
        }
    }

private:
    void ReleaseRemaining() {
        if (!activations_) return;
        for (std::uint32_t i = 0; i < count_; ++i) {
            ReleaseAt(i);
        }
    }

    void** activations_ = nullptr;
    std::uint32_t count_ = 0;
    const MftActivationOperations& operations_;
    bool* arrayFreed_ = nullptr;
    bool finished_ = false;
};

inline void RecordFailure(
    MftActivationLifecycleResult& result,
    MftActivationFailureKind kind,
    HRESULT hr) {
    if (kind == MftActivationFailureKind::Activation &&
        result.activationFailureHresult == S_OK) {
        result.activationFailureHresult = hr;
    } else if (kind == MftActivationFailureKind::Inspection &&
               result.inspectionFailureHresult == S_OK) {
        result.inspectionFailureHresult = hr;
    } else if (kind == MftActivationFailureKind::Shutdown &&
               result.shutdownFailureHresult == S_OK) {
        result.shutdownFailureHresult = hr;
    }
    if (result.firstFailureKind == MftActivationFailureKind::None) {
        result.firstFailureKind = kind;
        result.firstFailureHresult = hr;
    }
}

} // namespace detail

// Runs the complete lifecycle for every non-null activation in the array.
// A candidate is usable only when ActivateObject succeeds, the optional
// transform inspection succeeds, and ShutdownObject succeeds. The transform
// reference is released before ShutdownObject; every activation is released
// exactly once and the MFTEnumEx array is freed exactly once by the guard.
inline MftActivationLifecycleResult RunMftActivationLifecycle(
    void** activations,
    std::uint32_t count,
    const MftActivationOperations& operations,
    const MftTransformInspector& inspectTransform = {},
    MftActivationProcessingPolicy policy = MftActivationProcessingPolicy::ProcessAll) {
    MftActivationLifecycleResult result;
    result.returnedCandidateCount = count;
    detail::MftActivationArrayGuard arrayGuard(
        activations, count, operations, &result.activationArrayFreed);

    for (std::uint32_t i = 0; i < count; ++i) {
        if (policy == MftActivationProcessingPolicy::StopAfterFirstUsable &&
            result.usableCandidateCount > 0) {
            break;
        }
        void* activation = activations ? activations[i] : nullptr;
        if (!activation) {
            ++result.activationFailureCount;
            detail::RecordFailure(result, MftActivationFailureKind::Activation, E_POINTER);
            continue;
        }

        void* transform = nullptr;
        HRESULT activateHr = E_NOTIMPL;
        if (operations.activateObject) {
            activateHr = operations.activateObject(activation, &transform);
        }
        if (FAILED(activateHr)) {
            ++result.activationFailureCount;
            detail::RecordFailure(result, MftActivationFailureKind::Activation, activateHr);
            if (transform && operations.releaseTransform) {
                operations.releaseTransform(transform);
                transform = nullptr;
            }
            arrayGuard.ReleaseAt(i);
            continue;
        }

        ++result.activationSuccessCount;
        bool inspectionPassed = transform != nullptr;
        if (transform && inspectTransform) {
            inspectionPassed = inspectTransform(transform);
        }
        if (!inspectionPassed) {
            ++result.inspectionFailureCount;
            detail::RecordFailure(result, MftActivationFailureKind::Inspection, E_NOINTERFACE);
        }
        if (transform && operations.releaseTransform) {
            operations.releaseTransform(transform);
            transform = nullptr;
        }

        HRESULT shutdownHr = E_NOTIMPL;
        if (operations.shutdownObject) {
            shutdownHr = operations.shutdownObject(activation);
        }
        const bool shutdownSucceeded = SUCCEEDED(shutdownHr);
        if (!shutdownSucceeded) {
            ++result.shutdownFailureCount;
            detail::RecordFailure(result, MftActivationFailureKind::Shutdown, shutdownHr);
        }
        if (inspectionPassed && shutdownSucceeded) {
            ++result.usableCandidateCount;
        }
        arrayGuard.ReleaseAt(i);
    }

    arrayGuard.Finish();
    return result;
}

// MFTEnumEx may return an activation array even when enumeration itself fails
// in an injected/test seam. This path only releases the returned objects and
// array; it never calls ActivateObject or ShutdownObject.
inline MftActivationLifecycleResult RunMftActivationCleanupOnly(
    void** activations,
    std::uint32_t count,
    const MftActivationOperations& operations) {
    MftActivationLifecycleResult result;
    result.returnedCandidateCount = count;
    detail::MftActivationArrayGuard arrayGuard(
        activations, count, operations, &result.activationArrayFreed);
    arrayGuard.Finish();
    return result;
}

inline MftActivationOperations MakeNativeMftActivationOperations(
    IMFActivate** activationArray) {
    MftActivationOperations operations;
    operations.activateObject = [](void* activation, void** transform) {
        if (!activation || !transform) return E_POINTER;
        return static_cast<IMFActivate*>(activation)->ActivateObject(
            __uuidof(IMFTransform), transform);
    };
    operations.shutdownObject = [](void* activation) {
        if (!activation) return E_POINTER;
        return static_cast<IMFActivate*>(activation)->ShutdownObject();
    };
    operations.releaseTransform = [](void* transform) {
        if (transform) static_cast<IMFTransform*>(transform)->Release();
    };
    operations.releaseActivation = [](void* activation) {
        if (activation) static_cast<IMFActivate*>(activation)->Release();
    };
    operations.freeActivationArray = [activationArray]() {
        ::CoTaskMemFree(activationArray);
    };
    return operations;
}

} // namespace wgc
