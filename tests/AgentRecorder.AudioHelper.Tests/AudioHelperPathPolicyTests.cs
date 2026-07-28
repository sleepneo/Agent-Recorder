using System.IO;
using Xunit;

namespace AgentRecorder.AudioHelper.Tests;

public class AudioHelperPathPolicyTests : IDisposable
{
    private readonly string _root;

    public AudioHelperPathPolicyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"audio_helper_path_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void ValidateOutputPath_AbsoluteWavUnderRoot_ReturnsOk()
    {
        var policy = new PathPolicy(_root);
        var output = Path.Combine(_root, "rec.wav");

        var result = policy.ValidateOutputPath(output);

        Assert.True(result.Ok);
        Assert.EndsWith("rec.wav", result.CanonicalPath);
        Assert.EndsWith(".partial.wav", result.PartialPath);
    }

    [Fact]
    public void ValidateOutputPath_RelativePath_ReturnsError()
    {
        var policy = new PathPolicy(_root);
        var result = policy.ValidateOutputPath("rec.wav");

        Assert.False(result.Ok);
        Assert.Contains("absolute", result.Error);
    }

    [Fact]
    public void ValidateOutputPath_Traversal_ReturnsError()
    {
        var policy = new PathPolicy(_root);
        var result = policy.ValidateOutputPath(Path.Combine(_root, "..", "rec.wav"));

        Assert.False(result.Ok);
        Assert.Contains("allowed root", result.Error);
    }

    [Fact]
    public void ValidateOutputPath_WrongExtension_ReturnsError()
    {
        var policy = new PathPolicy(_root);
        var result = policy.ValidateOutputPath(Path.Combine(_root, "rec.mp3"));

        Assert.False(result.Ok);
        Assert.Contains(".wav", result.Error);
    }

    [Fact]
    public void ValidateOutputPath_AlreadyExists_ReturnsError()
    {
        var policy = new PathPolicy(_root);
        var output = Path.Combine(_root, "existing.wav");
        File.WriteAllText(output, "x");

        var result = policy.ValidateOutputPath(output);

        Assert.False(result.Ok);
        Assert.Contains("already exists", result.Error);
    }

    [Fact]
    public void ValidateOutputPath_PartialAlreadyExists_ReturnsError()
    {
        var policy = new PathPolicy(_root);
        var output = Path.Combine(_root, "rec.wav");
        var partial = Path.Combine(_root, $"rec.{Environment.ProcessId}.partial.wav");
        File.WriteAllText(partial, "x");

        var result = policy.ValidateOutputPath(output);

        Assert.False(result.Ok);
        Assert.Contains("Partial", result.Error);
    }

    [Fact]
    public void ValidateOutputPath_ParentDoesNotExist_ReturnsError()
    {
        var policy = new PathPolicy(_root);
        var result = policy.ValidateOutputPath(Path.Combine(_root, "missing", "rec.wav"));

        Assert.False(result.Ok);
        Assert.Contains("parent directory", result.Error);
    }

    [Fact]
    public void ValidateStopSignalPath_UnderRoot_ReturnsOk()
    {
        var policy = new PathPolicy(_root);
        var result = policy.ValidateStopSignalPath(Path.Combine(_root, "stop.signal"));

        Assert.True(result.Ok);
    }

    [Fact]
    public void ValidateStopSignalPath_OutsideRoot_ReturnsError()
    {
        var policy = new PathPolicy(_root);
        var result = policy.ValidateStopSignalPath(Path.Combine(Path.GetTempPath(), "stop.signal"));

        Assert.False(result.Ok);
        Assert.Contains("allowed root", result.Error);
    }

    [Fact]
    public void ValidateOutputPath_OutputEqualsAllowedRoot_ReturnsError()
    {
        var output = Path.Combine(_root, "rec.wav");
        var policy = new PathPolicy(output);

        var result = policy.ValidateOutputPath(output);

        Assert.False(result.Ok);
        Assert.Contains("allowed root", result.Error);
    }

    [Fact]
    public void ValidateStopSignalPath_EqualsOutput_ReturnsError()
    {
        var policy = new PathPolicy(_root);
        var output = Path.Combine(_root, "rec.wav");
        var outputResult = policy.ValidateOutputPath(output);
        Assert.True(outputResult.Ok);

        var result = policy.ValidateStopSignalPath(output, outputResult);

        Assert.False(result.Ok);
        Assert.Contains("output path", result.Error);
    }

    [Fact]
    public void ValidateStopSignalPath_EqualsPartialOutput_ReturnsError()
    {
        var policy = new PathPolicy(_root);
        var output = Path.Combine(_root, "rec.wav");
        var outputResult = policy.ValidateOutputPath(output);
        Assert.True(outputResult.Ok);

        var result = policy.ValidateStopSignalPath(outputResult.PartialPath, outputResult);

        Assert.False(result.Ok);
        Assert.Contains("partial", result.Error);
    }

    [Fact]
    public void ValidateOutputPath_UnsafeCharacters_ReturnsError()
    {
        var policy = new PathPolicy(_root);
        var result = policy.ValidateOutputPath(Path.Combine(_root, "rec*.wav"));

        Assert.False(result.Ok);
        Assert.Contains("unsafe", result.Error);
    }
}
