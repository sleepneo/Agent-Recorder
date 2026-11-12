using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class DurableAuthorizationIdentityTests
{
    private const string Sid = "S-1-5-21-100-200-300-1001";
    private static string Binding => DurableAuthorizationIdentity.Format(Sid, 99, 12345, 1)!;

    [Theory]
    [InlineData(null)]
    [InlineData("old-user-domain-session")]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("winlogon-v2|S-1-5-21-100-200-300-1001|00000063|0000000000003039|00000001")]
    [InlineData("winlogon-v1|S-1-5-21-100-200-300-1001|00000000|0000000000003039|00000001")]
    [InlineData("winlogon-v1|S-1-5-21-100-200-300-1001|00000063|0000000000000000|00000001")]
    [InlineData("winlogon-v1|bad-sid|00000063|0000000000003039|00000001")]
    [InlineData("winlogon-v1|S-1-5-21-100-200-300-1001|63|3039|1")]
    [InlineData("winlogon-v1|S-1-5-21-100-200-300-1001|00000063|0000000000003039")]
    [InlineData("winlogon-v1|S-1-5-21-100-200-300-1001|0000006A|0000000000003039|00000001")]
    public void LegacyIncompleteUnknownOrNoncanonicalSourceIsNeverUsable(string? value)
    {
        var result = new DurableAuthorizationIdentity(() => new(value, null)).Read();
        Assert.Null(result.Binding);
        Assert.Equal("authorization_identity_fields_invalid", result.Reason);
    }

    [Fact]
    public void IdentityIncludesBootLoginAndUserEvenWithSameNumericSession()
    {
        Assert.True(DurableAuthorizationIdentity.IsCanonical(Binding));
        Assert.NotEqual(Binding, DurableAuthorizationIdentity.Format(Sid, 100, 12345, 1));
        Assert.NotEqual(Binding, DurableAuthorizationIdentity.Format(Sid, 99, 12346, 1));
        Assert.NotEqual(Binding, DurableAuthorizationIdentity.Format("S-1-5-21-100-200-300-1002", 99, 12345, 1));
        Assert.Equal(Binding, DurableAuthorizationIdentity.Format(Sid, 99, 12345, 1));
    }

    [Fact]
    public void ExplicitFailureCannotReusePreviousSuccessAndManualIdentityRemainsAvailable()
    {
        var calls = 0;
        var authority = new DurableAuthorizationIdentity(() => ++calls == 1 ? new(Binding, null)
            : throw new InvalidOperationException("native source failed"));
        using var scope = CaptureAuthorizationSessionBinding.UseIdentityForTests(authority);
        Assert.Equal(Binding, CaptureAuthorizationSessionBinding.Persistent);
        Assert.Empty(CaptureAuthorizationSessionBinding.Persistent);
        Assert.Equal("authorization_identity_unavailable", CaptureAuthorizationSessionBinding.ObservePersistent().Reason);
        Assert.StartsWith("interactive-process-v1|", CaptureAuthorizationSessionBinding.Current);
        Assert.False(DurableAuthorizationIdentity.IsCanonical(CaptureAuthorizationSessionBinding.Current));
        Assert.False(string.IsNullOrWhiteSpace(CaptureAuthorizationSessionBinding.History));
    }

    [Fact]
    public async Task TimedOutQueryRetainsSingleOwnerUntilRetirementWithoutCachingSuccess()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var calls = 0;
        var authority = new DurableAuthorizationIdentity(() =>
        {
            Interlocked.Increment(ref calls); entered.Set(); release.Wait(); return new(Binding, null);
        });
        var read = Task.Run(() => authority.Read(TimeSpan.FromMilliseconds(20)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.Equal("authorization_identity_timeout", (await read).Reason);
            Assert.Equal("authorization_identity_busy", authority.Read().Reason);
            Assert.Equal(1, calls);
        }
        finally { release.Set(); }
        for (var i = 0; i < 100 && authority.Read().Reason == "authorization_identity_busy"; i++) await Task.Delay(10);
        Assert.Equal(Binding, authority.Read().Binding);
        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task RealNativeLoginIdentityMatchesIndependentReadOnlyChildProcess()
    {
        var observation = DurableAuthorizationIdentity.Windows.Read();
        Assert.True(observation.Binding is not null, observation.Reason);
        Assert.True(DurableAuthorizationIdentity.IsCanonical(observation.Binding));
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(observation.Binding!)));
        var host = Path.Combine(AppContext.BaseDirectory, "AgentRecorder.FutureWindowUiTestHost.exe");
        Assert.True(File.Exists(host), host);
        for (var i = 0; i < 2; i++)
        {
            using var process = Process.Start(new ProcessStartInfo(host, "durable-identity-readonly")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(expected, (await process.StandardOutput.ReadToEndAsync()).Trim());
        }
        Assert.Equal(observation.Binding, DurableAuthorizationIdentity.Windows.Read().Binding);
    }
}
