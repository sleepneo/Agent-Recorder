using System;
using System.IO;
using Xunit;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public class DataDirResolverTests
{
    [Fact]
    public void Resolve_Default_NoEnv_NoOverride_ReturnsLocalAppData()
    {
        DataDirResolver.ClearOverride();
        var result = DataDirResolver.Resolve();

        Assert.True(Path.IsPathFullyQualified(result));
        Assert.Equal("AgentRecorder", Path.GetFileName(result));
        Assert.EndsWith(Path.Combine("AppData", "Local", "AgentRecorder"), result);
    }

    [Fact]
    public void Resolve_WithEnvVar_ReturnsEnvVarValue()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "AgentRecorderTest_" + Guid.NewGuid().ToString("N")[..8]);
        var originalEnv = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", testDir);
            DataDirResolver.ClearOverride();

            var result = DataDirResolver.Resolve();

            Assert.Equal(Path.GetFullPath(testDir), result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", originalEnv);
            DataDirResolver.ClearOverride();
        }
    }

    [Fact]
    public void Resolve_WithOverride_ReturnsOverrideValue()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "AgentRecorderTest_" + Guid.NewGuid().ToString("N")[..8]);
        var originalEnv = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", "should-be-ignored");
            DataDirResolver.SetOverride(testDir);

            var result = DataDirResolver.Resolve();

            Assert.Equal(Path.GetFullPath(testDir), result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", originalEnv);
            DataDirResolver.ClearOverride();
        }
    }

    [Fact]
    public void Resolve_ReturnsAbsolutePath()
    {
        var result = DataDirResolver.Resolve();
        Assert.True(Path.IsPathFullyQualified(result));
    }

    [Fact]
    public void ApiKeyAuth_GetTokenFilePath_ReturnsAbsolutePath()
    {
        DataDirResolver.ClearOverride();
        var path = ApiKeyAuth.GetTokenFilePath();
        Assert.True(Path.IsPathFullyQualified(path));
        Assert.EndsWith("config\\api-key.txt", path);
    }

    [Fact]
    public void ApiKeyAuth_GetTokenFilePath_ConsistentWithDataDirResolver()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "AgentRecorderTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            DataDirResolver.SetOverride(testDir);
            var tokenPath = ApiKeyAuth.GetTokenFilePath();
            var expected = Path.Combine(testDir, "config", "api-key.txt");

            Assert.Equal(Path.GetFullPath(expected), tokenPath);
        }
        finally
        {
            DataDirResolver.ClearOverride();
        }
    }

    [Fact]
    public void RuntimeReadiness_ResolveDataDir_ConsistentWithDataDirResolver()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "AgentRecorderTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            DataDirResolver.SetOverride(testDir);
            var readinessDir = RuntimeReadiness.ResolveDataDir();
            var resolverDir = DataDirResolver.Resolve();

            Assert.Equal(resolverDir, readinessDir);
        }
        finally
        {
            DataDirResolver.ClearOverride();
        }
    }

    [Fact]
    public void Paths_DataDir_ConsistentWithDataDirResolver()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "AgentRecorderTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            DataDirResolver.SetOverride(testDir);
            var pathsDir = Paths.DataDir;
            var resolverDir = DataDirResolver.Resolve();

            Assert.Equal(resolverDir, pathsDir);
        }
        finally
        {
            DataDirResolver.ClearOverride();
        }
    }

    [Fact]
    public void ReadySnapshot_ApiKeyFile_IsAbsolutePath()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "AgentRecorderTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            DataDirResolver.SetOverride(testDir);
            var readiness = new RuntimeReadiness("test", 5000);

            Assert.True(Path.IsPathFullyQualified(readiness.ApiKeyFilePath));
            Assert.True(Path.IsPathFullyQualified(readiness.ReadyFilePath));
            Assert.True(Path.IsPathFullyQualified(readiness.AuditLogPathResolved));
            Assert.True(Path.IsPathFullyQualified(readiness.DataDir));
        }
        finally
        {
            DataDirResolver.ClearOverride();
        }
    }

    [Fact]
    public void ReadySnapshot_DataDir_ContainsApiKeyFileParent()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "AgentRecorderTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            DataDirResolver.SetOverride(testDir);
            var readiness = new RuntimeReadiness("test", 5000);

            var apiKeyDir = Path.GetDirectoryName(Path.GetDirectoryName(readiness.ApiKeyFilePath));
            Assert.Equal(testDir, apiKeyDir);
        }
        finally
        {
            DataDirResolver.ClearOverride();
        }
    }

    [Fact]
    public void Paths_AuditLogPath_ConsistentWithDataDirResolver()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "AgentRecorderTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            DataDirResolver.SetOverride(testDir);
            var auditPath = Paths.AuditLogPath;
            var expected = Path.Combine(testDir, "logs", "audit.jsonl");

            Assert.Equal(Path.GetFullPath(expected), auditPath);
        }
        finally
        {
            DataDirResolver.ClearOverride();
        }
    }

    [Fact]
    public void Paths_UsingEnvOverride_FalseWhenNoEnvSet()
    {
        var originalEnv = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null);
            DataDirResolver.ClearOverride();

            Assert.False(Paths.UsingEnvOverride);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", originalEnv);
        }
    }

    [Fact]
    public void Paths_DefaultOutputDir_WithEnvOverride_UsesDataDir()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "AgentRecorderTest_" + Guid.NewGuid().ToString("N")[..8]);
        var originalEnv = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", testDir);
            DataDirResolver.ClearOverride();

            var outputDir = Paths.DefaultOutputDir;
            var expected = Path.Combine(testDir, "Videos");

            Assert.Equal(Path.GetFullPath(expected), outputDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", originalEnv);
            DataDirResolver.ClearOverride();
        }
    }

    [Fact]
    public void DataDirResolver_AllComponents_SameDataDirUnderEnvOverride()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "AgentRecorderTest_" + Guid.NewGuid().ToString("N")[..8]);
        var originalEnv = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", testDir);
            DataDirResolver.ClearOverride();

            var resolverDir = DataDirResolver.Resolve();
            var pathsDir = Paths.DataDir;
            var readinessDir = RuntimeReadiness.ResolveDataDir();

            Assert.Equal(resolverDir, pathsDir);
            Assert.Equal(resolverDir, readinessDir);
            Assert.Equal(Path.GetFullPath(testDir), resolverDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", originalEnv);
            DataDirResolver.ClearOverride();
        }
    }
}