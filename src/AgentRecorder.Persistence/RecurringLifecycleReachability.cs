namespace AgentRecorder.Persistence;

/// <summary>
/// Exact finite-state reachability for persisted aggregate versions. A
/// version is the number of successful state transitions after the supplied
/// initial version; no arbitrary loop bound is used for cyclic graphs.
/// </summary>
internal static class RecurringLifecycleReachability
{
    internal static bool IsExactPath<TStatus>(
        TStatus initialStatus,
        long initialVersion,
        TStatus targetStatus,
        long targetVersion,
        Func<TStatus, TStatus, bool> isEdge)
        where TStatus : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(isEdge);
        if (initialVersion < 0 || targetVersion < initialVersion)
            return false;

        var states = Enum.GetValues<TStatus>();
        var initialIndex = Array.IndexOf(states, initialStatus);
        var targetIndex = Array.IndexOf(states, targetStatus);
        if (initialIndex < 0 || targetIndex < 0)
            return false;

        var transition = new bool[states.Length, states.Length];
        for (var row = 0; row < states.Length; row++)
        {
            for (var column = 0; column < states.Length; column++)
            {
                transition[row, column] = row != column && isEdge(states[row], states[column]);
            }
        }

        var reachable = Identity(states.Length);
        var power = transition;
        var steps = targetVersion - initialVersion;
        while (steps > 0)
        {
            if ((steps & 1L) != 0)
                reachable = Multiply(reachable, power);
            steps >>= 1;
            if (steps > 0)
                power = Multiply(power, power);
        }

        return reachable[initialIndex, targetIndex];
    }

    private static bool[,] Identity(int size)
    {
        var identity = new bool[size, size];
        for (var index = 0; index < size; index++)
            identity[index, index] = true;
        return identity;
    }

    private static bool[,] Multiply(bool[,] left, bool[,] right)
    {
        var size = left.GetLength(0);
        var result = new bool[size, size];
        for (var row = 0; row < size; row++)
        {
            for (var pivot = 0; pivot < size; pivot++)
            {
                if (!left[row, pivot])
                    continue;

                for (var column = 0; column < size; column++)
                {
                    if (right[pivot, column])
                        result[row, column] = true;
                }
            }
        }

        return result;
    }
}
