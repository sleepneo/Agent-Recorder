#pragma once

#include <windows.h>

#include <string>
#include <vector>

namespace wgc {

struct PathPolicy {
    // Allowed output roots (canonical, lowercase, trailing backslash).
    std::vector<std::wstring> outputRoots;

    // Allowed control-signal roots (canonical, lowercase, trailing backslash).
    std::vector<std::wstring> controlRoots;

    // Creates a policy whose roots are derived from the helper executable's
    // location (repository package root) and the system temp directory.
    static PathPolicy CreateDefault();

    // Creates a policy from explicit roots. Used for testing.
    static PathPolicy CreateForRoots(std::vector<std::wstring> outputRoots,
                                     std::vector<std::wstring> controlRoots);
};

struct PathCheckResult {
    bool ok = false;
    std::string error;
    std::wstring canonicalPath;
    std::wstring partialPath;
};

// Validates an output MP4 path. Does NOT create the file; checks parent,
// extension, containment and absence of existing final/partial files.
PathCheckResult ValidateOutputPath(std::wstring_view path, const PathPolicy& policy);

// Validates a control signal path (begin/stop). Must be absolute and under allowed roots.
PathCheckResult ValidateControlPath(std::wstring_view path, const PathPolicy& policy);

// Returns canonical absolute path (empty on failure). Resolves ., .. and
// reparse points for existing path components. Rejects relative inputs.
std::wstring CanonicalPath(std::wstring_view path);

// Walks up from the running executable to find the repository root (the first
// ancestor containing .local-data or AgentRecorder.sln). Returns empty if not found.
std::wstring FindRepositoryRoot();

// Checks whether child (canonical) is under parent (canonical, with trailing separator).
bool IsPathContained(std::wstring_view childCanonical, std::wstring_view parentCanonical);

// True if the path string contains only safe characters for a file path:
// no wildcards, no control characters, no device/NT paths, no invalid filename chars.
bool IsSafePathString(std::wstring_view path);

// Atomically creates a placeholder partial file with CREATE_NEW so that a
// concurrent helper cannot publish to the same path. Returns the handle on
// success or INVALID_HANDLE_VALUE on failure (caller must close handle).
HANDLE CreatePartialPlaceholder(std::wstring_view partialPath);

} // namespace wgc
