namespace AgentRecorder.Infrastructure;

// Process-local display identity only. RecordingId remains the API/authorization
// identity; this value is never persisted as a target or authorization scope.
public sealed record WindowRecordingIdentity(long Number, string Title);
