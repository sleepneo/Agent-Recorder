#include "test_framework.h"

#include <windows.h>

#include <filesystem>
#include <string>

namespace fs = std::filesystem;

namespace wgc {
namespace test {

namespace {

std::wstring GetSelfDirectory() {
    std::wstring path;
    path.resize(MAX_PATH);
    DWORD len = ::GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (len == 0) return L"";
    path.resize(len);
    return fs::path(path).parent_path().wstring();
}

std::wstring GetHelperExePath() {
    std::wstring dir = GetSelfDirectory();
    if (dir.empty()) return L"";
    return (fs::path(dir) / L"wgc-native-helper.exe").wstring();
}

std::string ReadEmbeddedManifest(const std::wstring& exePath) {
    HMODULE hMod = ::LoadLibraryExW(exePath.c_str(), nullptr, LOAD_LIBRARY_AS_DATAFILE);
    if (!hMod) return "";

    HRSRC hRes = ::FindResourceW(hMod, MAKEINTRESOURCEW(1), RT_MANIFEST);
    if (!hRes) {
        ::FreeLibrary(hMod);
        return "";
    }

    HGLOBAL hData = ::LoadResource(hMod, hRes);
    if (!hData) {
        ::FreeLibrary(hMod);
        return "";
    }

    DWORD size = ::SizeofResource(hMod, hRes);
    const char* data = static_cast<const char*>(::LockResource(hData));
    if (!data || size == 0) {
        ::FreeLibrary(hMod);
        return "";
    }

    std::string result(data, size);
    ::FreeLibrary(hMod);
    return result;
}

} // namespace

TEST_REGISTRAR(EmbeddedManifest_ResourceExists, []() {
    std::wstring helperExe = GetHelperExePath();
    ASSERT_FALSE(helperExe.empty());
    ASSERT_TRUE(fs::exists(helperExe));

    std::string manifest = ReadEmbeddedManifest(helperExe);
    ASSERT_FALSE(manifest.empty());
});

TEST_REGISTRAR(EmbeddedManifest_ContainsDpiAwareness, []() {
    std::wstring helperExe = GetHelperExePath();
    ASSERT_FALSE(helperExe.empty());
    ASSERT_TRUE(fs::exists(helperExe));

    std::string manifest = ReadEmbeddedManifest(helperExe);
    ASSERT_NE(manifest.find("dpiAwareness"), std::string::npos);
});

TEST_REGISTRAR(EmbeddedManifest_ValueIsPerMonitorV2, []() {
    std::wstring helperExe = GetHelperExePath();
    ASSERT_FALSE(helperExe.empty());
    ASSERT_TRUE(fs::exists(helperExe));

    std::string manifest = ReadEmbeddedManifest(helperExe);
    ASSERT_NE(manifest.find("permonitorv2"), std::string::npos);
});

} // namespace test
} // namespace wgc
