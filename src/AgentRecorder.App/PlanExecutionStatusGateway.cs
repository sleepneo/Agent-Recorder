using System.Security.Principal;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Persistence;

namespace AgentRecorder.App;

internal sealed class PlanExecutionStatusGateway : IPlanExecutionStatusGateway, IPlanExecutionHistoryGateway
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

    public PlanExecutionHistoryPageState? GetPage(string planId, int limit, string? cursor)
    {
        string? sid;
        try { sid = WindowsIdentity.GetCurrent().User?.Value; }
        catch { sid = null; }

        PlanExecutionHistoryPageSnapshot? snapshot;
        try { snapshot = _query.GetHistory(planId, sid, CaptureAuthorizationSessionBinding.Current, limit, cursor); }
        catch (PlanExecutionHistoryCursorException exception) { throw new ArgumentException("The history cursor is invalid.", exception); }
        return snapshot is null
            ? null
            : new PlanExecutionHistoryPageState(snapshot.PlanId, snapshot.Kind,
                snapshot.Items.Select(item => new PlanExecutionHistoryItemState(
                    item.OccurrenceId, item.CreatedAtUtc, item.WindowStartUtc, item.WindowEndUtc,
                    item.Status, item.TerminalReasonCode, item.RunId,
                    item.Run is null ? null : new PlanExecutionRunState(item.Run.RunId, item.Run.Status, item.Run.TerminalReasonCode),
                    item.ExecutionStatusCode, item.OutputPath, item.OutputPathRecorded, item.OutputFileExists)).ToArray(),
                snapshot.NextCursor);
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
