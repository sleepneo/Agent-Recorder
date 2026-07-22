#include "path_policy.h"

#include "string_utils.h"

#include <windows.h>
#include <shlobj.h>
#include <algorithm>
#include <cwctype>
#include <filesystem>

namespace wgc {

namespace {

std::wstring ToLowerWide(std::wstring_view text) {
    std::wstring result(text);
    std::transform(result.begin(), result.end(), result.begin(),
                   [](wchar_t c) { return static_cast<wchar_t>(std::towlower(static_cast<wint_t>(c))); });
    return result;
}

std::wstring EnsureTrailingSeparator(std::wstring_view path) {
    if (path.empty()) return {};
    std::wstring result(path);
    if (result.back() != L'\\' && result.back() != L'/') {
        result.push_back(L'\\');
    }
    return result;
}

std::wstring GetTempRoot() {
    wchar_t buffer[MAX_PATH + 1] = {};
    const DWORD len = ::GetTempPathW(MAX_PATH, buffer);
    if (len == 0 || len > MAX_PATH) return {};
    return EnsureTrailingSeparator(buffer);
}

// Returns the directory containing the running executable, with trailing separator.
std::wstring GetExecutableDirectory() {
    wchar_t buffer[MAX_PATH + 1] = {};
    const DWORD len = ::GetModuleFileNameW(nullptr, buffer, MAX_PATH);
    if (len == 0 || len >= MAX_PATH) return {};
    std::wstring path(buffer, len);
    const size_t lastSep = path.find_last_of(L"\\/");
    if (lastSep == std::wstring::npos) return {};
    return EnsureTrailingSeparator(path.substr(0, lastSep));
}

std::wstring GetLocalDataWgcTestsRoot() {
    std::wstring repoRoot = FindRepositoryRoot();
    if (!repoRoot.empty()) {
        return repoRoot + L".local-data\\wgc-tests\\";
    }
    return {};
}

std::wstring GetLocalDataWgcControlRoot() {
    std::wstring repoRoot = FindRepositoryRoot();
    if (!repoRoot.empty()) {
        return repoRoot + L".local-data\\wgc-control\\";
    }
    return {};
}

} // namespace

// Walks up from the given directory looking for the repository root.
// The canonical marker is AgentRecorder.sln. A .local-data directory is only
// used as a fallback, and the nearest (closest to startDir) candidate wins so
// that nested/stale .local-data folders (e.g. under tools\) do not override
// the application's own data root in portable layouts.
std::wstring FindRepositoryRootFrom(std::wstring_view startDir) {
    if (startDir.empty()) return {};
    // Remove trailing separator for filesystem operations.
    std::wstring dir(startDir);
    if (!dir.empty() && (dir.back() == L'\\' || dir.back() == L'/')) {
        dir.pop_back();
    }

    std::wstring localDataRoot;
    constexpr int kMaxDepth = 6;
    for (int i = 0; i < kMaxDepth && !dir.empty(); ++i) {
        std::wstring solution = dir + L"\\AgentRecorder.sln";
        if (::GetFileAttributesW(solution.c_str()) != INVALID_FILE_ATTRIBUTES) {
            return EnsureTrailingSeparator(dir);
        }

        std::wstring localData = dir + L"\\.local-data";
        if (localDataRoot.empty() &&
            ::GetFileAttributesW(localData.c_str()) != INVALID_FILE_ATTRIBUTES) {
            // Remember only the nearest .local-data as a fallback.
            localDataRoot = EnsureTrailingSeparator(dir);
        }

        const size_t lastSep = dir.find_last_of(L"\\/");
        if (lastSep == std::wstring::npos) break;
        dir = dir.substr(0, lastSep);
    }
    return localDataRoot;
}

std::wstring FindRepositoryRoot() {
    return FindRepositoryRootFrom(GetExecutableDirectory());
}

namespace {

// Rejects relative-looking inputs before any canonicalization.
bool IsAbsoluteInput(std::wstring_view path) {
    if (path.empty()) return false;
    if (path.size() >= 3 && path[1] == L':' && (path[2] == L'\\' || path[2] == L'/')) {
        return true;
    }
    if (path.size() >= 2 && path[0] == L'\\' && path[1] == L'\\') {
        return true;
    }
    return false;
}

// Resolves . and .. but does not follow reparse points.
std::wstring GetLongPath(std::wstring_view path) {
    const std::wstring input(path);
    DWORD len = ::GetFullPathNameW(input.c_str(), 0, nullptr, nullptr);
    if (len == 0) return {};
    std::wstring result(static_cast<size_t>(len) - 1, L'\0');
    DWORD actual = ::GetFullPathNameW(input.c_str(), len, result.data(), nullptr);
    if (actual == 0 || actual > len) return {};
    result.resize(actual);
    return result;
}

// Resolves reparse points for an existing file or directory. Returns empty on
// failure. If the path does not exist, returns the input unchanged.
std::wstring ResolveReparsePoints(std::wstring_view path) {
    const std::wstring input(path);
    HANDLE h = ::CreateFileW(input.c_str(), 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                             nullptr, OPEN_EXISTING,
                             FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                             nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        // Path does not exist; return input unchanged.
        return input;
    }

    wchar_t buffer[MAX_PATH + 1] = {};
    DWORD len = ::GetFinalPathNameByHandleW(h, buffer, MAX_PATH, VOLUME_NAME_DOS);
    ::CloseHandle(h);
    if (len == 0 || len > MAX_PATH) {
        return {};
    }

    std::wstring result(buffer, len);
    // GetFinalPathNameByHandle may prefix \\?\; normalize if possible.
    const std::wstring_view prefix = L"\\\\?\\";
    if (result.size() > prefix.size() && result.compare(0, prefix.size(), prefix) == 0) {
        result = result.substr(prefix.size());
    }
    return result;
}

bool PathExists(std::wstring_view path) {
    if (path.empty()) return false;
    const DWORD attribs = ::GetFileAttributesW(std::wstring(path).c_str());
    return attribs != INVALID_FILE_ATTRIBUTES;
}

// Resolves reparse points for the deepest existing ancestor of a path and
// appends the remaining non-existent components. This prevents symlink/junction
// escapes even when the final file has not been created yet.
std::wstring ResolveExistingPathComponents(std::wstring_view path) {
    const std::wstring input(path);

    std::wstring resolved = ResolveReparsePoints(input);
    if (resolved != input) {
        return resolved.empty() ? input : resolved;
    }

    std::wstring current = input;
    std::vector<std::wstring> suffixes;

    while (true) {
        std::filesystem::path p(current);
        std::wstring parent = p.parent_path().wstring();
        std::wstring filename = p.filename().wstring();

        if (parent.empty() || parent == current) break;

        suffixes.push_back(filename);
        current = parent;

        if (PathExists(current)) {
            std::wstring resolvedParent = ResolveReparsePoints(current);
            if (resolvedParent.empty()) return {};

            std::wstring result = resolvedParent;
            for (auto it = suffixes.rbegin(); it != suffixes.rend(); ++it) {
                if (it->empty()) continue;
                if (result.back() != L'\\' && result.back() != L'/') result += L'\\';
                result += *it;
            }
            return result;
        }
    }

    // No existing ancestor found; return input unchanged.
    return input;
}

} // namespace

std::wstring CanonicalPath(std::wstring_view path) {
    if (!IsAbsoluteInput(path)) {
        return {};
    }
    if (!IsSafePathString(path)) {
        return {};
    }

    std::wstring full = GetLongPath(path);
    if (full.empty()) return {};

    // Normalize separators to backslash.
    for (wchar_t& c : full) {
        if (c == L'/') c = L'\\';
    }

    // Resolve reparse points for existing components. For non-existent final
    // paths (typical output), ResolveExistingPathComponents resolves the deepest
    // existing ancestor (e.g., the parent directory) so containment checks see
    // the real physical location and cannot be fooled by junctions/symlinks.
    std::wstring resolved = ResolveExistingPathComponents(full);
    if (resolved.empty()) return {};

    // Normalize separators again after potential \\?\ removal.
    for (wchar_t& c : resolved) {
        if (c == L'/') c = L'\\';
    }
    return resolved;
}

bool IsPathContained(std::wstring_view childCanonical, std::wstring_view parentCanonical) {
    if (childCanonical.empty() || parentCanonical.empty()) return false;
    if (parentCanonical.size() > childCanonical.size()) return false;
    const std::wstring childLower = ToLowerWide(childCanonical);
    const std::wstring parentLower = ToLowerWide(parentCanonical);
    if (childLower.compare(0, parentLower.size(), parentLower) != 0) return false;
    if (childLower.size() == parentLower.size()) return true;
    const wchar_t lastParent = parentLower.back();
    if (lastParent == L'\\' || lastParent == L'/') return true;
    const wchar_t next = childLower[parentLower.size()];
    return next == L'\\' || next == L'/';
}

bool IsSafePathString(std::wstring_view path) {
    if (path.empty()) return false;

    // Reject device paths.
    const std::wstring_view devicePrefix = L"\\\\.\\";
    if (path.size() >= devicePrefix.size() && path.compare(0, devicePrefix.size(), devicePrefix) == 0) {
        return false;
    }

    // Reject explicit \\?\ paths except \\?\UNC\, which we skip during checks.
    const std::wstring_view ntPrefix = L"\\\\?\\";
    const std::wstring_view uncPrefix = L"\\\\?\\UNC\\";
    size_t checkStart = 0;
    if (path.size() >= ntPrefix.size() && path.compare(0, ntPrefix.size(), ntPrefix) == 0) {
        if (path.size() >= uncPrefix.size() && path.compare(0, uncPrefix.size(), uncPrefix) == 0) {
            checkStart = uncPrefix.size();
        } else {
            return false;
        }
    }

    for (size_t i = checkStart; i < path.size(); ++i) {
        const wchar_t c = path[i];
        // Reject control characters, wildcards, stream separators, and null.
        if (c < 32 || c == L'*' || c == L'?' || c == L'<' || c == L'>' || c == L'|' ||
            c == L'"' || c == 0) {
            return false;
        }
        // Colon is only allowed as the drive-letter separator at index 1.
        if (c == L':') {
            if (i != 1 || path.size() < 3 || path[1] != L':' ||
                (path[2] != L'\\' && path[2] != L'/')) {
                return false;
            }
        }
    }
    return true;
}

PathPolicy PathPolicy::CreateDefault() {
    PathPolicy policy;
    const std::wstring tempRoot = GetTempRoot();
    const std::wstring localTestsRoot = GetLocalDataWgcTestsRoot();
    const std::wstring localControlRoot = GetLocalDataWgcControlRoot();

    if (!localTestsRoot.empty()) {
        policy.outputRoots.push_back(localTestsRoot);
    }
    if (!tempRoot.empty()) {
        policy.outputRoots.push_back(tempRoot);
        policy.controlRoots.push_back(tempRoot);
    }
    if (!localControlRoot.empty()) {
        policy.controlRoots.push_back(localControlRoot);
    }
    return policy;
}

PathPolicy PathPolicy::CreateForRoots(std::vector<std::wstring> outputRoots,
                                      std::vector<std::wstring> controlRoots) {
    PathPolicy policy;
    policy.outputRoots = std::move(outputRoots);
    policy.controlRoots = std::move(controlRoots);
    return policy;
}

PathCheckResult ValidateOutputPath(std::wstring_view path, const PathPolicy& policy) {
    PathCheckResult result;
    if (path.empty()) {
        result.error = "Output path is empty";
        return result;
    }

    const std::wstring canonical = CanonicalPath(path);
    if (canonical.empty()) {
        result.error = "Output path must be absolute and safe";
        return result;
    }
    result.canonicalPath = canonical;

    if ((canonical.size() < 3 || canonical[1] != L':' || canonical[2] != L'\\') &&
        !(canonical.size() >= 2 && canonical[0] == L'\\' && canonical[1] == L'\\')) {
        result.error = "Output path must be absolute";
        return result;
    }

    const size_t dot = canonical.find_last_of(L'.');
    if (dot == std::wstring::npos || ToLowerWide(canonical.substr(dot)) != L".mp4") {
        result.error = "Output path must have .mp4 extension";
        return result;
    }

    bool allowed = false;
    for (const auto& root : policy.outputRoots) {
        if (IsPathContained(canonical, root)) {
            allowed = true;
            break;
        }
    }
    if (!allowed) {
        result.error = "Output path must be under .local-data/wgc-tests/ or the system temp directory";
        return result;
    }

    const std::wstring parent = std::filesystem::path(canonical).parent_path().wstring();
    if (parent.empty() || !std::filesystem::is_directory(parent)) {
        result.error = "Output parent directory does not exist";
        return result;
    }

    // Verify parent directory is writable by attempting to open a temp handle.
    {
        std::wstring probe = parent + L"\\wgc_write_probe_" + std::to_wstring(::GetCurrentProcessId()) + L".tmp";
        HANDLE h = ::CreateFileW(probe.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                                 FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE) {
            result.error = "Output parent directory is not writable";
            return result;
        }
        ::CloseHandle(h);
        ::DeleteFileW(probe.c_str());
    }

    const DWORD attribs = ::GetFileAttributesW(canonical.c_str());
    if (attribs != INVALID_FILE_ATTRIBUTES) {
        result.error = "Output file already exists";
        return result;
    }

    // Use a per-process unique partial name so concurrent helpers cannot collide.
    result.partialPath = canonical.substr(0, dot) + L"." + std::to_wstring(::GetCurrentProcessId()) +
                         L".partial.mp4";

    const DWORD partialAttribs = ::GetFileAttributesW(result.partialPath.c_str());
    if (partialAttribs != INVALID_FILE_ATTRIBUTES) {
        result.error = "Partial output file already exists";
        return result;
    }

    result.ok = true;
    return result;
}

PathCheckResult ValidateControlPath(std::wstring_view path, const PathPolicy& policy) {
    PathCheckResult result;
    if (path.empty()) {
        result.error = "Control signal path is empty";
        return result;
    }

    const std::wstring canonical = CanonicalPath(path);
    if (canonical.empty()) {
        result.error = "Control signal path must be absolute and safe";
        return result;
    }
    result.canonicalPath = canonical;

    if ((canonical.size() < 3 || canonical[1] != L':' || canonical[2] != L'\\') &&
        !(canonical.size() >= 2 && canonical[0] == L'\\' && canonical[1] == L'\\')) {
        result.error = "Control signal path must be absolute";
        return result;
    }

    bool allowed = false;
    for (const auto& root : policy.controlRoots) {
        if (IsPathContained(canonical, root)) {
            allowed = true;
            break;
        }
    }
    if (!allowed) {
        result.error = "Control signal path must be under .local-data/wgc-control/ or the system temp directory";
        return result;
    }

    const std::wstring parent = std::filesystem::path(canonical).parent_path().wstring();
    if (parent.empty() || !std::filesystem::is_directory(parent)) {
        result.error = "Control signal parent directory does not exist";
        return result;
    }

    // Control signals are created by the caller; ensure the directory is writable.
    {
        std::wstring probe = parent + L"\\wgc_control_write_probe_" + std::to_wstring(::GetCurrentProcessId()) + L".tmp";
        HANDLE h = ::CreateFileW(probe.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                                 FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE) {
            result.error = "Control signal parent directory is not writable";
            return result;
        }
        ::CloseHandle(h);
        ::DeleteFileW(probe.c_str());
    }

    result.ok = true;
    return result;
}

HANDLE CreatePartialPlaceholder(std::wstring_view partialPath) {
    const std::wstring path(partialPath);
    return ::CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                         FILE_ATTRIBUTE_NORMAL, nullptr);
}

} // namespace wgc
