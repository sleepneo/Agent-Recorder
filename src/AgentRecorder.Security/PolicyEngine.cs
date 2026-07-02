using System;
using System.Linq;
using AgentRecorder.Infrastructure;
using ApiException = AgentRecorder.Infrastructure.ApiException;

namespace AgentRecorder.Security;

public static class PolicyEngine
{
    private static readonly string[] WindowDenylist =
    {
        // English keywords
        "1password", "bitwarden", "keepass", "windows security", "credential manager",
        "password", "passkey", "vault", "keychain", "authy", "duo",
        // Chinese keywords
        "凭据管理器", "windows 安全中心", "密码管理器", "密钥库", "认证器", "令牌"
    };

    private static readonly string[] PathDenylistKeywords =
    {
        @"\program files\", @"\program files (x86)\", @"\windows\", @"\system32\",
        @"\users\public\", @"\users\all users\", @"\programdata\",
        // User sensitive directories
        @"\appdata\roaming\microsoft\credentials",
        @"\appdata\roaming\microsoft\protect",
        @"\appdata\local\microsoft\credentials"
    };

    public static bool RequiresConfirmation() => true;

    public static void CheckDenylist(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        
        var t = title.ToLowerInvariant();
        if (WindowDenylist.Any(d => t.Contains(d)))
            throw new ApiException(403, "SOURCE_UNAVAILABLE", 
                "This window is blocked by security policy: potential credential manager or security application");
    }

    public static void CheckDenylistByProcessName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;
        
        var p = processName.ToLowerInvariant();
        if (WindowDenylist.Any(d => p.Contains(d)))
            throw new ApiException(403, "SOURCE_UNAVAILABLE", 
                "This application is blocked by security policy");
    }

    public static void ValidateDirectory(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            throw new ApiException(400, "OUTPUT_PATH_INVALID", "Directory cannot be empty");

        if (dir.Equals("default", StringComparison.OrdinalIgnoreCase))
            return;

        if (!Path.IsPathFullyQualified(dir))
            throw new ApiException(400, "OUTPUT_PATH_INVALID", 
                "Directory must be an absolute path or 'default'");

        var lower = dir.ToLowerInvariant();

        // Check against Windows system directory
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant();
        if (lower.StartsWith(winDir))
            throw new ApiException(403, "PERMISSION_DENIED", "Writing into system directory is not allowed");

        // Check against Program Files
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).ToLowerInvariant();
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).ToLowerInvariant();
        if (lower.StartsWith(programFiles) || lower.StartsWith(programFilesX86))
            throw new ApiException(403, "PERMISSION_DENIED", "Writing into Program Files is not allowed");

        // Check against other dangerous paths
        if (PathDenylistKeywords.Any(k => lower.Contains(k)))
            throw new ApiException(403, "PERMISSION_DENIED", 
                "Writing into this directory is not allowed by security policy");
    }

    public static void ValidateOutputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ApiException(400, "OUTPUT_PATH_INVALID", "Output path cannot be empty");

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !dir.Equals(".", StringComparison.OrdinalIgnoreCase))
        {
            ValidateDirectory(dir);
        }
    }
}