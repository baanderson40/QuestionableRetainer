namespace QuestionableRetainer.Automation;

public readonly record struct RetainerReadiness(
    IReadOnlyList<long> RemainingSeconds,
    long? ClosestEligibleSeconds,
    bool WaitForAll,
    int AdvanceSeconds);

public static class ReadinessEvaluator
{
    public static bool IsRetainerReady(RetainerReadiness readiness)
    {
        if (readiness.ClosestEligibleSeconds is not { } closest || closest > readiness.AdvanceSeconds)
            return false;

        if (!readiness.WaitForAll)
            return true;

        return readiness.RemainingSeconds.Count == 0 ||
               readiness.RemainingSeconds.All(seconds => seconds <= readiness.AdvanceSeconds);
    }
}
