#include "string_utils.h"

#include <windows.h>
#include <cctype>
#include <cwctype>

namespace wgc {

std::wstring Utf8ToWide(std::string_view utf8) {
    if (utf8.empty()) return {};
    const int size = ::MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
                                           utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
    if (size <= 0) return {};
    std::wstring result(static_cast<size_t>(size), L'\0');
    ::MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8.data(),
                          static_cast<int>(utf8.size()), result.data(), size);
    return result;
}

std::string WideToUtf8(std::wstring_view wide) {
    if (wide.empty()) return {};
    const int size = ::WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
                                           wide.data(), static_cast<int>(wide.size()),
                                           nullptr, 0, nullptr, nullptr);
    if (size <= 0) return {};
    std::string result(static_cast<size_t>(size), '\0');
    ::WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide.data(),
                          static_cast<int>(wide.size()), result.data(), size, nullptr, nullptr);
    return result;
}

std::vector<std::wstring> SplitWide(std::wstring_view text, wchar_t delimiter) {
    std::vector<std::wstring> parts;
    size_t start = 0;
    while (start <= text.size()) {
        const size_t end = text.find(delimiter, start);
        if (end == std::wstring_view::npos) {
            parts.emplace_back(text.substr(start));
            break;
        }
        parts.emplace_back(text.substr(start, end - start));
        start = end + 1;
    }
    return parts;
}

std::wstring TrimWide(std::wstring_view text) {
    size_t first = 0;
    while (first < text.size() && std::iswspace(static_cast<wint_t>(text[first]))) ++first;
    size_t last = text.size();
    while (last > first && std::iswspace(static_cast<wint_t>(text[last - 1]))) --last;
    return std::wstring(text.substr(first, last - first));
}

bool ParseInt64(std::wstring_view text, long long& value) {
    try {
        size_t idx = 0;
        const long long v = std::stoll(std::wstring(text), &idx);
        if (idx == 0 || idx != text.size()) return false;
        value = v;
        return true;
    } catch (...) {
        return false;
    }
}

bool ParseInt(std::wstring_view text, int& value) {
    long long v = 0;
    if (!ParseInt64(text, v)) return false;
    if (v < INT_MIN || v > INT_MAX) return false;
    value = static_cast<int>(v);
    return true;
}

std::string ToLowerAscii(std::string_view text) {
    std::string result(text);
    for (char& c : result) {
        c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    }
    return result;
}

} // namespace wgc