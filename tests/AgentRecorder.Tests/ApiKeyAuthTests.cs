using System;
using System.IO;
using Xunit;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Tests;

public class ApiKeyAuthTests
{
    private static string GetTestDataDir() =>
        Path.Combine(TestHelper.ProjectRoot, ".local-data", "TestData_" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public void GenerateSecureToken_ReturnsNonEmptyString()
    {
        // 调用真实实现
        var token = ApiKeyAuth.GenerateSecureToken();

        Assert.NotNull(token);
        Assert.NotEmpty(token);
        Assert.True(token.Length >= 40, "Token should be at least 40 characters (Base64 of 32 bytes)");
    }

    [Fact]
    public void GenerateSecureToken_EachCallProducesDifferentToken()
    {
        var token1 = ApiKeyAuth.GenerateSecureToken();
        var token2 = ApiKeyAuth.GenerateSecureToken();

        Assert.NotEqual(token1, token2);
    }

    [Fact]
    public void GenerateSecureToken_NoWhitespace()
    {
        var token = ApiKeyAuth.GenerateSecureToken();

        Assert.DoesNotContain(" ", token);
        Assert.DoesNotContain("\n", token);
        Assert.DoesNotContain("\r", token);
        Assert.DoesNotContain("\t", token);
    }

    [Fact]
    public void GetTokenFilePath_ResolvesToConfigDirectory()
    {
        var testDataDir = GetTestDataDir();
        try
        {
            ApiKeyAuth.ResetForTesting(testDataDir);
            var path = ApiKeyAuth.GetTokenFilePath();

            Assert.EndsWith("config\\api-key.txt", path);
            Assert.Contains(testDataDir, path);
        }
        finally
        {
            ApiKeyAuth.ResetForTesting(null);
            CleanupDirectory(testDataDir);
        }
    }

    [Fact]
    public void InitializeForTesting_WithEmptyDataDir_CreatesTokenFile()
    {
        var testDataDir = GetTestDataDir();
        try
        {
            // 确保目录不存在
            Assert.False(Directory.Exists(testDataDir));

            // 初始化
            ApiKeyAuth.InitializeForTesting(testDataDir);

            // 验证 token 文件被创建
            var tokenFile = ApiKeyAuth.GetTokenFilePath();
            Assert.True(File.Exists(tokenFile), "Token file should be created");

            // 验证 token 内容有效
            var savedToken = File.ReadAllText(tokenFile).Trim();
            Assert.False(string.IsNullOrWhiteSpace(savedToken));
            Assert.Equal(savedToken, ApiKeyAuth.CurrentApiKey);

            // 验证来源是 generated
            Assert.Equal("generated", ApiKeyAuth.GetTokenSource());
        }
        finally
        {
            ApiKeyAuth.ResetForTesting(null);
            CleanupDirectory(testDataDir);
        }
    }

    [Fact]
    public void InitializeForTesting_WithExistingTokenFile_ReadsFromFile()
    {
        var testDataDir = GetTestDataDir();
        try
        {
            // 预先创建 token 文件
            var configDir = Path.Combine(testDataDir, "config");
            Directory.CreateDirectory(configDir);
            var tokenFile = Path.Combine(configDir, "api-key.txt");
            var presetToken = "preset-test-token-12345";
            File.WriteAllText(tokenFile, presetToken);

            // 初始化
            ApiKeyAuth.InitializeForTesting(testDataDir);

            // 验证读取了预设 token
            Assert.Equal(presetToken, ApiKeyAuth.CurrentApiKey);
            Assert.Equal("local_file", ApiKeyAuth.GetTokenSource());
        }
        finally
        {
            ApiKeyAuth.ResetForTesting(null);
            CleanupDirectory(testDataDir);
        }
    }

    [Fact]
    public void InitializeForTesting_WithEnvVar_PrioritizesEnvVar()
    {
        var testDataDir = GetTestDataDir();
        var originalEnv = Environment.GetEnvironmentVariable("AGENT_RECORDER_API_KEY");
        try
        {
            // 设置环境变量
            var envToken = "env-test-token-67890";
            Environment.SetEnvironmentVariable("AGENT_RECORDER_API_KEY", envToken);

            // 预先创建 token 文件（应该被忽略）
            var configDir = Path.Combine(testDataDir, "config");
            Directory.CreateDirectory(configDir);
            var tokenFile = Path.Combine(configDir, "api-key.txt");
            File.WriteAllText(tokenFile, "file-token-should-be-ignored");

            // 初始化
            ApiKeyAuth.InitializeForTesting(testDataDir);

            // 验证使用了环境变量
            Assert.Equal(envToken, ApiKeyAuth.CurrentApiKey);
            Assert.Equal("env", ApiKeyAuth.GetTokenSource());
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_API_KEY", originalEnv);
            ApiKeyAuth.ResetForTesting(null);
            CleanupDirectory(testDataDir);
        }
    }

    [Fact]
    public void ValidateProvidedKey_EmptyKey_ReturnsFalse()
    {
        var result = ApiKeyAuth.ValidateProvidedKey("expected-key", null);
        Assert.False(result);

        result = ApiKeyAuth.ValidateProvidedKey("expected-key", "");
        Assert.False(result);

        result = ApiKeyAuth.ValidateProvidedKey("expected-key", "   ");
        Assert.False(result);
    }

    [Fact]
    public void ValidateProvidedKey_WrongKey_ReturnsFalse()
    {
        var result = ApiKeyAuth.ValidateProvidedKey("correct-key", "wrong-key");
        Assert.False(result);
    }

    [Fact]
    public void ValidateProvidedKey_CorrectKey_ReturnsTrue()
    {
        var result = ApiKeyAuth.ValidateProvidedKey("correct-key", "correct-key");
        Assert.True(result);
    }

    [Fact]
    public void ValidateProvidedKey_CaseSensitive()
    {
        var result = ApiKeyAuth.ValidateProvidedKey("Key123", "key123");
        Assert.False(result, "Key comparison should be case-sensitive");
    }

    [Fact]
    public void IsEnabled_WhenKeySet_ReturnsTrue()
    {
        var testDataDir = GetTestDataDir();
        try
        {
            ApiKeyAuth.InitializeForTesting(testDataDir);
            Assert.True(ApiKeyAuth.IsEnabled);
        }
        finally
        {
            ApiKeyAuth.ResetForTesting(null);
            CleanupDirectory(testDataDir);
        }
    }

    private static void CleanupDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
