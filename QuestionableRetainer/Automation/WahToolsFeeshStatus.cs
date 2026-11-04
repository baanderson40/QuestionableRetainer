namespace QuestionableRetainer.Automation;

public enum WahToolsStatusAvailability
{
    Available,
    NotInstalled,
    Incompatible,
    ReadFailed,
}

public sealed record WahToolsFeeshStatus(
    WahToolsStatusAvailability Availability,
    DateTime ObservedAtUtc,
    string Detail,
    bool? TripInitialized,
    string? TripState,
    string? TripStatus,
    bool? TripRunning,
    bool? OnBoat,
    bool? Cleaning,
    string? CleanerState,
    string? CleanerStatus,
    bool? CleanerRunning,
    bool? DesyntherRunning,
    string? DesyntherStatus,
    bool? DiscarderRunning,
    string? DiscarderStatus,
    bool? BuyerBusy)
{
    public long Generation { get; init; }

    public bool IsAvailable => Availability == WahToolsStatusAvailability.Available;

    public static WahToolsFeeshStatus Unavailable(
        WahToolsStatusAvailability availability,
        string detail,
        DateTime? observedAtUtc = null) =>
        new(
            availability,
            observedAtUtc ?? DateTime.UtcNow,
            detail,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
}
