#pragma once

#include <algorithm>
#include <cwctype>
#include <string>
#include <string_view>

namespace wgc {

// EncoderMode is used both for the request policy and the selected result.
// Hardware is never a valid request value; it is only produced after the
// Sink Writer transform chain has been verified.
enum class EncoderMode {
    Software,
    HardwarePreferred,
    Hardware
};

enum class EncoderSelectionReason {
    SoftwareDefault,
    HardwareSelected,
    HardwareUnavailableFallback,
    HardwareInitFailedFallback,
    HardwareUnverifiedFallback
};

inline std::wstring TrimEncoderMode(std::wstring_view value) {
    std::size_t begin = 0;
    while (begin < value.size() && std::iswspace(static_cast<wint_t>(value[begin]))) {
        ++begin;
    }
    std::size_t end = value.size();
    while (end > begin && std::iswspace(static_cast<wint_t>(value[end - 1]))) {
        --end;
    }
    std::wstring result(value.substr(begin, end - begin));
    std::transform(result.begin(), result.end(), result.begin(), [](wchar_t c) {
        return static_cast<wchar_t>(std::towlower(static_cast<wint_t>(c)));
    });
    return result;
}

inline bool TryParseEncoderMode(std::wstring_view value, EncoderMode& mode) {
    const std::wstring normalized = TrimEncoderMode(value);
    if (normalized == L"software") {
        mode = EncoderMode::Software;
        return true;
    }
    if (normalized == L"hardware-preferred") {
        mode = EncoderMode::HardwarePreferred;
        return true;
    }
    return false;
}

inline const char* EncoderModeToString(EncoderMode mode) {
    switch (mode) {
        case EncoderMode::Hardware:
            return "hardware";
        case EncoderMode::HardwarePreferred:
            return "hardware-preferred";
        case EncoderMode::Software:
        default:
            return "software";
    }
}

inline const char* EncoderSelectionReasonToString(EncoderSelectionReason reason) {
    switch (reason) {
        case EncoderSelectionReason::HardwareSelected:
            return "hardware_selected";
        case EncoderSelectionReason::HardwareUnavailableFallback:
            return "hardware_unavailable_fallback";
        case EncoderSelectionReason::HardwareInitFailedFallback:
            return "hardware_init_failed_fallback";
        case EncoderSelectionReason::HardwareUnverifiedFallback:
            return "hardware_unverified_fallback";
        case EncoderSelectionReason::SoftwareDefault:
        default:
            return "software_default";
    }
}

} // namespace wgc
