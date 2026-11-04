namespace QuestionableRetainer.Automation;

public static class WahToolsFeeshStatusPolicy
{
    public static bool IsTripFailedBeforeBoarding(WahToolsFeeshStatus status, bool voyageEntryObserved) =>
        !voyageEntryObserved && IsTripFailed(status);

    public static bool IsTripFailed(WahToolsFeeshStatus status) =>
        status.IsAvailable && string.Equals(status.TripState, "Failed", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> GetCleanupBlockers(WahToolsFeeshStatus status)
    {
        if (!status.IsAvailable)
            return [];

        var blockers = new List<string>();
        if (status.Cleaning == true)
            blockers.Add("WahTools cleaning flag is active");
        if (status.CleanerRunning == true)
            blockers.Add($"WahTools cleaner {status.CleanerState ?? "running"}: {status.CleanerStatus ?? "in progress"}");
        if (status.DesyntherRunning == true)
            blockers.Add($"WahTools desynthesis active: {status.DesyntherStatus ?? "in progress"}");
        if (status.DiscarderRunning == true)
            blockers.Add($"WahTools discard active: {status.DiscarderStatus ?? "in progress"}");
        if (status.BuyerBusy == true)
            blockers.Add("WahTools bait buyer is active");
        if (status.OnBoat == true)
            blockers.Add("WahTools still observes the player aboard the boat");
        if (status.TripRunning == true)
            blockers.Add($"WahTools trip {status.TripState ?? "running"}: {status.TripStatus ?? "in progress"}");

        return blockers;
    }

    public static bool HasCleanerFailure(WahToolsFeeshStatus status) =>
        status.IsAvailable && string.Equals(status.CleanerState, "Failed", StringComparison.OrdinalIgnoreCase);
}
