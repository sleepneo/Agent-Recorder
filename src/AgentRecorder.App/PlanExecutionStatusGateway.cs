using System.Security.Principal;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Persistence;

namespace AgentRecorder.App;

internal sealed class PlanExecutionStatusGateway : IPlanExecutionStatusGateway
{
    private readonly PlanExecutionStatusQueryService _query;

    internal PlanExecutionStatusGateway(PlanExecutionStatusQueryService query) =>
        _query = query ?? throw new ArgumentNullException(nameof(query));

    public PlanExecutionStatusState? Get(string planId)
    {
        string? sid;
        try
        {
            sid = WindowsIdentity.GetCurrent().User?.Value;
        }
        catch
        {
            sid = null;
        }

        var snapshot = _query.Get(planId, sid, CaptureAuthorizationSessionBinding.Current);
        return snapshot is null
            ? null
            : new PlanExecutionStatusState(
                snapshot.PlanId,
                snapshot.Kind,
                snapshot.PlanStatus,
                snapshot.ScheduleExhausted,
                snapshot.OccurrenceCount,
                Map(snapshot.NextOccurrence),
                Map(snapshot.LatestOccurrence));
    }

    private static PlanExecutionOccurrenceState? Map(PlanExecutionStatusOccurrenceSnapshot? occurrence) =>
        occurrence is null
            ? null
            : new PlanExecutionOccurrenceState(
                occurrence.OccurrenceId,
                occurrence.WindowStartUtc,
                occurrence.WindowEndUtc,
                occurrence.Status,
                occurrence.TerminalReasonCode,
                occurrence.RunId,
                occurrence.Run is null
                    ? null
                    : new PlanExecutionRunState(
                        occurrence.Run.RunId,
                        occurrence.Run.Status,
                        occurrence.Run.TerminalReasonCode),
                occurrence.OutputPath,
                occurrence.OutputPathRecorded,
                occurrence.OutputFileExists,
                occurrence.ExecutionStatusCode);
}
