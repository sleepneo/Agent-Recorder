namespace AgentRecorder.Persistence;

// Both readers own the slot until their actual worker exits, including late native work.
// Static process scope survives service and gateway recreation. There is no queue.
internal static class OrdinaryContentVerificationAdmission
{
    private static int _busy;
    internal static bool TryAcquire() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;
    internal static void Release() => Volatile.Write(ref _busy, 0);
}
