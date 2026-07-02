using System;
using System.IO;
using System.Security.Cryptography;

namespace AgentRecorder.Infrastructure;

public static class ApiKeyAuth
{
    private const string HeaderName = "X-Agent-Recorder-Key";
    private static string? _apiKey;
    private static bool _initialized;
    private static string? _initializationSource; // 记录初始化时的真实来源
    private static string? _dataDirOverride; // 测试用：可覆盖数据目录
    private static readonly object _lock = new();

    private const string TokenFileName = "api-key.txt";
    private const string ConfigDirName = "config";

    static ApiKeyAuth()
    {
        Initialize();
    }

    private static void Initialize()
    {
        if (_initialized) return;

        lock (_lock)
        {
            if (_initialized) return;

            // 1. 优先使用环境变量
            _apiKey = Environment.GetEnvironmentVariable("AGENT_RECORDER_API_KEY");
            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                _initializationSource = "env";
                _initialized = true;
                return;
            }

            // 2. 从 token 文件读取
            var tokenFilePath = GetTokenFilePath();
            if (File.Exists(tokenFilePath))
            {
                _apiKey = File.ReadAllText(tokenFilePath).Trim();
                if (!string.IsNullOrWhiteSpace(_apiKey))
                {
                    _initializationSource = "local_file";
                    _initialized = true;
                    return;
                }
            }

            // 3. 生成新的随机 token
            _apiKey = GenerateSecureToken();
            EnsureTokenFileDirectory(tokenFilePath);
            File.WriteAllText(tokenFilePath, _apiKey);
            _initializationSource = "generated";

            _initialized = true;
        }
    }

    internal static string GetTokenFilePath()
    {
        var dataDir = _dataDirOverride ?? Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR") ?? ".local-data";
        var configDir = Path.Combine(dataDir, ConfigDirName);
        return Path.Combine(configDir, TokenFileName);
    }

    private static void EnsureTokenFileDirectory(string tokenFilePath)
    {
        var dir = Path.GetDirectoryName(tokenFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    internal static string GenerateSecureToken()
    {
        // 生成 32 字节（256 位）随机 token，Base64 编码后约 44 字符
        var bytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// 验证提供的 key 是否与期望的 key 匹配（纯函数，可测试）
    /// </summary>
    internal static bool ValidateProvidedKey(string expectedKey, string? providedKey)
    {
        if (string.IsNullOrWhiteSpace(providedKey))
            return false;
        return providedKey.Equals(expectedKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// 为测试重置状态（仅测试使用）
    /// </summary>
    internal static void ResetForTesting(string? dataDirOverride = null)
    {
        lock (_lock)
        {
            _apiKey = null;
            _initialized = false;
            _initializationSource = null;
            _dataDirOverride = dataDirOverride;
        }
    }

    /// <summary>
    /// 为测试初始化（仅测试使用）
    /// </summary>
    internal static void InitializeForTesting(string? dataDirOverride = null)
    {
        ResetForTesting(dataDirOverride);
        Initialize();
    }

    public static string CurrentApiKey => _apiKey ?? throw new InvalidOperationException("API Key not initialized");

    public static bool IsEnabled => !string.IsNullOrWhiteSpace(_apiKey);

    /// <summary>
    /// 获取初始化时的真实 token 来源：env（环境变量）、local_file（文件）、generated（自动生成）
    /// </summary>
    public static string GetTokenSource() => _initializationSource ?? "unknown";

    public static void ValidateHeader(string? providedKey)
    {
        if (!IsEnabled) return;

        if (string.IsNullOrWhiteSpace(providedKey))
        {
            throw new ApiException(401, "UNAUTHORIZED",
                $"Missing API key. Please provide '{HeaderName}' header.");
        }

        if (!ValidateProvidedKey(_apiKey!, providedKey))
        {
            throw new ApiException(403, "FORBIDDEN", "Invalid API key");
        }
    }

    public static bool TryValidateHeader(string? providedKey, out string? error)
    {
        if (!IsEnabled)
        {
            error = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(providedKey))
        {
            error = $"Missing API key. Please provide '{HeaderName}' header.";
            return false;
        }

        if (!ValidateProvidedKey(_apiKey!, providedKey))
        {
            error = "Invalid API key";
            return false;
        }

        error = null;
        return true;
    }

    public static string GetApiKey() => CurrentApiKey;
}
