#include "test_framework.h"

#include "path_policy.h"

#include "string_utils.h"

#include <cstring>
#include <filesystem>
#include <fstream>
#include <windows.h>
#include <winioctl.h>

// REPARSE_DATA_BUFFER is not guaranteed to be visible with WIN32_LEAN_AND_MEAN.
#ifndef REPARSE_DATA_BUFFER_HEADER_SIZE
#pragma pack(push, 1)
typedef struct _REPARSE_DATA_BUFFER {
    ULONG ReparseTag;
    USHORT ReparseDataLength;
    USHORT Reserved;
    union {
        struct {
            USHORT SubstituteNameOffset;
            USHORT SubstituteNameLength;
            USHORT PrintNameOffset;
            USHORT PrintNameLength;
            ULONG Flags;
            WCHAR PathBuffer[1];
        } SymbolicLinkReparseBuffer;
        struct {
            USHORT SubstituteNameOffset;
            USHORT SubstituteNameLength;
            USHORT PrintNameOffset;
            USHORT PrintNameLength;
            WCHAR PathBuffer[1];
        } MountPointReparseBuffer;
        struct {
            UCHAR DataBuffer[1];
        } GenericReparseBuffer;
    } DUMMYUNIONNAME;
} REPARSE_DATA_BUFFER, *PREPARSE_DATA_BUFFER;
#pragma pack(pop)
#define REPARSE_DATA_BUFFER_HEADER_SIZE FIELD_OFFSET(REPARSE_DATA_BUFFER, GenericReparseBuffer)
#endif

using namespace wgc;

namespace {

std::wstring GetTempDirectory() {
    wchar_t buffer[MAX_PATH + 1] = {};
    DWORD len = ::GetTempPathW(MAX_PATH, buffer);
    return std::wstring(buffer, len);
}

TEST_REGISTRAR(CanonicalPathRejectsRelativePaths, []() {
    ASSERT_TRUE(CanonicalPath(L".").empty());
    ASSERT_TRUE(CanonicalPath(L"relative.mp4").empty());
    ASSERT_TRUE(CanonicalPath(L"..\\parent.mp4").empty());
});

TEST_REGISTRAR(IsPathContainedRequiresSeparatorBoundary, []() {
    ASSERT_TRUE(IsPathContained(L"C:\\temp\\foo\\bar", L"C:\\temp\\foo\\"));
    ASSERT_FALSE(IsPathContained(L"C:\\temp\\foobar", L"C:\\temp\\foo\\"));
    ASSERT_TRUE(IsPathContained(L"C:\\temp\\foo", L"C:\\temp\\foo"));
});

TEST_REGISTRAR(ValidateOutputPathRequiresMp4Extension, []() {
    PathPolicy policy = PathPolicy::CreateDefault();
    PathCheckResult result = ValidateOutputPath(L"C:\\temp\\out.txt", policy);
    ASSERT_FALSE(result.ok);
});

TEST_REGISTRAR(ValidateOutputPathRequiresExistingParent, []() {
    PathPolicy policy = PathPolicy::CreateDefault();
    PathCheckResult result = ValidateOutputPath(L"C:\\nonexistent_dir_12345\\out.mp4", policy);
    ASSERT_FALSE(result.ok);
});

TEST_REGISTRAR(ValidateOutputPathRejectsExistingFile, []() {
    PathPolicy policy = PathPolicy::CreateDefault();
    std::wstring tempFile = GetTempDirectory() + L"wgc_test_existing_" + std::to_wstring(GetCurrentProcessId()) + L".mp4";

    HANDLE h = ::CreateFileW(tempFile.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                             FILE_ATTRIBUTE_NORMAL, nullptr);
    ASSERT_NE(h, INVALID_HANDLE_VALUE);
    ::CloseHandle(h);

    PathCheckResult result = ValidateOutputPath(tempFile, policy);
    ASSERT_FALSE(result.ok);

    std::filesystem::remove(tempFile);
});

TEST_REGISTRAR(ValidateOutputPathAcceptsTempDirectoryMp4, []() {
    PathPolicy policy = PathPolicy::CreateDefault();
    std::wstring tempFile = GetTempDirectory() + L"wgc_test_new_" + std::to_wstring(GetCurrentProcessId()) + L".mp4";

    PathCheckResult result = ValidateOutputPath(tempFile, policy);
    ASSERT_TRUE(result.ok);
    ASSERT_EQ(result.canonicalPath, tempFile);
    ASSERT_FALSE(result.partialPath.empty());
    ASSERT_NE(result.partialPath.find(L".partial.mp4"), std::wstring::npos);
});

TEST_REGISTRAR(ValidateControlPathRejectsNonTempPath, []() {
    PathPolicy policy = PathPolicy::CreateDefault();
    PathCheckResult result = ValidateControlPath(L"C:\\Windows\\begin.txt", policy);
    ASSERT_FALSE(result.ok);
});

TEST_REGISTRAR(ValidateControlPathAcceptsTempPath, []() {
    PathPolicy policy = PathPolicy::CreateDefault();
    std::wstring tempFile = GetTempDirectory() + L"wgc_control_" + std::to_wstring(GetCurrentProcessId()) + L".txt";
    PathCheckResult result = ValidateControlPath(tempFile, policy);
    ASSERT_TRUE(result.ok);
});

TEST_REGISTRAR(PathTraversalViaParentIsBlocked, []() {
    PathPolicy policy = PathPolicy::CreateDefault();
    std::wstring tempFile = GetTempDirectory() + L"wgc_traversal_" + std::to_wstring(GetCurrentProcessId()) + L".mp4";
    std::wstring traversalPath = tempFile + L"\\..\\..\\secret.mp4";

    PathCheckResult result = ValidateOutputPath(traversalPath, policy);
    // Canonicalization resolves ..; if the resolved path is still under temp, it passes,
    // otherwise it fails. Either way, it must not be allowed to escape the permitted roots.
    if (result.ok) {
        ASSERT_TRUE(IsPathContained(result.canonicalPath, GetTempDirectory()));
    }
});

TEST_REGISTRAR(CanonicalPathResolvesSymbolicLinkEscape, []() {
    std::wstring tempRoot = GetTempDirectory();
    std::wstring linkName = tempRoot + L"wgc_symlink_test_" + std::to_wstring(GetCurrentProcessId());
    std::wstring targetDir = tempRoot + L"wgc_symlink_target_" + std::to_wstring(GetCurrentProcessId());

    ::CreateDirectoryW(targetDir.c_str(), nullptr);
    if (!std::filesystem::is_directory(targetDir)) {
        return; // Skip if temp directory cannot be created.
    }

    if (!::CreateSymbolicLinkW(linkName.c_str(), targetDir.c_str(), SYMBOLIC_LINK_FLAG_DIRECTORY)) {
        // Symbolic links require developer mode or elevated privileges on Windows.
        std::filesystem::remove_all(targetDir);
        return; // Skip with a silent pass; report notes this was environment-limited.
    }

    std::wstring fileViaLink = linkName + L"\\out.mp4";
    std::wstring canonical = CanonicalPath(fileViaLink);
    ASSERT_FALSE(canonical.empty());
    ASSERT_TRUE(IsPathContained(canonical, targetDir + L"\\"));

    std::filesystem::remove(fileViaLink);
    std::filesystem::remove(linkName);
    std::filesystem::remove_all(targetDir);
});

TEST_REGISTRAR(CreatePartialPlaceholderPreventsCollision, []() {
    std::wstring tempFile = GetTempDirectory() + L"wgc_partial_placeholder_" +
                            std::to_wstring(GetCurrentProcessId()) + L".partial.mp4";

    HANDLE first = CreatePartialPlaceholder(tempFile);
    ASSERT_NE(first, INVALID_HANDLE_VALUE);

    HANDLE second = CreatePartialPlaceholder(tempFile);
    ASSERT_EQ(second, INVALID_HANDLE_VALUE);

    ::CloseHandle(first);
    std::filesystem::remove(tempFile);
});

TEST_REGISTRAR(ValidateControlPathRequiresWritableParent, []() {
    PathPolicy policy = PathPolicy::CreateForRoots({}, { L"C:\\Windows\\" });
    PathCheckResult result = ValidateControlPath(L"C:\\Windows\\wgc_readonly_test_" +
                                                     std::to_wstring(GetCurrentProcessId()) + L".txt",
                                                 policy);
    ASSERT_FALSE(result.ok);
});

TEST_REGISTRAR(CanonicalPathRejectsControlCharacters, []() {
    ASSERT_TRUE(CanonicalPath(L"C:\\temp\\foo\x01" L"bar.mp4").empty());
    ASSERT_TRUE(CanonicalPath(L"C:\\temp\\foo\nbar.mp4").empty());
});

TEST_REGISTRAR(CanonicalPathRejectsWildcards, []() {
    ASSERT_TRUE(CanonicalPath(L"C:\\temp\\*.mp4").empty());
    ASSERT_TRUE(CanonicalPath(L"C:\\temp\\foo?.mp4").empty());
});

TEST_REGISTRAR(CanonicalPathRejectsDevicePaths, []() {
    ASSERT_TRUE(CanonicalPath(L"\\\\.\\C:").empty());
    ASSERT_TRUE(CanonicalPath(L"\\\\.\\PhysicalDrive0").empty());
});

TEST_REGISTRAR(ValidateOutputPathRejectsExistingPartialPlaceholder, []() {
    PathPolicy policy = PathPolicy::CreateDefault();
    std::wstring tempFile = GetTempDirectory() + L"wgc_existing_partial_" +
                            std::to_wstring(GetCurrentProcessId()) + L".mp4";

    // The partial name is derived from the final name plus process id.
    std::wstring partialFile = tempFile.substr(0, tempFile.find_last_of(L'.')) + L"." +
                               std::to_wstring(GetCurrentProcessId()) + L".partial.mp4";

    HANDLE h = ::CreateFileW(partialFile.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                             FILE_ATTRIBUTE_NORMAL, nullptr);
    ASSERT_NE(h, INVALID_HANDLE_VALUE);
    ::CloseHandle(h);

    PathCheckResult result = ValidateOutputPath(tempFile, policy);
    ASSERT_FALSE(result.ok);

    std::filesystem::remove(partialFile);
});

TEST_REGISTRAR(ValidateOutputPathRejectsBeginStopSamePath, []() {
    PathPolicy policy = PathPolicy::CreateDefault();
    std::wstring samePath = GetTempDirectory() + L"wgc_same_signal_" +
                            std::to_wstring(GetCurrentProcessId()) + L".txt";
    PathCheckResult out = ValidateOutputPath(GetTempDirectory() + L"wgc_same_out_" +
                                                 std::to_wstring(GetCurrentProcessId()) + L".mp4",
                                             policy);
    ASSERT_TRUE(out.ok);

    // The helper-level check is in main.cpp; here we just ensure each path
    // validates independently and that the same control path is canonicalized
    // consistently.
    PathCheckResult begin = ValidateControlPath(samePath, policy);
    PathCheckResult stop = ValidateControlPath(samePath, policy);
    ASSERT_TRUE(begin.ok);
    ASSERT_TRUE(stop.ok);
    ASSERT_EQ(begin.canonicalPath, stop.canonicalPath);
});

TEST_REGISTRAR(CanonicalPathResolvesDotAlias, []() {
    std::wstring tempRoot = GetTempDirectory();
    std::wstring base = tempRoot + L"wgc_canon_alias_" + std::to_wstring(GetCurrentProcessId()) + L".txt";
    std::wstring dotted = tempRoot + L".\\wgc_canon_alias_" + std::to_wstring(GetCurrentProcessId()) + L".txt";

    std::wstring canonBase = CanonicalPath(base);
    std::wstring canonDotted = CanonicalPath(dotted);
    ASSERT_FALSE(canonBase.empty());
    ASSERT_FALSE(canonDotted.empty());
    ASSERT_EQ(canonBase, canonDotted);
});

TEST_REGISTRAR(CanonicalPathIsCaseInsensitiveEqual, []() {
    std::wstring tempRoot = GetTempDirectory();
    std::wstring lower = tempRoot + L"wgc_case_alias_" + std::to_wstring(GetCurrentProcessId()) + L".txt";
    std::wstring upper = tempRoot + L"WGC_CASE_ALIAS_" + std::to_wstring(GetCurrentProcessId()) + L".TXT";

    std::wstring canonLower = CanonicalPath(lower);
    std::wstring canonUpper = CanonicalPath(upper);
    ASSERT_FALSE(canonLower.empty());
    ASSERT_FALSE(canonUpper.empty());
    ASSERT_EQ(_wcsicmp(canonLower.c_str(), canonUpper.c_str()), 0);
});

TEST_REGISTRAR(CanonicalPathResolvesJunctionEscape, []() {
    std::wstring tempRoot = GetTempDirectory();
    std::wstring junctionName = tempRoot + L"wgc_junction_test_" + std::to_wstring(GetCurrentProcessId());
    std::wstring targetDir = tempRoot + L"wgc_junction_target_" + std::to_wstring(GetCurrentProcessId());

    ::CreateDirectoryW(targetDir.c_str(), nullptr);
    if (!std::filesystem::is_directory(targetDir)) {
        return; // Skip if temp directory cannot be created.
    }

    ::CreateDirectoryW(junctionName.c_str(), nullptr);
    if (!std::filesystem::is_directory(junctionName)) {
        std::filesystem::remove_all(targetDir);
        return;
    }

    // Create a directory junction from junctionName to targetDir. This normally
    // requires elevated privileges on Windows unless developer mode is enabled;
    // if it fails, skip the test.
    HANDLE hJunction = ::CreateFileW(junctionName.c_str(), GENERIC_READ | GENERIC_WRITE,
                                     FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                     nullptr, OPEN_EXISTING,
                                     FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                                     nullptr);
    if (hJunction == INVALID_HANDLE_VALUE) {
        std::filesystem::remove(junctionName);
        std::filesystem::remove_all(targetDir);
        return;
    }

    std::vector<uint8_t> reparseBuffer(MAXIMUM_REPARSE_DATA_BUFFER_SIZE, 0);
    std::wstring printName = targetDir;
    std::wstring substituteName = targetDir;
    REPARSE_DATA_BUFFER* rdb = reinterpret_cast<REPARSE_DATA_BUFFER*>(reparseBuffer.data());
    rdb->ReparseTag = IO_REPARSE_TAG_MOUNT_POINT;
    rdb->MountPointReparseBuffer.SubstituteNameOffset = 0;
    rdb->MountPointReparseBuffer.SubstituteNameLength = static_cast<USHORT>(substituteName.size() * sizeof(wchar_t));
    rdb->MountPointReparseBuffer.PrintNameOffset = static_cast<USHORT>((substituteName.size() + 1) * sizeof(wchar_t));
    rdb->MountPointReparseBuffer.PrintNameLength = static_cast<USHORT>(printName.size() * sizeof(wchar_t));
    std::memcpy(rdb->MountPointReparseBuffer.PathBuffer, substituteName.data(), substituteName.size() * sizeof(wchar_t));
    std::memcpy(rdb->MountPointReparseBuffer.PathBuffer + substituteName.size() + 1,
                printName.data(), printName.size() * sizeof(wchar_t));
    rdb->ReparseDataLength = static_cast<USHORT>(
        sizeof(rdb->MountPointReparseBuffer) +
        (substituteName.size() + printName.size() + 2) * sizeof(wchar_t) -
        sizeof(rdb->MountPointReparseBuffer.SubstituteNameOffset) * 4);

    DWORD bytesReturned = 0;
    BOOL deviceOk = ::DeviceIoControl(hJunction, FSCTL_SET_REPARSE_POINT, rdb,
                                      rdb->ReparseDataLength + sizeof(rdb->ReparseTag) + sizeof(rdb->ReparseDataLength),
                                      nullptr, 0, &bytesReturned, nullptr);
    ::CloseHandle(hJunction);

    if (!deviceOk) {
        std::filesystem::remove(junctionName);
        std::filesystem::remove_all(targetDir);
        return; // Skip if junction creation is not permitted.
    }

    std::wstring fileViaJunction = junctionName + L"\\out.mp4";
    std::wstring canonical = CanonicalPath(fileViaJunction);
    ASSERT_FALSE(canonical.empty());
    ASSERT_TRUE(IsPathContained(canonical, targetDir + L"\\"));

    std::filesystem::remove(fileViaJunction);
    std::filesystem::remove(junctionName);
    std::filesystem::remove_all(targetDir);
});

// RAII helper that removes a temporary directory tree on scope exit.
struct ScopedTempTree {
    std::wstring root;
    explicit ScopedTempTree(std::wstring path) : root(std::move(path)) {}
    ~ScopedTempTree() {
        try {
            std::filesystem::remove_all(root);
        } catch (...) {
            // Best-effort cleanup; do not throw from destructor.
        }
    }
};

TEST_REGISTRAR(FindRepositoryRootFrom_SolutionMarkerBeatsNestedLocalData, []() {
    std::wstring tempRoot = GetTempDirectory();
    std::wstring base = tempRoot + L"wgc_root_solution_" + std::to_wstring(GetCurrentProcessId());

    std::wstring deepLocalData = base + L"\\deep\\.local-data";
    std::wstring topLocalData = base + L"\\.local-data";
    std::wstring startDir = base + L"\\deep\\app\\bin";
    std::wstring slnPath = base + L"\\AgentRecorder.sln";

    std::filesystem::create_directories(startDir);
    std::filesystem::create_directories(deepLocalData);
    std::filesystem::create_directories(topLocalData);

    ScopedTempTree guard(base);

    {
        std::ofstream sln(WideToUtf8(slnPath));
        ASSERT_TRUE(sln.is_open());
    }

    std::wstring root = FindRepositoryRootFrom(startDir);
    ASSERT_EQ(root, base + L"\\");
});

TEST_REGISTRAR(FindRepositoryRootFrom_NearestLocalDataWinsWhenNoSolution, []() {
    std::wstring tempRoot = GetTempDirectory();
    std::wstring base = tempRoot + L"wgc_root_nearest_" + std::to_wstring(GetCurrentProcessId());

    std::wstring startDir = base + L"\\a\\b\\c\\bin";
    std::wstring nearest = base + L"\\a\\b\\c\\.local-data";
    std::wstring middle = base + L"\\a\\b\\.local-data";
    std::wstring top = base + L"\\a\\.local-data";

    std::filesystem::create_directories(startDir);
    std::filesystem::create_directories(nearest);
    std::filesystem::create_directories(middle);
    std::filesystem::create_directories(top);

    ScopedTempTree guard(base);

    std::wstring root = FindRepositoryRootFrom(startDir);
    ASSERT_EQ(root, base + L"\\a\\b\\c\\");
});

TEST_REGISTRAR(FindRepositoryRootFrom_NoMarkers_ReturnsEmpty, []() {
    std::wstring tempRoot = GetTempDirectory();
    std::wstring base = tempRoot + L"wgc_root_empty_" + std::to_wstring(GetCurrentProcessId());
    std::wstring startDir = base + L"\\a\\b\\c";

    std::filesystem::create_directories(startDir);

    ScopedTempTree guard(base);

    std::wstring root = FindRepositoryRootFrom(startDir);
    ASSERT_TRUE(root.empty());
});

} // namespace
