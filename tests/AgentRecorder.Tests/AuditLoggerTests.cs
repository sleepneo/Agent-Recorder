using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Verifies AuditLogger path injection behavior; default constructor production
/// wiring is preserved via code review and is not exercised against real user
/// directories in these tests.
/// </summary>
public class AuditLoggerTests
{
    [Fact]
    public void InternalConstructor_WritesToInjectedPath()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"audit-inject-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        var path = Path.Combine(tmp, "logs", "audit.jsonl");
        try
        {
            var logger = new AuditLogger(path);
            logger.Log("test.event", new { detail = "value" });

            Assert.True(File.Exists(path));
            var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            Assert.Single(lines);
            var node = JsonNode.Parse(lines[0]);
            Assert.Equal("test.event", node!["event"]!.GetValue<string>());
            Assert.Equal("value", node["detail"]!.GetValue<string>());
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void InternalConstructor_CreatesParentDirectory()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"audit-dir-{Guid.NewGuid():N}");
        var path = Path.Combine(tmp, "deep", "logs", "audit.jsonl");
        try
        {
            Assert.False(Directory.Exists(tmp));
            var logger = new AuditLogger(path);
            logger.Log("test.event", new { });

            Assert.True(Directory.Exists(Path.GetDirectoryName(path)!));
            Assert.True(File.Exists(path));
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void InternalConstructor_NullPath_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new AuditLogger(null!));
        Assert.Equal("path", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void InternalConstructor_EmptyOrWhitespacePath_ThrowsArgumentException(string path)
    {
        var ex = Assert.Throws<ArgumentException>(() => new AuditLogger(path));
        Assert.Equal("path", ex.ParamName);
    }
}
