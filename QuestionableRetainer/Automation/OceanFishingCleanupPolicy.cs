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
        snapshot.DutyCompleted &&
        !snapshot.InOceanTerritory &&
        !snapshot.ResultsVisible &&
        snapshot.NormalConditions &&
        !snapshot.HasPlayerBlockers &&
        !snapshot.Fishing &&
        !snapshot.Gathering &&
        !snapshot.Casting &&
        !snapshot.ChangingAreas &&
        snapshot.PlayerAvailable &&
        snapshot.LifestreamAvailable &&
        !snapshot.LifestreamBusy &&
        snapshot.AutoRetainerAvailable &&
        !snapshot.AutoRetainerBusy &&
        !snapshot.AutoRetainerMultiModeActive;
}
