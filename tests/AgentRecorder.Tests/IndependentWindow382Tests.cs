using System.Net;
using System.Text.Json.Nodes;
using AgentRecorder.Core;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class OrdinaryWgcBundleTests
{
    private static JsonObject IndependentWindow382Request(Fixture f, string window = "window_42")
    {
        var q = f.Request("window");
        q.Remove("required_capture_semantics");
        q["source"]!["window_id"] = window;
        q["output"]!.AsObject().Remove("filename");
        return q;
    }

    [Fact]
    public async Task IndependentWindow382SecondRawWindowCanAwaitOwnConfirmationWithoutParent()
    {
        using var f = RuntimeFixture(); NestedWindow381Providers(f); f.StartApi();
        var first = await NestedWindow381Accept(f, IndependentWindow382Request(f));
        var result = await NestedWindow381Post(f, IndependentWindow382Request(f, "window_43"));
        SaveSeriesBundleEvidence("independent382-red", new { http = (int)result.Status, result.Body,
            first = first.State.ToString(), confirmations = f.Engine._confs.Count, factories = f.FactoryCalls });
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal(2, f.Engine._recs.Count);
        Assert.All(f.Engine._recs.Values, r => {
            Assert.Equal(RecState.pending_confirmation, r.State);
            Assert.Null(r.NestedRole); Assert.Null(r.ParentRecordingId); Assert.Null(r.AuthorizationProof);
        });
        Assert.Equal(0, f.FactoryCalls);
    }
}
