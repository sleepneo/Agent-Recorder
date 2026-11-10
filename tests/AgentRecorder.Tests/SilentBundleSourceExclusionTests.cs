using System.Net;
using System.Text;
using System.Security.Cryptography;
using AgentRecorder.Core;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class OrdinaryWgcBundleTests
{
    private static async Task BundleSourceDenied(Fixture f, Recording rec)
    {
        if (f.Api is null) f.StartApi(); using var client = f.Client();
        var request = f.Request("display"); request["output"]!["conflict_policy"] = "rename";
        using var response = await client.PostAsync("/api/v1/recordings?creation_wait_ms=0",
            new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("OUTPUT_PATH_IN_USE", await response.Content.ReadAsStringAsync());
        Assert.Single(f.Engine._recs); Assert.Equal(1, f.FactoryCalls); Assert.Equal(RecState.completed, rec.State);
        Assert.True(File.Exists(rec.OutputPath));
    }


}
