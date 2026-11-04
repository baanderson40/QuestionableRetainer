namespace QuestionableRetainer.Automation;

public readonly record struct OceanFishingCharacterIdentity(ulong ContentId, string Name, string World);

public enum OceanFishingObservation
{
    Waiting,
    TerritoryEntered,
    DutyActive,
    DutyCompleted,
    TerritoryExitedUnconfirmed,
}

public static class OceanFishingPolicy
{
    public static bool TrySelectCharacter(
        OceanFishingCharacterMode mode,
        OceanFishingCharacterIdentity current,
        OceanFishingCharacterIdentity? selected,
        out OceanFishingCharacterIdentity target)
    {
        target = mode == OceanFishingCharacterMode.CurrentCharacter ? current : selected.GetValueOrDefault();
        return mode switch
        {
            OceanFishingCharacterMode.CurrentCharacter => IsValid(current),
            OceanFishingCharacterMode.SelectedCharacter => selected is { } value && IsValid(value),
            _ => false,
        };
    }

    public static OceanFishingObservation Observe(
        bool inOceanTerritory,
        bool dutyActive,
        bool dutyCompleted,
        bool territoryEnteredPreviously) =>
        dutyCompleted
            ? OceanFishingObservation.DutyCompleted
            : dutyActive
                ? OceanFishingObservation.DutyActive
                : inOceanTerritory
                    ? territoryEnteredPreviously
                        ? OceanFishingObservation.Waiting
                        : OceanFishingObservation.TerritoryEntered
                    : territoryEnteredPreviously
                        ? OceanFishingObservation.TerritoryExitedUnconfirmed
                        : OceanFishingObservation.Waiting;

    public static bool CanDispatch(DateTime nowUtc, DateTime? claimedWindowStartUtc, bool targetConfirmed, bool playerSafe) =>
        targetConfirmed &&
        playerSafe &&
        claimedWindowStartUtc is { } start &&
        nowUtc < start + OceanFishingSchedule.LaunchCutoffOffset;

    public static bool HasWahToolsStopSession(HandoffSession session) =>
        session.OceanWindowStartUtc is not null && session.WahToolsStopRequested;

    public static bool IsStableForReturn(DateTime stableSinceUtc, DateTime nowUtc, TimeSpan requiredDuration) =>
        nowUtc >= stableSinceUtc + requiredDuration;

    private static bool IsValid(OceanFishingCharacterIdentity identity) =>
        identity.ContentId != 0 &&
        !string.IsNullOrWhiteSpace(identity.Name) &&
        !string.IsNullOrWhiteSpace(identity.World);
}
