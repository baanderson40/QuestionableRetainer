namespace QuestionableRetainer.Automation;

public readonly record struct OceanFishingWindow(
    DateTime StartUtc,
    DateTime RegistrationEndUtc,
    DateTime LaunchCutoffUtc);

public readonly record struct OceanFishingScheduleState(
    OceanFishingWindow Window,
    TimeSpan TimeUntilRegistration,
    bool RegistrationOpen,
    bool LaunchEligible);

public static class OceanFishingSchedule
{
    public static readonly TimeSpan RegistrationLength = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(2);
    public static readonly TimeSpan LaunchCutoffOffset = TimeSpan.FromMinutes(14);

    public static OceanFishingScheduleState GetState(DateTime utcNow, TimeSpan leadTime)
    {
        utcNow = EnsureUtc(utcNow);
        leadTime = NormalizeLeadTime(leadTime);

        var window = GetWindow(utcNow);
        var launchStart = window.StartUtc - leadTime;
        var registrationOpen = utcNow >= window.StartUtc && utcNow < window.RegistrationEndUtc;
        var launchEligible = utcNow >= launchStart && utcNow < window.LaunchCutoffUtc;
        return new(window, window.StartUtc - utcNow, registrationOpen, launchEligible);
    }

    public static OceanFishingWindow GetWindow(DateTime utcNow)
    {
        utcNow = EnsureUtc(utcNow);
        var evenHour = utcNow.Hour - utcNow.Hour % 2;
        var start = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, evenHour, 0, 0, DateTimeKind.Utc);

        if (utcNow >= start + RegistrationLength)
            start += Interval;

        return new(start, start + RegistrationLength, start + LaunchCutoffOffset);
    }

    public static bool IsWindowHandled(DateTime? handledWindowStartUtc, DateTime windowStartUtc) =>
        handledWindowStartUtc is { } handled && EnsureUtc(handled) == EnsureUtc(windowStartUtc);

    public static TimeSpan NormalizeLeadTime(TimeSpan leadTime) =>
        TimeSpan.FromMinutes(Math.Clamp(leadTime.TotalMinutes, 0, 30));

    private static DateTime EnsureUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
