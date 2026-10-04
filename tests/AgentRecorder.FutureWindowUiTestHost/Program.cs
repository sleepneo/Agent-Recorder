using System.Text.Json;
using AgentRecorder.App;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.FutureWindowUiTestHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 || args[1] is not ("parent-close" or "cancel-after-visible"))
            return 2;

        var auditPath = Path.GetFullPath(args[0]);
        var authorizationId = "fwa_" + Guid.NewGuid().ToString("N");
        ApplicationConfiguration.Initialize();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        System.Threading.Timer? cancelTimer = null;
        var details = new FutureWindowAuthorizationApprovalDetails(
            ExecutablePath: @"C:\TestOnly\Player.exe",
            FileIdentity: "TESTONLY:0000000000000001",
            Sha256: new string('a', 64),
            PublisherSubject: "CN=Test-Only Publisher",
            PublisherCertificateSha256: new string('b', 64),
            SystemAudioEndpointName: null,
            SystemAudioEndpointId: null,
            MaximumDurationSeconds: 20,
            ValiditySeconds: 120,
            OutputPath: Path.Combine(Path.GetDirectoryName(auditPath)!, "not-created.mp4"),
            UserSid: "S-1-5-21-test-only",
            SessionBinding: "test-only-session");
        void WriteAudit(string stage, object payload)
        {
            var line = JsonSerializer.Serialize(new
            {
                authorization_id = authorizationId,
                stage,
                process_id = Environment.ProcessId,
                thread_id = Environment.CurrentManagedThreadId,
                payload,
            });
            File.AppendAllText(auditPath, line + Environment.NewLine);
        }

        Action? onVisible = args[1] == "cancel-after-visible"
            ? () => cancelTimer = new System.Threading.Timer(
                _ => cancellation.Cancel(), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan)
            : null;
        try
        {
            var outcome = FutureWindowAuthorizationApprovalForm.ShowModal(
                authorizationId, details, new UiTextProvider(UiLanguage.EnUs), WriteAudit,
                cancellation.Token, afterNativeVisibleForTest: onVisible,
                disableApprovalForTest: true);
            return outcome is FutureWindowApprovalOutcome.Rejected or FutureWindowApprovalOutcome.Cancelled ? 0 : 3;
        }
        catch (Exception exception)
        {
            WriteAudit("host_exception", new { exception_type = exception.GetType().Name });
            return 4;
        }
        finally
        {
            cancelTimer?.Dispose();
        }
    }
}
