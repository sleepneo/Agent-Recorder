using System.Net;
using System.Text.Json.Nodes;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;
public sealed partial class OrdinaryAvOutputOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultVideoIdentityTwoLegalRequestsShareDirectoryWithoutCollision(bool activeNested)
    {
        using var p = new Pair(); var f = p.F;
        var a = p.Request(f.Root, "unused.mp4", "outer", av: false); a["output"]!.AsObject().Remove("filename");
        if (!activeNested) a.Remove("nested");
        var first = await p.Accept(a);
        if (activeNested) { f.Tray.Approve(); p.Workers[0].Backend.StartVideo(); await Until(() => first.State == RecState.recording); }
        var b = p.Request(f.Root, "unused.mp4", activeNested ? "inner" : "outer", activeNested ? first : null, av: false);
        b["output"]!.AsObject().Remove("filename"); var second = await p.Accept(b);
        Assert.Contains(first.Id, Path.GetFileName(first.OutputPath)); Assert.Contains(second.Id, Path.GetFileName(second.OutputPath));
        Assert.NotEqual(first.OutputPath, second.OutputPath);
        if (activeNested) { f.Tray.Approve(); p.Workers[1].Backend.StartVideo(); await Until(() => second.State == RecState.recording); Assert.Equal(2, p.Starts); }
        else { Assert.Equal(2, f.Tray.Confirmations.Count); Assert.Empty(p.Workers); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultVideoIdentityExplicitCollisionStillRejectsAndDistinctNamesRemainAllowed(bool template)
    {
        using var p = new Pair(); var f = p.F;
        JsonObject Request(string name)
        {
            var q = p.Request(f.Root, name + ".mp4", "outer", av: false);
            if (template) { q["output"]!.AsObject().Remove("filename"); q["output"]!["filename_template"] = name; }
            return q;
        }
        var a = Request("explicit"); a.Remove("nested"); var first = await p.Accept(a);
        using var response = await f.Post("/api/v1/recordings?creation_wait_ms=0", Request("explicit"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Contains("OUTPUT_PATH_IN_USE", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(first.Id, first.OutputPath); Assert.Single(f.Tray.Confirmations); Assert.Empty(p.Workers);
        var second = await p.Accept(Request("different")); Assert.EndsWith("different.mp4", second.OutputPath);
        Assert.DoesNotContain(second.Id, second.OutputPath); Assert.Equal(2, f.Tray.Confirmations.Count);
    }
    [Fact]
    public void DefaultVideoIdentityExplicitTemplateAndFilenamePriorityStayUnchanged()
    {
        using var temp = new TempDirectory(); var rec = new Recording("rec_identity380");
        var output = new JsonObject { ["directory"] = temp.Path, ["filename"] = "chosen", ["filename_template"] = "ignored-{id}" };
        Assert.Equal("chosen.mp4", Path.GetFileName(OutputPathResolver.BuildOutputPath(output, rec)));
        output.Remove("filename"); output["filename_template"] = "recording-{datetime}";
        var name = Path.GetFileName(OutputPathResolver.BuildOutputPath(output, rec));
        Assert.Matches(@"^recording-\d{4}-\d{2}-\d{2}-\d{6}\.mp4$", name); Assert.DoesNotContain(rec.Id, name);
    }
    [Theory]
    [InlineData("rename")]
    [InlineData("fail")]
    [InlineData("overwrite")]
    public void DefaultVideoIdentityStaticExplicitFilePolicyRemainsUnchanged(string policy)
    {
        using var temp = new TempDirectory(); var path = Path.Combine(temp.Path, "chosen.mp4"); File.WriteAllText(path, "owned bytes");
        var output = new JsonObject { ["directory"] = temp.Path, ["filename"] = "chosen.mp4", ["conflict_policy"] = policy };
        if (policy == "overwrite") Assert.Equal("PERMISSION_DENIED", Assert.Throws<ApiException>(() => OutputPathResolver.BuildOutputPath(output, new Recording("rec_identity380"))).Code);
        else if (policy == "fail") Assert.Equal("OUTPUT_PATH_INVALID", Assert.Throws<ApiException>(() => OutputPathResolver.BuildOutputPath(output, new Recording("rec_identity380"))).Code);
        else Assert.Equal(Path.Combine(temp.Path, policy == "rename" ? "chosen-1.mp4" : "chosen.mp4"), OutputPathResolver.BuildOutputPath(output, new Recording("rec_identity380")));
        Assert.Equal("owned bytes", File.ReadAllText(path));
    }
}
