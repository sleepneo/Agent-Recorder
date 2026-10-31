using System.Text.Json.Nodes;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class OutputPathResolverValidationTests
{
    [Theory]
    [InlineData("filename", "../escape")]
    [InlineData("filename", "C:\\escape.mp4")]
    [InlineData("filename", "bad|name")]
    [InlineData("filename", "NUL.mp4")]
    [InlineData("filename", "LPT1.mp4")]
    [InlineData("filename", "bad.")]
    [InlineData("filename_template", "../{id}")]
    [InlineData("filename_template", "bad\0{datetime}")]
    public void PureCheckAndOrdinaryConstructionUseSameNameRulesWithoutCreatingDirectory(string field, string value)
    {
        using var temp = new TempDirectory();
        var dir = Path.Combine(temp.Path, "must-not-create");
        var output = new JsonObject { ["directory"] = dir, [field] = value };
        var early = Assert.Throws<ApiException>(() => OutputPathResolver.ValidateRequest(output));
        var normal = Assert.Throws<ApiException>(() => OutputPathResolver.BuildOutputPath(output, new Recording()));
        Assert.Equal("OUTPUT_PATH_INVALID", early.Code);
        Assert.Equal(early.Code, normal.Code);
        Assert.False(Directory.Exists(dir));
        Assert.Empty(Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("filename", "本次 录制.mp4")]
    [InlineData("filename_template", "本次-{date}-{time}-{datetime}-{source}-{id}")]
    public void PureCheckAllowsOrdinaryNamesWithoutCreatingDirectory(string field, string value)
    {
        using var temp = new TempDirectory();
        var dir = Path.Combine(temp.Path, "not-yet-created");
        OutputPathResolver.ValidateRequest(new JsonObject { ["directory"] = dir, [field] = value });
        Assert.False(Directory.Exists(dir));
        Assert.Empty(Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories));
    }
}
