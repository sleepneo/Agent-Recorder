#pragma once

#include <string>
#include <vector>

namespace wgc {

std::wstring Utf8ToWide(std::string_view utf8);
std::string WideToUtf8(std::wstring_view wide);

std::vector<std::wstring> SplitWide(std::wstring_view text, wchar_t delimiter);
std::wstring TrimWide(std::wstring_view text);

bool ParseInt64(std::wstring_view text, long long& value);
bool ParseInt(std::wstring_view text, int& value);

std::string ToLowerAscii(std::string_view text);

} // namespace wgc