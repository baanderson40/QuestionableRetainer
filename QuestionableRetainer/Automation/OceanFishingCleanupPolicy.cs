namespace QuestionableRetainer.Automation;

public readonly record struct OceanFishingCleanupSnapshot(
    bool DutyCompleted,
    bool InOceanTerritory,
    bool ResultsVisible,
    bool NormalConditions,
    bool HasPlayerBlockers,
    bool Fishing,
    bool Gathering,
    bool Casting,
    bool ChangingAreas,
    bool PlayerAvailable,
    bool LifestreamAvailable,
    bool LifestreamBusy,
    bool AutoRetainerAvailable,
    bool AutoRetainerBusy,
    bool AutoRetainerMultiModeActive);

public static class OceanFishingCleanupPolicy
{
    public static bool CanAccumulateStableTime(OceanFishingCleanupSnapshot snapshot) =>
        GetWaitReasons(snapshot).Count == 0;

    public static IReadOnlyList<string> GetWaitReasons(
        OceanFishingCleanupSnapshot snapshot,
        IReadOnlyList<string>? playerBlockers = null)
    {
        var reasons = new List<string>();
        if (!snapshot.DutyCompleted)
            reasons.Add("Ocean Fishing duty completion not confirmed");
        if (snapshot.InOceanTerritory)
            reasons.Add("still in Ocean Fishing territory");
        if (snapshot.ResultsVisible)
            reasons.Add("voyage results are still visible");
        if (!snapshot.NormalConditions)
            reasons.Add("player is not in normal conditions");
        if (snapshot.HasPlayerBlockers)
            reasons.Add(playerBlockers is { Count: > 0 }
                ? $"player blockers: {string.Join(',', playerBlockers)}"
                : "player state is blocked");
        if (snapshot.Fishing)
            reasons.Add("fishing action active");
        if (snapshot.Gathering)
            reasons.Add("gathering action active");
        if (snapshot.Casting)
            reasons.Add("casting active");
        if (snapshot.ChangingAreas)
            reasons.Add("player changing areas");
        if (!snapshot.PlayerAvailable)
            reasons.Add("player is not logged in or unavailable");
        if (!snapshot.LifestreamAvailable)
            reasons.Add("Lifestream state unavailable");
        else if (snapshot.LifestreamBusy)
            reasons.Add("Lifestream is busy");
        if (!snapshot.AutoRetainerAvailable)
            reasons.Add("AutoRetainer state unavailable");
        else
        {
            if (snapshot.AutoRetainerBusy)
                reasons.Add("AutoRetainer is busy");
            if (snapshot.AutoRetainerMultiModeActive)
                reasons.Add($"AutoRetainer Multi Mode is active (Busy={snapshot.AutoRetainerBusy})");
        }

        return reasons;
    }
}
